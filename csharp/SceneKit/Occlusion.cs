namespace SceneKit;

/// <summary>Which triangles of a group of meshes can be seen from outside: orthographic depth renders from many
/// directions on a sphere, then a test render that marks every triangle reaching the front (within a small depth
/// tolerance, so coplanar faces both count). Meshes that do not occlude (glass, alpha cut-outs) are only tested.
/// Visible triangles are grown by rings of neighbours (sharing a position), so no holes open at silhouettes.</summary>
public static class Occlusion
{
    public sealed class Options
    {
        public int Views = 162;
        /// <summary>Pixels per side of every view; 0 = automatic (a typical triangle covers about a pixel, 512 to 4096).</summary>
        public int Resolution = 0;
        public int Adjacency = 1;
        public Action<string>? Progress;
        /// <summary>Only views from above the horizon: every direction with a negative z (a camera below the horizon; the
        /// eye sits at +d) is dropped, and so is every direction exactly on the horizon (z = 0, the axis views): it sees a
        /// flat bottom edge-on, where each triangle covers no pixel and would count as seen at its centroid. For objects
        /// standing on the ground; wrong for objects seen from below. A triangle whose normal faces down (n_z &lt; -0.5, the
        /// rule of <see cref="PartProxy.Cut"/>) counts as unseen whatever the views show, also after the adjacency growth:
        /// such a face can only be seen from below the horizon. The horizon is z of the items' frame (for the per-part proxy
        /// the target's parent frame), not world z.</summary>
        public bool AboveHorizonOnly = false;
    }

    /// <summary>A mesh in the common frame: V (n x 3), T (m x 3). Occludes = false: it can be seen but hides nothing.</summary>
    public sealed record Item(double[] V, int[] T, bool Occludes = true);

