namespace SceneKit;

/// <summary>Small software renderer for the inspector preview (port of scenekit/softrender.py): z-buffered triangles
/// (pixel centre coverage), headlight shading, wireframe with depth test, back faces tinted red (flipped normals),
/// per-vertex scalar heat map (deviation after simplifying). Renders in parallel horizontal bands.</summary>
public static class SoftRender
{
    static readonly double[] Bg = { 0.17, 0.18, 0.2 };
    static readonly double[] WireCol = { 0.05, 0.05, 0.06 };
    static readonly double[] BackCol = { 0.85, 0.2, 0.2 };
    static readonly double[] DefaultCol = { 0.8, 0.8, 0.78 };

    /// <summary>What the triangles are coloured with (the heat map, when given, wins over all of them).</summary>
    public enum Paint { Material, Textured, UvChecker }

    /// <summary>World-space triangles: V (n x 3), T (m x 3), per-triangle colours (m x 3), per-vertex scalars and
    /// texture coordinates (n x 2, unit 0) with the texture of unit 0.</summary>
    public sealed class Mesh
    {
        public readonly double[] V;
        public readonly int[] T;
        public readonly double[]? Colors;
        public readonly double[]? Scalar;
        public readonly int Style;               // 0 normal, 1 context (dimmed grey, no wireframe), 2 highlighted selection
        public readonly double[]? Uv;
        public readonly Texture? Tex;
        /// <summary>Back faces are not drawn (the state set turns GL_CULL_FACE on, e.g. billboard cards).</summary>
        public readonly bool Cull;
        public Mesh(double[] v, int[] t, double[]? colors = null, double[]? scalar = null, int style = 0, double[]? uv = null, Texture? tex = null,
            bool cull = false)
        { V = v; T = t; Colors = colors; Scalar = scalar; Style = style; Uv = uv; Tex = tex; Cull = cull; }
        public Mesh WithStyle(int style) => new(V, T, Colors, Scalar, style, Uv, Tex, Cull);
        public int NumTris => T.Length / 3;
        public int NumVerts => V.Length / 3;
    }

    public static Mesh? MeshFromGeom(GeomData? g, double[]? mx = null, StateSet? stateset = null, double[]? scalar = null)
    {
        if (g?.Vertices == null) return null;
        var v0 = g.Vertices.Data;
        int nv = g.Vertices.Count;
        var tAll = g.Triangles();
        if (tAll.Length == 0 || nv == 0) return null;
        var tl = new List<int>(tAll.Length);
        for (int t = 0; t + 2 < tAll.Length; t += 3)
        {
            int a = tAll[t], b = tAll[t + 1], c = tAll[t + 2];
            if (a >= 0 && a < nv && b >= 0 && b < nv && c >= 0 && c < nv) { tl.Add(a); tl.Add(b); tl.Add(c); }
        }
        var T = tl.ToArray();
        int comps = g.Vertices.Comps;
        var V = new double[nv * 3];
        for (int i = 0; i < nv; i++)
        {
            double x = v0[i * comps], y = comps > 1 ? v0[i * comps + 1] : 0, z = comps > 2 ? v0[i * comps + 2] : 0;
            if (mx != null)
            {
                V[3 * i] = mx[0] * x + mx[1] * y + mx[2] * z + mx[3];
                V[3 * i + 1] = mx[4] * x + mx[5] * y + mx[6] * z + mx[7];
                V[3 * i + 2] = mx[8] * x + mx[9] * y + mx[10] * z + mx[11];
            }
            else { V[3 * i] = x; V[3 * i + 1] = y; V[3 * i + 2] = z; }
        }
        int m = T.Length / 3;
        double[]? col = null;
        var c0 = g.Colors;
        if (c0 != null && c0.Count > 0 && c0.Comps >= 3)
        {
            int cc = c0.Comps;
            if (c0.Binding == "overall" || c0.Count == 1)
            {
                col = new double[m * 3];
                for (int t = 0; t < m; t++) for (int k = 0; k < 3; k++) col[3 * t + k] = c0.Data[k];
            }
            else if (c0.Count == nv)
            {
                col = new double[m * 3];
                for (int t = 0; t < m; t++)
                    for (int k = 0; k < 3; k++)
                        col[3 * t + k] = (c0.Data[T[3 * t] * cc + k] + c0.Data[T[3 * t + 1] * cc + k] + c0.Data[T[3 * t + 2] * cc + k]) / 3.0;
            }
        }
        if (col == null && stateset?.Diffuse != null)
        {
            col = new double[m * 3];
            for (int t = 0; t < m; t++) for (int k = 0; k < 3; k++) col[3 * t + k] = stateset.Diffuse[k];
        }
        if (col != null && col.Length > 0 && col.Max() > 1.0)
            for (int i = 0; i < col.Length; i++) col[i] /= 255.0;
        double[]? uv = null;
        if (g.TexCoords.TryGetValue(0, out var tc) && tc.Count == nv && tc.Comps >= 2)
        {
            uv = new double[nv * 2];
            for (int i = 0; i < nv; i++) { uv[2 * i] = tc.Data[i * tc.Comps]; uv[2 * i + 1] = tc.Data[i * tc.Comps + 1]; }
        }
        var tex = stateset?.Textures.FirstOrDefault(t => t.Unit == 0);
        bool cull = stateset != null && stateset.Modes.TryGetValue("GL_CULL_FACE", out var cf) && cf.StartsWith("ON");
        return new Mesh(V, T, col, scalar, 0, uv, tex, cull);
    }

