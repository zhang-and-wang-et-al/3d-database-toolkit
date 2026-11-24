namespace SceneKit;

/// <summary>Simplification through meshoptimizer (meshopt_simplifyWithAttributes / meshopt_simplifySloppy) with the
/// same shape as <see cref="Decimate.Run"/>. Positions are recentred to the box centre and cast to float; all wedges of a
/// welded position (diag x 1e-9 grid, as Decimate) get the representative's float coordinates, so meshopt sees UV seams as
/// exactly coincident. The result vertices are a subset of the input: V and A are the original double rows (bit-identical,
/// so tile borders stay exact), Kept says which. Only call when <see cref="MeshoptNative.Available"/> is true.</summary>
public static class MeshoptBackend
{
    public sealed class Options
    {
        /// <summary>Lock and Match: border vertices stay (LockBorder; meshopt has no Douglas-Peucker thinning, so Match
        /// acts as Lock). Slide: borders may collapse along themselves. Sloppy has no LockBorder flag, so under Lock/Match the
        /// boundary vertices of the welded triangle list are added to the vertex locks.</summary>
        public BorderMode Borders = BorderMode.Match;
        /// <summary>Error limit in scene units (meshopt's own estimate, not a Hausdorff bound).</summary>
        public double? MaxError;
        /// <summary>Error limit as a share of the bounding diagonal when MaxError is not set. 0 = no limit.</summary>
        public double MaxRelativeError = 0.02;
        /// <summary>Weight per attribute channel (first 32 channels only); default 0.5.</summary>
        public double[]? AttrWeights;
        /// <summary>Input vertices that must not move.</summary>
        public bool[]? Lock;
        /// <summary>Drop disconnected parts below the error limit (meshopt Prune).</summary>
        public bool Prune;
        /// <summary>meshopt_simplifySloppy (grid clustering, relative error 1) instead of edge collapse.</summary>
        public bool Sloppy;
        /// <summary>meshopt Permissive option (collapses across attribute seams where the error allows).</summary>
        public bool Permissive;
    }

    const int MaxAttributes = 32;

    /// <summary>Simplify V (n x 3) / T (m x 3) to about each target triangle count; attrs (n x k) per input vertex.
    /// Results are in the order of targetTris sorted descending; each one is simplified from the input. resultErrors
    /// (when given) receives meshopt's error estimate per result in scene units.</summary>
    public static List<Simplify.Result> Run(double[] V0, int[] T0, IList<int> targetTris, double[]? attrs, int k,
                                           Options? opt = null, List<double>? resultErrors = null)
    {
        opt ??= new Options();
        var targets = targetTris.OrderByDescending(x => x).ToList();
        int n = V0.Length / 3;
        if (attrs == null) k = 0;

        // local frame (as Decimate.Run)
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

        // weld positions on the Decimate grid; every wedge gets its representative's float coordinates
        var pid = new int[n];
        var P = new float[3 * n];
        {
            double q = diag * 1e-9;
            var map = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < n; i++)
            {
                var key = ((long)Math.Round((V0[3 * i] - ctr[0]) / q), (long)Math.Round((V0[3 * i + 1] - ctr[1]) / q),
                           (long)Math.Round((V0[3 * i + 2] - ctr[2]) / q));
                if (!map.TryGetValue(key, out int rep)) { rep = i; map[key] = i; }
                pid[i] = rep;
                for (int c = 0; c < 3; c++) P[3 * i + c] = (float)(V0[3 * rep + c] - ctr[c]);
            }
        }

        // triangles, dropping ones that are degenerate after welding
        var tl = new List<uint>(T0.Length);
        for (int t = 0; t < T0.Length / 3; t++)
        {
            int a = T0[3 * t], b = T0[3 * t + 1], c = T0[3 * t + 2];
            if (pid[a] != pid[b] && pid[b] != pid[c] && pid[a] != pid[c]) { tl.Add((uint)a); tl.Add((uint)b); tl.Add((uint)c); }
        }
        var idx = tl.ToArray();
        int m = idx.Length / 3;

