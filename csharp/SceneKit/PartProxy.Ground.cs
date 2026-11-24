namespace SceneKit;

/// <summary>The ground an object stands on, in one frame: a point on the plane and its unit normal (up).</summary>
public readonly record struct GroundPlane(double[] Point, double[] Normal);

/// <summary>Per-part proxies, the ground rule (step 5): the plane at the object's lowest point and the cut of the
/// down-facing triangles at it; and step 8's last bullet: the static proxy triangles nobody sees are removed.</summary>
public static partial class PartProxy
{
    /// <summary>The ground plane of an object, in the target's parent frame: horizontal in world Z (the normal is world up
    /// taken into that frame), through the lowest world Z of the target's drawn geometry (every piece of the assembly, the
    /// original before any proxy; moving pieces through their <see cref="Assembly.Frame"/>).
    /// <paramref name="targetParentWorld"/> maps the target's parent frame to the world. No terrain lookup: the object is
    /// expected to stand on its lowest point.</summary>
    internal static GroundPlane GroundOf(Assembly asm, double[] targetParentWorld)
    {
        var w = targetParentWorld;
        double low = double.PositiveInfinity;
        var at = new double[3];
        foreach (var (a, toParent) in Owners(asm))
            foreach (var pc in a.Pieces)
            {
                var V = Place(pc.V, toParent);
                foreach (int v in pc.T)
                {
                    double z = w[8] * V[3 * v] + w[9] * V[3 * v + 1] + w[10] * V[3 * v + 2] + w[11];
                    if (z < low) { low = z; at = new[] { V[3 * v], V[3 * v + 1], V[3 * v + 2] }; }
                }
            }
        // world z = row 2 of the matrix applied to a point: its gradient (w8, w9, w10) is the up direction in this frame
        var n = new[] { w[8], w[9], w[10] };
        Proxy.Normalize(n);
        return new GroundPlane(at, n);
    }

    /// <summary>The plane in a frame below: <paramref name="frame"/> maps that frame into the plane's (a moving part's
    /// <see cref="Assembly.Frame"/>, which holds the static matrices above it, <see cref="MovingPart.StaticAbove"/>, too).
    /// The point goes through its inverse, the normal through its transpose. Unchanged when it cannot be inverted. A
    /// distance from the plane in that frame is one in the plane's frame divided by the scale from <see
    /// cref="Into(GroundPlane, double[], out double)"/>.</summary>
    internal static GroundPlane Into(GroundPlane g, double[] frame) => Into(g, frame, out _);

    /// <summary><see cref="Into(GroundPlane, double[])"/>, and <paramref name="scale"/> = |M^T N| (1 when the frame cannot
    /// be inverted): a point at distance d from the plane in the frame below lies at d * scale in the plane's frame, so a
    /// tolerance of tol metres in the plane's frame is tol / scale there.</summary>
    internal static GroundPlane Into(GroundPlane g, double[] frame, out double scale)
    {
        scale = 1;
        if (Mat.Invert(frame) is not { } inv) return g;
        var (x, y, z) = Mat.Apply(inv, g.Point[0], g.Point[1], g.Point[2]);
        var n = new double[3];
        for (int c = 0; c < 3; c++) n[c] = frame[c] * g.Normal[0] + frame[4 + c] * g.Normal[1] + frame[8 + c] * g.Normal[2];
        double l = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        if (l > 0) scale = l;
        Proxy.Normalize(n);
        return new GroundPlane(new[] { x, y, z }, n);
    }