    /// <summary>n unit vectors spread evenly on the sphere (Fibonacci sphere).</summary>
    public static double[][] SphereDirections(int n)
    {
        var o = new double[n][];
        double golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            double z = 1 - 2 * (i + 0.5) / n, r = Math.Sqrt(Math.Max(0, 1 - z * z)), a = golden * i;
            o[i] = new[] { r * Math.Cos(a), r * Math.Sin(a), z };
        }
        return o;
    }

    /// <summary>Views close to the six axes (0 and 0.3 to 8 degrees off, 8 ways around each): models are built along their
    /// axes, and a gun barrel or a deep slot along an axis is only seen into from almost exactly that direction.</summary>
    public static double[][] AxisDirections()
    {
        var o = new List<double[]>();
        double[][] axes = { new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, -1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0, 0, -1.0 } };
        foreach (var a in axes)
        {
            o.Add(a);
            var u = Norm(Cross(a, Math.Abs(a[2]) < 0.9 ? new[] { 0.0, 0, 1 } : new[] { 1.0, 0, 0 }));
            var v = Cross(a, u);
            foreach (double deg in new[] { 0.3, 1, 3, 8 })
            {
                double t = Math.Tan(deg * Math.PI / 180);
                for (int k = 0; k < 8; k++)
                {
                    double ph = Math.PI * k / 4, cu = Math.Cos(ph) * t, cv = Math.Sin(ph) * t;
                    o.Add(Norm(new[] { a[0] + u[0] * cu + v[0] * cv, a[1] + u[1] * cu + v[1] * cv, a[2] + u[2] * cu + v[2] * cv }));
                }
            }
        }
        return o.ToArray();
    }

    /// <summary>The view directions of a run: <see cref="SphereDirections"/> (<see cref="Options.Views"/>, at least 1), then
    /// <see cref="AxisDirections"/>; with <see cref="Options.AboveHorizonOnly"/> only those with a positive z.</summary>
    internal static double[][] Directions(Options opt)
    {
        var dirs = SphereDirections(Math.Max(1, opt.Views)).Concat(AxisDirections());
        return (opt.AboveHorizonOnly ? dirs.Where(d => d[2] > 0) : dirs).ToArray();
    }

    /// <summary>Resolution at which the median triangle edge is about one pixel of a view 2 * radius wide.</summary>
    public static int AutoResolution(IReadOnlyList<Item> items, double radius)
    {
        var edges = new List<double>();
        foreach (var it in items)
        {
            int m = it.T.Length / 3, step = Math.Max(1, m / 20000);
            for (int t = 0; t < m; t += step)
            {
                int a = it.T[3 * t], b = it.T[3 * t + 1];
                double dx = it.V[3 * a] - it.V[3 * b], dy = it.V[3 * a + 1] - it.V[3 * b + 1], dz = it.V[3 * a + 2] - it.V[3 * b + 2];
                double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (l > 0) edges.Add(l);
            }
        }
        if (edges.Count == 0) return 512;
        edges.Sort();
        double median = edges[edges.Count / 2];
        return (int)Math.Clamp(Math.Ceiling(2 * radius / median), 512, 4096);
    }

    public static bool[][] Visible(IReadOnlyList<Item> items, Options? opt = null)
    {
        opt ??= new Options();
        int ni = items.Count;
        var offs = new int[ni + 1];
        for (int i = 0; i < ni; i++) offs[i + 1] = offs[i] + items[i].T.Length / 3;
        int total = offs[ni];
        var seen = new byte[total];
        // bounding sphere of everything
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (var it in items)
            for (int i = 0; i < it.V.Length; i += 3)
                for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], it.V[i + k]); hi[k] = Math.Max(hi[k], it.V[i + k]); }
        var res = new bool[ni][];
        if (total == 0 || lo[0] > hi[0])
        {
            for (int i = 0; i < ni; i++) res[i] = new bool[items[i].T.Length / 3];
            return res;
        }
        var c = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
        double radius = Math.Max(1e-9, Math.Sqrt(Math.Pow(hi[0] - lo[0], 2) + Math.Pow(hi[1] - lo[1], 2) + Math.Pow(hi[2] - lo[2], 2)) / 2);
        int R = opt.Resolution > 0 ? opt.Resolution : AutoResolution(items, radius);
        float eps = (float)(radius * 2e-4);
        var dirs = Directions(opt);
        // above the horizon: the down-facing triangles are never tested and never seen (null otherwise: today's run)
        var down = opt.AboveHorizonOnly ? DownFacing(items, total, offs) : null;
        int done = 0, next = -1;
        int par = (int)Math.Clamp(1_500_000_000L / ((long)R * R * 4), 1, Environment.ProcessorCount);
        // a fixed set of workers, each with one depth buffer, taking views from a shared counter
        Parallel.For(0, Math.Min(par, dirs.Length), new ParallelOptions { MaxDegreeOfParallelism = par }, _ =>
        {
            var zbuf = new float[R * R];
            int vi;
            while ((vi = Interlocked.Increment(ref next)) < dirs.Length) View(vi, zbuf);
        });
        void View(int vi, float[] zbuf)
        {
            var d = dirs[vi];
            var up0 = Math.Abs(d[2]) < 0.9 ? new[] { 0.0, 0.0, 1.0 } : new[] { 1.0, 0.0, 0.0 };
            var right = Norm(Cross(d, up0));
            var up = Cross(right, d);
            Array.Fill(zbuf, float.PositiveInfinity);
            for (int pass = 0; pass < 2; pass++)
                for (int ii = 0; ii < ni; ii++)
                {
                    var it = items[ii];
                    if (pass == 0 && !it.Occludes) continue;
                    int nv = it.V.Length / 3;
                    var sx = new double[nv]; var sy = new double[nv]; var sz = new float[nv];
                    for (int v = 0; v < nv; v++)
                    {
                        double x = it.V[3 * v] - c[0], y = it.V[3 * v + 1] - c[1], z = it.V[3 * v + 2] - c[2];
                        sx[v] = ((x * right[0] + y * right[1] + z * right[2]) / radius * 0.5 + 0.5) * R;
                        sy[v] = ((x * up[0] + y * up[1] + z * up[2]) / radius * 0.5 + 0.5) * R;
                        sz[v] = (float)(radius - (x * d[0] + y * d[1] + z * d[2]));   // distance from the eye plane
                    }
                    int m = it.T.Length / 3;
                    for (int t = 0; t < m; t++)
                    {
                        int gid = offs[ii] + t;
                        if (pass == 1 && (seen[gid] != 0 || down != null && down[gid])) continue;
                        bool hit = Raster(it.T[3 * t], it.T[3 * t + 1], it.T[3 * t + 2], sx, sy, sz, R, zbuf, pass == 0, eps);
                        if (pass == 1 && hit) seen[gid] = 1;
                    }
                }
            int n = Interlocked.Increment(ref done);
            if (n % 16 == 0) opt.Progress?.Invoke($"visibility: {n} of {dirs.Length} views");
        }
        for (int i = 0; i < ni; i++)
        {
            var v = new bool[items[i].T.Length / 3];
            for (int t = 0; t < v.Length; t++) v[t] = seen[offs[i] + t] != 0;
            Grow(items[i], v, opt.Adjacency, radius);
            if (down != null) for (int t = 0; t < v.Length; t++) if (down[offs[i] + t]) v[t] = false;
            res[i] = v;
        }
        return res;
    }

    /// <summary>Per triangle of all items (numbered through <paramref name="offs"/>): its normal (from the winding) faces
    /// down, n_z &lt; -0.5 of its unit normal. Triangles without area do not.</summary>
    static bool[] DownFacing(IReadOnlyList<Item> items, int total, int[] offs)
    {
        var o = new bool[total];
        for (int i = 0; i < items.Count; i++)
        {
            var (V, T) = (items[i].V, items[i].T);
            for (int t = 0; t < T.Length / 3; t++)
            {
                int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
                double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
                double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                o[offs[i] + t] = l > 0 && nz / l < -0.5;
            }
        }
        return o;
    }

    /// <summary>write: depth pass (keep the nearest depth). Otherwise test: true if a covered pixel is not behind the
    /// stored depth (+ eps). A triangle that covers no pixel centre uses the pixel of its centroid.</summary>
    static bool Raster(int a, int b, int c, double[] X, double[] Y, float[] Z, int R, float[] zbuf, bool write, float eps)
    {
        double xa = X[a], ya = Y[a], xb = X[b], yb = Y[b], xc = X[c], yc = Y[c];
        double area = (xb - xa) * (yc - ya) - (xc - xa) * (yb - ya);
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(xa, Math.Min(xb, xc)))), x1 = Math.Min(R - 1, (int)Math.Ceiling(Math.Max(xa, Math.Max(xb, xc))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ya, Math.Min(yb, yc)))), y1 = Math.Min(R - 1, (int)Math.Ceiling(Math.Max(ya, Math.Max(yb, yc))));
        bool covered = false, hit = false;
        if (Math.Abs(area) > 1e-12)
            for (int py = y0; py <= y1; py++)
            {
                double cy = py + 0.5;
                for (int px = x0; px <= x1; px++)
                {
                    double cx = px + 0.5;
                    double w0 = ((xb - cx) * (yc - cy) - (xc - cx) * (yb - cy)) / area;
                    double w1 = ((xc - cx) * (ya - cy) - (xa - cx) * (yc - cy)) / area;
                    double w2 = 1 - w0 - w1;
                    if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                    covered = true;
                    float z = (float)(w0 * Z[a] + w1 * Z[b] + w2 * Z[c]);
                    int p = py * R + px;
                    if (write) { if (z < zbuf[p]) zbuf[p] = z; }
                    else if (z <= zbuf[p] + eps) { hit = true; break; }
                }
                if (hit) break;
            }
        if (!covered)
        {
            int mx = (int)((xa + xb + xc) / 3), my = (int)((ya + yb + yc) / 3);
            if (mx < 0 || mx >= R || my < 0 || my >= R) return false;
            float z = (Z[a] + Z[b] + Z[c]) / 3;
            int p = my * R + mx;
            if (write) { if (z < zbuf[p]) zbuf[p] = z; }
            else hit = z <= zbuf[p] + eps;
        }
        return hit;
    }

    /// <summary>Mark `rings` rings of neighbours (triangles sharing a vertex position) of visible triangles visible.</summary>
    static void Grow(Item it, bool[] vis, int rings, double radius)
    {
        if (rings <= 0 || vis.All(v => v) || !vis.Any(v => v)) return;
        int nv = it.V.Length / 3, m = vis.Length;
        double q = radius * 1e-7;
        var ids = new Dictionary<(long, long, long), int>();
        var pid = new int[nv];
        for (int v = 0; v < nv; v++)
        {
            var k = ((long)Math.Round(it.V[3 * v] / q), (long)Math.Round(it.V[3 * v + 1] / q), (long)Math.Round(it.V[3 * v + 2] / q));
            if (!ids.TryGetValue(k, out var id)) ids[k] = id = ids.Count;
            pid[v] = id;
        }
        var trisOf = new List<int>[ids.Count];
        for (int t = 0; t < m; t++)
            for (int k = 0; k < 3; k++) (trisOf[pid[it.T[3 * t + k]]] ??= new List<int>()).Add(t);
        var frontier = Enumerable.Range(0, m).Where(t => vis[t]).ToList();
        for (int r = 0; r < rings && frontier.Count > 0; r++)
        {
            var next = new List<int>();
            foreach (int t in frontier)
                for (int k = 0; k < 3; k++)
                    foreach (int u in trisOf[pid[it.T[3 * t + k]]])
                        if (!vis[u]) { vis[u] = true; next.Add(u); }
            frontier = next;
        }
    }

    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    static double[] Norm(double[] v)
    {
        double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return new[] { v[0] / l, v[1] / l, v[2] / l };
    }
}
