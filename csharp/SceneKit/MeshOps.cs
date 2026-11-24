namespace SceneKit;

/// <summary>Low level mesh helpers shared by the analysis and the optimizer.</summary>
public static class MeshOps
{
    /// <summary>All per-vertex attributes side by side: (row major n x k matrix, layout).</summary>
    public static (double[] mat, int k, List<(string name, int cols)> layout) VertexMatrix(GeomData g, ICollection<string>? include = null)
    {
        int n = g.NumVertices;
        var cols = new List<VArray>();
        var layout = new List<(string, int)>();
        foreach (var (name, a) in g.Arrays())
        {
            if (include != null && !include.Contains(name)) continue;
            if (a.Binding != "vertex" || a.Count != n) continue;
            layout.Add((name, a.Comps));
            cols.Add(a);
        }
        int k = cols.Sum(c => c.Comps);
        var mat = new double[(long)n * k];
        int off = 0;
        foreach (var a in cols)
        {
            int c = a.Comps;
            var d = a.Data;
            for (int r = 0; r < n; r++)
                for (int j = 0; j < c; j++) mat[(long)r * k + off + j] = d[r * c + j];
            off += c;
        }
        return (mat, k, layout);
    }

    /// <summary>Quantize as numpy does before comparing rows (positions by posTol, the rest by attrTol; -0 == 0).</summary>
    public static double[] Quantize(double[] mat, int n, int k, double posTol = 0.0, double attrTol = 1e-6, int posCols = 3)
    {
        var q = (double[])mat.Clone();
        for (int r = 0; r < n; r++)
            for (int j = 0; j < k; j++)
            {
                long i = (long)r * k + j;
                double v = q[i];
                if (j < posCols) { if (posTol > 0) v = Math.Round(v / posTol, MidpointRounding.ToEven); }
                else if (attrTol > 0) v = Math.Round(v / attrTol, MidpointRounding.ToEven);
                if (v == 0) v = 0.0;   // -0.0 -> 0.0
                q[i] = v;
            }
        return q;
    }

    /// <summary>Number of distinct rows (after quantization).</summary>
    public static int CountUniqueRows(double[] mat, int n, int k, double posTol = 0.0, double attrTol = 1e-6, int posCols = 3)
    {
        if (n == 0) return 0;
        var q = Quantize(mat, n, k, posTol, attrTol, posCols);
        var set = new HashSet<int>(new RowComparer(q, k));
        for (int r = 0; r < n; r++) set.Add(r);
        return set.Count;
    }

    /// <summary>numpy.unique(rows, return_index, return_inverse) on raw bytes: (first index of every unique row in
    /// numpy's order, inverse mapping). The order matters: it decides the vertex order after welding.</summary>
    public static (int[] first, int[] inverse) UniqueRows(double[] mat, int n, int k, double posTol = 0.0, double attrTol = 1e-6, int posCols = 3)
    {
        if (n == 0) return (Array.Empty<int>(), Array.Empty<int>());
        var q = Quantize(mat, n, k, posTol, attrTol, posCols);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var cmp = new ByteRowComparer(q, k);
        // stable sort (numpy uses a stable sort for return_index)
        var keyed = order.Select((r, i) => (r, i)).ToArray();
        Array.Sort(keyed, (x, y) => { int c = cmp.Compare(x.r, y.r); return c != 0 ? c : x.i.CompareTo(y.i); });
        var first = new List<int>();
        var inverse = new int[n];
        int u = -1;
        for (int i = 0; i < n; i++)
        {
            int r = keyed[i].r;
            if (i == 0 || cmp.Compare(keyed[i - 1].r, r) != 0) { u++; first.Add(r); }
            inverse[r] = u;
        }
        return (first.ToArray(), inverse);
    }

    sealed class RowComparer : IEqualityComparer<int>
    {
        readonly double[] q; readonly int k;
        public RowComparer(double[] q, int k) { this.q = q; this.k = k; }
        public bool Equals(int a, int b)
        {
            for (int j = 0; j < k; j++)
                if (BitConverter.DoubleToInt64Bits(q[(long)a * k + j]) != BitConverter.DoubleToInt64Bits(q[(long)b * k + j])) return false;
            return true;
        }
        public int GetHashCode(int r)
        {
            ulong h = 1469598103934665603UL;
            for (int j = 0; j < k; j++) { h ^= (ulong)BitConverter.DoubleToInt64Bits(q[(long)r * k + j]); h *= 1099511628211UL; h ^= h >> 29; }
            return (int)(h ^ (h >> 32));
        }
    }

    /// <summary>memcmp order of the little-endian float64 bytes of two rows (numpy void comparison).</summary>
    sealed class ByteRowComparer
    {
        readonly double[] q; readonly int k;
        public ByteRowComparer(double[] q, int k) { this.q = q; this.k = k; }
        public int Compare(int a, int b)
        {
            for (int j = 0; j < k; j++)
            {
                ulong x = (ulong)BitConverter.DoubleToInt64Bits(q[(long)a * k + j]);
                ulong y = (ulong)BitConverter.DoubleToInt64Bits(q[(long)b * k + j]);
                if (x == y) continue;
                // compare byte by byte, lowest address (= least significant byte) first
                for (int s = 0; s < 64; s += 8)
                {
                    int bx = (int)((x >> s) & 0xFF), by = (int)((y >> s) & 0xFF);
                    if (bx != by) return bx < by ? -1 : 1;
                }
            }
            return 0;
        }
    }

