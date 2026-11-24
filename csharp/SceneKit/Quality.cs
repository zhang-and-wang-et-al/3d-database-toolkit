namespace SceneKit;

public sealed class QualityReport
{
    public long TrisBefore, TrisAfter, VertsBefore, VertsAfter;
    public double MaxDevPct, MeanDevPct, MaxDev, Diag, FlippedPct, BBoxChangePct;
    /// <summary>(1 - area after / area before) x 100; 0 when the before mesh has no area.</summary>
    public double AreaLossPct;
    public int BoundaryBefore, BoundaryAfter, NonManifoldBefore, NonManifoldAfter, DegenerateAfter;
    public double[]? VertexDev;          // per result vertex, % of the object size (heat map)
    public string Verdict = "ok";        // ok / check / damaged
    public List<string> Warnings = new();
    /// <summary>Texture shift of sampled surface points in texels of the largest texture (mean, 95th percentile); null when
    /// not measured (see <see cref="TextureNote"/>).</summary>
    public double? TexShiftMean, TexShiftP95;
    /// <summary>Largest per-triangle UV density change after vs before (max of r and 1/r); null when not measured.</summary>
    public double? UvStretch;
    /// <summary>Result triangles whose UV winding turned against their before-state (texture mirrored).</summary>
    public int UvFlipped;
    /// <summary>Why the texture was not measured: "textures rebaked", "textures ignored", "no texture", "texture size unknown".</summary>
    public string? TextureNote;
    /// <summary>Further remarks for the verdict line (not warnings).</summary>
    public List<string> Notes = new();
    /// <summary>A verdict line of its own (cards: "leaf area at distance 97 %"); <see cref="SummaryLine"/> returns it when set.</summary>
    public string? Headline;

    public string SummaryLine()
    {
        if (Headline != null) return Headline;
        var parts = new List<string> { $"max dev {PyFormat.Fixed(MaxDevPct, 2)} %" };
        if (BoundaryAfter != BoundaryBefore) parts.Add($"open edges {PyFormat.Int(BoundaryBefore)}->{PyFormat.Int(BoundaryAfter)}");
        if (FlippedPct >= 0.5) parts.Add($"flipped {PyFormat.Fixed(FlippedPct, 1)} %");
        return string.Join(", ", parts);
    }
}

/// <summary>One part for the crack check: its geometry before and after a change and its world matrix (an unchanged
/// neighbour has <see cref="Before"/> == <see cref="After"/>).</summary>
public sealed record Part(string Name, GeomData Before, GeomData After, double[] World);

/// <summary>The largest gap (world units, metres) that opened between two parts where they touched before.</summary>
public sealed record Crack(string A, string B, double Gap);

/// <summary>How much did an operation change a mesh? Surface deviation (sampled two-sided point-to-surface distance),
/// new holes, non-manifold edges, flipped faces, outline change -> verdict ok / check / damaged.</summary>
public static class Quality
{
    public const double OkDev = 0.5, CheckDev = 2.0;
    /// <summary>Area loss in % above which a part counts as almost gone.</summary>
    public const double GoneAreaLoss = 90;
    public const double OkTexels = 1, DamagedTexels = 4, OkStretch = 1.5;

    static (double[] V, int[] T) Tris(GeomData? g)
    {
        if (g?.Vertices == null) return (Array.Empty<double>(), Array.Empty<int>());
        var a = g.Vertices;
        var V = new double[a.Count * 3];
        for (int i = 0; i < a.Count; i++) for (int c = 0; c < Math.Min(3, a.Comps); c++) V[3 * i + c] = a.Data[i * a.Comps + c];
        var t = g.Triangles();
        var tl = new List<int>(t.Length);
        int n = a.Count;
        for (int i = 0; i + 2 < t.Length; i += 3)
            if (t[i] >= 0 && t[i] < n && t[i + 1] >= 0 && t[i + 1] < n && t[i + 2] >= 0 && t[i + 2] < n) { tl.Add(t[i]); tl.Add(t[i + 1]); tl.Add(t[i + 2]); }
        return (V, tl.ToArray());
    }

    /// <summary>(boundary edges, non-manifold edges), vertices welded by position.</summary>
    public static (int boundary, int nonManifold) EdgeStats(double[] V, int[] T)
    {
        if (T.Length == 0) return (0, 0);
        var weld = new Dictionary<(double, double, double), int>();
        int n = V.Length / 3;
        var id = new int[n];
        for (int i = 0; i < n; i++)
        {
            var k = (Math.Round(V[3 * i], 9), Math.Round(V[3 * i + 1], 9), Math.Round(V[3 * i + 2], 9));
            if (!weld.TryGetValue(k, out var w)) weld[k] = w = weld.Count;
            id[i] = w;
        }
        var cnt = new Dictionary<(int, int), int>();
        for (int t = 0; t < T.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = id[T[t + e]], b = id[T[t + (e + 1) % 3]];
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                cnt[key] = cnt.GetValueOrDefault(key) + 1;
            }
        return (cnt.Values.Count(c => c == 1), cnt.Values.Count(c => c > 2));
    }