    /// <summary>(geometry node, matrix relative to <paramref name="node"/>, state set) below a node: only the most detailed
    /// LOD level, only enabled switch children, hidden (node mask 0) subtrees skipped. <paramref name="externals"/> false:
    /// external references are not followed (the content of loaded files is left out, as the passes do without externals).</summary>
    public static List<(Node node, double[] world, StateSet? ss)> GeometryItems(Node node, int maxItems = int.MaxValue, double[]? start = null, int maxNodes = 5_000_000,
        bool externals = true)
    {
        var outl = new List<(Node, double[], StateSet?)>();
        Walk(node, start, maxItems, maxNodes, (n, w, ss, _) => outl.Add((n, w, ss)), externals);
        return outl;
    }

    /// <summary>Like GeometryItems, plus the path from <paramref name="node"/> down to each geometry (both included), so
    /// callers can look at every state set and node above a geometry.</summary>
    public static List<(Node node, double[] world, StateSet? ss, Node[] path)> GeometryPaths(Node node, double[]? start = null)
    {
        var outl = new List<(Node, double[], StateSet?, Node[])>();
        Walk(node, start, int.MaxValue, int.MaxValue, (n, w, ss, up) =>
        {
            var l = new List<Node>();
            for (var p = up; p != null; p = p.Up) l.Add(p.N);
            l.Reverse();
            outl.Add((n, w, ss, l.ToArray()));
        });
        return outl;
    }

    /// <summary>Like GeometryPaths, but a node below <paramref name="node"/> for which <paramref name="stopAt"/> holds is
    /// not entered: it goes into <paramref name="stopped"/> with the matrix above it (its own not applied) and the path
    /// from <paramref name="node"/> down to it (both included). <paramref name="lodAlternativesDropped"/>: LOD children
    /// left out (the levels other than the nearest one, hidden LOD siblings).</summary>
    public static List<(Node node, double[] world, StateSet? ss, Node[] path)> GeometryPaths(Node node, double[]? start, Func<Node, bool> stopAt,
        List<(Node node, double[] world, Node[] path)> stopped, out int lodAlternativesDropped)
    {
        var outl = new List<(Node, double[], StateSet?, Node[])>();
        int dropped = 0;
        Walk(node, start, int.MaxValue, int.MaxValue, (n, w, ss, up) => outl.Add((n, w, ss, Path(up))), stop: (stopAt, (n, w, up) => stopped.Add((n, w, Path(up)))),
            lodDropped: k => dropped += k);
        lodAlternativesDropped = dropped;
        return outl;
    }

    static Node[] Path(PathLink up)
    {
        var l = new List<Node>();
        for (var p = up; p != null; p = p.Up) l.Add(p.N);
        l.Reverse();
        return l.ToArray();
    }

    sealed record PathLink(Node N, PathLink? Up);

    static void Walk(Node node, double[]? start, int maxItems, int maxNodes, Action<Node, double[], StateSet?, PathLink> found, bool externals = true,
        (Func<Node, bool> at, Action<Node, double[], PathLink> found)? stop = null, Action<int>? lodDropped = null)
    {
        var stack = new Stack<(Node n, double[] w, StateSet? ss, PathLink? up)>();
        stack.Push((node, start ?? Mat.Identity(), null, null));
        int seen = 0, items = 0;
        while (stack.Count > 0 && items < maxItems && seen < maxNodes)
        {
            var (n, w, ss, up) = stack.Pop();
            seen++;
            if (n.NodeMask == 0) continue;
            if (!externals && n.Kind == Kind.External) continue;
            if (stop != null && !ReferenceEquals(n, node) && stop.Value.at(n)) { stop.Value.found(n, w, new PathLink(n, up)); continue; }
            if (n.Matrix != null) w = Mat.Mul(w, n.Matrix);
            if (n.Kind == Kind.Geode && n.Cls.EndsWith("Billboard", StringComparison.Ordinal) && n.Center is { Length: >= 3 } bc)
            {
                // a billboard: its drawables are relative to its position (drawn as stored, not turned to the camera)
                var t = Mat.Identity();
                t[3] = bc[0]; t[7] = bc[1]; t[11] = bc[2];
                w = Mat.Mul(w, t);
            }
            ss = n.StateSet ?? ss;
            var here = new PathLink(n, up);
            if (n.Kind == Kind.Geometry)
            {
                if (n.Geom != null) { found(n, w, ss, here); items++; }
                continue;
            }
            IEnumerable<Node> kids = n.AllChildren();
            bool lodCut = false;
            if (n.Kind == Kind.Lod && n.Ranges is { Count: > 0 } && n.Children.Count > 1 && !Analyzer.OneRangeForAll(n))
            {
                int cnt = Math.Min(n.Children.Count, n.Ranges.Count), k = 0;
                for (int i = 1; i < cnt; i++) if (n.Ranges[i].min < n.Ranges[k].min) k = i;
                kids = new[] { n.Children[k] };
                lodCut = true;
            }
            if (n.Kind == Kind.Switch && n.SwitchValues != null)
                kids = n.Children.Where((c, i) => i >= n.SwitchValues.Count || n.SwitchValues[i]);
            else if (Analyzer.HiddenLodSiblings(n.AllChildren()) is bool[] hiddenLods)
            {
                kids = n.AllChildren().Where((c, i) => !hiddenLods[i]);
                lodCut = true;
            }
            if (lodCut && lodDropped != null) lodDropped(n.AllChildren().Count - kids.Count());
            foreach (var c in kids) stack.Push((c, w, ss, here));
        }
    }