    /// <summary>Triangles that repeat a vertex index or have (near) zero area.</summary>
    public static bool[] DegenerateMask(VArray? pos, int[] tris, double areaEps = 0.0)
    {
        int nt = tris.Length / 3;
        var bad = new bool[nt];
        for (int t = 0; t < nt; t++)
        {
            int a = tris[3 * t], b = tris[3 * t + 1], c = tris[3 * t + 2];
            bad[t] = a == b || b == c || a == c;
        }
        if (pos == null || pos.Count == 0) return bad;
        var p = pos.Data;
        int pc = pos.Comps;
        if (areaEps <= 0)
        {
            double ext = double.NegativeInfinity;
            for (int j = 0; j < Math.Min(3, pc); j++)
            {
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                bool nan = false;
                for (int r = 0; r < pos.Count; r++) { double v = p[r * pc + j]; if (double.IsNaN(v)) nan = true; if (v < lo) lo = v; if (v > hi) hi = v; }
                double e = nan ? double.NaN : hi - lo;
                ext = double.IsNaN(e) || double.IsNaN(ext) ? double.NaN : Math.Max(ext, e);
            }
            areaEps = (ext * 1e-7) * (ext * 1e-7);
        }
        double eps2 = areaEps * areaEps;
        for (int t = 0; t < nt; t++)
        {
            int ia = tris[3 * t], ib = tris[3 * t + 1], ic = tris[3 * t + 2];
            double ax = C(p, ia, 0, pc), ay = C(p, ia, 1, pc), az = C(p, ia, 2, pc);
            double ux = C(p, ib, 0, pc) - ax, uy = C(p, ib, 1, pc) - ay, uz = C(p, ib, 2, pc) - az;
            double vx = C(p, ic, 0, pc) - ax, vy = C(p, ic, 1, pc) - ay, vz = C(p, ic, 2, pc) - az;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            if (cx * cx + cy * cy + cz * cz <= eps2) bad[t] = true;
        }
        return bad;
    }

    static double C(double[] p, int row, int col, int comps) => col < comps ? p[row * comps + col] : 0.0;

    /// <summary>Vertices used by any primitive.</summary>
    public static bool[] Referenced(GeomData g)
    {
        int nv = g.NumVertices;
        var used = new bool[nv];
        foreach (var p in g.Prims)
            foreach (var i in p.VertexIndices())
                if (i >= 0 && i < nv) used[i] = true;
        return used;
    }

    public static string SmallestIndexType(int nverts) => nverts <= 65536 ? "DrawElementsUShort" : "DrawElementsUInt";

    public static bool Instanced(GeomData g) => g.Prims.Any(p => p.Instances != 0);

    /// <summary>Average cache miss ratio of a FIFO post-transform cache (lower is better; 0.5-0.7 is good).</summary>
    public static double Acmr(int[] tris, int cache = 32)
    {
        int nt = tris.Length / 3;
        if (nt == 0) return 0.0;
        var fifo = new Queue<int>(cache);
        var inset = new HashSet<int>();
        long misses = 0;
        for (int i = 0; i < nt * 3; i++)
        {
            int v = tris[i];
            if (inset.Contains(v)) continue;
            misses++;
            if (fifo.Count == cache) inset.Remove(fifo.Dequeue());
            fifo.Enqueue(v);
            inset.Add(v);
        }
        return (double)misses / nt;
    }

    /// <summary>Per geometry: duplicate vertices, degenerate / duplicated triangles, unused vertices, degenerate lines
    /// (the numbers normalized() and the analysis work with).</summary>
    public sealed record Stats(int DupVertices, int Degenerate, int Unused, int DuplicateTris, int DegenerateLines);

    public static Stats? MeshStats(GeomData g)
    {
        if (g.Vertices == null || g.NumVertices == 0) return null;
        int nv = g.NumVertices;
        var tris = g.Triangles();
        foreach (var i in tris) if (i < 0 || i >= nv) return null;
        var (mat, k, _) = VertexMatrix(g);
        int dup = nv - CountUniqueRows(mat, nv, k);
        var bad = tris.Length > 0 ? DegenerateMask(g.Vertices, tris) : Array.Empty<bool>();
        int degenerate = bad.Count(b => b);
        // duplicated triangles among the good ones (same canonical rotation as normalized())
        var set = new HashSet<(int, int, int)>();
        int dupTris = 0;
        for (int t = 0; t < bad.Length; t++)
        {
            if (bad[t]) continue;
            if (!set.Add(Canon(tris[3 * t], tris[3 * t + 1], tris[3 * t + 2]))) dupTris++;
        }
        var used = Referenced(g);
        int unused = used.Count(u => !u);
        var ln = g.Lines();
        int badLines = 0;
        for (int i = 0; i + 1 < ln.Length; i += 2) if (ln[i] == ln[i + 1]) badLines++;
        return new Stats(dup, degenerate, unused, dupTris, badLines);
    }

    /// <summary>Rotation that starts at the smallest index (keeps the winding).</summary>
    public static (int, int, int) Canon(int a, int b, int c)
    {
        if (a <= b && a <= c) return (a, b, c);
        if (b <= a && b <= c) return (b, c, a);
        return (c, a, b);
    }
}
