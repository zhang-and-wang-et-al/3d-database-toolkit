namespace SceneKit;

/// <summary>Overlays on terrain (R2-D5 UX-10): roads and draped decals are separate geometry lying a few centimetres
/// above a terrain tile. Simplifying the tile under them would let them float or sink, so the tile keeps its vertices
/// under their footprint. World coordinates, z up.</summary>
public static class Overlays
{
    /// <summary>Faces up to this height (metres) above the tile count as lying on it (a little below too: −0.005).</summary>
    public const double Height = 0.05, MinShare = 0.8;
    const double Below = -0.005;
    /// <summary>A candidate whose footprint covers this share of the tile's vertices or more is a copy of the tile's
    /// surface, not an overlay.</summary>
    public const double MaxCover = 0.9;

    /// <summary>Notes when an overlay is found but not protected (R2-D5: never skipped silently).</summary>
    public const string FastNote = "Fast, rough cannot keep terrain under roads",
        CopyNote = "overlay covering the tile treated as a copy, not protected";

    /// <summary>At most this many sample points decide whether a candidate is an overlay (R2-D5: the test must not
    /// walk every face of a large candidate, e.g. a 1M-triangle tile tested as the overlay of a prop).</summary>
    public const int MaxSamples = 2000;

    /// <summary>True when at least <see cref="MinShare"/> of the candidate's face area lies within [−0.005,
    /// <see cref="Height"/>] above the tile surface straight below it. Decided from the candidate's
    /// <see cref="Sampled"/> points. Samples with no tile below count against it.</summary>
    public static bool IsOverlay(double[] tileV, int[] tileT, double[] candV, int[] candT) =>
        tileT.Length >= 3 && IsOverlay(new SurfaceIndex(tileV, tileT), Sample(candV, candT));

    static bool IsOverlay(SurfaceIndex below, Sampled s)
    {
        if (!(s.Total > 0)) return false;
        double off = 0, limit = (1 - MinShare) * s.Total;
        for (int k = 0; k < s.W.Length; k++)
        {
            double x = s.P[3 * k], y = s.P[3 * k + 1], z = s.P[3 * k + 2];
            if (below.HeightAbove(x, y, z) is not double h || h < Below || h > Height)
            {
                off += s.W[k];
                if (off > limit) return false;
            }
        }
        return true;
    }

    /// <summary>Area-weighted sample points of a mesh (xyz triples <see cref="P"/>, weights <see cref="W"/> summing to the
    /// face area <see cref="Total"/>).</summary>
    public sealed record Sampled(double[] P, double[] W, double Total);

    /// <summary>The sample points of a mesh: up to <see cref="MaxSamples"/> / 4 faces each give their centroid and three
    /// inner points (a quarter of the face area each); larger meshes give <see cref="MaxSamples"/> points spread over
    /// the cumulative face area (stratified, deterministic), each weighing Total / MaxSamples.</summary>
    public static Sampled Sample(double[] V, int[] T)
    {
        int m = T.Length / 3;
        if (m == 0) return new(Array.Empty<double>(), Array.Empty<double>(), 0);
        var cum = new double[m];
        double total = 0;
        for (int t = 0; t < m; t++) cum[t] = total += Area(V, T[3 * t], T[3 * t + 1], T[3 * t + 2]);
        if (!(total > 0)) return new(Array.Empty<double>(), Array.Empty<double>(), 0);
        (double a, double b, double c)[] bary = { (1 / 3.0, 1 / 3.0, 1 / 3.0), (4 / 6.0, 1 / 6.0, 1 / 6.0), (1 / 6.0, 4 / 6.0, 1 / 6.0), (1 / 6.0, 1 / 6.0, 4 / 6.0) };
        var P = new List<double>();
        var W = new List<double>();
        void Add(int t, double a, double b, double c, double w)
        {
            int i = 3 * T[3 * t], j = 3 * T[3 * t + 1], k = 3 * T[3 * t + 2];
            P.Add(a * V[i] + b * V[j] + c * V[k]); P.Add(a * V[i + 1] + b * V[j + 1] + c * V[k + 1]); P.Add(a * V[i + 2] + b * V[j + 2] + c * V[k + 2]);
            W.Add(w);
        }
        if (m * bary.Length <= MaxSamples)
        {
            for (int t = 0; t < m; t++)
            {
                double area = cum[t] - (t > 0 ? cum[t - 1] : 0);
                if (area <= 0) continue;
                foreach (var (a, b, c) in bary) Add(t, a, b, c, area / bary.Length);
            }
        }
        else
        {
            for (int k = 0; k < MaxSamples; k++)
            {
                double u = (k + 0.5) / MaxSamples * total;
                int t = Array.BinarySearch(cum, u);
                if (t < 0) t = ~t;
                t = Math.Min(t, m - 1);
                // a fixed low-discrepancy point inside the face (golden-ratio sequences), uniform over its area
                double r1 = (k * 0.6180339887498949) % 1.0, r2 = (k * 0.7548776662466927 + 0.5) % 1.0, sq = Math.Sqrt(r1);
                Add(t, 1 - sq, sq * (1 - r2), sq * r2, total / MaxSamples);
            }
        }
        return new(P.ToArray(), W.ToArray(), total);
    }

