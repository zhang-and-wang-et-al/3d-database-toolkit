namespace SceneKit;

/// <summary>Attribute-aware quadric edge collapse for real-world assets (terrain, buildings, vehicles).
///
/// Built on:
///  - Garland &amp; Heckbert 1998 / Hoppe 1999: per-vertex attribute quadrics. UVs, normals and colours are part of the
///    error, and the attribute value of a collapsed vertex is the optimum of that quadric (no edge interpolation).
///  - Wedges (Hoppe 1999, meshoptimizer): topology runs on welded positions, attributes live on the input vertices.
///    UV / normal seams therefore slide along their own curve instead of being frozen.
///  - Liu et al. 2024 "Simplifying Triangle Meshes in the Wild" and FA-QEM 2026: virtual edges between nearby separate
///    parts so that assembled models (vehicles, props) can merge parts at strong reduction.
/// Borders (tile edges) are either locked, so neighbouring terrain tiles keep matching, or slide along themselves.</summary>
public enum BorderMode { Match, Lock, Slide }

public static class Decimate
{
    public sealed class Options
    {
        /// <summary>Open borders (terrain tile edges). Match: border vertices never move; each border run between corners
        /// is thinned with Douglas-Peucker, which only depends on the shared border, so neighbouring tiles thin it the same
        /// way (any mismatch stays below BorderTolerance). Lock: every border vertex stays. Slide: borders move along
        /// themselves (single objects). Corners never move.</summary>
        public BorderMode Borders = BorderMode.Match;
        /// <summary>Douglas-Peucker tolerance for Match (scene units). Default: MaxError, else 0.2 % of each border run.</summary>
        public double? BorderTolerance;
        /// <summary>Allow separate parts closer than PartGap x bounding diagonal to merge (virtual edges).</summary>
        public bool MergeParts = true;
        public double PartGap = 0.01;
        /// <summary>Weight of the constraint planes along borders and seams (relative to area weighted planes).</summary>
        public double BorderWeight = 1000.0;
        /// <summary>Weight of the tracked true distance (original points absorbed by a vertex to the new surface) in the
        /// collapse cost. 0 = plain quadric error.</summary>
        public double DistanceWeight = 0.0;
        /// <summary>Least-squares refit of the result to samples of the original surface (Hoppe et al. 1993).</summary>
        public bool Refit = true;
        /// <summary>No original vertex may end up farther than this from the result (scene units, e.g. metres).
        /// Note: this is a one-sided collapse bound, not a Hausdorff bound. Only the points absorbed by the two collapsing
        /// vertices are checked, against their own ring; the neighbours' points and the new vertex's distance to the
        /// original surface are not, so the sampled deviation can be several times larger. The simplify pass and Try keep
        /// to a Within limit by measuring and tightening (Optimizer.SimplifyWithin). Parked follow-up: a true bound inside
        /// Decimate (neighbour rings + new-vertex distance to the original surface).</summary>
        public double? MaxError;
        /// <summary>Safety limit when MaxError is not set, as a share of the bounding diagonal: collapses that would move
        /// the surface further are not made, even if the target is not reached (2 % = the damage check's threshold).
        /// 0 = no limit.</summary>
        public double MaxRelativeError = 0.02;
        /// <summary>Weight per attribute channel (k values); an attribute difference of 1 costs like weight x bounding diagonal.</summary>
        public double[]? AttrWeights;
        /// <summary>Input vertices that must not move.</summary>
        public bool[]? Lock;
        public Action<string>? Progress;
    }

    struct Cand { public double Cost, X, Y, Z; public bool Sliver; }

    /// <summary>Simplify V (n x 3) / T (m x 3) to about each target triangle count. attrs (n x k) per input vertex.</summary>
    public static List<Simplify.Result> Run(double[] V0, int[] T0, IList<int> targetTris, double[]? attrs, int k, Options? opt = null)
    {
        opt ??= new Options();
        var targets = targetTris.OrderByDescending(x => x).ToList();
        int n = V0.Length / 3;
        if (attrs == null) k = 0;

        // ---------------------------------------------------------------- local frame (precision far from the origin)
        double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 3; c++) { double v = V0[3 * i + c]; if (v < lo[c]) lo[c] = v; if (v > hi[c]) hi[c] = v; }
        var ctr = new double[3];
        double diag = 0;
        if (n > 0)
            for (int c = 0; c < 3; c++) { ctr[c] = (lo[c] + hi[c]) / 2; diag += (hi[c] - lo[c]) * (hi[c] - lo[c]); }
        diag = Math.Sqrt(diag);
        if (diag == 0) diag = 1.0;

        double? maxErr = opt.MaxError ?? (opt.MaxRelativeError > 0 ? opt.MaxRelativeError * diag : null);

