namespace SceneKit;

/// <summary>Fast top-down simplification: R-Simp (Brodsky &amp; Watson 2000, "Model Simplification Through Refinement").
/// All vertices start in one cluster; the cluster with the largest surface variation (area x normal spread, plus a small
/// size term so flat regions still refine) is split along its principal axis until there are enough clusters. Each
/// cluster becomes one vertex placed at the optimum of its faces' plane quadrics. Works on any input (soups,
/// non-manifold, many parts) in near-linear time; less accurate than Decimate, which it complements for previews and
/// very large data.</summary>
public static class RSimp
{
    public static Simplify.Result Run(double[] V, int[] T, int targetTris, double[]? attrs, int k)
    {
        int n = V.Length / 3, m0 = T.Length / 3;
        if (m0 <= targetTris)
            return new Simplify.Result((double[])V.Clone(), (int[])T.Clone(), attrs == null ? null : (double[])attrs.Clone(), k, Enumerable.Range(0, n).ToArray());

        // weld positions
        var lo = new double[3]; var hi = new double[3];
        for (int c = 0; c < 3; c++) { lo[c] = double.PositiveInfinity; hi[c] = double.NegativeInfinity; }
        for (int i = 0; i < n; i++) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], V[3 * i + c]); hi[c] = Math.Max(hi[c], V[3 * i + c]); }
        double diag = Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi[c] - lo[c]) * (hi[c] - lo[c])));
        if (diag == 0) diag = 1;
        var pid = new int[n];
        var P = new List<double>();
        {
            double q = diag * 1e-9;
            var map = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < n; i++)
            {
                var key = ((long)Math.Round(V[3 * i] / q), (long)Math.Round(V[3 * i + 1] / q), (long)Math.Round(V[3 * i + 2] / q));
                if (!map.TryGetValue(key, out int p)) { p = map.Count; map[key] = p; P.Add(V[3 * i]); P.Add(V[3 * i + 1]); P.Add(V[3 * i + 2]); }
                pid[i] = p;
            }
        }
        int np = P.Count / 3;
        // faces, plane quadrics and per-position normal / area sums
        var faces = new List<int>();
        for (int t = 0; t < m0; t++)
        {
            int a = pid[T[3 * t]], b = pid[T[3 * t + 1]], c = pid[T[3 * t + 2]];
            if (a != b && b != c && a != c) faces.Add(t);
        }
        var Q = new double[np * 10];
        var nSum = new double[np * 3];
        var aSum = new double[np];
        foreach (int t in faces)
        {
            int a = pid[T[3 * t]], b = pid[T[3 * t + 1]], c = pid[T[3 * t + 2]];
            double ux = P[3 * b] - P[3 * a], uy = P[3 * b + 1] - P[3 * a + 1], uz = P[3 * b + 2] - P[3 * a + 2];
            double vx = P[3 * c] - P[3 * a], vy = P[3 * c + 1] - P[3 * a + 1], vz = P[3 * c + 2] - P[3 * a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l == 0) continue;
            double area = l / 2;
            nx /= l; ny /= l; nz /= l;
            double d = -(nx * P[3 * a] + ny * P[3 * a + 1] + nz * P[3 * a + 2]);
            foreach (int p in new[] { a, b, c })
            {
                int o = 10 * p;
                Q[o] += nx * nx * area; Q[o + 1] += nx * ny * area; Q[o + 2] += nx * nz * area; Q[o + 3] += nx * d * area;
                Q[o + 4] += ny * ny * area; Q[o + 5] += ny * nz * area; Q[o + 6] += ny * d * area; Q[o + 7] += nz * nz * area;
                Q[o + 8] += nz * d * area; Q[o + 9] += d * d * area;
                nSum[3 * p] += nx * area / 3; nSum[3 * p + 1] += ny * area / 3; nSum[3 * p + 2] += nz * area / 3;
                aSum[p] += area / 3;
            }
        }

        double Score(int[] pos)
        {
            double A = 0, nx = 0, ny = 0, nz = 0;
            var mn = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var mx = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (int p in pos)
            {
                A += aSum[p]; nx += nSum[3 * p]; ny += nSum[3 * p + 1]; nz += nSum[3 * p + 2];
                for (int c = 0; c < 3; c++) { mn[c] = Math.Min(mn[c], P[3 * p + c]); mx[c] = Math.Max(mx[c], P[3 * p + c]); }
            }
            if (pos.Length < 2) return -1;
            double size = Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (mx[c] - mn[c]) * (mx[c] - mn[c]))) / diag;
            double spread = A > 0 ? 1 - Math.Sqrt(nx * nx + ny * ny + nz * nz) / A : 1;
            return Math.Max(A, 1e-300) * (spread + 0.05 * size) + size * 1e-12;
        }

        // split along the principal axis of the positions, at the mean
        (int[], int[])? Split(int[] pos)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (int p in pos) { cx += P[3 * p]; cy += P[3 * p + 1]; cz += P[3 * p + 2]; }
            cx /= pos.Length; cy /= pos.Length; cz /= pos.Length;
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (int p in pos)
            {
                double dx = P[3 * p] - cx, dy = P[3 * p + 1] - cy, dz = P[3 * p + 2] - cz;
                xx += dx * dx; xy += dx * dy; xz += dx * dz; yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }
            double ax = 1, ay = 0.7, az = 0.3;   // power iteration
            for (int it = 0; it < 24; it++)
            {
                double bx = xx * ax + xy * ay + xz * az, by = xy * ax + yy * ay + yz * az, bz = xz * ax + yz * ay + zz * az;
                double l = Math.Sqrt(bx * bx + by * by + bz * bz);
                if (l == 0) break;
                ax = bx / l; ay = by / l; az = bz / l;
            }
            var left = new List<int>(); var right = new List<int>();
            foreach (int p in pos)
                ((P[3 * p] - cx) * ax + (P[3 * p + 1] - cy) * ay + (P[3 * p + 2] - cz) * az <= 0 ? left : right).Add(p);
            if (left.Count == 0 || right.Count == 0) return null;
            return (left.ToArray(), right.ToArray());
        }

        var used = new bool[np];
        foreach (int t in faces) for (int s = 0; s < 3; s++) used[pid[T[3 * t + s]]] = true;
        var start = Enumerable.Range(0, np).Where(p => used[p]).ToArray();
        var heap = new PriorityQueue<int[], double>();
        var final = new List<int[]>();
        heap.Enqueue(start, -Score(start));
        int clusterCount = 1;
        int[] owner = new int[np];

        int CountTris(IEnumerable<int[]> clusters)
        {
            int ci = 0;
            foreach (var c in clusters) { foreach (int p in c) owner[p] = ci; ci++; }
            var seen = new HashSet<(int, int, int)>();
            foreach (int t in faces)
            {
                int a = owner[pid[T[3 * t]]], b = owner[pid[T[3 * t + 1]]], c = owner[pid[T[3 * t + 2]]];
                if (a == b || b == c || a == c) continue;
                var s = new[] { a, b, c }; Array.Sort(s);
                seen.Add((s[0], s[1], s[2]));
            }
            return seen.Count;
        }

        int goal = Math.Max(4, targetTris / 2);
        for (int round = 0; round < 8; round++)
        {
            while (clusterCount < goal && heap.Count > 0)
            {
                var c = heap.Dequeue();
                var sp = c.Length >= 2 ? Split(c) : null;
                if (sp == null) { final.Add(c); continue; }
                heap.Enqueue(sp.Value.Item1, -Score(sp.Value.Item1));
                heap.Enqueue(sp.Value.Item2, -Score(sp.Value.Item2));
                clusterCount++;
            }
            int tris = CountTris(final.Concat(heap.UnorderedItems.Select(x => x.Element)));
            if (tris >= targetTris * 0.95 || heap.Count == 0) break;
            goal = (int)(goal * Math.Min(2.0, Math.Max(1.05, (double)targetTris / Math.Max(tris, 1))));
        }
        var all = final.Concat(heap.UnorderedItems.Select(x => x.Element)).ToList();
        int nc = all.Count;
        for (int ci = 0; ci < nc; ci++) foreach (int p in all[ci]) owner[p] = ci;

        // representative positions: quadric optimum, kept inside the cluster's (slightly grown) box
        var rep = new double[nc * 3];
        for (int ci = 0; ci < nc; ci++)
        {
            var q = new double[10];
            double cx = 0, cy = 0, cz = 0;
            var mn = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var mx = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (int p in all[ci])
            {
                for (int j = 0; j < 10; j++) q[j] += Q[10 * p + j];
                cx += P[3 * p]; cy += P[3 * p + 1]; cz += P[3 * p + 2];
                for (int c = 0; c < 3; c++) { mn[c] = Math.Min(mn[c], P[3 * p + c]); mx[c] = Math.Max(mx[c], P[3 * p + c]); }
            }
            int cnt = all[ci].Length;
            double[] best = { cx / cnt, cy / cnt, cz / cnt };
            var o = Optimum(q);
            if (o != null)
            {
                bool inside = true;
                for (int c = 0; c < 3; c++)
                {
                    double pad = (mx[c] - mn[c]) * 0.25 + diag * 1e-9;
                    if (o[c] < mn[c] - pad || o[c] > mx[c] + pad) inside = false;
                }
                if (inside) best = o;
            }
            rep[3 * ci] = best[0]; rep[3 * ci + 1] = best[1]; rep[3 * ci + 2] = best[2];
        }

        // triangles between three different clusters (first occurrence keeps its orientation)
        var corners = new List<int>();
        {
            var seen = new HashSet<(int, int, int)>();
            foreach (int t in faces)
            {
                int a = owner[pid[T[3 * t]]], b = owner[pid[T[3 * t + 1]]], c = owner[pid[T[3 * t + 2]]];
                if (a == b || b == c || a == c) continue;
                var s = new[] { a, b, c }; Array.Sort(s);
                if (seen.Add((s[0], s[1], s[2]))) { corners.Add(T[3 * t]); corners.Add(T[3 * t + 1]); corners.Add(T[3 * t + 2]); }
            }
        }
        // one output vertex per (cluster, attribute values): UV seams and hard edges stay sharp
        var outIdx = new Dictionary<(int, string), int>();
        var Vout = new List<double>();
        var Aout = new List<double>();
        var kept = new List<int>();
        var Tout = new int[corners.Count];
        for (int i = 0; i < corners.Count; i++)
        {
            int w = corners[i], ci = owner[pid[w]];
            string akey = "";
            if (attrs != null)
            {
                var parts = new string[k];
                for (int j = 0; j < k; j++) parts[j] = Math.Round(attrs[w * k + j], 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                akey = string.Join(",", parts);
            }
            if (!outIdx.TryGetValue((ci, akey), out int oi))
            {
                oi = kept.Count;
                outIdx[(ci, akey)] = oi;
                kept.Add(w);
                Vout.Add(rep[3 * ci]); Vout.Add(rep[3 * ci + 1]); Vout.Add(rep[3 * ci + 2]);
                if (attrs != null) for (int j = 0; j < k; j++) Aout.Add(attrs[w * k + j]);
            }
            Tout[i] = oi;
        }
        return new Simplify.Result(Vout.ToArray(), Tout, attrs == null ? null : Aout.ToArray(), attrs == null ? 0 : k, kept.ToArray());
    }

    static double[]? Optimum(double[] q)
    {
        double a = q[0], b = q[1], c = q[2], d = q[3], e = q[4], f = q[5], g = q[6], h = q[7], i = q[8];
        double det = a * (e * h - f * f) - b * (b * h - f * c) + c * (b * f - e * c);
        double sc = Math.Abs(a) + Math.Abs(e) + Math.Abs(h);
        if (sc <= 0 || Math.Abs(det) < 1e-8 * sc * sc * sc) return null;
        double rx = -d, ry = -g, rz = -i;
        double x = (rx * (e * h - f * f) - b * (ry * h - f * rz) + c * (ry * f - e * rz)) / det;
        double y = (a * (ry * h - f * rz) - rx * (b * h - f * c) + c * (b * rz - ry * c)) / det;
        double z = (a * (e * rz - ry * f) - b * (b * rz - ry * c) + rx * (b * f - e * c)) / det;
        return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) ? new[] { x, y, z } : null;
    }
}