        // attributes: the first min(k, 32) channels count in the error; the rest are carried through the original rows
        int ka = Math.Min(k, MaxAttributes);
        var AF = new float[n * ka];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < ka; j++) AF[i * ka + j] = (float)attrs![i * k + j];
        var W = new float[ka];
        for (int j = 0; j < ka; j++) W[j] = (float)(opt.AttrWeights != null && j < opt.AttrWeights.Length ? opt.AttrWeights[j] : 0.5);

        // user locks, ORed over all wedges of a welded position so every wedge of a position agrees
        byte[] locks = Array.Empty<byte>();
        var lockedPos = new HashSet<int>();
        if (opt.Lock != null)
        {
            for (int i = 0; i < n && i < opt.Lock.Length; i++) if (opt.Lock[i]) lockedPos.Add(pid[i]);
            if (lockedPos.Count > 0)
            {
                locks = new byte[n];
                for (int i = 0; i < n; i++) if (lockedPos.Contains(pid[i])) locks[i] = MeshoptNative.VertexLock;
            }
        }

        // boundary positions (edges used by one triangle, on the welded positions) under Lock/Match
        HashSet<int>? border = null;
        if (opt.Borders != BorderMode.Slide && m > 0 && (opt.Sloppy || opt.Prune))
        {
            var edgeUse = new Dictionary<(int, int), int>();
            void Edge(int u, int v)
            {
                var key = u < v ? (u, v) : (v, u);
                edgeUse[key] = edgeUse.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            for (int t = 0; t < m; t++)
            {
                int a = pid[idx[3 * t]], b = pid[idx[3 * t + 1]], c = pid[idx[3 * t + 2]];
                Edge(a, b); Edge(b, c); Edge(c, a);
            }
            border = new HashSet<int>();
            foreach (var (e, use) in edgeUse) if (use == 1) { border.Add(e.Item1); border.Add(e.Item2); }
        }

        // meshopt_simplifySloppy has no LockBorder flag: lock every wedge of a boundary position
        if (opt.Sloppy && border != null)
        {
            if (locks.Length == 0) locks = new byte[n];
            for (int i = 0; i < n; i++) if (border.Contains(pid[i])) locks[i] |= MeshoptNative.VertexLock;
        }

        // meshopt's Prune ignores vertex locks and LockBorder: the positions that must survive it (used by the input)
        HashSet<int>? mustKeep = null;
        if (opt.Prune && !opt.Sloppy && (lockedPos.Count > 0 || border != null))
        {
            mustKeep = new HashSet<int>();
            foreach (uint i in idx)
            {
                int q = pid[i];
                if (lockedPos.Contains(q) || (border != null && border.Contains(q))) mustKeep.Add(q);
            }
            if (mustKeep.Count == 0) mustKeep = null;
        }

        uint options = MeshoptNative.ErrorAbsolute;
        if (opt.Borders != BorderMode.Slide) options |= MeshoptNative.LockBorder;
        if (opt.Prune) options |= MeshoptNative.Prune;
        if (opt.Permissive) options |= MeshoptNative.Permissive;
        float targetError = (float)(opt.MaxError ?? (opt.MaxRelativeError > 0 ? opt.MaxRelativeError * diag : float.MaxValue));
        float scale = opt.Sloppy && n > 0 ? MeshoptNative.SimplifyScale(P, n) : 1f;

        var results = new List<Simplify.Result>(targets.Count);
        var dst = new uint[idx.Length];
        foreach (int target in targets)
        {
            if (m <= target || m < 2)
            {
                results.Add(Decimate.Snapshot0(V0, T0, attrs, k));
                resultErrors?.Add(0);
                continue;
            }
            int targetIndexCount = Math.Clamp(3 * target, 0, idx.Length);
            int cnt;
            float err;
            if (opt.Sloppy)
            {
                cnt = MeshoptNative.SimplifySloppy(dst, idx, P, n, locks, targetIndexCount, 1f, out err);
                err *= scale;
            }
            else
            {
                cnt = MeshoptNative.SimplifyWithAttributes(dst, idx, P, n, AF, ka, W, locks, targetIndexCount, targetError, options, out err);
                if (mustKeep != null && !KeepsAll(dst.AsSpan(0, cnt), pid, mustKeep))
                    cnt = MeshoptNative.SimplifyWithAttributes(dst, idx, P, n, AF, ka, W, locks, targetIndexCount, targetError,
                                                               options & ~MeshoptNative.Prune, out err);
            }
            if (cnt == 0)
            {
                // like Decimate.Run, never return an empty mesh (e.g. Prune dropped everything): fall back to the input
                results.Add(Decimate.Snapshot0(V0, T0, attrs, k));
                resultErrors?.Add(0);
                continue;
            }
            results.Add(Compact(V0, attrs, k, dst.AsSpan(0, cnt)));
            resultErrors?.Add(err);
        }
        return results;
    }

    /// <summary>True when every position in mustKeep is used by some output index.</summary>
    static bool KeepsAll(ReadOnlySpan<uint> tris, int[] pid, HashSet<int> mustKeep)
    {
        var seen = new HashSet<int>();
        foreach (uint i in tris) if (mustKeep.Contains(pid[i])) seen.Add(pid[i]);
        return seen.Count == mustKeep.Count;
    }

    /// <summary>The used input vertices (sorted) with their original rows, and the triangles remapped onto them.</summary>
    static Simplify.Result Compact(double[] V0, double[]? attrs, int k, ReadOnlySpan<uint> tris)
    {
        int n = V0.Length / 3;
        var newId = new int[n];
        Array.Fill(newId, -1);
        foreach (uint i in tris) newId[i] = 0;
        var kept = new List<int>();
        for (int i = 0; i < n; i++) if (newId[i] == 0) { newId[i] = kept.Count; kept.Add(i); }
        var V = new double[kept.Count * 3];
        var A = attrs == null ? null : new double[kept.Count * k];
        for (int r = 0; r < kept.Count; r++)
        {
            Array.Copy(V0, 3 * kept[r], V, 3 * r, 3);
            if (A != null) Array.Copy(attrs!, k * kept[r], A, k * r, k);
        }
        var T = new int[tris.Length];
        for (int t = 0; t < tris.Length; t++) T[t] = newId[tris[t]];
        return new Simplify.Result(V, T, A, k, kept.ToArray());
    }
}