        // ---------------------------------------------------------------- weld positions; input vertices become wedges
        var pid = new int[n];
        var posList = new List<double>();
        {
            double q = diag * 1e-9;
            var map = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < n; i++)
            {
                var key = ((long)Math.Round((V0[3 * i] - ctr[0]) / q), (long)Math.Round((V0[3 * i + 1] - ctr[1]) / q),
                           (long)Math.Round((V0[3 * i + 2] - ctr[2]) / q));
                if (!map.TryGetValue(key, out int p))
                {
                    p = map.Count;
                    map[key] = p;
                    for (int c = 0; c < 3; c++) posList.Add(V0[3 * i + c] - ctr[c]);
                }
                pid[i] = p;
            }
        }
        var P = posList.ToArray();
        int np = P.Length / 3;

        // triangles (wedge indices), dropping ones that are degenerate after welding
        var tl = new List<int>(T0.Length);
        for (int t = 0; t < T0.Length / 3; t++)
        {
            int a = T0[3 * t], b = T0[3 * t + 1], c = T0[3 * t + 2];
            if (pid[a] != pid[b] && pid[b] != pid[c] && pid[a] != pid[c]) { tl.Add(a); tl.Add(b); tl.Add(c); }
        }
        var TW = tl.ToArray();
        int m = TW.Length / 3;
        if (m <= targets[^1] || m < 2)
            return targets.Select(_ => Snapshot0(V0, T0, attrs, k)).ToList();

        // ---------------------------------------------------------------- quadrics
        var Qg = new double[np * 10];
        var areaP = new double[np];
        var triN = new double[m * 3];
        int AQn = 10 + 1 + 4 * k;                      // combined quadric, area, per channel (grad sum xyz, offset sum)
        var AQ = new double[k > 0 ? n * AQn : 0];
        var scale = new double[k];
        for (int j = 0; j < k; j++) scale[j] = diag * (opt.AttrWeights != null && j < opt.AttrWeights.Length ? opt.AttrWeights[j] : 0.5);
        var A = attrs == null ? null : (double[])attrs.Clone();

        for (int t = 0; t < m; t++)
        {
            int w0 = TW[3 * t], w1 = TW[3 * t + 1], w2 = TW[3 * t + 2];
            int p0 = pid[w0], p1 = pid[w1], p2 = pid[w2];
            double e1x = P[3 * p1] - P[3 * p0], e1y = P[3 * p1 + 1] - P[3 * p0 + 1], e1z = P[3 * p1 + 2] - P[3 * p0 + 2];
            double e2x = P[3 * p2] - P[3 * p0], e2y = P[3 * p2 + 1] - P[3 * p0 + 1], e2z = P[3 * p2 + 2] - P[3 * p0 + 2];
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            double nn = nx * nx + ny * ny + nz * nz;
            double ln = Math.Sqrt(nn);
            if (ln == 0) continue;
            double area = 0.5 * ln;
            double ux = nx / ln, uy = ny / ln, uz = nz / ln;
            triN[3 * t] = ux; triN[3 * t + 1] = uy; triN[3 * t + 2] = uz;
            double d = -(ux * P[3 * p0] + uy * P[3 * p0 + 1] + uz * P[3 * p0 + 2]);
            foreach (int p in new[] { p0, p1, p2 }) { AddPlane(Qg, 10 * p, ux, uy, uz, d, area); areaP[p] += area; }
            if (k == 0) continue;
            // attribute gradients: a(x) = g.x + d over the triangle plane
            // g = ((a1-a0) (e2 x n) + (a2-a0) (n x e1)) / |n|^2
            double ax = e2y * nz - e2z * ny, ay = e2z * nx - e2x * nz, az = e2x * ny - e2y * nx;   // e2 x n
            double bx = ny * e1z - nz * e1y, by = nz * e1x - nx * e1z, bz = nx * e1y - ny * e1x;   // n x e1
            foreach (int w in new[] { w0, w1, w2 })
            {
                int o = w * AQn;
                AQ[o + 10] += area;
                for (int j = 0; j < k; j++)
                {
                    double s = scale[j];
                    double a0 = A![w0 * k + j] * s, a1 = A[w1 * k + j] * s, a2 = A[w2 * k + j] * s;
                    double gx = ((a1 - a0) * ax + (a2 - a0) * bx) / nn, gy = ((a1 - a0) * ay + (a2 - a0) * by) / nn,
                           gz = ((a1 - a0) * az + (a2 - a0) * bz) / nn;
                    // the wedge's own value on this triangle (wedges at one corner may differ: seams)
                    double aw = A[w * k + j] * s;
                    double gd = aw - (gx * P[3 * pid[w]] + gy * P[3 * pid[w] + 1] + gz * P[3 * pid[w] + 2]);
                    AddPlane(AQ, o, gx, gy, gz, gd, area);
                    AQ[o + 11 + 4 * j] += area * gx; AQ[o + 12 + 4 * j] += area * gy; AQ[o + 13 + 4 * j] += area * gz;
                    AQ[o + 14 + 4 * j] += area * gd;
                }
            }
        }

        // ---------------------------------------------------------------- edges: borders, seams, non-manifold
        var lockedP = new bool[np];
        if (opt.Lock != null) for (int i = 0; i < n; i++) if (opt.Lock[i]) lockedP[pid[i]] = true;
        var borderP = new bool[np];
        var borderNbr = new Dictionary<int, List<int>>();
        {
            var edges = new Dictionary<long, (int count, int t, int wa, int wb, bool seam)>();
            for (int t = 0; t < m; t++)
                for (int s = 0; s < 3; s++)
                {
                    int wi = TW[3 * t + s], wj = TW[3 * t + (s + 1) % 3];
                    int pi = pid[wi], pj = pid[wj];
                    if (pi > pj) { (pi, pj) = (pj, pi); (wi, wj) = (wj, wi); }
                    long key = (long)pi * np + pj;
                    if (edges.TryGetValue(key, out var e))
                        edges[key] = (e.count + 1, e.t, e.wa, e.wb, e.seam || e.wa != wi || e.wb != wj);
                    else edges[key] = (1, t, wi, wj, false);
                }
            foreach (var (key, e) in edges)
            {
                int pa = (int)(key / np), pb = (int)(key % np);
                if (e.count > 2) { lockedP[pa] = lockedP[pb] = true; continue; }
                bool border = e.count == 1;
                if (!border && !(e.seam && k > 0)) continue;
                if (border)
                {
                    borderP[pa] = borderP[pb] = true;
                    (borderNbr.TryGetValue(pa, out var la) ? la : borderNbr[pa] = new List<int>(2)).Add(pb);
                    (borderNbr.TryGetValue(pb, out var lb) ? lb : borderNbr[pb] = new List<int>(2)).Add(pa);
                    if (opt.Borders != BorderMode.Slide) continue;   // border vertices do not move: no planes needed
                }
                // constraint plane through the edge, perpendicular to its triangle: keeps the curve in place
                double ex = P[3 * pb] - P[3 * pa], ey = P[3 * pb + 1] - P[3 * pa + 1], ez = P[3 * pb + 2] - P[3 * pa + 2];
                double elen2 = ex * ex + ey * ey + ez * ez;
                double cx = ey * triN[3 * e.t + 2] - ez * triN[3 * e.t + 1], cy = ez * triN[3 * e.t] - ex * triN[3 * e.t + 2],
                       cz = ex * triN[3 * e.t + 1] - ey * triN[3 * e.t];
                double cl = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (cl == 0) continue;
                cx /= cl; cy /= cl; cz /= cl;
                double d = -(cx * P[3 * pa] + cy * P[3 * pa + 1] + cz * P[3 * pa + 2]);
                AddPlane(Qg, 10 * pa, cx, cy, cz, d, opt.BorderWeight * elen2);
                AddPlane(Qg, 10 * pb, cx, cy, cz, d, opt.BorderWeight * elen2);
            }
        }

        // ---------------------------------------------------------------- border corners and runs
        if (borderNbr.Count > 0)
        {
            bool IsCorner(int p)
            {
                var l = borderNbr[p];
                if (l.Count != 2) return true;
                double ux = P[3 * p] - P[3 * l[0]], uy = P[3 * p + 1] - P[3 * l[0] + 1], uz = P[3 * p + 2] - P[3 * l[0] + 2];
                double vx = P[3 * l[1]] - P[3 * p], vy = P[3 * l[1] + 1] - P[3 * p + 1], vz = P[3 * l[1] + 2] - P[3 * p + 2];
                double lu = Math.Sqrt(ux * ux + uy * uy + uz * uz), lv = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                return lu == 0 || lv == 0 || (ux * vx + uy * vy + uz * vz) < 0.94 * lu * lv;   // turns by more than ~20 degrees
            }
            var corner = new HashSet<int>(borderNbr.Keys.Where(IsCorner));
            if (opt.Borders == BorderMode.Lock) foreach (int p in borderNbr.Keys) lockedP[p] = true;
            else foreach (int p in corner) lockedP[p] = true;
            if (opt.Borders == BorderMode.Match)
            {
                // runs between corners (closed loops without a corner start at their smallest position)
                var done = new HashSet<long>();
                long EK(int x, int y) => (long)Math.Min(x, y) * np + Math.Max(x, y);
                var runs = new List<List<int>>();
                void Walk(int start, int next)
                {
                    var run = new List<int> { start };
                    int prev = start, cur = next;
                    while (done.Add(EK(prev, cur)))
                    {
                        run.Add(cur);
                        if (corner.Contains(cur) || cur == start) break;
                        int nx = borderNbr[cur][0] == prev ? borderNbr[cur][1] : borderNbr[cur][0];
                        prev = cur; cur = nx;
                    }
                    if (run.Count > 2) runs.Add(run);
                }
                foreach (int c in corner.OrderBy(x => x)) foreach (int nb in borderNbr[c]) if (!done.Contains(EK(c, nb))) Walk(c, nb);
                foreach (int p in borderNbr.Keys.OrderBy(x => x))
                    if (!corner.Contains(p)) foreach (int nb in borderNbr[p]) if (!done.Contains(EK(p, nb))) Walk(p, nb);
                foreach (var run in runs)
                {
                    double len = 0;
                    for (int i = 1; i < run.Count; i++) len += Math.Sqrt(Dist2(P[3 * run[i - 1]], P[3 * run[i - 1] + 1], P[3 * run[i - 1] + 2], P[3 * run[i]], P[3 * run[i] + 1], P[3 * run[i] + 2]));
                    double tol = opt.BorderTolerance ?? opt.MaxError ?? 0.002 * len;
                    var keep = new bool[run.Count];
                    keep[0] = keep[^1] = true;
                    if (run[0] == run[^1] && run.Count > 3)   // closed loop: also keep the point farthest from the start
                    {
                        int far = 1;
                        for (int i = 2; i < run.Count - 1; i++)
                            if (Dist2(P[3 * run[i]], P[3 * run[i] + 1], P[3 * run[i] + 2], P[3 * run[0]], P[3 * run[0] + 1], P[3 * run[0] + 2]) >
                                Dist2(P[3 * run[far]], P[3 * run[far] + 1], P[3 * run[far] + 2], P[3 * run[0]], P[3 * run[0] + 1], P[3 * run[0] + 2])) far = i;
                        keep[far] = true;
                        DouglasPeucker(P, run, 0, far, tol, keep);
                        DouglasPeucker(P, run, far, run.Count - 1, tol, keep);
                    }
                    else DouglasPeucker(P, run, 0, run.Count - 1, tol, keep);
                    for (int i = 0; i < run.Count; i++) if (keep[i]) lockedP[run[i]] = true;
                }
            }
        }

        // ---------------------------------------------------------------- adjacency
        var pTris = new List<int>[np];
        for (int p = 0; p < np; p++) pTris[p] = new List<int>(6);
        for (int t = 0; t < m; t++) for (int s = 0; s < 3; s++) pTris[pid[TW[3 * t + s]]].Add(t);
        var pWedges = new List<int>[np];
        for (int p = 0; p < np; p++) pWedges[p] = new List<int>(1);
        {
            var used = new bool[n];
            foreach (int w in TW) used[w] = true;
            for (int w = 0; w < n; w++) if (used[w]) pWedges[pid[w]].Add(w);
        }
        // original positions absorbed by each position: the true error is measured on them
        var P0 = (double[])P.Clone();
        var pid0 = (int[])pid.Clone();
        var T0p = new int[3 * m];
        for (int i = 0; i < 3 * m; i++) T0p[i] = pid[TW[i]];
        var pts = new List<int>[np];
        for (int p = 0; p < np; p++) pts[p] = new List<int>(1) { p };
        var aliveT = new bool[m];
        Array.Fill(aliveT, true);
        var aliveP = new bool[np];
        Array.Fill(aliveP, true);
        var version = new int[np];
        int live = m;

        // ---------------------------------------------------------------- virtual edges between separate parts
        var virt = new List<int>?[np];
        var forward = new int[np];
        for (int p = 0; p < np; p++) forward[p] = p;
        if (opt.MergeParts && opt.PartGap > 0) AddVirtualEdges(P, np, TW, pid, m, diag * opt.PartGap, virt);
        int Resolve(int p) { while (forward[p] != p) p = forward[p] = forward[forward[p]]; return p; }

        // ---------------------------------------------------------------- collapse evaluation
        var qtmp = new double[10];
        var ringBuf = new List<double>(64);
        var mapB = new List<int>(4);
        var mapA = new List<int>(4);
        var shared = new List<int>(4);

        // wedges of b mapped onto wedges of a through the triangles that contain both positions (seams must line up)
        bool Mapping(int a, int b)
        {
            shared.Clear(); mapB.Clear(); mapA.Clear();
            foreach (int t in pTris[b])
            {
                int wa = -1, wb = -1;
                for (int s = 0; s < 3; s++)
                {
                    int w = TW[3 * t + s];
                    if (pid[w] == a) wa = w; else if (pid[w] == b) wb = w;
                }
                if (wa < 0) continue;
                shared.Add(t);
                int idx = mapB.IndexOf(wb);
                if (idx >= 0) { if (mapA[idx] != wa) return false; continue; }
                if (mapA.Contains(wa)) return false;           // two wedges of b onto one of a: a seam would end
                mapB.Add(wb); mapA.Add(wa);
            }
            if (shared.Count == 0) return true;               // virtual edge: positions merge, wedges stay apart
            foreach (int w in pWedges[b]) if (!mapB.Contains(w)) return false;   // a seam leaves b elsewhere
            return true;
        }

        void AddWedge(double[] dst, int w, int w2)
        {
            int o = w * AQn, o2 = w2 * AQn;
            double W = AQ[o + 10] + (w2 >= 0 ? AQ[o2 + 10] : 0);
            for (int i = 0; i < 10; i++) dst[i] += AQ[o + i] + (w2 >= 0 ? AQ[o2 + i] : 0);
            if (W <= 0) return;
            for (int j = 0; j < k; j++)
            {
                double gx = AQ[o + 11 + 4 * j], gy = AQ[o + 12 + 4 * j], gz = AQ[o + 13 + 4 * j], sd = AQ[o + 14 + 4 * j];
                if (w2 >= 0) { gx += AQ[o2 + 11 + 4 * j]; gy += AQ[o2 + 12 + 4 * j]; gz += AQ[o2 + 13 + 4 * j]; sd += AQ[o2 + 14 + 4 * j]; }
                dst[0] -= gx * gx / W; dst[1] -= gx * gy / W; dst[2] -= gx * gz / W; dst[3] -= gx * sd / W;
                dst[4] -= gy * gy / W; dst[5] -= gy * gz / W; dst[6] -= gy * sd / W;
                dst[7] -= gz * gz / W; dst[8] -= gz * sd / W; dst[9] -= sd * sd / W;
            }
        }

        // collapse b into a; null when not allowed
        Cand? Evaluate(int a, int b)
        {
            if (lockedP[b]) return null;
            if (!Mapping(a, b)) return null;
            bool isVirtual = shared.Count == 0;
            // a border vertex only moves along its own border (edge with a single triangle)
            if (borderP[b] && (isVirtual ? opt.Borders != BorderMode.Slide : shared.Count != 1)) return null;
            for (int i = 0; i < 10; i++) qtmp[i] = Qg[10 * a + i] + Qg[10 * b + i];
            if (k > 0)
            {
                for (int i = 0; i < mapB.Count; i++) AddWedge(qtmp, mapA[i], mapB[i]);
                foreach (int w in pWedges[a]) if (!mapA.Contains(w)) AddWedge(qtmp, w, -1);
                if (isVirtual) foreach (int w in pWedges[b]) AddWedge(qtmp, w, -1);
            }
            double pax = P[3 * a], pay = P[3 * a + 1], paz = P[3 * a + 2];
            double pbx = P[3 * b], pby = P[3 * b + 1], pbz = P[3 * b + 2];
            Span<(double x, double y, double z)> cands = stackalloc (double, double, double)[4];
            int nc = 0;
            cands[nc++] = (pax, pay, paz);
            if (!lockedP[a] && !(borderP[a] && opt.Borders != BorderMode.Slide))
            {
                cands[nc++] = (pbx, pby, pbz);
                cands[nc++] = ((pax + pbx) * .5, (pay + pby) * .5, (paz + pbz) * .5);
                var o = Optimum(qtmp);
                if (o != null)
                {
                    var (ox, oy, oz) = o.Value;
                    double dx = pbx - pax, dy = pby - pay, dz = pbz - paz;
                    double dd = dx * dx + dy * dy + dz * dz;
                    double tt = dd <= 0 ? 0.5 : ((ox - pax) * dx + (oy - pay) * dy + (oz - paz) * dz) / dd;
                    double off2 = Dist2(ox, oy, oz, pax + tt * dx, pay + tt * dy, paz + tt * dz);
                    if (tt >= -0.5 && tt <= 1.5 && off2 <= dd) cands[nc++] = (ox, oy, oz);
                }
            }
            // cost = quadric error + tracked true distance; the flip check runs on the cheapest candidates first
            Span<double> costs = stackalloc double[4];
            Span<double> dists = stackalloc double[4];
            double areaSum = areaP[a] + areaP[b];
            for (int ci = 0; ci < nc; ci++)
            {
                var (x, y, z) = cands[ci];
                // the true distance is expensive: in the cost only when weighted, otherwise measured lazily below
                double d = opt.DistanceWeight > 0 ? TrueDistance(a, b, x, y, z) : -1;
                dists[ci] = d;
                costs[ci] = Math.Max(0.0, Eval(qtmp, x, y, z)) + (d > 0 ? opt.DistanceWeight * areaSum * d * d : 0);
            }
            for (int round = 0; round < nc; round++)
            {
                int ci = -1;
                for (int i = 0; i < nc; i++) if (costs[i] >= 0 && (ci < 0 || costs[i] < costs[ci])) ci = i;
                double cost = costs[ci];
                costs[ci] = -1;
                var (x, y, z) = cands[ci];
                if (maxErr != null && (dists[ci] >= 0 ? dists[ci] : TrueDistance(a, b, x, y, z)) > maxErr.Value) continue;
                var chk = CheckTriangles(a, b, x, y, z);
                if (chk < 0) continue;                       // flips a triangle
                return new Cand { Cost = cost, X = x, Y = y, Z = z, Sliver = chk == 0 };
            }
            return null;
        }

        // keep only the absorbed points farthest from the current surface around p: only they can break the limit
        void KeepFarthest(int p, int keep)
        {
            ringBuf.Clear();
            foreach (int t in pTris[p])
                for (int s = 0; s < 3; s++) { int q = pid[TW[3 * t + s]]; ringBuf.Add(P[3 * q]); ringBuf.Add(P[3 * q + 1]); ringBuf.Add(P[3 * q + 2]); }
            var scored = new List<(double d, int q)>(pts[p].Count);
            foreach (int q in pts[p])
            {
                double best = double.PositiveInfinity;
                for (int i = 0; i < ringBuf.Count; i += 9)
                    best = Math.Min(best, PointTri2(P0[3 * q], P0[3 * q + 1], P0[3 * q + 2], ringBuf[i], ringBuf[i + 1], ringBuf[i + 2],
                        ringBuf[i + 3], ringBuf[i + 4], ringBuf[i + 5], ringBuf[i + 6], ringBuf[i + 7], ringBuf[i + 8]));
                scored.Add((best, q));
            }
            pts[p] = scored.OrderByDescending(x => x.d).Take(keep).Select(x => x.q).ToList();
        }

        // largest distance of the original points absorbed by a and b to the triangles around them after the collapse
        double TrueDistance(int a, int b, double x, double y, double z)
        {
            ringBuf.Clear();
            for (int vi = 0; vi < 2; vi++)
                foreach (int t in pTris[vi == 0 ? a : b])
                {
                    if (vi == 1 && HasPos(t, a)) continue;   // removed (or already listed from a)
                    if (vi == 0 && HasPos(t, b)) continue;
                    for (int s = 0; s < 3; s++)
                    {
                        int p = pid[TW[3 * t + s]];
                        if (p == a || p == b) { ringBuf.Add(x); ringBuf.Add(y); ringBuf.Add(z); }
                        else { ringBuf.Add(P[3 * p]); ringBuf.Add(P[3 * p + 1]); ringBuf.Add(P[3 * p + 2]); }
                    }
                }
            double worst = 0;
            for (int vi = 0; vi < 2; vi++)
                foreach (int q in pts[vi == 0 ? a : b])
                {
                    double qx = P0[3 * q], qy = P0[3 * q + 1], qz = P0[3 * q + 2];
                    double best = Dist2(qx, qy, qz, x, y, z);
                    for (int i = 0; i < ringBuf.Count && best > worst; i += 9)
                        best = Math.Min(best, PointTri2(qx, qy, qz, ringBuf[i], ringBuf[i + 1], ringBuf[i + 2],
                            ringBuf[i + 3], ringBuf[i + 4], ringBuf[i + 5], ringBuf[i + 6], ringBuf[i + 7], ringBuf[i + 8]));
                    if (best > worst) worst = best;
                }
            return Math.Sqrt(worst);
        }

        bool HasPos(int t, int p) => pid[TW[3 * t]] == p || pid[TW[3 * t + 1]] == p || pid[TW[3 * t + 2]] == p;

        // -1: a triangle flips or degenerates, 0: creates a sliver, 1: fine
        int CheckTriangles(int a, int b, double x, double y, double z)
        {
            int result = 1;
            Span<double> c = stackalloc double[9], cn = stackalloc double[9];
            for (int vi = 0; vi < 2; vi++)
                foreach (int t in pTris[vi == 0 ? a : b])
                {
                    int v = vi == 0 ? a : b;
                    if (HasPos(t, vi == 0 ? b : a)) continue;   // removed by the collapse
                    for (int s = 0; s < 3; s++)
                    {
                        int p = pid[TW[3 * t + s]];
                        for (int j = 0; j < 3; j++) c[3 * s + j] = cn[3 * s + j] = P[3 * p + j];
                        if (p == v) { cn[3 * s] = x; cn[3 * s + 1] = y; cn[3 * s + 2] = z; }
                    }
                    var (ox, oy, oz) = Normal(c);
                    var (nx, ny, nz) = Normal(cn);
                    double lo2 = Math.Sqrt(ox * ox + oy * oy + oz * oz), ln2 = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (lo2 == 0) continue;
                    if (ln2 == 0 || ox * nx + oy * ny + oz * nz < 0.2 * lo2 * ln2) return -1;
                    double qn = Quality(cn, ln2), qo = Quality(c, lo2);
                    if (qn < 0.15 && qn < qo) result = 0;
                }
            return result;
        }

        // ---------------------------------------------------------------- queue
        var heap = new PriorityQueue<(int a, int b, int va, int vb, bool deferred), double>();
        double sliverPenalty = Math.Pow(diag * 1e-4, 2) * (areaP.Sum() / Math.Max(np, 1));

        void Push(int x, int y, bool deferred = false)
        {
            var c1 = Evaluate(x, y);
            var c2 = Evaluate(y, x);
            int a = x, b = y;
            Cand? c = c1;
            if (c2 != null && (c1 == null || c2.Value.Cost < c1.Value.Cost)) { c = c2; a = y; b = x; }
            if (c == null) return;
            heap.Enqueue((a, b, version[a], version[b], deferred), c.Value.Cost);
        }

        for (int t = 0; t < m; t++)
            for (int s = 0; s < 3; s++)
            {
                int pa = pid[TW[3 * t + s]], pb = pid[TW[3 * t + (s + 1) % 3]];
                if (!EdgeSeenBefore(t, s)) Push(pa, pb);
            }
        for (int p = 0; p < np; p++)
            if (virt[p] != null) foreach (int q in virt[p]!) if (p < q) Push(p, q);

        // only the first triangle that contains an edge queues it
        bool EdgeSeenBefore(int t, int s)
        {
            int pa = pid[TW[3 * t + s]], pb = pid[TW[3 * t + (s + 1) % 3]];
            foreach (int t2 in pTris[pa])
            {
                if (t2 >= t) break;
                if (HasPos(t2, pb)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- collapse
        Simplify.Result Snapshot()
        {
            var keep = new List<int>();
            var used = new SortedSet<int>();
            for (int t = 0; t < m; t++)
                if (aliveT[t]) for (int s = 0; s < 3; s++) { keep.Add(TW[3 * t + s]); used.Add(TW[3 * t + s]); }
            var u = used.ToArray();
            var remap = new int[n];
            for (int i = 0; i < u.Length; i++) remap[u[i]] = i;
            var V2 = new double[u.Length * 3];
            var Pout = opt.Refit ? Refit() : P;
            for (int i = 0; i < u.Length; i++)
            {
                int w = u[i], p = pid[w], q = pid0[w];
                bool unmoved = Pout[3 * p] == P0[3 * q] && Pout[3 * p + 1] == P0[3 * q + 1] && Pout[3 * p + 2] == P0[3 * q + 2];
                // unmoved vertices keep their exact input coordinates (bit-identical borders between tiles)
                for (int c = 0; c < 3; c++) V2[3 * i + c] = unmoved ? V0[3 * w + c] : Pout[3 * p + c] + ctr[c];
            }
            double[]? A2 = null;
            if (A != null)
            {
                A2 = new double[u.Length * k];
                for (int i = 0; i < u.Length; i++) Array.Copy(A, u[i] * k, A2, i * k, k);
            }
            return new Simplify.Result(V2, keep.Select(w => remap[w]).ToArray(), A2, k, u);
        }

        // ---------------------------------------------------------------- refit
        // Samples of the original surface (vertices and triangle centroids) are projected onto the result, then the
        // vertex positions are solved by least squares (Jacobi). Kept only when the error goes down and nothing flips.
        double[] Refit()
        {
            // samples: original positions (with their vertex normal) and triangle centroids (with the face normal)
            var vn = new double[3 * np];
            var fnrm = new double[3 * m];
            for (int t = 0; t < m; t++)
            {
                int a0 = T0p[3 * t], b0 = T0p[3 * t + 1], c0 = T0p[3 * t + 2];
                double ux = P0[3 * b0] - P0[3 * a0], uy = P0[3 * b0 + 1] - P0[3 * a0 + 1], uz = P0[3 * b0 + 2] - P0[3 * a0 + 2];
                double wx = P0[3 * c0] - P0[3 * a0], wy = P0[3 * c0 + 1] - P0[3 * a0 + 1], wz = P0[3 * c0 + 2] - P0[3 * a0 + 2];
                fnrm[3 * t] = uy * wz - uz * wy; fnrm[3 * t + 1] = uz * wx - ux * wz; fnrm[3 * t + 2] = ux * wy - uy * wx;
                foreach (int q in new[] { a0, b0, c0 }) for (int k2 = 0; k2 < 3; k2++) vn[3 * q + k2] += fnrm[3 * t + k2];
            }
            var S = new List<(double x, double y, double z, double nx, double ny, double nz)>(np + m);
            for (int q = 0; q < np; q++) S.Add((P0[3 * q], P0[3 * q + 1], P0[3 * q + 2], vn[3 * q], vn[3 * q + 1], vn[3 * q + 2]));
            for (int t = 0; t < m; t++)
            {
                int a0 = T0p[3 * t], b0 = T0p[3 * t + 1], c0 = T0p[3 * t + 2];
                S.Add(((P0[3 * a0] + P0[3 * b0] + P0[3 * c0]) / 3, (P0[3 * a0 + 1] + P0[3 * b0 + 1] + P0[3 * c0 + 1]) / 3,
                       (P0[3 * a0 + 2] + P0[3 * b0 + 2] + P0[3 * c0 + 2]) / 3, fnrm[3 * t], fnrm[3 * t + 1], fnrm[3 * t + 2]));
            }
            var liveT = Enumerable.Range(0, m).Where(t => aliveT[t]).ToArray();
            var Tcur = new int[3 * liveT.Length];
            for (int i2 = 0; i2 < liveT.Length; i2++) for (int s2 = 0; s2 < 3; s2++) Tcur[3 * i2 + s2] = pid[TW[3 * liveT[i2] + s2]];
            double reach = diag * 0.05;
            var cur = (double[])P.Clone();
            var proj = new (int t, double b0, double b1, double b2)[S.Count];
            // closest point on the current result (spatial index; samples prefer faces with the same orientation)
            (double mean, double max) Project(double[] X)
            {
                var idx = new TriIndex(X, Tcur);
                double sum = 0, mx = 0;
                int cnt = 0;
                for (int i2 = 0; i2 < S.Count; i2++)
                {
                    var (x, y, z, nx, ny, nz) = S[i2];
                    var h = idx.Closest(x, y, z, reach, nx, ny, nz, 0.0);
                    if (h == null) { proj[i2] = (-1, 0, 0, 0); continue; }
                    proj[i2] = (liveT[h.Value.Tri], h.Value.U, h.Value.V, h.Value.W);
                    double d = Math.Sqrt(h.Value.D2);
                    sum += d; cnt++;
                    if (d > mx) mx = d;
                }
                return (sum / Math.Max(cnt, 1), mx);
            }
            var start = Project(cur);
            var normals0 = new double[3 * m];
            for (int t = 0; t < m; t++) if (aliveT[t]) TriNormalInto(cur, t, normals0);
            var num = new double[3 * np];
            var den = new double[np];
            for (int outer = 0; outer < 4; outer++)
            {
                for (int sweep = 0; sweep < 10; sweep++)
                {
                    Array.Clear(num); Array.Clear(den);
                    for (int i = 0; i < S.Count; i++)
                    {
                        var (t, b0, b1, b2) = proj[i];
                        if (t < 0) continue;
                        int pa = pid[TW[3 * t]], pb = pid[TW[3 * t + 1]], pc = pid[TW[3 * t + 2]];
                        for (int c = 0; c < 3; c++)
                        {
                            double target = c == 0 ? S[i].x : c == 1 ? S[i].y : S[i].z;
                            double r = b0 * cur[3 * pa + c] + b1 * cur[3 * pb + c] + b2 * cur[3 * pc + c] - target;
                            num[3 * pa + c] += b0 * r; num[3 * pb + c] += b1 * r; num[3 * pc + c] += b2 * r;
                        }
                        den[pa] += b0 * b0; den[pb] += b1 * b1; den[pc] += b2 * b2;
                    }
                    for (int p = 0; p < np; p++)
                    {
                        if (!aliveP[p] || lockedP[p] || borderP[p] || den[p] < 1e-12) continue;
                        for (int c = 0; c < 3; c++) cur[3 * p + c] -= 0.5 * num[3 * p + c] / den[p];
                    }
                }
                Project(cur);
            }
            // reject flips (halve the move until none are left)
            var nn = new double[3];
            for (int tries = 0; tries < 4; tries++)
            {
                bool flip = false;
                for (int t = 0; t < m && !flip; t++)
                {
                    if (!aliveT[t]) continue;
                    TriNormalInto(cur, t, nn, 0);
                    double ox = normals0[3 * t], oy = normals0[3 * t + 1], oz = normals0[3 * t + 2];
                    double lo2 = Math.Sqrt(ox * ox + oy * oy + oz * oz), ln2 = Math.Sqrt(nn[0] * nn[0] + nn[1] * nn[1] + nn[2] * nn[2]);
                    if (lo2 > 0 && (ln2 == 0 || ox * nn[0] + oy * nn[1] + oz * nn[2] < 0.5 * lo2 * ln2)) flip = true;
                }
                if (!flip) break;
                if (tries == 3) return P;
                for (int i = 0; i < cur.Length; i++) cur[i] = (cur[i] + P[i]) / 2;
            }
            var end = Project(cur);
            return end.mean < start.mean && end.max <= start.max * 1.02 ? cur : P;
        }

        void TriNormalInto(double[] X, int t, double[] o, int at = -1)
        {
            int pa = pid[TW[3 * t]], pb = pid[TW[3 * t + 1]], pc = pid[TW[3 * t + 2]];
            double ux = X[3 * pb] - X[3 * pa], uy = X[3 * pb + 1] - X[3 * pa + 1], uz = X[3 * pb + 2] - X[3 * pa + 2];
            double vx = X[3 * pc] - X[3 * pa], vy = X[3 * pc + 1] - X[3 * pa + 1], vz = X[3 * pc + 2] - X[3 * pa + 2];
            int b = at < 0 ? 3 * t : at;
            o[b] = uy * vz - uz * vy; o[b + 1] = uz * vx - ux * vz; o[b + 2] = ux * vy - uy * vx;
        }

        var results = new List<Simplify.Result>();
        var pending = new List<int>(targets);
        while (pending.Count > 0 && live <= pending[0]) { results.Add(Snapshot()); pending.RemoveAt(0); }
        int step = 0;
        int reportEvery = Math.Max(1000, (m - (pending.Count > 0 ? pending[^1] : 0)) / 20);
        while (heap.Count > 0 && pending.Count > 0)
        {
            var e = heap.Dequeue();
            int a = e.a, b = e.b;
            if (!aliveP[a] || !aliveP[b] || version[a] != e.va || version[b] != e.vb) continue;
            var c = Evaluate(a, b);
            if (c == null) continue;
            if (c.Value.Sliver && !e.deferred)
            {
                heap.Enqueue((a, b, e.va, e.vb, true), c.Value.Cost * 8 + sliverPenalty);
                continue;
            }
            // positions and quadrics
            P[3 * a] = c.Value.X; P[3 * a + 1] = c.Value.Y; P[3 * a + 2] = c.Value.Z;
            for (int i = 0; i < 10; i++) Qg[10 * a + i] += Qg[10 * b + i];
            areaP[a] += areaP[b];
            pts[a].AddRange(pts[b]);
            pts[b] = new List<int>(0);
            lockedP[a] |= lockedP[b];
            borderP[a] |= borderP[b];
            // wedges: mapped ones merge, the rest move over with the position
            var wmap = new Dictionary<int, int>();
            for (int i = 0; i < mapB.Count; i++)
            {
                int wb = mapB[i], wa = mapA[i];
                wmap[wb] = wa;
                if (k > 0) for (int j = 0; j < AQn; j++) AQ[wa * AQn + j] += AQ[wb * AQn + j];
            }
            foreach (int w in pWedges[b])
                if (!wmap.ContainsKey(w)) { pid[w] = a; pWedges[a].Add(w); }
            foreach (int t in pTris[b])
            {
                if (!aliveT[t]) continue;
                if (shared.Contains(t))
                {
                    aliveT[t] = false;
                    live--;
                    for (int s = 0; s < 3; s++) { int p = pid[TW[3 * t + s]]; if (p != b) pTris[p].Remove(t); }
                    continue;
                }
                for (int s = 0; s < 3; s++) if (wmap.TryGetValue(TW[3 * t + s], out int wa)) TW[3 * t + s] = wa;
                pTris[a].Add(t);
            }
            pTris[b] = new List<int>(0);
            pWedges[b] = new List<int>(0);
            aliveP[b] = false;
            forward[b] = a;
            if (pts[a].Count > 48) KeepFarthest(a, 32);
            if (virt[b] != null) { virt[a] ??= new List<int>(); virt[a]!.AddRange(virt[b]!); virt[b] = null; }
            // attribute values at the new position: optimum of each wedge's attribute quadric
            if (A != null)
                foreach (int w in pWedges[a])
                {
                    int o = w * AQn;
                    double W = AQ[o + 10];
                    if (W <= 0) continue;
                    for (int j = 0; j < k; j++)
                        A[w * k + j] = (AQ[o + 11 + 4 * j] * P[3 * a] + AQ[o + 12 + 4 * j] * P[3 * a + 1] + AQ[o + 13 + 4 * j] * P[3 * a + 2]
                                        + AQ[o + 14 + 4 * j]) / W / scale[j];
                }
            version[a]++;
            var neigh = new HashSet<int>();
            foreach (int t in pTris[a]) for (int s = 0; s < 3; s++) neigh.Add(pid[TW[3 * t + s]]);
            if (virt[a] != null) foreach (int q in virt[a]!) neigh.Add(Resolve(q));
            neigh.Remove(a);
            foreach (int v in neigh) if (aliveP[v]) Push(a, v);
            step++;
            if (opt.Progress != null && step % reportEvery == 0) opt.Progress($"simplifying ... {live:N0} triangles");
            while (pending.Count > 0 && live <= pending[0]) { results.Add(Snapshot()); pending.RemoveAt(0); }
        }
        while (pending.Count > 0) { results.Add(Snapshot()); pending.RemoveAt(0); }
        return results;
    }

    internal static Simplify.Result Snapshot0(double[] V, int[] T, double[]? attrs, int k) =>
        new((double[])V.Clone(), (int[])T.Clone(), attrs == null ? null : (double[])attrs.Clone(), k, Enumerable.Range(0, V.Length / 3).ToArray());

    /// <summary>Each position gets a virtual edge to the nearest position of another part within gap (uniform grid search).</summary>
    static void AddVirtualEdges(double[] P, int np, int[] TW, int[] pid, int m, double gap, List<int>?[] virt)
    {
        // parts = connected components over triangles
        var parent = new int[np];
        for (int i = 0; i < np; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int t = 0; t < m; t++)
        {
            int a = Find(pid[TW[3 * t]]), b = Find(pid[TW[3 * t + 1]]), c = Find(pid[TW[3 * t + 2]]);
            parent[b] = a; parent[Find(c)] = Find(a);
        }
        var comp = new int[np];
        var roots = new HashSet<int>();
        for (int i = 0; i < np; i++) { comp[i] = Find(i); roots.Add(comp[i]); }
        if (roots.Count < 2) return;
        var grid = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Cell(int p) => ((long)Math.Floor(P[3 * p] / gap), (long)Math.Floor(P[3 * p + 1] / gap), (long)Math.Floor(P[3 * p + 2] / gap));
        for (int p = 0; p < np; p++)
        {
            var c = Cell(p);
            if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>(2);
            l.Add(p);
        }
        double g2 = gap * gap;
        for (int p = 0; p < np; p++)
        {
            var (cx, cy, cz) = Cell(p);
            int best = -1;
            double bd = g2;
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                        foreach (int q in l)
                        {
                            if (comp[q] == comp[p]) continue;
                            double d = Dist2(P[3 * p], P[3 * p + 1], P[3 * p + 2], P[3 * q], P[3 * q + 1], P[3 * q + 2]);
                            if (d < bd) { bd = d; best = q; }
                        }
                    }
            if (best < 0) continue;
            (virt[p] ??= new List<int>(1)).Add(best);
            (virt[best] ??= new List<int>(1)).Add(p);
        }
    }

    // ---------------------------------------------------------------- quadric helpers (layout: xx xy xz x yy yz y zz z c)

    static void AddPlane(double[] q, int o, double a, double b, double c, double d, double w)
    {
        q[o] += a * a * w; q[o + 1] += a * b * w; q[o + 2] += a * c * w; q[o + 3] += a * d * w; q[o + 4] += b * b * w;
        q[o + 5] += b * c * w; q[o + 6] += b * d * w; q[o + 7] += c * c * w; q[o + 8] += c * d * w; q[o + 9] += d * d * w;
    }

    static double Eval(double[] q, double x, double y, double z) =>
        q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x + q[4] * y * y
        + 2 * q[5] * y * z + 2 * q[6] * y + q[7] * z * z + 2 * q[8] * z + q[9];

    static (double x, double y, double z)? Optimum(double[] q)
    {
        double a = q[0], b = q[1], c = q[2], d = q[3], e = q[4], f = q[5], g = q[6], h = q[7], i = q[8];
        double det = a * (e * h - f * f) - b * (b * h - f * c) + c * (b * f - e * c);
        double sc = Math.Abs(a) + Math.Abs(e) + Math.Abs(h);
        if (sc <= 0 || Math.Abs(det) < 1e-10 * sc * sc * sc) return null;
        double rx = -d, ry = -g, rz = -i;
        double x = (rx * (e * h - f * f) - b * (ry * h - f * rz) + c * (ry * f - e * rz)) / det;
        double y = (a * (ry * h - f * rz) - rx * (b * h - f * c) + c * (b * rz - ry * c)) / det;
        double z = (a * (e * rz - ry * f) - b * (b * rz - ry * c) + rx * (b * f - e * c)) / det;
        return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) ? (x, y, z) : null;
    }

    /// <summary>Squared distance from point p to triangle abc (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    static double PointTri2(double px, double py, double pz, double ax, double ay, double az,
        double bx, double by, double bz, double cx, double cy, double cz)
    {
        double abx = bx - ax, aby = by - ay, abz = bz - az, acx = cx - ax, acy = cy - ay, acz = cz - az;
        double apx = px - ax, apy = py - ay, apz = pz - az;
        double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        if (d1 <= 0 && d2 <= 0) return Dist2(px, py, pz, ax, ay, az);
        double bpx = px - bx, bpy = py - by, bpz = pz - bz;
        double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
        if (d3 >= 0 && d4 <= d3) return Dist2(px, py, pz, bx, by, bz);
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            double v = d1 / (d1 - d3);
            return Dist2(px, py, pz, ax + v * abx, ay + v * aby, az + v * abz);
        }
        double cpx = px - cx, cpy = py - cy, cpz = pz - cz;
        double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
        if (d6 >= 0 && d5 <= d6) return Dist2(px, py, pz, cx, cy, cz);
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            double w = d2 / (d2 - d6);
            return Dist2(px, py, pz, ax + w * acx, ay + w * acy, az + w * acz);
        }
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
        {
            double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return Dist2(px, py, pz, bx + w * (cx - bx), by + w * (cy - by), bz + w * (cz - bz));
        }
        double den = 1.0 / (va + vb + vc);
        double v2 = vb * den, w2 = vc * den;
        return Dist2(px, py, pz, ax + abx * v2 + acx * w2, ay + aby * v2 + acy * w2, az + abz * v2 + acz * w2);
    }

    /// <summary>Marks the points of run[i0..i1] that Douglas-Peucker keeps at tolerance tol.</summary>
    static void DouglasPeucker(double[] P, List<int> run, int i0, int i1, double tol, bool[] keep)
    {
        if (i1 - i0 < 2) return;
        int a = run[i0], b = run[i1];
        double ax = P[3 * a], ay = P[3 * a + 1], az = P[3 * a + 2];
        double dx = P[3 * b] - ax, dy = P[3 * b + 1] - ay, dz = P[3 * b + 2] - az;
        double dd = dx * dx + dy * dy + dz * dz;
        int best = -1;
        double bestD = tol * tol;
        for (int i = i0 + 1; i < i1; i++)
        {
            int p = run[i];
            double px = P[3 * p] - ax, py = P[3 * p + 1] - ay, pz = P[3 * p + 2] - az;
            double t = dd > 0 ? Math.Clamp((px * dx + py * dy + pz * dz) / dd, 0, 1) : 0;
            double d = Dist2(px, py, pz, t * dx, t * dy, t * dz);
            if (d > bestD) { bestD = d; best = i; }
        }
        if (best < 0) return;
        keep[best] = true;
        DouglasPeucker(P, run, i0, best, tol, keep);
        DouglasPeucker(P, run, best, i1, tol, keep);
    }

    static double Dist2(double ax, double ay, double az, double bx, double by, double bz) =>
        (ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz);

    static (double, double, double) Normal(ReadOnlySpan<double> c)
    {
        double ux = c[3] - c[0], uy = c[4] - c[1], uz = c[5] - c[2];
        double vx = c[6] - c[0], vy = c[7] - c[1], vz = c[8] - c[2];
        return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    /// <summary>Triangle shape quality: 1 for equilateral, 0 for degenerate.</summary>
    static double Quality(ReadOnlySpan<double> c, double nlen)
    {
        double s = 0;
        for (int e = 0; e < 3; e++)
        {
            int u = e, w = (e + 1) % 3;
            s += Dist2(c[3 * u], c[3 * u + 1], c[3 * u + 2], c[3 * w], c[3 * w + 1], c[3 * w + 2]);
        }
        return s <= 0 ? 0 : 3.4641016 * nlen / s;
    }
}
