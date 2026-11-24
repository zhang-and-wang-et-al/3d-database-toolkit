namespace SceneKit;

/// <summary>Automatic UV atlas for baking. Charts grow from the largest free triangle over neighbours whose normal stays
/// within ~45 degrees of the seed normal; each chart is projected onto the seed plane (orientation preserving, because
/// every normal is in front of the plane), triangles that would overlap the chart in 2D start a new chart, the chart is
/// rotated to its principal axes and all charts are shelf-packed into the unit square with a gutter for mip-mapping.
/// Vertices are split where charts meet.</summary>
public static class Unwrap
{
    public sealed class Result
    {
        public double[] V = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public double[] UV = Array.Empty<double>();
        /// <summary>Input vertex of each output vertex.</summary>
        public int[] Source = Array.Empty<int>();
        public int Charts;
    }

    public static Result Run(double[] V, int[] T, int textureSize = 512, double coneCos = 0.7, int gutterPx = 4)
    {
        int m = T.Length / 3;
        var fn = new double[3 * m];
        var area = new double[m];
        for (int t = 0; t < m; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
            double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
            double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
            double l = Math.Sqrt(x * x + y * y + z * z);
            area[t] = l / 2;
            if (l > 0) { fn[3 * t] = x / l; fn[3 * t + 1] = y / l; fn[3 * t + 2] = z / l; }
        }
        // edge adjacency
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < m; t++)
            for (int s = 0; s < 3; s++)
            {
                int a = T[3 * t + s], b = T[3 * t + (s + 1) % 3];
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!edgeFaces.TryGetValue(key, out var l)) edgeFaces[key] = l = new List<int>(2);
                l.Add(t);
            }

        // ---------------------------------------------------------------- charts
        var chartOf = new int[m];
        Array.Fill(chartOf, -1);
        var charts = new List<(List<int> faces, double[] u, double[] v)>();
        var order = Enumerable.Range(0, m).OrderByDescending(t => area[t]).ToArray();
        var cornerUV = new double[6 * m];   // per corner, chart-local, world units
        foreach (int seed in order)
        {
            if (chartOf[seed] >= 0) continue;
            int ci = charts.Count;
            double nx = fn[3 * seed], ny = fn[3 * seed + 1], nz = fn[3 * seed + 2];
            if (nx == 0 && ny == 0 && nz == 0) nz = 1;
            // plane basis
            double ax = Math.Abs(nx) < 0.9 ? 1 : 0, ay = ax == 1 ? 0 : 1, az = 0;
            double ux = ay * nz - az * ny, uy = az * nx - ax * nz, uz = ax * ny - ay * nx;
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            ux /= ul; uy /= ul; uz /= ul;
            double vx = ny * uz - nz * uy, vy = nz * ux - nx * uz, vz = nx * uy - ny * ux;
            var faces = new List<int>();
            var grid = new Dictionary<(int, int), List<int>>();
            var big = new List<int>();   // triangles covering too many grid cells: tested against every candidate
            double gcell = Math.Sqrt(area[seed]) * 2 + 1e-12;
            (double, double) Proj(int p) => (V[3 * p] * ux + V[3 * p + 1] * uy + V[3 * p + 2] * uz, V[3 * p] * vx + V[3 * p + 1] * vy + V[3 * p + 2] * vz);

            bool Overlaps(int t)
            {
                var (p0, q0) = Proj(T[3 * t]); var (p1, q1) = Proj(T[3 * t + 1]); var (p2, q2) = Proj(T[3 * t + 2]);
                int x0 = (int)Math.Floor(Math.Min(p0, Math.Min(p1, p2)) / gcell), x1 = (int)Math.Floor(Math.Max(p0, Math.Max(p1, p2)) / gcell);
                int y0 = (int)Math.Floor(Math.Min(q0, Math.Min(q1, q2)) / gcell), y1 = (int)Math.Floor(Math.Max(q0, Math.Max(q1, q2)) / gcell);
                var tested = new HashSet<int>();
                foreach (int o in big)
                    if (tested.Add(o) && TriOverlap2D(p0, q0, p1, q1, p2, q2, Proj(T[3 * o]), Proj(T[3 * o + 1]), Proj(T[3 * o + 2]))) return true;
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096)
                {
                    // large candidate: test every triangle of the chart directly instead of walking the grid
                    foreach (int o in faces)
                        if (tested.Add(o) && TriOverlap2D(p0, q0, p1, q1, p2, q2, Proj(T[3 * o]), Proj(T[3 * o + 1]), Proj(T[3 * o + 2]))) return true;
                    return false;
                }
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        if (grid.TryGetValue((x, y), out var l))
                            foreach (int o in l)
                                if (tested.Add(o) && TriOverlap2D(p0, q0, p1, q1, p2, q2, Proj(T[3 * o]), Proj(T[3 * o + 1]), Proj(T[3 * o + 2]))) return true;
                return false;
            }
            void Add(int t)
            {
                chartOf[t] = ci;
                faces.Add(t);
                var (p0, q0) = Proj(T[3 * t]); var (p1, q1) = Proj(T[3 * t + 1]); var (p2, q2) = Proj(T[3 * t + 2]);
                int x0 = (int)Math.Floor(Math.Min(p0, Math.Min(p1, p2)) / gcell), x1 = (int)Math.Floor(Math.Max(p0, Math.Max(p1, p2)) / gcell);
                int y0 = (int)Math.Floor(Math.Min(q0, Math.Min(q1, q2)) / gcell), y1 = (int)Math.Floor(Math.Max(q0, Math.Max(q1, q2)) / gcell);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4096) { big.Add(t); return; }
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        if (!grid.TryGetValue((x, y), out var l)) grid[(x, y)] = l = new List<int>(2);
                        l.Add(t);
                    }
            }
            Add(seed);
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                for (int s = 0; s < 3; s++)
                {
                    int a = T[3 * t + s], b = T[3 * t + (s + 1) % 3];
                    var lst = edgeFaces[(Math.Min(a, b), Math.Max(a, b))];
                    if (lst.Count != 2) continue;          // borders and non-manifold edges end a chart
                    foreach (int o in lst)
                    {
                        if (chartOf[o] >= 0) continue;
                        if (fn[3 * o] * nx + fn[3 * o + 1] * ny + fn[3 * o + 2] * nz < coneCos) continue;
                        if (Overlaps(o)) continue;
                        Add(o);
                        queue.Enqueue(o);
                    }
                }
            }
            foreach (int t in faces)
                for (int s = 0; s < 3; s++)
                {
                    var (p, q) = Proj(T[3 * t + s]);
                    cornerUV[6 * t + 2 * s] = p; cornerUV[6 * t + 2 * s + 1] = q;
                }
            charts.Add((faces, new double[] { ux, uy, uz }, new double[] { vx, vy, vz }));
        }

        // ---------------------------------------------------------------- minimum-area rectangle per chart (landscape)
        int nc = charts.Count;
        var bw = new double[nc]; var bh = new double[nc];
        for (int ci = 0; ci < nc; ci++)
        {
            var f = charts[ci].faces;
            var pts = new List<(double x, double y)>();
            foreach (int t in f) for (int s = 0; s < 3; s++) pts.Add((cornerUV[6 * t + 2 * s], cornerUV[6 * t + 2 * s + 1]));
            // rotating calipers: the best rectangle has a side on a convex hull edge
            var hull = ConvexHull(pts);
            double bestA = double.PositiveInfinity, bestAng = 0;
            for (int e = 0; e < hull.Count; e++)
            {
                var (ax, ay) = hull[e]; var (bx, by) = hull[(e + 1) % hull.Count];
                double ang = Math.Atan2(by - ay, bx - ax);
                double cs = Math.Cos(-ang), sn = Math.Sin(-ang);
                double mnx = double.PositiveInfinity, mny = double.PositiveInfinity, mxx = double.NegativeInfinity, mxy = double.NegativeInfinity;
                foreach (var (x, y) in hull)
                {
                    double rx = x * cs - y * sn, ry = x * sn + y * cs;
                    mnx = Math.Min(mnx, rx); mny = Math.Min(mny, ry); mxx = Math.Max(mxx, rx); mxy = Math.Max(mxy, ry);
                }
                double a = (mxx - mnx) * (mxy - mny);
                if (a < bestA) { bestA = a; bestAng = ang; }
            }
            double c0 = Math.Cos(-bestAng), s0 = Math.Sin(-bestAng);
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (int t in f)
                for (int s = 0; s < 3; s++)
                {
                    double x = cornerUV[6 * t + 2 * s], y = cornerUV[6 * t + 2 * s + 1];
                    double rx = x * c0 - y * s0, ry = x * s0 + y * c0;
                    cornerUV[6 * t + 2 * s] = rx; cornerUV[6 * t + 2 * s + 1] = ry;
                    minX = Math.Min(minX, rx); minY = Math.Min(minY, ry); maxX = Math.Max(maxX, rx); maxY = Math.Max(maxY, ry);
                }
            bool turn = maxY - minY > maxX - minX;   // landscape packs better on shelves
            foreach (int t in f)
                for (int s = 0; s < 3; s++)
                {
                    double x = cornerUV[6 * t + 2 * s] - minX, y = cornerUV[6 * t + 2 * s + 1] - minY;
                    // a 90 degree turn (x, y) -> (h - y, x) keeps the winding
                    if (turn) { cornerUV[6 * t + 2 * s] = (maxY - minY) - y; cornerUV[6 * t + 2 * s + 1] = x; }
                    else { cornerUV[6 * t + 2 * s] = x; cornerUV[6 * t + 2 * s + 1] = y; }
                }
            bw[ci] = turn ? maxY - minY : maxX - minX;
            bh[ci] = turn ? maxX - minX : maxY - minY;
        }

        // ---------------------------------------------------------------- shelf packing at the largest scale that fits
        double totalArea = 0;
        for (int ci = 0; ci < nc; ci++) totalArea += (bw[ci] + 1e-12) * (bh[ci] + 1e-12);
        double gutter = (double)gutterPx / textureSize;
        var px = new double[nc]; var py = new double[nc];
        var byH = Enumerable.Range(0, nc).OrderByDescending(ci => bh[ci]).ThenByDescending(ci => bw[ci]).ToArray();
        bool Pack(double sc, bool store)
        {
            double x = gutter, y = gutter, shelf = 0;
            foreach (int ci in byH)
            {
                double w = bw[ci] * sc, h = bh[ci] * sc;
                if (x + w + gutter > 1) { x = gutter; y += shelf + gutter; shelf = 0; }
                if (x + w + gutter > 1 || y + h + gutter > 1) return false;
                if (store) { px[ci] = x; py[ci] = y; }
                x += w + gutter;
                shelf = Math.Max(shelf, h);
            }
            return true;
        }
        double lo = 0, hi = 1.0 / Math.Sqrt(Math.Max(totalArea, 1e-30));
        while (Pack(hi, false)) { lo = hi; hi *= 2; }
        for (int it = 0; it < 40; it++) { double mid = (lo + hi) / 2; if (Pack(mid, false)) lo = mid; else hi = mid; }
        double scale = lo;
        Pack(scale, true);

        // ---------------------------------------------------------------- output, splitting vertices between charts
        var map = new Dictionary<(int v, int c), int>();
        var oV = new List<double>(); var oUV = new List<double>(); var src = new List<int>();
        var oT = new int[3 * m];
        for (int t = 0; t < m; t++)
        {
            int ci = chartOf[t];
            for (int s = 0; s < 3; s++)
            {
                int v = T[3 * t + s];
                if (!map.TryGetValue((v, ci), out int ov))
                {
                    ov = src.Count;
                    map[(v, ci)] = ov;
                    src.Add(v);
                    oV.Add(V[3 * v]); oV.Add(V[3 * v + 1]); oV.Add(V[3 * v + 2]);
                    oUV.Add(px[ci] + cornerUV[6 * t + 2 * s] * scale);
                    oUV.Add(py[ci] + cornerUV[6 * t + 2 * s + 1] * scale);
                }
                oT[3 * t + s] = ov;
            }
        }
        return new Result { V = oV.ToArray(), T = oT, UV = oUV.ToArray(), Source = src.ToArray(), Charts = nc };
    }

    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise.</summary>
    static List<(double x, double y)> ConvexHull(List<(double x, double y)> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.x).ThenBy(q => q.y).ToList();
        if (p.Count < 3) return p;
        double Cross((double x, double y) o, (double x, double y) a, (double x, double y) b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        var h = new List<(double x, double y)>();
        foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(p[i]); }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    /// <summary>Do two 2D triangles overlap with positive area? (separating axis test; touching edges do not count)</summary>
    static bool TriOverlap2D(double ax, double ay, double bx, double by, double cx, double cy,
        (double x, double y) d, (double x, double y) e, (double x, double y) f)
    {
        Span<double> P = stackalloc double[] { ax, ay, bx, by, cx, cy };
        Span<double> Q = stackalloc double[] { d.x, d.y, e.x, e.y, f.x, f.y };
        double scale = 0;
        for (int i = 0; i < 6; i++) scale = Math.Max(scale, Math.Max(Math.Abs(P[i]), Math.Abs(Q[i])));
        double eps = 1e-9 * Math.Max(scale, 1e-30);
        for (int pass = 0; pass < 2; pass++)
        {
            var A = pass == 0 ? P : Q;
            for (int i = 0; i < 3; i++)
            {
                double ex = A[2 * ((i + 1) % 3)] - A[2 * i], ey = A[2 * ((i + 1) % 3) + 1] - A[2 * i + 1];
                double nx = -ey, ny = ex;
                double l = Math.Sqrt(nx * nx + ny * ny);
                if (l == 0) continue;
                nx /= l; ny /= l;
                double pMin = double.PositiveInfinity, pMax = double.NegativeInfinity, qMin = double.PositiveInfinity, qMax = double.NegativeInfinity;
                for (int j = 0; j < 3; j++)
                {
                    double p = P[2 * j] * nx + P[2 * j + 1] * ny, q = Q[2 * j] * nx + Q[2 * j + 1] * ny;
                    pMin = Math.Min(pMin, p); pMax = Math.Max(pMax, p); qMin = Math.Min(qMin, q); qMax = Math.Max(qMax, q);
                }
                if (pMax <= qMin + eps || qMax <= pMin + eps) return false;
            }
        }
        return true;
    }
}