    /// <summary>Product of the matrices of <paramref name="ancestors"/> (root first): where the node below them sits.</summary>
    public static double[] PathMatrix(IEnumerable<Node> ancestors)
    {
        var w = Mat.Identity();
        foreach (var a in ancestors) if (a.Matrix != null) w = Mat.Mul(w, a.Matrix);
        return w;
    }

    /// <summary>Matrix from the scene root down to (not including) <paramref name="node"/>, along the first parent of
    /// every node (shared nodes: the first place they appear).</summary>
    public static double[] ParentMatrix(Node node, IReadOnlyDictionary<Node, List<Node>> parents)
    {
        var chain = new List<double[]>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance) { node };
        var cur = node;
        while (parents.TryGetValue(cur, out var ps) && ps.Count > 0 && seen.Add(ps[0]))
        {
            cur = ps[0];
            if (cur.Matrix != null) chain.Add(cur.Matrix);
        }
        var w = Mat.Identity();
        for (int i = chain.Count - 1; i >= 0; i--) w = Mat.Mul(w, chain[i]);
        return w;
    }

    /// <summary>World-space meshes below a node (stops after <paramref name="maxTris"/> triangles).</summary>
    public static List<Mesh> Collect(Node node, long maxTris = 3_000_000)
    {
        var outl = new List<Mesh>();
        long total = 0;
        foreach (var (n, w, ss) in GeometryItems(node, int.MaxValue))
        {
            var m = MeshFromGeom(n.Geom, w, ss);
            if (m == null) continue;
            total += m.NumTris;
            if (total > maxTris) break;
            outl.Add(m);
        }
        return outl;
    }

    public sealed class Camera
    {
        public double[] Target;
        public double Radius, Yaw, Pitch, Dist, Fov;

        public Camera(double[] target, double radius, double yaw = -35.0, double pitch = 25.0, double? dist = null, double fov = 35.0)
        {
            Target = (double[])target.Clone();
            Radius = radius != 0 ? radius : 1.0;
            Yaw = yaw; Pitch = pitch; Fov = fov;
            Dist = dist ?? Radius / Math.Sin(Fov * Math.PI / 180 / 2) * 0.85;
        }

        public static Camera Fit(IEnumerable<Mesh> meshes)
        {
            var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            bool any = false;
            foreach (var m in meshes)
                foreach (int i in m.T)
                {
                    any = true;
                    for (int k = 0; k < 3; k++) { double v = m.V[3 * i + k]; if (v < lo[k]) lo[k] = v; if (v > hi[k]) hi[k] = v; }
                }
            if (!any) return new Camera(new double[3], 1.0);
            double dx = hi[0] - lo[0], dy = hi[1] - lo[1], dz = hi[2] - lo[2];
            return new Camera(new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 }, Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2);
        }

        public Camera Copy() => new(Target, Radius, Yaw, Pitch, Dist, Fov);

        public (double[] eye, double[] fwd, double[] right, double[] up) Basis()
        {
            double y = Yaw * Math.PI / 180, p = Pitch * Math.PI / 180;
            var d = new[] { Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p) };   // eye direction
            var eye = new[] { Target[0] + d[0] * Dist, Target[1] + d[1] * Dist, Target[2] + d[2] * Dist };
            var fwd = new[] { -d[0], -d[1], -d[2] };
            var up0 = Math.Abs(d[2]) < 0.999 ? new[] { 0.0, 0.0, 1.0 } : new[] { 0.0, 1.0, 0.0 };
            var right = Cross(fwd, up0);
            double ln = Math.Sqrt(right[0] * right[0] + right[1] * right[1] + right[2] * right[2]);
            for (int k = 0; k < 3; k++) right[k] /= ln;
            return (eye, fwd, right, Cross(right, fwd));
        }

        public void Orbit(double dyaw, double dpitch)
        {
            Yaw = ((Yaw + dyaw) % 360 + 360) % 360;
            Pitch = Math.Max(-89.0, Math.Min(89.0, Pitch + dpitch));
        }

        public void Zoom(double factor) => Dist = Math.Max(Radius * 0.05, Dist * factor);

        public void Pan(double dx, double dy)
        {
            var (_, _, right, up) = Basis();
            double s = Dist * Math.Tan(Fov * Math.PI / 180 / 2);
            for (int k = 0; k < 3; k++) Target[k] = Target[k] - right[k] * dx * s + up[k] * dy * s;
        }
    }

    static double[] Cross(double[] a, double[] b) =>
        new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    /// <summary>0 -> blue, vmax/2 -> green / yellow, >= vmax -> red.</summary>
    public static (double r, double g, double b) HeatColor(double v, double vmax)
    {
        double t = Math.Clamp(v / Math.Max(vmax, 1e-12), 0, 1);
        if (double.IsNaN(t)) t = 0;
        return (Math.Clamp(3.0 * t - 0.5, 0, 1), Math.Clamp(1.5 - Math.Abs(t - 0.5) * 3, 0, 1), Math.Clamp(1.0 - t * 2, 0, 1));
    }

    public sealed class Info { public long Triangles, Drawn; public bool Wire; }

    // ------------------------------------------------------------------ textures

    /// <summary>Decoded textures for the preview, at most MaxSide pixels per side, cached by file (cleared when the
    /// cache passes CacheBytes, so a scene with gigabytes of textures cannot fill the memory).</summary>
    public static class PreviewTextures
    {
        public const int MaxSide = 1024;
        const long CacheBytes = 1L << 30;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RawImage?> cache = new();
        static long bytes;

        /// <summary>The image of a texture, or null: missing = true when the texture should exist but cannot be read,
        /// false when it simply cannot be shown (embedded image data).</summary>
        public static RawImage? Get(Texture t, out bool missing)
        {
            missing = false;
            if (t.Baked != null) return t.Baked;
            if (t.Embedded) return null;
            if (!t.Exists || string.IsNullOrEmpty(t.Path)) { missing = true; return null; }
            var img = cache.GetOrAdd(t.Path, Load);
            missing = img == null;
            return img;
        }

        static RawImage? Load(string path)
        {
            RawImage? img;
            try { img = ImageCodecs.Read(path); }
            catch (Exception) { img = null; }
            if (img == null || img.Width == 0 || img.Height == 0) return null;
            int side = Math.Max(img.Width, img.Height);
            if (side > MaxSide)
                img = ImageCodecs.Resize(img, Math.Max(1, img.Width * MaxSide / side), Math.Max(1, img.Height * MaxSide / side));
            if (Interlocked.Add(ref bytes, img.Pixels.Length) > CacheBytes) { cache.Clear(); Interlocked.Exchange(ref bytes, img.Pixels.Length); }
            return img;
        }
    }

    /// <summary>Per-pixel colour source of one mesh: image texture, missing-texture checker, plain magenta (texture
    /// without UVs) or UV checker. Mul / Light / Back are per visible triangle.</summary>
    sealed class TexJob
    {
        public const int Image = 0, Missing = 1, Magenta = 2, Checker = 3;
        public int Mode;
        public double[]? Uv;
        public RawImage? Img;
        public int Style;
        public float[] Mul = Array.Empty<float>(), Light = Array.Empty<float>();
        public bool[] Back = Array.Empty<bool>();
    }

    static TexJob? JobFor(Mesh m, Paint paint)
    {
        if (paint == Paint.Material || m.Style == 1) return null;
        if (paint == Paint.UvChecker) return m.Uv != null ? new TexJob { Mode = TexJob.Checker, Uv = m.Uv, Style = m.Style } : null;
        if (m.Tex == null) return null;
        if (m.Uv == null) return new TexJob { Mode = TexJob.Magenta, Style = m.Style };
        var img = PreviewTextures.Get(m.Tex, out bool missing);
        if (img != null) return new TexJob { Mode = TexJob.Image, Uv = m.Uv, Img = img, Style = m.Style };
        return missing ? new TexJob { Mode = TexJob.Missing, Uv = m.Uv, Style = m.Style } : null;
    }

    static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Colour at (u, v) and whether the pixel is kept (alpha test at 0.5, like a cut-out leaf texture).</summary>
    static bool Sample(TexJob tj, double u, double v, out double r, out double g, out double b)
    {
        switch (tj.Mode)
        {
            case TexJob.Image:
            {
                var img = tj.Img!;
                int x = Math.Min(img.Width - 1, (int)(Frac(u) * img.Width));
                int y = Math.Min(img.Height - 1, (int)((1.0 - Frac(v)) * img.Height));   // v = 0 is the bottom row
                int c = img.Channels, o = (y * img.Width + x) * c;
                var p = img.Pixels;
                if (c >= 3) { r = p[o] / 255.0; g = p[o + 1] / 255.0; b = p[o + 2] / 255.0; }
                else { r = g = b = p[o] / 255.0; }
                return !((c == 4 && p[o + 3] < 128) || (c == 2 && p[o + 1] < 128));
            }
            case TexJob.Missing:
            {
                bool on = (((int)Math.Floor(u * 4) + (int)Math.Floor(v * 4)) & 1) == 0;
                r = on ? 1 : 0; g = 0; b = on ? 1 : 0;
                return true;
            }
            case TexJob.Checker:
            {
                bool on = (((int)Math.Floor(u * 8) + (int)Math.Floor(v * 8)) & 1) == 0;
                double l = on ? 0.9 : 0.3;
                r = l * (0.55 + 0.45 * Frac(u)); g = l * (0.55 + 0.45 * Frac(v)); b = l * 0.75;   // red grows with u, green with v
                return true;
            }
            default: r = 1; g = 0; b = 1; return true;
        }
    }

    /// <summary>Render to a BGRA32 image (width * height * 4 bytes, alpha 255).</summary>
    public static (byte[] bgra, Info info) Render(IReadOnlyList<Mesh> meshes, Camera cam, int width = 640, int height = 480,
        bool wire = false, bool shade = true, bool backfaces = true, double? heatMax = null, long wireLimit = 400_000,
        Paint paint = Paint.Material)
    {
        int W = Math.Max(1, width), H = Math.Max(1, height);
        var info = new Info();
        var img = new float[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            double f0 = 0.85 + 0.15 * (H > 1 ? 1.0 - (double)y / (H - 1) : 1.0);
            for (int x = 0; x < W; x++) { int o = 3 * (y * W + x); img[o] = (float)(Bg[0] * f0); img[o + 1] = (float)(Bg[1] * f0); img[o + 2] = (float)(Bg[2] * f0); }
        }
        if (meshes.Count == 0) return (ToBgra(img, W, H), info);
        var (eye, fwd, right, up) = cam.Basis();
        double f = 1.0 / Math.Tan(cam.Fov * Math.PI / 180 / 2);
        double aspect = (double)W / H;
        double near = cam.Radius * 1e-3;
        var zbuf = new double[W * H];
        Array.Fill(zbuf, double.PositiveInfinity);
        var edges = new List<(double[] sx, double[] sy, double[] cz, long[] e)>();
        foreach (var m in meshes)
        {
            int nt = m.NumTris, nv = m.NumVerts;
            if (nt == 0) continue;
            info.Triangles += nt;
            var sx = new double[nv]; var sy = new double[nv]; var cz = new double[nv];
            void Project(double wx, double wy, double wz, out double px, out double py, out double pz)
            {
                double rx = wx - eye[0], ry = wy - eye[1], rz = wz - eye[2];
                double z = rx * fwd[0] + ry * fwd[1] + rz * fwd[2];
                double cx = rx * right[0] + ry * right[1] + rz * right[2];
                double cy = rx * up[0] + ry * up[1] + rz * up[2];
                double zs = Math.Max(z, near);
                pz = z;
                px = (cx * f / zs / aspect * 0.5 + 0.5) * W;
                py = (0.5 - cy * f / zs * 0.5) * H;
            }
            Parallel.For(0, nv, i => Project(m.V[3 * i], m.V[3 * i + 1], m.V[3 * i + 2], out sx[i], out sy[i], out cz[i]));
            // visible triangles and their colour (flat shading with a headlight, back faces tinted)
            var vis = new List<int>(nt);
            List<int>? cut = null;
            for (int t = 0; t < nt; t++)
            {
                if (m.Cull)
                {
                    int ia = m.T[3 * t], ib = m.T[3 * t + 1], ic = m.T[3 * t + 2];
                    double ux = m.V[3 * ib] - m.V[3 * ia], uy = m.V[3 * ib + 1] - m.V[3 * ia + 1], uz = m.V[3 * ib + 2] - m.V[3 * ia + 2];
                    double vx = m.V[3 * ic] - m.V[3 * ia], vy = m.V[3 * ic + 1] - m.V[3 * ia + 1], vz = m.V[3 * ic + 2] - m.V[3 * ia + 2];
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    if (nx * (m.V[3 * ia] - eye[0]) + ny * (m.V[3 * ia + 1] - eye[1]) + nz * (m.V[3 * ia + 2] - eye[2]) >= 0) continue;
                }
                int front = (cz[m.T[3 * t]] > near ? 1 : 0) + (cz[m.T[3 * t + 1]] > near ? 1 : 0) + (cz[m.T[3 * t + 2]] > near ? 1 : 0);
                if (front == 3) vis.Add(t);
                else if (front > 0) (cut ??= new List<int>()).Add(t);
            }
            // triangles crossing the near plane are clipped (Sutherland-Hodgman); the pieces are appended after the mesh's
            // own triangles and vertices, and `orig` maps a piece back to its triangle (colour, normal)
            int[] T = m.T;
            double[] xs = sx, ys = sy, zz = cz;
            double[]? uvx = m.Uv;
            int[]? orig = null;
            if (cut != null)
            {
                var tl = new List<int>(m.T); var xl = new List<double>(sx); var yl = new List<double>(sy); var zl = new List<double>(cz);
                var ul = m.Uv != null ? new List<double>(m.Uv) : null;
                var ol = new List<int>();
                double nz = near * (1 + 1e-6);
                foreach (int t in cut)
                {
                    var poly = new List<int>(4);
                    for (int k = 0; k < 3; k++)
                    {
                        int p = m.T[3 * t + k], q = m.T[3 * t + (k + 1) % 3];
                        bool pin = cz[p] > near, qin = cz[q] > near;
                        if (pin) poly.Add(p);
                        if (pin == qin) continue;
                        double a = (nz - cz[p]) / (cz[q] - cz[p]);
                        Project(m.V[3 * p] + (m.V[3 * q] - m.V[3 * p]) * a, m.V[3 * p + 1] + (m.V[3 * q + 1] - m.V[3 * p + 1]) * a,
                            m.V[3 * p + 2] + (m.V[3 * q + 2] - m.V[3 * p + 2]) * a, out double px, out double py, out double pz);
                        xl.Add(px); yl.Add(py); zl.Add(pz);
                        if (ul != null) { ul.Add(m.Uv![2 * p] + (m.Uv[2 * q] - m.Uv[2 * p]) * a); ul.Add(m.Uv[2 * p + 1] + (m.Uv[2 * q + 1] - m.Uv[2 * p + 1]) * a); }
                        poly.Add(xl.Count - 1);
                    }
                    for (int k = 1; k + 1 < poly.Count; k++)
                    {
                        tl.Add(poly[0]); tl.Add(poly[k]); tl.Add(poly[k + 1]);
                        vis.Add(tl.Count / 3 - 1);
                        ol.Add(t);
                    }
                }
                T = tl.ToArray(); xs = xl.ToArray(); ys = yl.ToArray(); zz = zl.ToArray(); uvx = ul?.ToArray(); orig = ol.ToArray();
            }
            if (vis.Count == 0) continue;
            var col = new float[vis.Count * 3];
            bool heatOn = m.Scalar != null && heatMax is double hm0 && hm0 != 0;
            var tj = heatOn ? null : JobFor(m, paint);
            if (tj != null) { tj.Mul = new float[vis.Count * 3]; tj.Light = new float[vis.Count]; tj.Back = new bool[vis.Count]; }
            Parallel.For(0, vis.Count, j =>
            {
                int t = vis[j];
                if (t >= nt) t = orig![t - nt];   // a clipped piece: colour and normal of its triangle
                int ia = m.T[3 * t], ib = m.T[3 * t + 1], ic = m.T[3 * t + 2];
                double ax = m.V[3 * ia], ay = m.V[3 * ia + 1], az = m.V[3 * ia + 2];
                double ux = m.V[3 * ib] - ax, uy = m.V[3 * ib + 1] - ay, uz = m.V[3 * ib + 2] - az;
                double vx = m.V[3 * ic] - ax, vy = m.V[3 * ic + 1] - ay, vz = m.V[3 * ic + 2] - az;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double nl = Math.Max(Math.Sqrt(nx * nx + ny * ny + nz * nz), 1e-30);
                double wx = (ax + m.V[3 * ib] + m.V[3 * ic]) / 3 - eye[0], wy = (ay + m.V[3 * ib + 1] + m.V[3 * ic + 1]) / 3 - eye[1],
                       wz = (az + m.V[3 * ib + 2] + m.V[3 * ic + 2]) / 3 - eye[2];
                double wl = Math.Max(Math.Sqrt(wx * wx + wy * wy + wz * wz), 1e-30);
                double facing = -(nx * wx + ny * wy + nz * wz) / (nl * wl);
                double light = shade ? 0.4 + 0.6 * Math.Pow(Math.Abs(facing), 0.7) : 1.0;
                double r, g, b;
                if (m.Scalar != null && heatMax is double hm && hm != 0)
                    (r, g, b) = HeatColor((m.Scalar[ia] + m.Scalar[ib] + m.Scalar[ic]) / 3, hm);
                else if (m.Colors != null)
                { r = Math.Clamp(m.Colors[3 * t], 0, 1); g = Math.Clamp(m.Colors[3 * t + 1], 0, 1); b = Math.Clamp(m.Colors[3 * t + 2], 0, 1); }
                else { r = DefaultCol[0]; g = DefaultCol[1]; b = DefaultCol[2]; }
                if (tj != null)
                {
                    // the texture is modulated by the vertex / material colour (like OpenGL's MODULATE); the
                    // generated checkers only by the light, so they stay loud
                    bool modulate = tj.Mode == TexJob.Image && m.Colors != null;
                    tj.Mul[3 * j] = (float)((modulate ? r : 1) * light); tj.Mul[3 * j + 1] = (float)((modulate ? g : 1) * light);
                    tj.Mul[3 * j + 2] = (float)((modulate ? b : 1) * light);
                    tj.Light[j] = (float)light;
                    tj.Back[j] = backfaces && facing < 0;
                }
                if (m.Style == 1)   // context: grey, a little of the material left
                {
                    double l = 0.3 * r + 0.55 * g + 0.15 * b;
                    r = 0.30 + 0.25 * l + 0.05 * r; g = 0.31 + 0.25 * l + 0.05 * g; b = 0.34 + 0.25 * l + 0.05 * b;
                }
                else if (m.Style == 2) { r = 0.45 * r + 0.55; g = 0.45 * g + 0.33; b = 0.45 * b + 0.05; }   // selection: orange
                if (backfaces && facing < 0 && m.Style != 1)
                {
                    r = 0.35 * r + 0.65 * BackCol[0]; g = 0.35 * g + 0.65 * BackCol[1]; b = 0.35 * b + 0.65 * BackCol[2];
                }
                col[3 * j] = (float)(r * light); col[3 * j + 1] = (float)(g * light); col[3 * j + 2] = (float)(b * light);
            });
            if (tj?.Uv != null) tj.Uv = uvx;
            Raster(T, vis, xs, ys, zz, col, W, H, zbuf, img, tj);
            info.Drawn += vis.Count;
            if (wire && m.Style != 1)
            {
                var kl = new List<long>(vis.Count * 3);
                foreach (int t in vis)
                {
                    if (t >= nt) continue;   // clipped pieces: their edges cross the near plane
                    for (int k = 0; k < 3; k++)
                    {
                        long a = m.T[3 * t + k], b = m.T[3 * t + (k + 1) % 3];
                        kl.Add(Math.Min(a, b) * nv + Math.Max(a, b));
                    }
                }
                var keys = kl.ToArray();
                Array.Sort(keys);
                int u = 0;
                for (int i = 0; i < keys.Length; i++) if (i == 0 || keys[i] != keys[i - 1]) keys[u++] = keys[i];
                Array.Resize(ref keys, u);
                edges.Add((sx, sy, cz, keys));
            }
        }
        if (wire && edges.Sum(e => (long)e.e.Length) <= wireLimit)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                var (sx, sy, cz, e) = edges[i];
                Lines(sx, sy, cz, e, sx.Length, W, H, zbuf, img, cam.Radius);
            }
            info.Wire = true;
        }
        return (ToBgra(img, W, H), info);
    }

    static byte[] ToBgra(float[] img, int W, int H)
    {
        var o = new byte[W * H * 4];
        for (int p = 0; p < W * H; p++)
        {
            o[4 * p] = (byte)(Math.Clamp(img[3 * p + 2], 0f, 1f) * 255);
            o[4 * p + 1] = (byte)(Math.Clamp(img[3 * p + 1], 0f, 1f) * 255);
            o[4 * p + 2] = (byte)(Math.Clamp(img[3 * p], 0f, 1f) * 255);
            o[4 * p + 3] = 255;
        }
        return o;
    }

    const int Band = 16;

    /// <summary>Rasterise the visible triangles: pixel centres inside (edge function), nearest depth wins, earlier
    /// triangles win ties. Triangles that cover no pixel centre still get their centre pixel.</summary>
    static void Raster(int[] T, List<int> vis, double[] X, double[] Y, double[] Z, float[] col, int W, int H, double[] zbuf, float[] img,
        TexJob? tj = null)
    {
        int nb = (H + Band - 1) / Band;
        var bands = new List<int>[nb];
        for (int i = 0; i < nb; i++) bands[i] = new List<int>();
        var hit = new byte[vis.Count];
        for (int j = 0; j < vis.Count; j++)
        {
            int t = vis[j];
            double y0 = Math.Min(Y[T[3 * t]], Math.Min(Y[T[3 * t + 1]], Y[T[3 * t + 2]]));
            double y1 = Math.Max(Y[T[3 * t]], Math.Max(Y[T[3 * t + 1]], Y[T[3 * t + 2]]));
            double x0 = Math.Min(X[T[3 * t]], Math.Min(X[T[3 * t + 1]], X[T[3 * t + 2]]));
            double x1 = Math.Max(X[T[3 * t]], Math.Max(X[T[3 * t + 1]], X[T[3 * t + 2]]));
            if (x1 < 0 || x0 > W || y1 < 0 || y0 > H) { hit[j] = 1; continue; }   // off screen
            int b0 = Math.Clamp((int)Math.Floor(y0), 0, H - 1) / Band, b1 = Math.Clamp((int)Math.Ceiling(y1), 0, H - 1) / Band;
            for (int b = b0; b <= b1; b++) bands[b].Add(j);
        }
        Parallel.For(0, nb, bi =>
        {
            int rowLo = bi * Band, rowHi = Math.Min(H - 1, rowLo + Band - 1);
            foreach (int j in bands[bi])
            {
                int t = vis[j];
                int ia = T[3 * t], ib = T[3 * t + 1], ic = T[3 * t + 2];
                double xa = X[ia], ya = Y[ia], xb = X[ib], yb = Y[ib], xc = X[ic], yc = Y[ic];
                double area = (xb - xa) * (yc - ya) - (xc - xa) * (yb - ya);
                if (Math.Abs(area) <= 1e-12) { hit[j] = 1; continue; }
                int px0 = Math.Clamp((int)Math.Floor(Math.Min(xa, Math.Min(xb, xc))), 0, W - 1);
                int px1 = Math.Clamp((int)Math.Ceiling(Math.Max(xa, Math.Max(xb, xc))), 0, W - 1);
                int py0 = Math.Max(rowLo, Math.Clamp((int)Math.Floor(Math.Min(ya, Math.Min(yb, yc))), 0, H - 1));
                int py1 = Math.Min(rowHi, Math.Clamp((int)Math.Ceiling(Math.Max(ya, Math.Max(yb, yc))), 0, H - 1));
                double za = Z[ia], zb = Z[ib], zc = Z[ic];
                for (int py = py0; py <= py1; py++)
                {
                    double cy = py + 0.5;
                    for (int px = px0; px <= px1; px++)
                    {
                        double cx = px + 0.5;
                        double w0 = ((xb - cx) * (yc - cy) - (xc - cx) * (yb - cy)) / area;
                        double w1 = ((xc - cx) * (ya - cy) - (xa - cx) * (yc - cy)) / area;
                        double w2 = 1.0 - w0 - w1;
                        if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                        hit[j] = 1;
                        double z = 1.0 / (w0 / za + w1 / zb + w2 / zc);   // perspective correct (1/z is linear on screen)
                        int pix = py * W + px;
                        if (!(z < zbuf[pix])) continue;
                        if (tj == null)
                        {
                            zbuf[pix] = z;
                            img[3 * pix] = col[3 * j]; img[3 * pix + 1] = col[3 * j + 1]; img[3 * pix + 2] = col[3 * j + 2];
                            continue;
                        }
                        double u = 0, v = 0;
                        if (tj.Uv != null)
                        {
                            // perspective correct: interpolate uv / depth and 1 / depth
                            var uv = tj.Uv;
                            double qa = w0 / za, qb = w1 / zb, qc = w2 / zc, q = qa + qb + qc;
                            u = (qa * uv[2 * ia] + qb * uv[2 * ib] + qc * uv[2 * ic]) / q;
                            v = (qa * uv[2 * ia + 1] + qb * uv[2 * ib + 1] + qc * uv[2 * ic + 1]) / q;
                        }
                        if (!Sample(tj, u, v, out double r, out double g, out double b)) continue;   // cut-out texel
                        r *= tj.Mul[3 * j]; g *= tj.Mul[3 * j + 1]; b *= tj.Mul[3 * j + 2];
                        double l = tj.Light[j];
                        if (tj.Style == 2) { r = 0.45 * r + 0.55 * l; g = 0.45 * g + 0.33 * l; b = 0.45 * b + 0.05 * l; }
                        if (tj.Back[j]) { r = 0.35 * r + 0.65 * BackCol[0] * l; g = 0.35 * g + 0.65 * BackCol[1] * l; b = 0.35 * b + 0.65 * BackCol[2] * l; }
                        zbuf[pix] = z;
                        img[3 * pix] = (float)r; img[3 * pix + 1] = (float)g; img[3 * pix + 2] = (float)b;
                    }
                }
            }
        });
        // sub-pixel triangles: centre pixel
        for (int j = 0; j < vis.Count; j++)
        {
            if (hit[j] != 0) continue;
            int t = vis[j];
            int ia = T[3 * t], ib = T[3 * t + 1], ic = T[3 * t + 2];
            int mx = Math.Clamp((int)((X[ia] + X[ib] + X[ic]) / 3), 0, W - 1);
            int my = Math.Clamp((int)((Y[ia] + Y[ib] + Y[ic]) / 3), 0, H - 1);
            double z = (Z[ia] + Z[ib] + Z[ic]) / 3;
            int pix = my * W + mx;
            if (z < zbuf[pix])
            {
                zbuf[pix] = z;
                img[3 * pix] = col[3 * j]; img[3 * pix + 1] = col[3 * j + 1]; img[3 * pix + 2] = col[3 * j + 2];
            }
        }
    }

    static void Lines(double[] sx, double[] sy, double[] cz, long[] edges, int nv, int W, int H, double[] zbuf, float[] img, double radius)
    {
        long total = 0;
        foreach (long key in edges)
        {
            int a = (int)(key / nv), b = (int)(key % nv);
            if (cz[a] > 0 && cz[b] > 0)
                total += Math.Clamp((long)Math.Ceiling(Math.Sqrt(Math.Pow(sx[b] - sx[a], 2) + Math.Pow(sy[b] - sy[a], 2))), 1, 4L * Math.Max(W, H)) + 1;
        }
        if (total == 0 || total > 20_000_000) return;
        // like numpy's img[pix] = ...: a pixel is blended once per mesh, however many line samples hit it
        var done = new bool[W * H];
        foreach (long key in edges)
        {
            int a = (int)(key / nv), b = (int)(key % nv);
            if (!(cz[a] > 0 && cz[b] > 0)) continue;
            long len = Math.Clamp((long)Math.Ceiling(Math.Sqrt(Math.Pow(sx[b] - sx[a], 2) + Math.Pow(sy[b] - sy[a], 2))), 1, 4L * Math.Max(W, H));
            for (long k = 0; k <= len; k++)
            {
                double t = (double)k / len;
                double x = sx[a] + (sx[b] - sx[a]) * t, y = sy[a] + (sy[b] - sy[a]) * t, z = 1.0 / ((1 - t) / cz[a] + t / cz[b]);
                long xi = (long)x, yi = (long)y;
                if (xi < 0 || xi >= W || yi < 0 || yi >= H) continue;
                int pix = (int)(yi * W + xi);
                if (z <= zbuf[pix] * (1 + 2e-3) + radius * 2e-4 && !done[pix])
                {
                    done[pix] = true;
                    for (int c = 0; c < 3; c++) img[3 * pix + c] = (float)(img[3 * pix + c] * 0.35 + WireCol[c] * 0.65);
                }
            }
        }
    }

    /// <summary>Binary PPM (P6) of a BGRA image - for tests and debugging without an image library.</summary>
    public static byte[] ToPpm(byte[] bgra, int width, int height)
    {
        var head = System.Text.Encoding.ASCII.GetBytes($"P6 {width} {height} 255\n");
        var o = new byte[head.Length + width * height * 3];
        head.CopyTo(o, 0);
        for (int p = 0; p < width * height; p++)
        {
            o[head.Length + 3 * p] = bgra[4 * p + 2]; o[head.Length + 3 * p + 1] = bgra[4 * p + 1]; o[head.Length + 3 * p + 2] = bgra[4 * p];
        }
        return o;
    }
}
