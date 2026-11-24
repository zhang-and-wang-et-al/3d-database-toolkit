namespace SceneKit;

/// <summary>Shared result type, unique rows and Tipsify triangle reordering. Edge collapse lives in Decimate, fast clustering in RSimp.</summary>
public static class Simplify
{
    public sealed record Result(double[] V, int[] T, double[]? A, int K, int[] Kept);

    // ======================================================================= clustering

    /// <summary>numpy unique rows (numeric lexicographic, stable): (first index of each row, inverse).</summary>
    public static (int[] first, int[] inverse) UniqueRowsNumeric(double[] rows, int n, int k)
    {
        var order = Enumerable.Range(0, n).ToArray();
        int Cmp(int x, int y)
        {
            for (int j = 0; j < k; j++)
            {
                double a = rows[x * k + j], b = rows[y * k + j];
                if (a < b) return -1;
                if (a > b) return 1;
                if (double.IsNaN(a) != double.IsNaN(b)) return double.IsNaN(a) ? 1 : -1;
            }
            return 0;
        }
        var sorted = order.OrderBy(i => i, Comparer<int>.Create(Cmp)).ToArray();   // OrderBy is stable
        var first = new List<int>();
        var inv = new int[n];
        int u = -1;
        for (int i = 0; i < n; i++)
        {
            if (i == 0 || Cmp(sorted[i - 1], sorted[i]) != 0) { u++; first.Add(sorted[i]); }
            inv[sorted[i]] = u;
        }
        return (first.ToArray(), inv);
    }

    // ======================================================================= tipsify

    /// <summary>Reorder triangles for the post-transform vertex cache (Sander et al. 2007): new triangle order.</summary>
    public static int[] Tipsify(int[] T, int nverts, int cache = 16)
    {
        int m = T.Length / 3;
        if (m == 0) return Array.Empty<int>();
        var flat = T;
        var counts = new int[nverts];
        foreach (var v in flat) counts[v]++;
        var start = new int[nverts + 1];
        for (int i = 0; i < nverts; i++) start[i + 1] = start[i] + counts[i];
        var fill = (int[])start.Clone();
        var adj = new int[flat.Length];
        for (int i = 0; i < flat.Length; i++) adj[fill[flat[i]]++] = i / 3;   // stable argsort // 3
        var live = (int[])counts.Clone();
        var ctime = new int[nverts];
        var emitted = new bool[m];
        var dead = new List<int>();
        var outl = new List<int>(m);
        int s = cache + 1, cursor = 0;
        int f = flat[0];
        var cands = new List<int>();
        while (f >= 0)
        {
            cands.Clear();
            for (int kk = start[f]; kk < start[f + 1]; kk++)
            {
                int t = adj[kk];
                if (emitted[t]) continue;
                emitted[t] = true;
                outl.Add(t);
                for (int j = 0; j < 3; j++)
                {
                    int v = T[3 * t + j];
                    dead.Add(v);
                    cands.Add(v);
                    live[v]--;
                    if (s - ctime[v] > cache) { ctime[v] = s; s++; }
                }
            }
            int best = -1, bestP = -1;
            foreach (int v in cands)
            {
                if (live[v] > 0)
                {
                    int p = 0;
                    if (s - ctime[v] + 2 * live[v] <= cache) p = s - ctime[v];
                    if (p > bestP) { best = v; bestP = p; }
                }
            }
            if (best < 0)
            {
                while (dead.Count > 0)
                {
                    int v = dead[^1];
                    dead.RemoveAt(dead.Count - 1);
                    if (live[v] > 0) { best = v; break; }
                }
            }
            if (best < 0)
            {
                while (cursor < nverts && live[cursor] <= 0) cursor++;
                best = cursor < nverts ? cursor : -1;
            }
            f = best;
        }
        if (outl.Count < m) for (int i = 0; i < m; i++) if (!emitted[i]) outl.Add(i);
        return outl.ToArray();
    }
}