    /// <summary>The overlays' footprint: their xy projection rasterised into square cells of <paramref name="cell"/>
    /// (the tile's mean edge length), dilated by one cell. The predicate (x, y, z) is true inside; z is ignored.</summary>
    public static Func<double, double, double, bool> Footprint(IReadOnlyList<(double[] V, int[] T)> overlays, double cell)
    {
        var cells = new HashSet<long>();
        if (!(cell > 0) || !double.IsFinite(cell)) return (_, _, _) => false;
        static long Key(long ix, long iy) => (ix << 32) ^ (iy & 0xffffffffL);
        long C(double v) => (long)Math.Floor(v / cell);
        foreach (var (V, T) in overlays)
            for (int t = 0; t + 2 < T.Length; t += 3)
            {
                double ax = V[3 * T[t]], ay = V[3 * T[t] + 1], bx = V[3 * T[t + 1]], by = V[3 * T[t + 1] + 1], cx = V[3 * T[t + 2]], cy = V[3 * T[t + 2] + 1];
                // corners and edges (sampled at half a cell), then every cell whose centre lies inside
                foreach (var (px, py, qx, qy) in new[] { (ax, ay, bx, by), (bx, by, cx, cy), (cx, cy, ax, ay) })
                {
                    double len = Math.Sqrt((qx - px) * (qx - px) + (qy - py) * (qy - py));
                    int steps = Math.Max(1, (int)Math.Ceiling(2 * len / cell));
                    for (int s = 0; s <= steps; s++)
                    {
                        double f = (double)s / steps;
                        cells.Add(Key(C(px + f * (qx - px)), C(py + f * (qy - py))));
                    }
                }
                double d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(d) < 1e-300) continue;
                for (long ix = C(Math.Min(ax, Math.Min(bx, cx))), x1 = C(Math.Max(ax, Math.Max(bx, cx))); ix <= x1; ix++)
                    for (long iy = C(Math.Min(ay, Math.Min(by, cy))), y1 = C(Math.Max(ay, Math.Max(by, cy))); iy <= y1; iy++)
                    {
                        double x = (ix + 0.5) * cell, y = (iy + 0.5) * cell;
                        double l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d, l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
                        if (l1 >= 0 && l2 >= 0 && l1 + l2 <= 1) cells.Add(Key(ix, iy));
                    }
            }
        var grown = new HashSet<long>(cells);
        foreach (long k in cells)
        {
            long ix = k >> 32, iy = (int)(k & 0xffffffffL);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) grown.Add(Key(ix + dx, iy + dy));
        }
        return (x, y, _) => grown.Contains(Key(C(x), C(y)));
    }

    /// <summary>Mean length of the triangle edges (each counted per triangle), 0 for none.</summary>
    public static double MeanEdge(double[] V, int[] T)
    {
        double s = 0;
        int n = 0;
        for (int t = 0; t + 2 < T.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = 3 * T[t + e], b = 3 * T[t + (e + 1) % 3];
                s += Math.Sqrt((V[a] - V[b]) * (V[a] - V[b]) + (V[a + 1] - V[b + 1]) * (V[a + 1] - V[b + 1]) + (V[a + 2] - V[b + 2]) * (V[a + 2] - V[b + 2]));
                n++;
            }
        return n > 0 ? s / n : 0;
    }

    /// <summary>A geometry placed in the world: its triangles in world coordinates (made on first use) and its world box.
    /// The geometry is the one the node holds on construction (the start of the run), so a candidate simplified later in
    /// the same run is still tested in its original form.</summary>
    public sealed class Placed
    {
        public readonly Node Node;
        public readonly GeomData? Geom;
        public readonly double[] World;
        public readonly double[] Lo = { double.MaxValue, double.MaxValue, double.MaxValue }, Hi = { double.MinValue, double.MinValue, double.MinValue };
        (double[] V, int[] T)? mesh;
        Sampled? samples;

        public Placed(Node node, double[] world)
        {
            Node = node;
            Geom = node.Geom;
            World = world;
            var va = Geom?.Vertices;
            if (va == null) return;
            int c = va.Comps;
            for (int i = 0; i < va.Count; i++)
            {
                var (x, y, z) = Mat.Apply(world, va.Data[i * c], c > 1 ? va.Data[i * c + 1] : 0, c > 2 ? va.Data[i * c + 2] : 0);
                Lo[0] = Math.Min(Lo[0], x); Lo[1] = Math.Min(Lo[1], y); Lo[2] = Math.Min(Lo[2], z);
                Hi[0] = Math.Max(Hi[0], x); Hi[1] = Math.Max(Hi[1], y); Hi[2] = Math.Max(Hi[2], z);
            }
        }

        public bool Empty => Lo[0] > Hi[0];

        public (double[] V, int[] T) Mesh => mesh ??= WorldMesh(Geom!, World);

        /// <summary>The area-weighted sample points of <see cref="Mesh"/>, made once per placed geometry and run.</summary>
        public Sampled Samples => samples ??= Sample(Mesh.V, Mesh.T);
    }

    /// <summary>The triangles of a geometry in world coordinates.</summary>
    public static (double[] V, int[] T) WorldMesh(GeomData g, double[] world)
    {
        var va = g.Vertices;
        if (va == null) return (Array.Empty<double>(), Array.Empty<int>());
        int c = va.Comps;
        var v = new double[va.Count * 3];
        for (int i = 0; i < va.Count; i++)
        {
            var (x, y, z) = Mat.Apply(world, va.Data[i * c], c > 1 ? va.Data[i * c + 1] : 0, c > 2 ? va.Data[i * c + 2] : 0);
            v[3 * i] = x; v[3 * i + 1] = y; v[3 * i + 2] = z;
        }
        return (v, g.Triangles());
    }

    /// <summary>The keep predicate for simplifying <paramref name="tile"/> (in its LOCAL coordinates, true where the
    /// vertex lies under an overlay at any of its places <paramref name="places"/>), or null when no candidate is an
    /// overlay of it. Candidates whose world xy box misses the tile's or that reach far above / below it are skipped
    /// without a look at their faces; those with less than <see cref="MinShare"/> of their sampled area inside the
    /// tile's xy box (more than a fifth would have no tile below) are skipped without building the tile's surface. <paramref name="found"/> = the number of overlays, <paramref name="copies"/> = the
    /// overlays that cover (almost) the whole tile and are treated as a copy of it, not protected.</summary>
    public static Func<double, double, double, bool>? KeepFor(GeomData tile, IReadOnlyList<double[]> places, IEnumerable<Placed> candidates, out int found) =>
        KeepFor(tile, places, candidates, out found, out _);

    /// <inheritdoc cref="KeepFor(GeomData, IReadOnlyList{double[]}, IEnumerable{Placed}, out int)"/>
    public static Func<double, double, double, bool>? KeepFor(GeomData tile, IReadOnlyList<double[]> places, IEnumerable<Placed> candidates, out int found, out int copies)
    {
        found = 0;
        copies = 0;
        var tests = new List<(double[] world, Func<double, double, double, bool> fp)>();
        var cands = candidates.Where(c => !c.Empty && c.Geom is { } g && !ReferenceEquals(g, tile)).ToList();
        foreach (var w in places)
        {
            var tp = new Placed(new Node(Kind.Geometry, "osg::Geometry", "") { Geom = tile }, w);
            if (tp.Empty) continue;
            var overlays = new List<(double[] V, int[] T)>();
            SurfaceIndex? surface = null;
            foreach (var c in cands)
            {
                if (c.Hi[0] < tp.Lo[0] || c.Lo[0] > tp.Hi[0] || c.Hi[1] < tp.Lo[1] || c.Lo[1] > tp.Hi[1]) continue;
                if (c.Hi[2] < tp.Lo[2] + Below || c.Lo[2] > tp.Hi[2] + Height) continue;
                // a bound, not a guess: samples outside the tile's xy box have no tile below and count against the
                // candidate, so when less than MinShare of its sampled area lies inside the box, IsOverlay must fail
                // (the samples are made once per candidate and run; this needs no tile surface)
                if (ShareInBox(c.Samples, tp.Lo, tp.Hi) < MinShare) continue;
                var (tv, tt) = tp.Mesh;
                var (cv, ct) = c.Mesh;
                if (tt.Length < 3 || ct.Length < 3 || !IsOverlay(surface ??= new SurfaceIndex(tv, tt), c.Samples)) continue;
                // one that covers (almost) the whole tile is a copy of its surface (a duplicate, another tile on the
                // same spot), not a road or decal: it would freeze the whole tile
                var own = Footprint(new[] { (cv, ct) }, MeanEdge(tv, tt));
                int under = 0, used = 0;
                foreach (int i in tt.Distinct()) { used++; if (own(tv[3 * i], tv[3 * i + 1], tv[3 * i + 2])) under++; }
                if (under >= MaxCover * used) { copies++; continue; }
                overlays.Add((cv, ct));
            }
            if (overlays.Count == 0) continue;
            found += overlays.Count;
            tests.Add((w, Footprint(overlays, MeanEdge(tp.Mesh.V, tp.Mesh.T))));
        }
        if (tests.Count == 0) return null;
        return (x, y, z) =>
        {
            foreach (var (w, fp) in tests)
            {
                var (wx, wy, wz) = Mat.Apply(w, x, y, z);
                if (fp(wx, wy, wz)) return true;
            }
            return false;
        };
    }

    /// <summary>The share of a candidate's sampled area whose points lie inside the xy box [lo, hi] (with a hair of
    /// tolerance, so a point on the tile's border edge still counts); 0 for no samples.</summary>
    internal static double ShareInBox(Sampled s, double[] lo, double[] hi)
    {
        if (!(s.Total > 0)) return 0;
        double tol = 1e-9 * Math.Max(1.0, Math.Max(hi[0] - lo[0], hi[1] - lo[1]));
        double inside = 0;
        for (int k = 0; k < s.W.Length; k++)
        {
            double x = s.P[3 * k], y = s.P[3 * k + 1];
            if (x >= lo[0] - tol && x <= hi[0] + tol && y >= lo[1] - tol && y <= hi[1] + tol) inside += s.W[k];
        }
        // IsOverlay rejects when the off weight exceeds (1 - MinShare) x Total; summing in another order may differ by a
        // rounding step, so a share a hair below MinShare is still passed on to IsOverlay
        return inside / s.Total + 1e-12;
    }

    static double Area(double[] V, int a, int b, int c)
    {
        double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
        double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
    }

    /// <summary>Triangles bucketed by their xy box on a grid, for "the surface straight below a point".</summary>
    sealed class SurfaceIndex
    {
        readonly double[] V;
        readonly int[] T;
        readonly double x0, y0, cell;
        readonly int nx, ny;
        readonly List<int>[] buckets;

        public SurfaceIndex(double[] v, int[] t)
        {
            V = v; T = t;
            double lx = double.MaxValue, ly = double.MaxValue, hx = double.MinValue, hy = double.MinValue;
            foreach (int i in t) { lx = Math.Min(lx, v[3 * i]); ly = Math.Min(ly, v[3 * i + 1]); hx = Math.Max(hx, v[3 * i]); hy = Math.Max(hy, v[3 * i + 1]); }
            int m = t.Length / 3;
            double span = Math.Max(hx - lx, hy - ly);
            int side = Math.Clamp((int)Math.Sqrt(m), 1, 1024);
            cell = span > 0 ? span / side : 1;
            x0 = lx; y0 = ly;
            nx = Math.Max(1, (int)Math.Ceiling((hx - lx) / cell) + 1);
            ny = Math.Max(1, (int)Math.Ceiling((hy - ly) / cell) + 1);
            buckets = new List<int>[nx * ny];
            for (int k = 0; k < m; k++)
            {
                int a = 3 * t[3 * k], b = 3 * t[3 * k + 1], c = 3 * t[3 * k + 2];
                int i0 = Ix(Math.Min(v[a], Math.Min(v[b], v[c]))), i1 = Ix(Math.Max(v[a], Math.Max(v[b], v[c])));
                int j0 = Iy(Math.Min(v[a + 1], Math.Min(v[b + 1], v[c + 1]))), j1 = Iy(Math.Max(v[a + 1], Math.Max(v[b + 1], v[c + 1])));
                for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) (buckets[j * nx + i] ??= new List<int>()).Add(k);
            }
        }

        int Ix(double x) => Math.Clamp((int)Math.Floor((x - x0) / cell), 0, nx - 1);
        int Iy(double y) => Math.Clamp((int)Math.Floor((y - y0) / cell), 0, ny - 1);

        /// <summary>Height of (x, y, z) above the triangle straight below / above it (the nearest one in z), or null
        /// when no triangle covers (x, y).</summary>
        public double? HeightAbove(double x, double y, double z)
        {
            if (x < x0 - cell || y < y0 - cell || x > x0 + nx * cell || y > y0 + ny * cell) return null;
            var b = buckets[Iy(y) * nx + Ix(x)];
            if (b == null) return null;
            double? best = null;
            const double eps = 1e-9;
            foreach (int k in b)
            {
                int a = 3 * T[3 * k], bb = 3 * T[3 * k + 1], c = 3 * T[3 * k + 2];
                double d = (V[bb + 1] - V[c + 1]) * (V[a] - V[c]) + (V[c] - V[bb]) * (V[a + 1] - V[c + 1]);
                if (Math.Abs(d) < 1e-18) continue;   // vertical or degenerate: no surface to stand on
                double l1 = ((V[bb + 1] - V[c + 1]) * (x - V[c]) + (V[c] - V[bb]) * (y - V[c + 1])) / d;
                double l2 = ((V[c + 1] - V[a + 1]) * (x - V[c]) + (V[a] - V[c]) * (y - V[c + 1])) / d;
                double l3 = 1 - l1 - l2;
                if (l1 < -eps || l2 < -eps || l3 < -eps) continue;
                double h = z - (l1 * V[a + 2] + l2 * V[bb + 2] + l3 * V[c + 2]);
                if (best is not double bh || Math.Abs(h) < Math.Abs(bh)) best = h;
            }
            return best;
        }
    }
}