    /// <summary>The triangles of <paramref name="T"/> without those whose three corners lie within <paramref name="tol"/>
    /// of the plane (either side) and whose normal faces down, n . <see cref="GroundPlane.Normal"/> &lt; -0.5. The caller
    /// passes tol = max(e, cell). Triangles without area stay.</summary>
    internal static int[] Cut(double[] V, int[] T, GroundPlane g, double tol)
    {
        var o = new List<int>(T.Length);
        double[] P = g.Point, N = g.Normal;
        double Dist(int v) => Math.Abs((V[3 * v] - P[0]) * N[0] + (V[3 * v + 1] - P[1]) * N[1] + (V[3 * v + 2] - P[2]) * N[2]);
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            if (Dist(a) <= tol && Dist(b) <= tol && Dist(c) <= tol)
            {
                double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
                double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l > 0 && (nx * N[0] + ny * N[1] + nz * N[2]) / l < -0.5) continue;
            }
            o.Add(a); o.Add(b); o.Add(c);
        }
        return o.ToArray();
    }

    /// <summary><see cref="Cut"/>, except that a cut that would remove every triangle is not made: then
    /// <paramref name="T"/> itself comes back and <paramref name="wouldEmpty"/> is true (the caller notes it; an empty
    /// proxy would make the part fall back to its uncut original).</summary>
    internal static int[] CutOrKeep(double[] V, int[] T, GroundPlane g, double tol, out bool wouldEmpty)
    {
        var cut = Cut(V, T, g, tol);
        wouldEmpty = cut.Length == 0 && T.Length > 0;
        return wouldEmpty ? T : cut;
    }

    /// <summary>A convex or decimated part's proxy cut at the ground (<paramref name="g"/> in its owner's frame); other
    /// results, and a proxy the cut does not touch, stay as they are. False when the cut would remove every triangle: the
    /// proxy then stays uncut.</summary>
    static bool CutSmall(PartResult pr, GroundPlane g, double tol)
    {
        if (pr.Method is not (PartMethod.Convex or PartMethod.Decimated)) return true;
        var T = CutOrKeep(pr.V, pr.T, g, tol, out bool wouldEmpty);
        if (T.Length != pr.T.Length) Retriangulate(pr, T);
        return !wouldEmpty;
    }

    /// <summary>Step 8, hidden triangles: one <see cref="Occlusion.Visible"/> run on the whole assembled proxy (in the
    /// target's parent frame) with <see cref="Limits.MeasureViews"/> views, from above the horizon only unless
    /// <paramref name="ground"/> is full. The static parts are tested and hide what lies behind them; the moving parts
    /// (every result below a moving part) are seen but hide nothing, and lose nothing, as in remove_occluded. The static
    /// voxel, convex and decimated proxies lose the triangles that are never seen; kept parts (the original) stay as they
    /// are. Returns the number of triangles removed.</summary>
    internal static int RemoveHidden(ObjectResult r, ProxyGround ground, Action<string>? progress = null)
    {
        var items = new List<Occlusion.Item>();
        var edit = new List<(PartResult pr, int item)>();
        void Add(ObjectResult o, double[]? toParent, bool moving)
        {
            foreach (var pr in o.Static)
            {
                if (pr.T.Length == 0) continue;
                items.Add(new Occlusion.Item(Place(pr.V, toParent), pr.T, Occludes: !moving));
                if (!moving && pr.Method != PartMethod.Kept) edit.Add((pr, items.Count - 1));
            }
            foreach (var (_, inner) in o.Moving)
                if (inner != null) Add(inner, inner.Asm.Frame, true);
        }
        Add(r, null, false);
        if (edit.Count == 0) return 0;
        progress?.Invoke("proxy: removing hidden triangles ...");
        var vis = Occlusion.Visible(items, new Occlusion.Options { Views = Limits.MeasureViews, AboveHorizonOnly = ground != ProxyGround.Full, Progress = progress });
        int removed = 0;
        foreach (var (pr, i) in edit)
        {
            var seen = vis[i];
            int gone = seen.Count(s => !s);
            if (gone == 0) continue;
            var T = new List<int>(3 * (seen.Length - gone));
            for (int t = 0; t < seen.Length; t++)
                if (seen[t]) { T.Add(pr.T[3 * t]); T.Add(pr.T[3 * t + 1]); T.Add(pr.T[3 * t + 2]); }
            Retriangulate(pr, T.ToArray());
            removed += gone;
        }
        return removed;
    }

    /// <summary>A result with fewer triangles: <paramref name="T"/> (numbers into its V), the vertices no triangle uses
    /// dropped and <see cref="PartResult.From"/> following them.</summary>
    static void Retriangulate(PartResult pr, int[] T)
    {
        var id = new Dictionary<int, int>();
        var used = new List<int>();
        var nt = new int[T.Length];
        for (int i = 0; i < T.Length; i++)
        {
            if (!id.TryGetValue(T[i], out int j)) { id[T[i]] = j = used.Count; used.Add(T[i]); }
            nt[i] = j;
        }
        var V = new double[3 * used.Count];
        for (int j = 0; j < used.Count; j++) for (int c = 0; c < 3; c++) V[3 * j + c] = pr.V[3 * used[j] + c];
        var from = pr.From;
        pr.From = from == null ? null : used.Select(u => from[u]).ToArray();
        (pr.V, pr.T) = (V, nt);
    }
}