    sealed class Grid
    {
        readonly Dictionary<(long, long, long), List<int>> cells = new();
        readonly double cell; readonly double[] P;
        public Grid(double[] pts, double cellSize)
        {
            P = pts; cell = cellSize;
            for (int i = 0; i < pts.Length / 3; i++)
            {
                var k = Key(pts[3 * i], pts[3 * i + 1], pts[3 * i + 2]);
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                l.Add(i);
            }
        }
        (long, long, long) Key(double x, double y, double z) => ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell), (long)Math.Floor(z / cell));

        /// <summary>Nearest point (searching growing rings of cells).</summary>
        public (double d, int idx) Nearest(double x, double y, double z)
        {
            var (kx, ky, kz) = Key(x, y, z);
            double best = double.PositiveInfinity;
            int bi = -1;
            for (int r = 0; r < 64; r++)
            {
                for (long dx = -r; dx <= r; dx++)
                    for (long dy = -r; dy <= r; dy++)
                        for (long dz = -r; dz <= r; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                            if (!cells.TryGetValue((kx + dx, ky + dy, kz + dz), out var l)) continue;
                            foreach (var i in l)
                            {
                                double ex = P[3 * i] - x, ey = P[3 * i + 1] - y, ez = P[3 * i + 2] - z;
                                double d = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                                if (d < best) { best = d; bi = i; }
                            }
                        }
                if (bi >= 0 && best <= r * cell) break;
            }
            if (bi < 0)
                for (int i = 0; i < P.Length / 3; i++)
                {
                    double ex = P[3 * i] - x, ey = P[3 * i + 1] - y, ez = P[3 * i + 2] - z;
                    double d = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                    if (d < best) { best = d; bi = i; }
                }
            return (best, bi);
        }
    }

    /// <summary>Distance from p to triangle abc (Ericson's closest point).</summary>
    public static double PointTriangle(double px, double py, double pz, double[] V, int ia, int ib, int ic)
    {
        double ax = V[3 * ia], ay = V[3 * ia + 1], az = V[3 * ia + 2];
        double abx = V[3 * ib] - ax, aby = V[3 * ib + 1] - ay, abz = V[3 * ib + 2] - az;
        double acx = V[3 * ic] - ax, acy = V[3 * ic + 1] - ay, acz = V[3 * ic + 2] - az;
        double apx = px - ax, apy = py - ay, apz = pz - az;
        double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        double cx, cy, cz;
        if (d1 <= 0 && d2 <= 0) { cx = ax; cy = ay; cz = az; return Dist(px, py, pz, cx, cy, cz); }
        double bpx = px - V[3 * ib], bpy = py - V[3 * ib + 1], bpz = pz - V[3 * ib + 2];
        double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
        if (d3 >= 0 && d4 <= d3) return Dist(px, py, pz, V[3 * ib], V[3 * ib + 1], V[3 * ib + 2]);
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / (d1 - d3); return Dist(px, py, pz, ax + abx * v, ay + aby * v, az + abz * v); }
        double cpx = px - V[3 * ic], cpy = py - V[3 * ic + 1], cpz = pz - V[3 * ic + 2];
        double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
        if (d6 >= 0 && d5 <= d6) return Dist(px, py, pz, V[3 * ic], V[3 * ic + 1], V[3 * ic + 2]);
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / (d2 - d6); return Dist(px, py, pz, ax + acx * w, ay + acy * w, az + acz * w); }
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        {
            double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            double bx = V[3 * ib], by = V[3 * ib + 1], bz = V[3 * ib + 2];
            return Dist(px, py, pz, bx + (V[3 * ic] - bx) * w, by + (V[3 * ic + 1] - by) * w, bz + (V[3 * ic + 2] - bz) * w);
        }
        double denom = va + vb + vc;
        if (denom == 0) return Dist(px, py, pz, ax, ay, az);
        double vv = vb / denom, ww = vc / denom;
        return Dist(px, py, pz, ax + abx * vv + acx * ww, ay + aby * vv + acy * ww, az + abz * vv + acz * ww);
    }

    static double Dist(double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = ax - bx, dy = ay - by, dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Distance of a point to a mesh: candidate triangles = the triangle of the nearest sample and its neighbours.</summary>
    sealed class Surface
    {
        public readonly double[] V; public readonly int[] T;
        readonly double[] samples; readonly int[] sampleTri;
        readonly Grid grid;
        readonly List<int>[] vertTris;
        readonly int[] weldId;
        readonly Dictionary<int, List<int>> weldTris = new();
        readonly Grid vgrid;
        readonly double[] welded;

        public Surface(double[] V, int[] T, double[] samples, int[] sampleTri, double cell)
        {
            this.V = V; this.T = T; this.samples = samples; this.sampleTri = sampleTri;
            grid = new Grid(samples, cell);
            int n = V.Length / 3;
            vertTris = new List<int>[n];
            weldId = new int[n];
            var weld = new Dictionary<(double, double, double), int>();
            var wl = new List<double>();
            for (int i = 0; i < n; i++)
            {
                var k = (Math.Round(V[3 * i], 9), Math.Round(V[3 * i + 1], 9), Math.Round(V[3 * i + 2], 9));
                if (!weld.TryGetValue(k, out var w)) { weld[k] = w = weld.Count; wl.Add(V[3 * i]); wl.Add(V[3 * i + 1]); wl.Add(V[3 * i + 2]); }
                weldId[i] = w;
            }
            welded = wl.ToArray();
            for (int t = 0; t < T.Length / 3; t++)
                for (int c = 0; c < 3; c++)
                {
                    int w = weldId[T[3 * t + c]];
                    if (!weldTris.TryGetValue(w, out var l)) weldTris[w] = l = new List<int>();
                    l.Add(t);
                }
            vgrid = new Grid(welded, cell);
        }

        public (double d, int sample) Distance(double x, double y, double z)
        {
            var (d, j, _) = Closest(x, y, z);
            return (d, j);
        }

        /// <summary>(distance, nearest sample, nearest triangle): the triangle of the nearest sample and its neighbours.</summary>
        public (double d, int sample, int tri) Closest(double x, double y, double z)
        {
            var (d, j) = grid.Nearest(x, y, z);
            if (j < 0) return (d, j, -1);
            double best = d;
            int seed = sampleTri[j], bt = seed;
            var cand = new HashSet<int>();
            for (int c = 0; c < 3; c++) if (weldTris.TryGetValue(weldId[T[3 * seed + c]], out var l)) cand.UnionWith(l);
            var (_, wv) = vgrid.Nearest(x, y, z);
            if (wv >= 0 && weldTris.TryGetValue(wv, out var l2)) cand.UnionWith(l2);
            foreach (int t in cand)
            {
                double dd = PointTriangle(x, y, z, V, T[3 * t], T[3 * t + 1], T[3 * t + 2]);
                if (dd < best) { best = dd; bt = t; }
            }
            return (best, j, bt);
        }

        /// <summary>The point of the surface closest to p (on the triangle <see cref="Closest"/> finds); null for an empty surface.</summary>
        public (double x, double y, double z)? ClosestPoint(double x, double y, double z)
        {
            var (_, _, t) = Closest(x, y, z);
            if (t < 0) return null;
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            var (wa, wb, wc) = ClosestBary(x, y, z, V, a, b, c);
            return (wa * V[3 * a] + wb * V[3 * b] + wc * V[3 * c], wa * V[3 * a + 1] + wb * V[3 * b + 1] + wc * V[3 * c + 1],
                wa * V[3 * a + 2] + wb * V[3 * b + 2] + wc * V[3 * c + 2]);
        }
    }

    /// <summary>Area weighted random points on the triangles (+ all used vertices): (points, triangle of each point).</summary>
    internal static (double[] pts, int[] tri) Sample(double[] V, int[] T, int count, Random rng)
    {
        int m = T.Length / 3;
        var area = new double[m];
        var cum = new double[m];
        double tot = 0;
        for (int t = 0; t < m; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
            double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            area[t] = 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            tot += area[t];
            cum[t] = tot;
        }
        var pts = new List<double>();
        var tri = new List<int>();
        if (tot > 0)
            for (int s = 0; s < count; s++)
            {
                double u = rng.NextDouble() * tot;
                int t = Array.BinarySearch(cum, u);
                if (t < 0) t = ~t;
                t = Math.Min(t, m - 1);
                double r1 = rng.NextDouble(), r2 = rng.NextDouble(), sq = Math.Sqrt(r1);
                int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
                for (int k = 0; k < 3; k++)
                    pts.Add((1 - sq) * V[3 * a + k] + sq * (1 - r2) * V[3 * b + k] + sq * r2 * V[3 * c + k]);
                tri.Add(t);
            }
        var vt = new Dictionary<int, int>();
        for (int t = 0; t < m; t++) for (int c = 0; c < 3; c++) vt[T[3 * t + c]] = t;
        foreach (var (v, t) in vt.OrderBy(x => x.Key))
        {
            pts.Add(V[3 * v]); pts.Add(V[3 * v + 1]); pts.Add(V[3 * v + 2]);
            tri.Add(t);
        }
        return (pts.ToArray(), tri.ToArray());
    }

    static (double x, double y, double z) FaceNormal(double[] V, int[] T, int t)
    {
        int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
        double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
        double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return l > 0 ? (nx / l, ny / l, nz / l) : (0, 0, 0);
    }

    static int Rank(string v) => v switch { "check" => 1, "damaged" => 2, _ => 0 };
    static string Worse(string a, string b) => Rank(b) > Rank(a) ? b : a;

    /// <summary>Compare two geometries in the same coordinate system.</summary>
    public static QualityReport Compare(GeomData? before, GeomData? after, int samples = 20000, int seed = 1)
    {
        var rng = new Random(seed);
        var (V0, T0) = Tris(before);
        var (V1, T1) = Tris(after);
        var q = new QualityReport { TrisBefore = T0.Length / 3, TrisAfter = T1.Length / 3, VertsBefore = V0.Length / 3, VertsAfter = V1.Length / 3 };
        if (T0.Length == 0 || T1.Length == 0)
        {
            q.Verdict = T0.Length != T1.Length ? "check" : "ok";
            if (T0.Length > 0 && T1.Length == 0) { q.Warnings.Add("all triangles removed"); q.Verdict = "damaged"; q.AreaLossPct = 100; }
            return q;
        }
        (double[] lo, double[] hi) Box(double[] V, int[] T)
        {
            var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var v in T) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], V[3 * v + c]); hi[c] = Math.Max(hi[c], V[3 * v + c]); }
            return (lo, hi);
        }
        var (lo0, hi0) = Box(V0, T0);
        var (lo1, hi1) = Box(V1, T1);
        double diag = Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi0[c] - lo0[c]) * (hi0[c] - lo0[c])));
        if (diag == 0) diag = 1.0;
        q.Diag = diag;
        q.BBoxChangePct = Enumerable.Range(0, 3).Max(c => Math.Max(Math.Abs(lo1[c] - lo0[c]), Math.Abs(hi1[c] - hi0[c]))) / diag * 100;
        int ns = Math.Min(samples, Math.Max(2000, T0.Length / 3 * 2));
        var (P0, F0) = Sample(V0, T0, ns, rng);
        var (P1, F1) = Sample(V1, T1, ns, rng);
        double area0 = 0, area1 = 0;
        for (int t = 0; t < T0.Length / 3; t++) area0 += Area3(V0, T0, t);
        for (int t = 0; t < T1.Length / 3; t++) area1 += Area3(V1, T1, t);
        q.AreaLossPct = area0 > 0 ? (1 - area1 / area0) * 100 : 0;
        double cell = Math.Max(Math.Sqrt(Math.Max(area0, 1e-30) / Math.Max(P0.Length / 3, 1)) * 2.0, diag * 1e-6);
        var s0 = new Surface(V0, T0, P0, F0, cell);
        var s1 = new Surface(V1, T1, P1, F1, cell);
        int n1 = P1.Length / 3, n0 = P0.Length / 3;
        var d10 = new double[n1];
        var j10 = new int[n1];
        Parallel.For(0, n1, i => { var (d, j) = s0.Distance(P1[3 * i], P1[3 * i + 1], P1[3 * i + 2]); d10[i] = d; j10[i] = j; });
        var d01 = new double[n0];
        Parallel.For(0, n0, i => d01[i] = s1.Distance(P0[3 * i], P0[3 * i + 1], P0[3 * i + 2]).d);
        double dev = Math.Max(d10.Max(), d01.Max());
        q.MaxDevPct = dev / diag * 100;
        q.MeanDevPct = (d10.Average() + d01.Average()) / 2 / diag * 100;
        q.MaxDev = dev;
        // flipped: result faces facing the other way than the original surface next to them (and no
        // same-facing original face nearby: double-sided originals have both orientations)
        double tol = Math.Max(cell, diag * 0.01);
        int close = 0, flipped = 0;
        for (int i = 0; i < n1; i++)
        {
            if (d10[i] >= tol) continue;
            close++;
            var n1v = FaceNormal(V1, T1, F1[i]);
            var n0v = FaceNormal(V0, T0, F0[j10[i]]);
            if (n1v.x * n0v.x + n1v.y * n0v.y + n1v.z * n0v.z >= -0.5) continue;
            bool agree = false;
            for (int k = 0; k < n0 && !agree; k++)
            {
                double ex = P0[3 * k] - P1[3 * i], ey = P0[3 * k + 1] - P1[3 * i + 1], ez = P0[3 * k + 2] - P1[3 * i + 2];
                if (ex * ex + ey * ey + ez * ez > tol * tol) continue;
                var nk = FaceNormal(V0, T0, F0[k]);
                if (n1v.x * nk.x + n1v.y * nk.y + n1v.z * nk.z > 0.5) agree = true;
            }
            if (!agree) flipped++;
        }
        q.FlippedPct = (double)flipped / Math.Max(close, 1) * 100;
        var vd = new double[V1.Length / 3];
        Parallel.For(0, vd.Length, i => vd[i] = s0.Distance(V1[3 * i], V1[3 * i + 1], V1[3 * i + 2]).d / diag * 100);
        q.VertexDev = vd;
        (q.BoundaryBefore, q.NonManifoldBefore) = EdgeStats(V0, T0);
        (q.BoundaryAfter, q.NonManifoldAfter) = EdgeStats(V1, T1);
        int degenerate = 0;
        double tiny = Math.Pow(diag * 1e-5, 2);
        for (int t = 0; t < T1.Length / 3; t++)
        {
            int a = T1[3 * t], b = T1[3 * t + 1], c = T1[3 * t + 2];
            double ux = V1[3 * b] - V1[3 * a], uy = V1[3 * b + 1] - V1[3 * a + 1], uz = V1[3 * b + 2] - V1[3 * a + 2];
            double vx = V1[3 * c] - V1[3 * a], vy = V1[3 * c + 1] - V1[3 * a + 1], vz = V1[3 * c + 2] - V1[3 * a + 2];
            double ar = 0.5 * Math.Sqrt(Math.Pow(uy * vz - uz * vy, 2) + Math.Pow(uz * vx - ux * vz, 2) + Math.Pow(ux * vy - uy * vx, 2));
            if (ar < tiny) degenerate++;
        }
        q.DegenerateAfter = degenerate;
        string verdict = "ok";
        if (q.MaxDevPct > CheckDev) { verdict = "damaged"; q.Warnings.Add($"surface moved by up to {PyFormat.Fixed(q.MaxDevPct, 2)} % of the object size"); }
        else if (q.MaxDevPct > OkDev) { verdict = "check"; q.Warnings.Add($"surface moved by up to {PyFormat.Fixed(q.MaxDevPct, 2)} % of the object size"); }
        int b0 = q.BoundaryBefore, b1 = q.BoundaryAfter;
        if (b1 > b0 * 1.1 + 4)
        {
            verdict = b1 > b0 * 2 + 20 ? "damaged" : Worse(verdict, "check");
            q.Warnings.Add($"new holes / open edges: {PyFormat.Int(b0)} -> {PyFormat.Int(b1)} boundary edges");
        }
        if (q.NonManifoldAfter > q.NonManifoldBefore + 2)
        {
            verdict = Worse(verdict, "check");
            q.Warnings.Add($"non-manifold edges {PyFormat.Int(q.NonManifoldBefore)} -> {PyFormat.Int(q.NonManifoldAfter)}");
        }
        if (q.FlippedPct > 1.0)
        {
            verdict = Worse(verdict, q.FlippedPct < 5 ? "check" : "damaged");
            q.Warnings.Add($"{PyFormat.Fixed(q.FlippedPct, 1)} % of the surface now faces the other way (flipped)");
        }
        if (q.BBoxChangePct > CheckDev)
        {
            verdict = Worse(verdict, "check");
            q.Warnings.Add($"outline changed by {PyFormat.Fixed(q.BBoxChangePct, 1)} % (parts shrank or disappeared)");
        }
        if (q.DegenerateAfter > Math.Max(10, 0.01 * T1.Length / 3)) q.Warnings.Add($"{PyFormat.Int(q.DegenerateAfter)} sliver / zero-area triangles");
        if (q.AreaLossPct > GoneAreaLoss)
        {
            verdict = Worse(verdict, "check");
            q.Warnings.Add($"almost gone (area −{PyFormat.Fixed(q.AreaLossPct, 0)} %)");
        }
        q.Verdict = verdict;
        return q;
    }
    /// <summary>Barycentric weights (wa, wb, wc) of the point of triangle abc closest to p (Ericson).</summary>
    static (double wa, double wb, double wc) ClosestBary(double px, double py, double pz, double[] V, int ia, int ib, int ic)
    {
        double ax = V[3 * ia], ay = V[3 * ia + 1], az = V[3 * ia + 2];
        double abx = V[3 * ib] - ax, aby = V[3 * ib + 1] - ay, abz = V[3 * ib + 2] - az;
        double acx = V[3 * ic] - ax, acy = V[3 * ic + 1] - ay, acz = V[3 * ic + 2] - az;
        double apx = px - ax, apy = py - ay, apz = pz - az;
        double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        if (d1 <= 0 && d2 <= 0) return (1, 0, 0);
        double bpx = px - V[3 * ib], bpy = py - V[3 * ib + 1], bpz = pz - V[3 * ib + 2];
        double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
        if (d3 >= 0 && d4 <= d3) return (0, 1, 0);
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / (d1 - d3); return (1 - v, v, 0); }
        double cpx = px - V[3 * ic], cpy = py - V[3 * ic + 1], cpz = pz - V[3 * ic + 2];
        double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
        if (d6 >= 0 && d5 <= d6) return (0, 0, 1);
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / (d2 - d6); return (1 - w, 0, w); }
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { double w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (0, 1 - w, w); }
        double denom = va + vb + vc;
        if (denom == 0) return (1, 0, 0);
        double vv = vb / denom, ww = vc / denom;
        return (1 - vv - ww, vv, ww);
    }

    static double Area3(double[] V, int[] T, int t)
    {
        int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
        double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
        double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
    }

    /// <summary>Signed area of triangle t in UV space.</summary>
    static double UvArea(VArray uv, int[] T, int t)
    {
        int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
        double ua = uv.Get(a, 0), va = uv.Get(a, 1);
        return 0.5 * ((uv.Get(b, 0) - ua) * (uv.Get(c, 1) - va) - (uv.Get(c, 0) - ua) * (uv.Get(b, 1) - va));
    }

    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Texture metrics of a change, filled into <paramref name="into"/> (normally the shape report of the same
    /// geometries): the texture-space shift of sampled surface points in texels of the largest texture
    /// (<see cref="QualityReport.TexShiftMean"/> / <see cref="QualityReport.TexShiftP95"/>), the largest UV density change
    /// per result triangle (<see cref="QualityReport.UvStretch"/>) and the result triangles whose UV winding flipped against
    /// their before-state (<see cref="QualityReport.UvFlipped"/>). The verdict becomes the worse of shape and texture.
    /// Rebaked / ignored textures, untextured geometry and textures of unknown size are not measured: only
    /// <see cref="QualityReport.TextureNote"/> says so and the verdict is left as it is.
    /// Cost: two area-weighted sample sets of <paramref name="samples"/> points (+ the used vertices); every after sample
    /// and every after triangle finds its before triangle through the sample grid of <see cref="Surface"/> (the nearest
    /// before sample's triangle and its welded neighbours), so the work is linear in samples + triangles.</summary>
    public static void CompareTextured(QualityReport into, GeomData? before, GeomData? after, StateSet? stateSet, string seams = "keep",
        int samples = 4000, int seed = 1)
    {
        if (seams == "rebake") { into.TextureNote = "textures rebaked"; return; }
        if (seams == "ignore") { into.TextureNote = "textures ignored"; return; }
        if (stateSet == null || stateSet.Textures.Count == 0 || before == null || before.TexCoords.Count == 0) { into.TextureNote = "no texture"; return; }
        // the largest texture whose unit has UVs on both sides (a larger unit without texcoords cannot be measured)
        bool HasUvs(Texture t) => before.TexCoords.TryGetValue(t.Unit, out var a) && after != null && after.TexCoords.TryGetValue(t.Unit, out var b)
            && a.Comps >= 2 && b.Comps >= 2 && a.Count == before.NumVertices && b.Count == after.NumVertices;
        var tex = stateSet.Textures.Where(HasUvs).OrderByDescending(t => (long)t.Width * t.Height).FirstOrDefault();
        if (tex == null) { into.TextureNote = "no texture"; return; }
        if (tex.Width == 0) { into.TextureNote = "texture size unknown"; return; }
        var uv0 = before.TexCoords[tex.Unit];
        var uv1 = after!.TexCoords[tex.Unit];
        var (V0, T0) = Tris(before);
        var (V1, T1) = Tris(after);
        if (T0.Length == 0 || T1.Length == 0) return;   // nothing to compare on (the shape check says so)
        double W = tex.Width, H = tex.Height > 0 ? tex.Height : tex.Width;
        var rng = new Random(seed);

        double diag2 = 0;
        for (int c = 0; c < 3; c++)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var v in T0) { lo = Math.Min(lo, V0[3 * v + c]); hi = Math.Max(hi, V0[3 * v + c]); }
            diag2 += (hi - lo) * (hi - lo);
        }
        if (diag2 == 0) diag2 = 1;
        double area0 = 0;
        for (int t = 0; t < T0.Length / 3; t++) area0 += Area3(V0, T0, t);
        var (P0, F0) = Sample(V0, T0, samples, rng);
        var (P1, F1) = Sample(V1, T1, samples, rng);
        double cell = Math.Max(Math.Sqrt(Math.Max(area0, 1e-30) / Math.Max(P0.Length / 3, 1)) * 2.0, Math.Sqrt(diag2) * 1e-6);
        var s0 = new Surface(V0, T0, P0, F0, cell);

        static (double u, double v) UvAt(double x, double y, double z, double[] V, int[] T, VArray uv, int t)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            var (wa, wb, wc) = ClosestBary(x, y, z, V, a, b, c);
            return (wa * uv.Get(a, 0) + wb * uv.Get(b, 0) + wc * uv.Get(c, 0), wa * uv.Get(a, 1) + wb * uv.Get(b, 1) + wc * uv.Get(c, 1));
        }

        // texture shift: each after sample against the closest point on the before surface
        int n1 = P1.Length / 3;
        var shift = new double[n1];
        Parallel.For(0, n1, i =>
        {
            double x = P1[3 * i], y = P1[3 * i + 1], z = P1[3 * i + 2];
            var (_, _, t0) = s0.Closest(x, y, z);
            if (t0 < 0) return;
            var (ua, va) = UvAt(x, y, z, V1, T1, uv1, F1[i]);
            var (ub, vb) = UvAt(x, y, z, V0, T0, uv0, t0);
            double du = (ua - ub) * W, dv = (va - vb) * H;
            shift[i] = Math.Sqrt(du * du + dv * dv);
        });
        if (n1 == 0) return;
        var sorted = (double[])shift.Clone();
        Array.Sort(sorted);
        into.TexShiftMean = shift.Average();
        into.TexShiftP95 = sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1)];

        // stretch and flips: each after triangle against the before triangle at its centroid
        int m1 = T1.Length / 3;
        double minArea = 1e-12 * diag2;
        var stretch = new double[m1];
        var flip = new bool[m1];
        Parallel.For(0, m1, t =>
        {
            double a1 = Area3(V1, T1, t);
            if (a1 <= minArea) return;
            double cx = 0, cy = 0, cz = 0;
            for (int k = 0; k < 3; k++) { int v = T1[3 * t + k]; cx += V1[3 * v] / 3; cy += V1[3 * v + 1] / 3; cz += V1[3 * v + 2] / 3; }
            var (_, _, t0) = s0.Closest(cx, cy, cz);
            if (t0 < 0) return;
            double a0 = Area3(V0, T0, t0);
            if (a0 <= minArea) return;
            double s1 = UvArea(uv1, T1, t), sb = UvArea(uv0, T0, t0);
            if (Math.Abs(s1) <= 1e-12 || Math.Abs(sb) <= 1e-12) return;   // no UV area on one side: no ratio, no winding
            double r = Math.Abs(s1) / a1 / (Math.Abs(sb) / a0);
            stretch[t] = Math.Max(r, 1 / r);
            flip[t] = Math.Sign(s1) != Math.Sign(sb);
        });
        double maxStretch = m1 > 0 ? stretch.Max() : 0;
        into.UvStretch = maxStretch > 0 ? maxStretch : null;
        into.UvFlipped = flip.Count(f => f);

        double p95 = into.TexShiftP95.Value;
        string tv = "ok";
        if (p95 > OkTexels || into.UvStretch > OkStretch) tv = "check";
        if (p95 >= DamagedTexels || into.UvFlipped > 0) tv = "damaged";
        if (p95 > OkTexels) into.Warnings.Add($"texture shifted by up to {p95.ToString("0.#", Inv)} texels");
        if (into.UvStretch is double s && s > OkStretch) into.Warnings.Add($"texture stretched {s.ToString("0.#", Inv)}×");
        if (into.UvFlipped > 0) into.Warnings.Add($"{into.UvFlipped} UV triangle(s) flipped (texture mirrored)");
        into.Verdict = Worse(into.Verdict, tv);
    }

    /// <summary>The largest new gap between parts that touched before a change. World positions (each part through its
    /// <see cref="Part.World"/>); <c>eps</c> = <paramref name="contactRel"/> x the diagonal of all parts' world bounds. For every
    /// pair whose world bounds are within <c>eps</c>: the contacts are the before vertices of A within <c>eps</c> of a before
    /// vertex of B, found through a hash grid of B's before vertices with cell <c>eps</c> (linear in the vertices, never their
    /// product), at most <paramref name="maxContacts"/> of them taken with an even stride; the gap at a contact is the
    /// distance between its closest points on A's and B's after surfaces (<see cref="Surface"/> lookups). Returns the pair
    /// with the largest gap; null when no pair has contacts. Pairs of two unchanged parts (Before and After the same object)
    /// are skipped: nothing can have opened between them. A part without triangles after the change is left to the
    /// "almost gone" check.</summary>
    public static Crack? Cracks(IReadOnlyList<Part> parts, double contactRel = 1e-4, int maxContacts = 2000)
    {
        int np = parts.Count;
        if (np < 2 || maxContacts < 1) return null;
        // before: world positions of the used vertices and the world bounds
        var bv = new double[np][];
        var lo = new double[np][];
        var hi = new double[np][];
        double[] glo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] ghi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (int i = 0; i < np; i++)
        {
            var (V, T) = Tris(parts[i].Before);
            var used = new SortedSet<int>(T);
            var W = new List<double>(used.Count * 3);
            lo[i] = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            hi[i] = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (int v in used)
            {
                var (x, y, z) = Mat.Apply(parts[i].World, V[3 * v], V[3 * v + 1], V[3 * v + 2]);
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) continue;
                W.Add(x); W.Add(y); W.Add(z);
                lo[i][0] = Math.Min(lo[i][0], x); lo[i][1] = Math.Min(lo[i][1], y); lo[i][2] = Math.Min(lo[i][2], z);
                hi[i][0] = Math.Max(hi[i][0], x); hi[i][1] = Math.Max(hi[i][1], y); hi[i][2] = Math.Max(hi[i][2], z);
            }
            for (int c = 0; c < 3; c++) { glo[c] = Math.Min(glo[c], lo[i][c]); ghi[c] = Math.Max(ghi[c], hi[i][c]); }
            bv[i] = W.ToArray();
        }
        if (!double.IsFinite(glo[0])) return null;
        double diag = Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (ghi[c] - glo[c]) * (ghi[c] - glo[c])));
        double eps = contactRel * (diag > 0 ? diag : 1.0);
        if (!(eps > 0)) return null;

        // hash grid of a part's before vertices (cell = eps), built when a pair needs it
        var grids = new Dictionary<(long, long, long), List<int>>?[np];
        (long, long, long) Key(double x, double y, double z) => ((long)Math.Floor(x / eps), (long)Math.Floor(y / eps), (long)Math.Floor(z / eps));
        Dictionary<(long, long, long), List<int>> GridOf(int i)
        {
            if (grids[i] is { } g) return g;
            g = new();
            var P = bv[i];
            for (int k = 0; k < P.Length / 3; k++)
            {
                var key = Key(P[3 * k], P[3 * k + 1], P[3 * k + 2]);
                if (!g.TryGetValue(key, out var l)) g[key] = l = new List<int>();
                l.Add(k);
            }
            return grids[i] = g;
        }
        // after surface in world positions, built when a pair has contacts
        var surfaces = new Surface?[np];
        var built = new bool[np];
        Surface? SurfaceOf(int i)
        {
            if (built[i]) return surfaces[i];
            built[i] = true;
            var (V, T) = Tris(parts[i].After);
            if (T.Length == 0) return null;
            var W = new double[V.Length];
            for (int k = 0; k < V.Length / 3; k++)
                (W[3 * k], W[3 * k + 1], W[3 * k + 2]) = Mat.Apply(parts[i].World, V[3 * k], V[3 * k + 1], V[3 * k + 2]);
            double area = 0;
            for (int t = 0; t < T.Length / 3; t++) area += Area3(W, T, t);
            int ns = Math.Min(20000, Math.Max(2000, T.Length / 3 * 2));
            var (P, F) = Sample(W, T, ns, new Random(1));
            double cell = Math.Max(Math.Sqrt(Math.Max(area, 1e-30) / Math.Max(P.Length / 3, 1)) * 2.0, eps);
            return surfaces[i] = new Surface(W, T, P, F, cell);
        }
        bool Near(int a, int b)
        {
            for (int c = 0; c < 3; c++)
                if (lo[a][c] > hi[b][c] + eps || lo[b][c] > hi[a][c] + eps) return false;
            return true;
        }

        Crack? best = null;
        for (int a = 0; a < np; a++)
            for (int b = a + 1; b < np; b++)
            {
                if (bv[a].Length == 0 || bv[b].Length == 0 || !Near(a, b)) continue;
                if (ReferenceEquals(parts[a].Before, parts[a].After) && ReferenceEquals(parts[b].Before, parts[b].After)) continue;
                var gb = GridOf(b);
                double[] PA = bv[a], PB = bv[b], lb = lo[b], hb = hi[b];
                var contacts = new List<int>();
                for (int k = 0; k < PA.Length / 3; k++)
                {
                    double x = PA[3 * k], y = PA[3 * k + 1], z = PA[3 * k + 2];
                    if (x < lb[0] - eps || x > hb[0] + eps || y < lb[1] - eps || y > hb[1] + eps || z < lb[2] - eps || z > hb[2] + eps) continue;
                    var (kx, ky, kz) = Key(x, y, z);
                    bool hit = false;
                    for (long dx = -1; dx <= 1 && !hit; dx++)
                        for (long dy = -1; dy <= 1 && !hit; dy++)
                            for (long dz = -1; dz <= 1 && !hit; dz++)
                                if (gb.TryGetValue((kx + dx, ky + dy, kz + dz), out var l))
                                    foreach (int j in l)
                                        if (Dist(x, y, z, PB[3 * j], PB[3 * j + 1], PB[3 * j + 2]) <= eps) { hit = true; break; }
                    if (hit) contacts.Add(k);
                }
                if (contacts.Count == 0) continue;
                if (contacts.Count > maxContacts)
                {
                    double stride = (double)contacts.Count / maxContacts;
                    var all = contacts;
                    contacts = Enumerable.Range(0, maxContacts).Select(i => all[(int)(i * stride)]).ToList();
                }
                var sa = SurfaceOf(a);
                var sb = SurfaceOf(b);
                if (sa == null || sb == null) continue;
                var gaps = new double[contacts.Count];
                Parallel.For(0, contacts.Count, i =>
                {
                    int k = contacts[i];
                    double x = PA[3 * k], y = PA[3 * k + 1], z = PA[3 * k + 2];
                    if (sa.ClosestPoint(x, y, z) is { } ca && sb.ClosestPoint(x, y, z) is { } cb)
                        gaps[i] = Dist(ca.x, ca.y, ca.z, cb.x, cb.y, cb.z);
                });
                double gap = gaps.Max();
                if (best == null || gap > best.Gap) best = new Crack(parts[a].Name, parts[b].Name, gap);
            }
        return best;
    }
}
