using SceneKit;
using Xunit;

public class LodToolsTests
{
    static GeomData G(double[] V, int[] T)
    {
        var g = new GeomData { Vertices = new VArray(V, 3, "Vec3dArray") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Length, indices: (int[])T.Clone(), indexType: "DrawElementsUInt"));
        return g;
    }

    /// <summary>Axis-aligned box, every face subdivided s x s with its own vertices and UVs 0..1 (outward winding).</summary>
    internal static (double[] V, int[] T, double[] UV) Box(double x0, double y0, double z0, double x1, double y1, double z1, int s)
    {
        var V = new List<double>(); var T = new List<int>(); var UV = new List<double>();
        double[] lo = { x0, y0, z0 }, hi = { x1, y1, z1 };
        for (int f = 0; f < 6; f++)
        {
            int ax = f / 2; bool hiSide = f % 2 == 1;
            int u = (ax + 1) % 3, v = (ax + 2) % 3;
            int b = V.Count / 3;
            for (int j = 0; j <= s; j++)
                for (int i = 0; i <= s; i++)
                {
                    var p = new double[3];
                    p[ax] = hiSide ? hi[ax] : lo[ax];
                    p[u] = lo[u] + (hi[u] - lo[u]) * i / s; p[v] = lo[v] + (hi[v] - lo[v]) * j / s;
                    V.AddRange(p); UV.Add((double)i / s); UV.Add((double)j / s);
                }
            for (int j = 0; j < s; j++)
                for (int i = 0; i < s; i++)
                {
                    int a = b + j * (s + 1) + i, bb = a + 1, c = a + s + 1, d = c + 1;
                    if (hiSide) T.AddRange(new[] { a, bb, c, bb, d, c }); else T.AddRange(new[] { a, c, bb, bb, c, d });
                }
        }
        return (V.ToArray(), T.ToArray(), UV.ToArray());
    }

    /// <summary>Low-poly round tower: a capped n-sided prism with one box merlon on every second side of the rim.</summary>
    internal static (double[] V, int[] T) Tower(int n = 24, double r = 3, double h = 10)
    {
        var V = new List<double>(); var T = new List<int>();
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            V.AddRange(new[] { r * Math.Cos(a), r * Math.Sin(a), 0.0 }); V.AddRange(new[] { r * Math.Cos(a), r * Math.Sin(a), h });
        }
        V.AddRange(new[] { 0.0, 0, 0 }); V.AddRange(new[] { 0.0, 0, h });
        for (int i = 0; i < n; i++)
        {
            int a0 = 2 * i, a1 = 2 * i + 1, b0 = 2 * ((i + 1) % n), b1 = b0 + 1;
            T.AddRange(new[] { a0, b0, b1, a0, b1, a1, 2 * n, b0, a0, 2 * n + 1, a1, b1 });
        }
        for (int i = 0; i < n; i += 2)
        {
            double a = 2 * Math.PI * (i + 0.5) / n, cx = (r - 0.3) * Math.Cos(a), cy = (r - 0.3) * Math.Sin(a);
            var m = Box(cx - 0.35, cy - 0.35, h - 0.1, cx + 0.35, cy + 0.35, h + 1.2, 1);
            int b = V.Count / 3;
            V.AddRange(m.V); T.AddRange(m.T.Select(x => x + b));
        }
        return (V.ToArray(), T.ToArray());
    }

    /// <summary>Left half red, right half blue (u &lt; 0.5 / u &gt;= 0.5).</summary>
    internal static RawImage HalfImage()
    {
        var im = new RawImage(64, 64, 3);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int i = (y * 64 + x) * 3;
                if (x < 32) { im.Pixels[i] = 255; } else { im.Pixels[i + 2] = 255; }
            }
        return im;
    }

    [Fact]
    public void ProxyIsClosedAccurateAndFacesOutward()
    {
        var body = Box(0, 0, 0.5, 6, 2.5, 1.6, 12);
        var cabin = Box(1.5, 0.2, 1.6, 4, 2.3, 2.6, 10);
        var srcs = new List<Proxy.Source>
        {
            new() { V = body.V, T = body.T, UV = body.UV, Image = HalfImage() },
            new() { V = cabin.V, T = cabin.T, UV = cabin.UV, Color = new[] { 0.0, 1.0, 0.0, 1.0 } },
        };
        var r = Proxy.Build(srcs, new Proxy.Options { TargetTris = 300, Resolution = 64, TextureSize = 256 });
        Assert.NotNull(r);
        Assert.True(r!.T.Length / 3 <= 300, $"{r.T.Length / 3} triangles");
        var V = body.V.Concat(cabin.V).ToArray();
        var T = body.T.Concat(cabin.T.Select(x => x + body.V.Length / 3)).ToArray();
        var q = Quality.Compare(G(V, T), G(r.V, r.T));
        // two-sided max would count the hidden faces between body and cabin, which the proxy drops on purpose:
        // check the mean both ways, and the proxy surface against the original one way
        Assert.True(q.MeanDevPct < 1.0, $"mean deviation {q.MeanDevPct:F2} %");
        var orig = new TriIndex(V, T);
        double worst = 0;
        for (int t = 0; t < r.T.Length / 3; t++)
            for (int k = 0; k < 3; k++)
            {
                double w0 = k == 0 ? 0.6 : 0.2, w1 = k == 1 ? 0.6 : 0.2, w2 = 1 - w0 - w1;
                int a = r.T[3 * t], b = r.T[3 * t + 1], c = r.T[3 * t + 2];
                var h = orig.Closest(w0 * r.V[3 * a] + w1 * r.V[3 * b] + w2 * r.V[3 * c], w0 * r.V[3 * a + 1] + w1 * r.V[3 * b + 1] + w2 * r.V[3 * c + 1],
                                     w0 * r.V[3 * a + 2] + w1 * r.V[3 * b + 2] + w2 * r.V[3 * c + 2], 10);
                worst = Math.Max(worst, Math.Sqrt(h!.Value.D2));
            }
        Assert.True(worst / q.Diag * 100 < 2.0, $"proxy -> original {worst / q.Diag * 100:F2} %");
        Assert.True(q.FlippedPct < 1, $"flipped {q.FlippedPct:F1} %");
        Assert.Equal(r.V.Length / 3 * 2, r.UV.Length);
        Assert.All(r.UV, x => Assert.InRange(x, 0.0, 1.0));
    }

    [Fact]
    public void ProxyOfALowPolyModelReachesItsTarget()
    {
        // both sides of the hull around a single-sided sheet snapped onto the same points of it; Decimate welds equal
        // positions, so the hull became non-manifold, got locked there, and the proxy came out heavier than the original
        var (tv, tt) = Tower();
        int b = tv.Length / 3;
        var floor = new[] { -4.0, -4, 5, 4, -4, 5, 4, 4, 5, -4, 4, 5 };
        foreach (var (V, T, target, most) in new[]
        {
            (floor, new[] { 0, 1, 2, 0, 2, 3 }, 12, 12),
            (tv.Concat(floor).ToArray(), tt.Concat(new[] { b, b + 1, b + 2, b, b + 2, b + 3 }).ToArray(), 12, 160),
        })
        {
            var r = Proxy.Build(new List<Proxy.Source> { new() { V = V, T = T } }, new Proxy.Options { TargetTris = target, Resolution = 128, Bake = false });
            Assert.NotNull(r);
            Assert.True(r!.T.Length / 3 <= most, $"{r.T.Length / 3} triangles from {T.Length / 3}, target {target}");
        }
    }

    [Fact]
    public void BakedColoursMatchTheOriginal()
    {
        var body = Box(0, 0, 0, 4, 2, 1, 8);
        var srcs = new List<Proxy.Source> { new() { V = body.V, T = body.T, UV = body.UV, Image = HalfImage() } };
        var r = Proxy.Build(srcs, new Proxy.Options { TargetTris = 200, Resolution = 64, TextureSize = 256 })!;
        Assert.NotNull(r.Texture);
        var orig = new TriIndex(body.V, body.T);
        // sample the proxy surface; compare the baked texel with the original colour at the same spot
        var rng = new Random(3);
        int bad = 0, total = 0;
        Span<double> baked = stackalloc double[4];
        for (int t = 0; t < r.T.Length / 3; t++)
            for (int k = 0; k < 5; k++)
            {
                double u = rng.NextDouble(), v = rng.NextDouble();
                if (u + v > 1) { u = 1 - u; v = 1 - v; }
                double w = 1 - u - v;
                int a = r.T[3 * t], b = r.T[3 * t + 1], c = r.T[3 * t + 2];
                double px = w * r.V[3 * a] + u * r.V[3 * b] + v * r.V[3 * c], py = w * r.V[3 * a + 1] + u * r.V[3 * b + 1] + v * r.V[3 * c + 1],
                       pz = w * r.V[3 * a + 2] + u * r.V[3 * b + 2] + v * r.V[3 * c + 2];
                double tu = w * r.UV[2 * a] + u * r.UV[2 * b] + v * r.UV[2 * c], tv = w * r.UV[2 * a + 1] + u * r.UV[2 * b + 1] + v * r.UV[2 * c + 1];
                var h = orig.Closest(px, py, pz, 0.2);
                if (h == null) continue;
                int oa = body.T[3 * h.Value.Tri], ob = body.T[3 * h.Value.Tri + 1], oc = body.T[3 * h.Value.Tri + 2];
                double ou = h.Value.U * body.UV[2 * oa] + h.Value.V * body.UV[2 * ob] + h.Value.W * body.UV[2 * oc];
                if (Math.Abs(ou - 0.5) < 0.08) continue;                  // right at the colour edge
                Bake.Bilinear(r.Texture!, tu, tv, baked);
                bool redExpected = ou < 0.5;
                bool redBaked = baked[0] > baked[2];
                total++;
                if (redExpected != redBaked) bad++;
            }
        Assert.True(total > 200);
        Assert.True(bad <= total * 0.03, $"{bad} of {total} samples have the wrong colour");
    }

    [Fact]
    public void RSimpReachesTargetAndStaysClose()
    {
        int nu = 80, nv = 40;
        var V = new List<double>(); var T = new List<int>();
        for (int i = 0; i <= nv; i++) for (int j = 0; j <= nu; j++)
        { double th = Math.PI * i / nv, ph = 2 * Math.PI * j / nu; V.Add(Math.Sin(th) * Math.Cos(ph)); V.Add(Math.Sin(th) * Math.Sin(ph)); V.Add(Math.Cos(th)); }
        for (int i = 0; i < nv; i++) for (int j = 0; j < nu; j++)
        { int a = i * (nu + 1) + j, b = a + 1, c = a + nu + 1, d = c + 1; T.AddRange(new[] { a, c, b, b, c, d }); }
        var r = RSimp.Run(V.ToArray(), T.ToArray(), 640, null, 0);
        Assert.InRange(r.T.Length / 3, 400, 900);
        var q = Quality.Compare(G(V.ToArray(), T.ToArray()), G(r.V, r.T));
        Assert.True(q.MaxDevPct < 4, $"max deviation {q.MaxDevPct:F2} %");
    }

    [Fact]
    public void UnwrapChartsDoNotOverlap()
    {
        var (V, T, _) = Box(0, 0, 0, 3, 1, 1, 4);
        var u = Unwrap.Run(V, T, 256);
        // rasterise all UV triangles into a 256 grid; no texel centre may be inside two triangles
        var hits = new int[256 * 256];
        for (int t = 0; t < u.T.Length / 3; t++)
        {
            int a = u.T[3 * t], b = u.T[3 * t + 1], c = u.T[3 * t + 2];
            double ax = u.UV[2 * a] * 256, ay = u.UV[2 * a + 1] * 256, bx = u.UV[2 * b] * 256, by = u.UV[2 * b + 1] * 256, cx = u.UV[2 * c] * 256, cy = u.UV[2 * c + 1] * 256;
            double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double l0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den, l1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                    if (l0 > 1e-6 && l1 > 1e-6 && 1 - l0 - l1 > 1e-6) hits[y * 256 + x]++;
                }
        }
        Assert.True(hits.Max() <= 1);
        Assert.True(u.Charts >= 6);
    }

    // ------------------------------------------------------------------ R3-C12: generate_lod far level per part

    /// <summary>One geometry with vertex normals and UVs; the normals are the face normals unless given.</summary>
    static Node Geo(string name, double[] V, int[] T, double[]? UV = null, double[]? N = null)
    {
        var g = G(V, T);
        if (N == null)
        {
            N = new double[V.Length];
            for (int t = 0; t < T.Length / 3; t++)
            {
                int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
                double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
                double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                foreach (int v in new[] { a, b, c }) { N[3 * v] += nx; N[3 * v + 1] += ny; N[3 * v + 2] += nz; }
            }
            Proxy.Normalize(N);
        }
        g.Normals = new VArray(N, 3, "Vec3Array");
        if (UV != null) g.TexCoords[0] = new VArray(UV, 2, "Vec2Array");
        return new Node(Kind.Geometry, "osg::Geometry", name) { Geom = g };
    }

    static StateSet Painted() => new()
    {
        MaterialName = "paint", Diffuse = new[] { 0.8, 0.8, 0.8, 1 },
        Textures = { new Texture { File = "half.png", Baked = HalfImage(), Exists = true, Channels = 3, Width = 64, Height = 64 } },
    };

    /// <summary>A body box (4 x 2 x 0.9, from z 0.5) and four wheel boxes below it (z 0 .. 0.4, not touching), all in one geometry.</summary>
    static (double[] V, int[] T, double[] UV) CarMesh()
    {
        var parts = new List<(double[] V, int[] T, double[] UV)> { Box(0, 0, 0.5, 4, 2, 1.4, 10) };
        foreach (double cx in new[] { 0.8, 3.2 })
            foreach (double cy in new[] { 0.25, 1.75 })
                parts.Add(Box(cx - 0.3, cy - 0.15, 0, cx + 0.3, cy + 0.15, 0.4, 3));
        var V = new List<double>(); var T = new List<int>(); var UV = new List<double>();
        foreach (var p in parts) { int b = V.Count / 3; V.AddRange(p.V); T.AddRange(p.T.Select(x => x + b)); UV.AddRange(p.UV); }
        return (V.ToArray(), T.ToArray(), UV.ToArray());
    }

    /// <summary>A closed cylinder standing on z = 0: n segments, rows of side quads with radial normals, flat caps (own
    /// vertices) with normals down / up.</summary>
    static (double[] V, int[] T, double[] UV, double[] N) Cylinder(int n, int rows, double r, double h)
    {
        var V = new List<double>(); var T = new List<int>(); var UV = new List<double>(); var N = new List<double>();
        for (int j = 0; j <= rows; j++)
            for (int i = 0; i <= n; i++)
            {
                double a = 2 * Math.PI * i / n;
                V.AddRange(new[] { r * Math.Cos(a), r * Math.Sin(a), h * j / rows }); N.AddRange(new[] { Math.Cos(a), Math.Sin(a), 0 });
                UV.AddRange(new[] { (double)i / n, (double)j / rows });
            }
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                T.AddRange(new[] { a, b, d, a, d, c });
            }
        foreach (var (z, up) in new[] { (0.0, -1.0), (h, 1.0) })
        {
            int ctr = V.Count / 3;
            V.AddRange(new[] { 0.0, 0, z }); N.AddRange(new[] { 0.0, 0, up }); UV.AddRange(new[] { 0.5, 0.5 });
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                V.AddRange(new[] { r * Math.Cos(a), r * Math.Sin(a), z }); N.AddRange(new[] { 0.0, 0, up }); UV.AddRange(new[] { 0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a) });
            }
            for (int i = 0; i < n; i++)
            {
                int a = ctr + 1 + i, b = ctr + 1 + (i + 1) % n;
                if (up > 0) T.AddRange(new[] { ctr, a, b }); else T.AddRange(new[] { ctr, b, a });
            }
        }
        return (V.ToArray(), T.ToArray(), UV.ToArray(), N.ToArray());
    }

    static Scene SceneWith(params Node[] kids)
    {
        var root = new Node(Kind.Group, "osg::Group", "root");
        root.Children.AddRange(kids);
        return new Scene("x.osgt", "osgt") { Root = root };
    }

    static Node Geode(string name, params Node[] geos)
    {
        var n = new Node(Kind.Geode, "osg::Geode", name) { StateSet = Painted() };
        n.Children.AddRange(geos);
        return n;
    }

    /// <summary>generate_lod with a proxy far level (small texture and grid); returns the LOD nodes in scene order.</summary>
    static List<Node> FarLod(Scene sc, Dictionary<string, object> prms, List<Node>? targets = null) => FarLod(sc, prms, targets, out _);

    static List<Node> FarLod(Scene sc, Dictionary<string, object> prms, List<Node>? targets, out string message)
    {
        prms.TryAdd("generate_lod.min_tris", 100);
        prms.TryAdd("generate_lod.far_level", "proxy");
        prms.TryAdd("generate_lod.proxy_texture", 64);
        prms.TryAdd("generate_lod.proxy_voxels", 32);
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, prms, targets: targets);
        Assert.Null(results[0].Error);
        message = results[0].Message;
        var lods = sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList();
        Assert.NotEmpty(lods);
        return lods;
    }

    /// <summary>The geometries of the far (last) level of a LOD.</summary>
    static List<Node> Far(Node lod)
    {
        var o = new List<Node>();
        var stack = new Stack<Node>();
        stack.Push(lod.Children[^1]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Kind == Kind.Geometry) o.Add(n);
            foreach (var k in n.Children) stack.Push(k);
        }
        return o;
    }

    /// <summary>Connected components of a mesh, its vertices welded by position.</summary>
    static int Components(double[] V, int[] T)
    {
        var id = new Dictionary<(long, long, long), int>();
        var pos = new int[V.Length / 3];
        for (int i = 0; i < pos.Length; i++)
        {
            var k = ((long)Math.Round(V[3 * i] * 1e6), (long)Math.Round(V[3 * i + 1] * 1e6), (long)Math.Round(V[3 * i + 2] * 1e6));
            if (!id.TryGetValue(k, out int j)) id[k] = j = id.Count;
            pos[i] = j;
        }
        var up = Enumerable.Range(0, id.Count).ToArray();
        int Find(int x) { while (up[x] != x) x = up[x] = up[up[x]]; return x; }
        for (int t = 0; t < T.Length / 3; t++)
            for (int k = 1; k < 3; k++) { int a = Find(pos[T[3 * t]]), b = Find(pos[T[3 * t + k]]); if (a != b) up[Math.Max(a, b)] = Math.Min(a, b); }
        return T.Select(v => Find(pos[v])).Distinct().Count();
    }

    [Fact]
    public void ProxyFromSetsTheErrorAndActsAsAFloor()
    {
        var (V, T, _) = RebakeTests.Sphere(60, 30, 2.0);
        var ball = Geo("ball", V, T);
        var orig = ball.Geom!;
        var sc = SceneWith(new Node(Kind.Geode, "osg::Geode", "ballgeode") { Children = { ball } });
        var lod = Assert.Single(FarLod(sc, new()
        {
            ["generate_lod.proxy_from"] = 60.0, ["generate_lod.pixel_error"] = 1.0, ["generate_lod.screen_height"] = 1080, ["generate_lod.fov"] = 45.0,
            ["generate_lod.proxy_voxels"] = 64, ["generate_lod.ratios"] = "0.5, 0.06",
        }));
        var far = Far(lod);
        Assert.Contains(far, n => n.Name.Contains("proxy"));
        var merged = new List<double>(); var mt = new List<int>();
        foreach (var n in far) { int b = merged.Count / 3; merged.AddRange(n.Geom!.Vertices!.Data); mt.AddRange(n.Geom.Triangles().Select(x => x + b)); }
        double dev = Quality.Compare(orig, G(merged.ToArray(), mt.ToArray())).MaxDev;
        // 1 px at 60 m is 4.6 cm, finer than the 6 % hull reaches on this ball (5.5 cm on this grid)
        double e = ProxyParams.PixelsToMetres(1, 60, 1080, 45);
        Assert.InRange(e, 0.0460, 0.0461);
        Assert.True(dev <= e, $"far level deviation {dev:F4} > e {e:F4}");
        // the switching distance is placed from the reached deviation as today: the far level is not seen closer than that
        double k = 1080 / (2 * Math.Tan(22.5 * Math.PI / 180));
        double radius = Math.Sqrt(3 * 4.0 * 4.0) / 2;
        Assert.True(lod.Ranges![^1].min >= Math.Max(dev * k, radius * 0.5) * (1 - 1e-9), $"far level from {lod.Ranges[^1].min:F2}, deviation {dev:F4} switches at {dev * k:F2}");
        // proxy_from is a floor: the far level never appears nearer than 60 m, though its deviation (within e) alone would allow it
        Assert.True(dev * k < 60, $"deviation {dev:F4} alone switches at {dev * k:F2}, not nearer than proxy_from");
        Assert.True(lod.Ranges[^1].min >= 60 * (1 - 1e-9), $"far level from {lod.Ranges[^1].min:F2}, proxy_from 60");
    }

    [Fact]
    public void PerPartFarLevelSplitsComponents()
    {
        var (V, T, UV) = CarMesh();
        var sc = SceneWith(Geode("car", Geo("car body", V, T, UV)));
        var lod = Assert.Single(FarLod(sc, new() { ["generate_lod.proxy_parts"] = "per_part" }));
        var geo = Assert.Single(Far(lod));
        Assert.Equal(5, Components(geo.Geom!.Vertices!.Data, geo.Geom.Triangles()));
        var tex = Assert.Single(geo.StateSet!.Textures);
        Assert.NotNull(tex.Baked);
        Assert.Equal(0, tex.Unit);
    }

    [Fact]
    public void GroundAndNormalsApplyToTheFarLevel()
    {
        GeomData Run(string ground, string normals)
        {
            var (V, T, UV, N) = Cylinder(64, 8, 1.0, 1.5);
            var sc = SceneWith(Geode("drumgeode", Geo("drum", V, T, UV, N)));
            var lod = Assert.Single(FarLod(sc, new()
            {
                ["generate_lod.proxy_parts"] = "per_part", ["generate_lod.proxy_ground"] = ground, ["generate_lod.proxy_normals"] = normals,
                ["generate_lod.ratios"] = "0.5, 0.06",
            }));
            return Assert.Single(Far(lod)).Geom!;
        }
        // down-facing triangles with every corner at the plane (well inside the cut tolerance of one 2/32 cell)
        int AtPlane(GeomData g)
        {
            var V = g.Vertices!.Data; var T = g.Triangles();
            int n = 0;
            for (int t = 0; t < T.Length / 3; t++)
            {
                int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
                if (new[] { a, b, c }.Any(v => V[3 * v + 2] > 0.03)) continue;
                double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
                double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l > 0 && nz / l < -0.5) n++;
            }
            return n;
        }
        var full = Run("full", "hull");
        var cut = Run("cut", "original");
        var hull = Run("cut", "hull");
        Assert.True(AtPlane(full) > 0, "the full underside has a bottom at the plane");
        Assert.Equal(0, AtPlane(cut));
        // same geometry, other shading: the normals come from the round original, not the hull's facets
        Assert.Equal(hull.Vertices!.Data, cut.Vertices!.Data);
        double worst = 0;
        for (int i = 0; i < cut.Normals!.Data.Length / 3; i++)
        {
            double d = 0;
            for (int c = 0; c < 3; c++) d += cut.Normals.Data[3 * i + c] * hull.Normals!.Data[3 * i + c];
            worst = Math.Max(worst, Math.Acos(Math.Clamp(d, -1, 1)) * 180 / Math.PI);
        }
        Assert.True(worst > 5, $"normals differ by at most {worst:F1} degrees");
    }

    [Fact]
    public void OneAtlasPerObjectAcrossHolders()
    {
        var a = Box(0, 0, 0, 2, 1, 1, 6);
        var b = Box(4, 0, 0, 6, 1, 1.5, 6);
        var group = new Node(Kind.Group, "osg::Group", "car") { Children = { Geode("left", Geo("left", a.V, a.T, a.UV)), Geode("right", Geo("right", b.V, b.T, b.UV)) } };
        var sc = SceneWith(group);
        var lods = FarLod(sc, new() { ["generate_lod.proxy_parts"] = "per_part" }, new List<Node> { group });
        Assert.Equal(2, lods.Count);
        var far = lods.Select(l => Assert.Single(Far(l))).ToList();
        var tex = far.Select(g => Assert.Single(g.StateSet!.Textures)).ToList();
        Assert.Same(tex[0], tex[1]);
        Assert.NotNull(tex[0].Baked);
    }

    [Fact]
    public void MovingHolderKeepsItsFullDetailLevelAndTheSimplifiedFarLevel()
    {
        var (V, T, UV) = CarMesh();
        var body = Geo("car body", V, T, UV);
        var geode = Geode("hatch", body);
        geode.Src = new Obj("osg::Geode", null, new List<Stmt> { new(new[] { "UpdateCallback", "TRUE" }) }, ObjStyle.Osgt);
        Assert.True(Optimizer.Moving(geode));
        var sc = SceneWith(geode);
        var before = body.Geom!.Vertices!.Data.ToArray();
        var lod = Assert.Single(FarLod(sc, new() { ["generate_lod.proxy_parts"] = "per_part" }, null, out string msg));
        Assert.Contains("far level: hatch is a moving node, kept the simplified level", msg);
        var level0 = lod.Children[0];
        Assert.Same(geode, level0);
        Assert.Same(body, Assert.Single(level0.Children));
        Assert.Equal(before, body.Geom.Vertices!.Data);
        Assert.DoesNotContain(Far(lod), n => n.Name.Contains("proxy"));
    }

    [Fact]
    public void ProxyFromWithFixedSwitchingSaysItHasNoFloor()
    {
        var (V, T, UV) = CarMesh();
        FarLod(SceneWith(Geode("car", Geo("car body", V, T, UV))),
            new() { ["generate_lod.proxy_from"] = 60.0, ["generate_lod.switching"] = "fixed" }, null, out string fixedMsg);
        Assert.Contains("proxy_from applies with switching = error", fixedMsg);
        FarLod(SceneWith(Geode("car", Geo("car body", V, T, UV))),
            new() { ["generate_lod.proxy_from"] = 60.0 }, null, out string errMsg);
        Assert.DoesNotContain("proxy_from applies with switching", errMsg);
    }

    [Fact]
    public void WholeModeNormalsGroundAndUnmetErrorAreSaid()
    {
        GeomData Run(string normals, out string msg)
        {
            var (V, T, UV, N) = Cylinder(64, 8, 1.0, 1.5);
            var sc = SceneWith(Geode("drumgeode", Geo("drum", V, T, UV, N)));
            var lod = Assert.Single(FarLod(sc, new() { ["generate_lod.proxy_normals"] = normals, ["generate_lod.proxy_ground"] = "cut", ["generate_lod.ratios"] = "0.5, 0.06" }, null, out msg));
            return Assert.Single(Far(lod)).Geom!;
        }
        var orig = Run("original", out string msg);
        var hull = Run("hull", out _);
        Assert.Contains("proxy_ground applies with proxy_parts = per_part", msg);
        Assert.Equal(hull.Vertices!.Data, orig.Vertices!.Data);
        double worst = 0;
        for (int i = 0; i < orig.Normals!.Data.Length / 3; i++)
        {
            double d = 0;
            for (int c = 0; c < 3; c++) d += orig.Normals.Data[3 * i + c] * hull.Normals!.Data[3 * i + c];
            worst = Math.Max(worst, Math.Acos(Math.Clamp(d, -1, 1)) * 180 / Math.PI);
        }
        Assert.True(worst > 1, $"normals differ by at most {worst:F1} degrees");

        // an error the hull cannot meet (1 px at 1 m is under a millimetre): the simplified level, and a note
        var (V2, T2, _) = RebakeTests.Sphere(60, 30, 2.0);
        var lod2 = FarLod(SceneWith(new Node(Kind.Geode, "osg::Geode", "ballgeode") { Children = { Geo("ball", V2, T2) } }),
            new() { ["generate_lod.proxy_from"] = 1.0, ["generate_lod.ratios"] = "0.5, 0.06" }, null, out string unmet);
        Assert.Contains("proxy_from: the proxy could not stay within the error", unmet);
        Assert.DoesNotContain(Far(Assert.Single(lod2)), n => n.Name.Contains("proxy"));
    }

    [Fact]
    public void DefaultFarLevelIsTodays()
    {
        // every new key spelled out at its default: today's single hull, bit for bit
        var (sc, target) = ProxyGoldenTests.Fixture("remove_occluded_car");
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
        {
            ["generate_lod.far_level"] = "proxy", ["generate_lod.proxy_texture"] = 64, ["generate_lod.proxy_voxels"] = 48,
            ["generate_lod.proxy_parts"] = "whole", ["generate_lod.proxy_from"] = 0.0, ["generate_lod.proxy_ground"] = "full", ["generate_lod.proxy_normals"] = "hull",
        }, targets: new List<Node> { target });
        Assert.Null(results[0].Error);
        string got = ProxyGoldenTests.Fingerprint(sc);
        Assert.True(ProxyGoldenTests.Expected["generate_lod remove_occluded_car"] == got, $"fingerprint {got}");
    }
}
