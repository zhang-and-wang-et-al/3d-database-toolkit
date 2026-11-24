namespace SceneKit;

/// <summary>Textured proxy for far LOD levels (the "remeshing" approach of production LOD tools):
///  1. the source drawables are voxelised and the inside is filled (flood fill from outside), so interiors, hidden
///     parts and small gaps disappear and the hull is closed;
///  2. surface nets extract the hull; every vertex is projected back onto the original surface (preferring faces with
///     the same orientation), so the proxy lies on the real surface instead of the voxel grid;
///  3. the hull is reduced to the target with Decimate (quadric collapse + refit);
///  4. it gets its own UV atlas (Unwrap) and a baked texture with the colours of the original (Bake).
/// The result is one geometry with one texture (plus one per extra texture unit, same layout): one draw call for the
/// whole object.</summary>
public static class Proxy
{
    /// <summary>One source drawable: triangles in a common frame, optional UVs (unit 0), texture, material colour and
    /// vertex colours (RGBA, 0..1).</summary>
    public sealed class Source
    {
        public double[] V = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public double[]? UV;
        public RawImage? Image;
        public double[] Color = { 1, 1, 1, 1 };
        public double[]? VertexColors;
        /// <summary>Layers (texture units above 0, companion images): UVs (2 x n), image, and whether it is a class map
        /// (nearest lookup, never blended).</summary>
        public Dictionary<LayerKey, (double[] UV, RawImage Image, bool ClassMap)> Extra = new();
        /// <summary>The authored normals (3 x n, per vertex, in the frame of <see cref="V"/>), or null: the callers fill it
        /// from the geometry's normals when those are per vertex. Read by <see cref="TransferNormals"/> only.</summary>
        public double[]? N;

        /// <summary>The same source with other positions and triangles (everything else shared).</summary>
        internal Source With(double[] v, int[] t) => new() { V = v, T = t, UV = UV, Image = Image, Color = Color, VertexColors = VertexColors, Extra = Extra, N = N };
    }

    public sealed class Options
    {
        public int TargetTris = 500;
        /// <summary>Voxels along the longest side of the bounding box.</summary>
        public int Resolution = 128;
        public int TextureSize = 512;
        public bool Bake = true;
        public Action<string>? Progress;
        /// <summary>What a layer shows where a source has no image for it.</summary>
        public ExtraFill Fill = ExtraFill.White;
        /// <summary>What a class-map layer shows where a source has none.</summary>
        public ExtraFill ClassFill = ExtraFill.ClassFill(0);
        /// <summary>The ground (in the sources' frame) to cut the hull at, or null: the hull's down-facing triangles within
        /// max(<see cref="CutTolerance"/>, the voxel cell) of it are removed before the reduction.</summary>
        public GroundPlane? Cut;
        /// <summary>How far from the plane the cut reaches (callers pass max(e, the object's cell)); at least one voxel cell.</summary>
        public double CutTolerance;
        /// <summary>Within (the per-part proxy): the largest deviation, or null (keep: <see cref="TargetTris"/> and
        /// <see cref="Resolution"/>). With a value the cell is this size, the resolution ceil(extent / value) clamped to
        /// 16 .. min(512, <see cref="Resolution"/>) (so <see cref="Resolution"/> is the upper limit, and
        /// <see cref="HullResult.Cell"/> says the cell actually used), and the reduction gets MaxError = value and a target
        /// of 8 triangles instead of <see cref="TargetTris"/>.</summary>
        public double? MaxError;
        /// <summary>Within: the cell as a share of <see cref="MaxError"/> (1: the cell is MaxError, as generate_lod's
        /// proxy_from and parts = whole build; the per-part proxy asks for cells of half of it, so the surface has room
        /// within the error and each tighter attempt refines). The resolution stays clamped to <see cref="Resolution"/>.</summary>
        public double CellShare = 1;
        /// <summary>Shading: from the hull (<see cref="CornerNormals"/>, hard edges above ~45 degrees; today's output) or
        /// from the original (<see cref="TransferNormals"/> within max(<see cref="MaxError"/>, the cell), <see cref="Crease"/>).</summary>
        public ProxyNormals Normals = ProxyNormals.Hull;
        /// <summary>The crease angle (degrees) of <see cref="ProxyNormals.Original"/>.</summary>
        public double Crease = 45;
    }

    public sealed class Result
    {
        public double[] V = Array.Empty<double>();
        public double[] N = Array.Empty<double>();
        public double[] UV = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public RawImage? Texture;
        /// <summary>Baked texture per layer (unit above 0, companion image), for the same UVs.</summary>
        public Dictionary<LayerKey, RawImage> Extra = new();
    }

    /// <summary>The reduced hull (steps 1-3) and what the bake needs of the original: the merged sources (<see cref="OV"/>,
    /// <see cref="OT"/>, the source of each triangle), their index and the voxel cell.</summary>
    public sealed class HullResult
    {
        public double[] V = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public double Cell;
        public TriIndex Index = null!;
        public double[] OV = Array.Empty<double>();
        public int[] OT = Array.Empty<int>();
        public int[] TriSource = Array.Empty<int>();
        /// <summary>The cut at <see cref="Options.Cut"/> would have removed every triangle, so the hull was kept uncut.</summary>
        public bool CutWouldEmpty;
    }

    /// <summary>How often <see cref="Hull"/> ran (for tests).</summary>
    internal static int HullCalls;

    public static Result? Build(IList<Source> sources, Options opt)
    {
        var h = Hull(sources, opt);
        if (h == null) return null;

        // ---------------------------------------------------------------- unwrap and bake
        opt.Progress?.Invoke("proxy: unwrapping ...");
        var uw = Unwrap.Run(h.V, h.T, opt.TextureSize);
        // smooth normals with hard edges above ~45 degrees (computed on the welded hull, so chart cuts do not show); or
        // those of the original
        var cn = opt.Normals == ProxyNormals.Original
            ? TransferNormals(h.V, h.T, h.OV, h.OT, MergeNormals(sources), Math.Max(opt.MaxError is double me && me > 0 ? me : 0, h.Cell), opt.Crease)
            : CornerNormals(h.V, h.T, 0.7);
        var N = new double[uw.V.Length];
        for (int i = 0; i < uw.T.Length; i++) for (int k = 0; k < 3; k++) N[3 * uw.T[i] + k] += cn[3 * i + k];
        Normalize(N);
        var result = new Result { V = uw.V, T = uw.T, UV = uw.UV, N = N };
        if (opt.Bake)
        {
            opt.Progress?.Invoke("proxy: baking the texture ...");
            (result.Texture, result.Extra) = BakeSources(sources, h.Index, h.OV, h.OT, h.TriSource, uw.V, uw.T, uw.UV, opt.TextureSize, h.Cell * 3, opt.Fill, opt.ClassFill);
        }
        return result;
    }

    /// <summary>The sources in one mesh: positions, triangles and the source of each triangle.</summary>
    internal static (double[] V, int[] T, int[] TriSource) Merge(IList<Source> sources)
    {
        var V = new List<double>(); var T = new List<int>(); var triSrc = new List<int>();
        for (int s = 0; s < sources.Count; s++)
        {
            int b = V.Count / 3;
            V.AddRange(sources[s].V);
            for (int t = 0; t < sources[s].T.Length / 3; t++)
            {
                T.Add(sources[s].T[3 * t] + b); T.Add(sources[s].T[3 * t + 1] + b); T.Add(sources[s].T[3 * t + 2] + b);
                triSrc.Add(s);
            }
        }
        return (V.ToArray(), T.ToArray(), triSrc.ToArray());
    }

    /// <summary>Steps 1-3: the sources merged, voxelised, the surface-net hull projected onto the original, cut at the
    /// ground (<see cref="Options.Cut"/>, when set; a cut that would remove every triangle is not made, see
    /// <see cref="HullResult.CutWouldEmpty"/>) and reduced to <see cref="Options.TargetTris"/> (no UVs, no bake). Null
    /// when there is nothing to build a hull of.</summary>
    public static HullResult? Hull(IList<Source> sources, Options opt)
    {
        Interlocked.Increment(ref HullCalls);
        var (Va, Ta, triSrc) = Merge(sources);
        if (Ta.Length == 0) return null;
        var lo = new double[3]; var hi = new double[3];
        for (int c = 0; c < 3; c++) { lo[c] = double.PositiveInfinity; hi[c] = double.NegativeInfinity; }
        foreach (int v in Ta) for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], Va[3 * v + c]); hi[c] = Math.Max(hi[c], Va[3 * v + c]); }
        double ext = Math.Max(Math.Max(hi[0] - lo[0], hi[1] - lo[1]), hi[2] - lo[2]);
        if (ext <= 0) return null;

        // ---------------------------------------------------------------- voxelise
        opt.Progress?.Invoke("proxy: voxelising ...");
        int res = opt.MaxError is double me && me > 0
            ? (int)Math.Clamp(Math.Ceiling(ext / (me * (opt.CellShare > 0 && opt.CellShare <= 1 ? opt.CellShare : 1))), 16, Math.Max(16, Math.Min(512, opt.Resolution)))
            : Math.Clamp(opt.Resolution, 16, 512);
        double cell = ext / res;
        var o = new double[3];
        var dim = new int[3];
        for (int c = 0; c < 3; c++) { dim[c] = (int)Math.Ceiling((hi[c] - lo[c]) / cell) + 5; o[c] = lo[c] - cell * 2.5; }   // two empty voxels around
        int nx = dim[0], ny = dim[1], nz = dim[2];
        long total = (long)nx * ny * nz;
        if (total > 200_000_000) return null;
        var solid = new byte[total];   // 1 surface, 2 outside, 0 inside
        int Vi(int x, int y, int z) => (x * ny + y) * nz + z;
        for (int t = 0; t < Ta.Length / 3; t++)
        {
            int a = Ta[3 * t], b = Ta[3 * t + 1], cc = Ta[3 * t + 2];
            double l1 = Math.Sqrt(Geo.Dist2(Va[3 * a], Va[3 * a + 1], Va[3 * a + 2], Va[3 * b], Va[3 * b + 1], Va[3 * b + 2]));
            double l2 = Math.Sqrt(Geo.Dist2(Va[3 * a], Va[3 * a + 1], Va[3 * a + 2], Va[3 * cc], Va[3 * cc + 1], Va[3 * cc + 2]));
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(l1, l2) / (cell * 0.4)));
            for (int i = 0; i <= steps; i++)
                for (int j = 0; j <= steps - i; j++)
                {
                    double u = (double)i / steps, w = (double)j / steps;
                    int x = (int)((Va[3 * a] + u * (Va[3 * b] - Va[3 * a]) + w * (Va[3 * cc] - Va[3 * a]) - o[0]) / cell);
                    int y = (int)((Va[3 * a + 1] + u * (Va[3 * b + 1] - Va[3 * a + 1]) + w * (Va[3 * cc + 1] - Va[3 * a + 1]) - o[1]) / cell);
                    int z = (int)((Va[3 * a + 2] + u * (Va[3 * b + 2] - Va[3 * a + 2]) + w * (Va[3 * cc + 2] - Va[3 * a + 2]) - o[2]) / cell);
                    solid[Vi(Math.Clamp(x, 2, nx - 3), Math.Clamp(y, 2, ny - 3), Math.Clamp(z, 2, nz - 3))] = 1;
                }
        }
        // outside: flood fill from the corner through empty voxels
        {
            var stack = new Stack<int>();
            stack.Push(Vi(0, 0, 0));
            solid[Vi(0, 0, 0)] = 2;
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                int z = id % nz, y = id / nz % ny, x = id / nz / ny;
                void Try(int xx, int yy, int zz)
                {
                    if (xx < 0 || yy < 0 || zz < 0 || xx >= nx || yy >= ny || zz >= nz) return;
                    int j = Vi(xx, yy, zz);
                    if (solid[j] != 0) return;
                    solid[j] = 2;
                    stack.Push(j);
                }
                Try(x - 1, y, z); Try(x + 1, y, z); Try(x, y - 1, z); Try(x, y + 1, z); Try(x, y, z - 1); Try(x, y, z + 1);
            }
        }
        bool In(int x, int y, int z) => solid[Vi(x, y, z)] != 2;
        // where inside (or outside) voxels of a 2x2x2 cell only touch along an edge or at a corner, the surface net gets
        // non-manifold edges and vertices, which Decimate has to keep: fill outside voxels until no such cell is left
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int x = 1; x < nx - 2; x++)
                for (int y = 1; y < ny - 2; y++)
                    for (int z = 1; z < nz - 2; z++)
                    {
                        int m = 0;
                        for (int q = 0; q < 8; q++) if (In(x + (q & 1), y + (q >> 1 & 1), z + (q >> 2))) m |= 1 << q;
                        if (!Pinched[m]) continue;
                        for (int q = 0; q < 8; q++)
                        {
                            int fx = x + (q & 1), fy = y + (q >> 1 & 1), fz = z + (q >> 2);
                            // the two voxels around the grid stay empty, so the hull stays closed
                            if ((m >> q & 1) != 0 || fx < 2 || fy < 2 || fz < 2 || fx > nx - 3 || fy > ny - 3 || fz > nz - 3) continue;
                            solid[Vi(fx, fy, fz)] = 1;
                            changed = true;
                            break;
                        }
                    }
        }

        // ---------------------------------------------------------------- surface nets
        opt.Progress?.Invoke("proxy: extracting the hull ...");
        var dual = new Dictionary<long, int>();   // dual cell (min voxel corner) -> vertex
        var HV = new List<double>();
        var HT = new List<int>();
        long Dk(int x, int y, int z) => ((long)x * ny + y) * nz + z;
        int DualV(int x, int y, int z)
        {
            long key = Dk(x, y, z);
            if (dual.TryGetValue(key, out int v)) return v;
            v = HV.Count / 3;
            dual[key] = v;
            HV.Add(o[0] + (x + 1) * cell); HV.Add(o[1] + (y + 1) * cell); HV.Add(o[2] + (z + 1) * cell);
            return v;
        }
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (flip) (b, d) = (d, b);
            // split along the shorter diagonal
            if (Geo.Dist2(HV[3 * a], HV[3 * a + 1], HV[3 * a + 2], HV[3 * c], HV[3 * c + 1], HV[3 * c + 2]) <=
                Geo.Dist2(HV[3 * b], HV[3 * b + 1], HV[3 * b + 2], HV[3 * d], HV[3 * d + 1], HV[3 * d + 2]))
            { HT.AddRange(new[] { a, b, c, a, c, d }); }
            else { HT.AddRange(new[] { a, b, d, b, c, d }); }
        }
        for (int x = 1; x < nx - 1; x++)
            for (int y = 1; y < ny - 1; y++)
                for (int z = 1; z < nz - 1; z++)
                {
                    bool s0 = In(x, y, z);
                    // face towards +x / +y / +z between this voxel and the next one
                    if (s0 != In(x + 1, y, z))
                        Quad(DualV(x, y - 1, z - 1), DualV(x, y, z - 1), DualV(x, y, z), DualV(x, y - 1, z), !s0);
                    if (s0 != In(x, y + 1, z))
                        Quad(DualV(x - 1, y, z - 1), DualV(x - 1, y, z), DualV(x, y, z), DualV(x, y, z - 1), !s0);
                    if (s0 != In(x, y, z + 1))
                        Quad(DualV(x - 1, y - 1, z), DualV(x, y - 1, z), DualV(x, y, z), DualV(x - 1, y, z), !s0);
                }
        if (HT.Count == 0) return null;
        var hv = HV.ToArray(); var ht = HT.ToArray();

        // ---------------------------------------------------------------- project onto the original surface
        opt.Progress?.Invoke("proxy: fitting the hull to the original ...");
        var index = new TriIndex(Va, Ta);
        var vn = VertexNormals(hv, ht);
        var taken = new HashSet<(double, double, double)>();
        for (int v = 0; v < hv.Length / 3; v++)
        {
            var h = index.Closest(hv[3 * v], hv[3 * v + 1], hv[3 * v + 2], cell * 2.5, vn[3 * v], vn[3 * v + 1], vn[3 * v + 2], SameFacing);
            if (h == null) continue;
            var hit = h.Value;
            int a = Ta[3 * hit.Tri], b = Ta[3 * hit.Tri + 1], c = Ta[3 * hit.Tri + 2];
            double P(int k) => hit.U * Va[3 * a + k] + hit.V * Va[3 * b + k] + hit.W * Va[3 * c + k];
            // many hull vertices snap onto the same corner of a low-poly original; Decimate welds equal positions, which
            // would make the hull non-manifold there and lock it (the target is then missed): keep such a vertex in place
            var p = (P(0), P(1), P(2));
            if (!taken.Add(p)) continue;
            (hv[3 * v], hv[3 * v + 1], hv[3 * v + 2]) = p;
        }

        // ---------------------------------------------------------------- cut at the ground
        // the flood fill above is unchanged; the down-facing bottom at the plane goes, and the reduction keeps the open rim
        // in place (Borders = Lock)
        bool cutWouldEmpty = false;
        if (opt.Cut is { } ground)
        {
            ht = PartProxy.CutOrKeep(hv, ht, ground, Math.Max(opt.CutTolerance, cell), out cutWouldEmpty);   // all bottom: stays uncut
        }

        // ---------------------------------------------------------------- reduce
        opt.Progress?.Invoke("proxy: reducing the hull ...");
        var red = opt.MaxError is double maxError && maxError > 0
            ? Decimate.Run(hv, ht, new[] { 8 }, null, 0,
                new Decimate.Options { Borders = BorderMode.Lock, MergeParts = false, Progress = opt.Progress, MaxError = maxError })[0]
            : Decimate.Run(hv, ht, new[] { Math.Max(opt.TargetTris, 8) }, null, 0,
                new Decimate.Options { Borders = BorderMode.Lock, MergeParts = false, Progress = opt.Progress })[0];
        if (red.T.Length == 0) return null;

        return new HullResult { V = red.V, T = red.T, Cell = cell, Index = index, OV = Va, OT = Ta, TriSource = triSrc, CutWouldEmpty = cutWouldEmpty };
    }

    /// <summary>Bakes the sources onto an unwrapped mesh (<paramref name="V"/>, <paramref name="T"/>, <paramref name="UV"/>):
    /// the base texture and one image per layer. <paramref name="OV"/>, <paramref name="OT"/> and <paramref name="triSrc"/>
    /// are the sources merged as <see cref="Merge"/> does, <paramref name="index"/> their index. The part arrays (all
    /// null: no filter) are <see cref="Bake.Input.PartOfProxyTri"/>, <see cref="Bake.Input.PartOfOriginalTri"/> and
    /// <see cref="Bake.Input.MaxDistOfPart"/>.</summary>
    internal static (RawImage Texture, Dictionary<LayerKey, RawImage> Extra) BakeSources(IList<Source> sources, TriIndex index, double[] OV, int[] OT,
        int[] triSrc, double[] V, int[] T, double[] UV, int size, double maxDist, ExtraFill fill, ExtraFill classFill,
        int[]? partOfProxyTri = null, int[]? partOfOriginalTri = null, double[]? maxDistOfPart = null)
    {
        var vBase = new int[sources.Count];
        for (int s = 1; s < sources.Count; s++) vBase[s] = vBase[s - 1] + sources[s - 1].V.Length / 3;
        var srcUV = new double[OV.Length / 3 * 2];
        var srcCol = new double[OV.Length / 3 * 4];
        var hasUV = new bool[sources.Count];
        for (int s = 0; s < sources.Count; s++)
        {
            int n = sources[s].V.Length / 3;
            if (sources[s].UV != null) { hasUV[s] = true; Array.Copy(sources[s].UV!, 0, srcUV, vBase[s] * 2, n * 2); }
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 4; k++)
                    srcCol[(vBase[s] + i) * 4 + k] = sources[s].Color[k] * (sources[s].VertexColors != null ? sources[s].VertexColors![i * 4 + k] : 1.0);
        }
        var layers = new Dictionary<LayerKey, Bake.Layer>();
        foreach (var u in sources.SelectMany(s => s.Extra.Keys).Distinct())
        {
            bool cls = sources.Any(s => s.Extra.TryGetValue(u, out var e) && e.ClassMap);
            var layer = layers[u] = new Bake.Layer { OUV = new double[OV.Length / 3 * 2], Images = new RawImage?[sources.Count], Nearest = cls, Fill = cls ? classFill : null };
            for (int s = 0; s < sources.Count; s++)
            {
                if (!sources[s].Extra.TryGetValue(u, out var e)) continue;   // no such layer: the fill
                layer.Images[s] = e.Image;
                Array.Copy(e.UV, 0, layer.OUV, vBase[s] * 2, sources[s].V.Length / 3 * 2);
            }
        }
        return Bake.RunAll(new Bake.Input
        {
            V = V, T = T, UV = UV, Size = size, Original = index, OV = OV, OT = OT, TriSource = triSrc,
            OUV = srcUV, OColor = srcCol, HasUV = hasUV, Images = sources.Select(s => s.Image).ToArray(), MaxDist = maxDist,
            Layers = layers, Fill = fill, PartOfProxyTri = partOfProxyTri, PartOfOriginalTri = partOfOriginalTri, MaxDistOfPart = maxDistOfPart,
        });
    }

    /// <summary>2x2x2 voxel patterns (bit = corner x + 2y + 4z inside) whose inside or outside corners fall apart into
    /// several face-connected parts.</summary>
    static readonly bool[] Pinched = Enumerable.Range(0, 256).Select(m => Parts(m) > 1 || Parts(~m & 255) > 1).ToArray();

    static int Parts(int set)
    {
        int n = 0, seen = 0;
        for (int s0 = 0; s0 < 8; s0++)
        {
            if ((set >> s0 & 1) == 0 || (seen >> s0 & 1) != 0) continue;
            n++;
            var st = new Stack<int>(); st.Push(s0); seen |= 1 << s0;
            while (st.Count > 0)
            {
                int q = st.Pop();
                for (int bit = 1; bit < 8; bit <<= 1)   // corners differing in one coordinate share a face
                    if ((set >> (q ^ bit) & 1) != 0 && (seen >> (q ^ bit) & 1) == 0) { seen |= 1 << (q ^ bit); st.Push(q ^ bit); }
            }
        }
        return n;
    }

    /// <summary>Per-corner normals: area-weighted average of the faces around the vertex whose normal is within the
    /// crease angle (cosine) of this face's normal.</summary>
    public static double[] CornerNormals(double[] V, int[] T, double creaseCos)
    {
        int m = T.Length / 3;
        var fn = new double[3 * m];
        var vf = new Dictionary<int, List<int>>();
        for (int t = 0; t < m; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
            double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
            fn[3 * t] = uy * vz - uz * vy; fn[3 * t + 1] = uz * vx - ux * vz; fn[3 * t + 2] = ux * vy - uy * vx;
            for (int s = 0; s < 3; s++) { if (!vf.TryGetValue(T[3 * t + s], out var l)) vf[T[3 * t + s]] = l = new List<int>(6); l.Add(t); }
        }
        double Len(int t) => Math.Sqrt(fn[3 * t] * fn[3 * t] + fn[3 * t + 1] * fn[3 * t + 1] + fn[3 * t + 2] * fn[3 * t + 2]);
        var o = new double[9 * m];
        for (int t = 0; t < m; t++)
        {
            double lt = Len(t);
            for (int s = 0; s < 3; s++)
            {
                double x = 0, y = 0, z = 0;
                foreach (int f in vf[T[3 * t + s]])
                {
                    double lf = Len(f);
                    if (lt > 0 && lf > 0 && (fn[3 * t] * fn[3 * f] + fn[3 * t + 1] * fn[3 * f + 1] + fn[3 * t + 2] * fn[3 * f + 2]) < creaseCos * lt * lf) continue;
                    x += fn[3 * f]; y += fn[3 * f + 1]; z += fn[3 * f + 2];
                }
                o[9 * t + 3 * s] = x; o[9 * t + 3 * s + 1] = y; o[9 * t + 3 * s + 2] = z;
            }
        }
        Normalize(o);
        return o;
    }

    /// <summary>Per-corner normals of a (welded) proxy taken from the original (<paramref name="OV"/>, <paramref name="OT"/>;
    /// <paramref name="ON"/> its authored per-vertex normals, zero where a vertex has none, or null), in the layout of
    /// <see cref="CornerNormals"/>, computed before the unwrap. Per corner: (1) the closest original point within
    /// <see cref="PartProxy.Limits.TransferReach"/> x <paramref name="radius"/>, preferring faces that face the same way as
    /// the proxy face (minDot <see cref="SameFacing"/>, as the projection does); (2) the original's normal there, interpolated over the corners
    /// of its triangle: the authored normals, else the original's own <see cref="CornerNormals"/> at the crease angle (so
    /// the original's hard edges stay hard); (3) averaged with the area-weighted normals (each the mean of its corner
    /// normals) of the original triangles within <paramref name="radius"/> of that point that lie within the crease angle
    /// of it; (4) used when it lies within <paramref name="creaseDeg"/> of the proxy face normal; otherwise, and where
    /// nothing is found, the proxy's own <see cref="CornerNormals"/>(…, 0.7) corner normal stays.</summary>
    public static double[] TransferNormals(double[] V, int[] T, double[] OV, int[] OT, double[]? ON, double radius, double creaseDeg)
    {
        var o = CornerNormals(V, T, 0.7);
        int m = T.Length / 3, om = OT.Length / 3;
        if (om == 0 || !(radius > 0) || !double.IsFinite(radius)) return o;
        double cos = Math.Cos(creaseDeg * Math.PI / 180);
        // the original's corner normals: authored where given, else smooth within the crease
        var ocn = CornerNormals(OV, OT, cos);
        if (ON != null && ON.Length == OV.Length)   // ignored when it does not fit the positions
            for (int c = 0; c < OT.Length; c++)
            {
                int v = OT[c];
                double x = ON[3 * v], y = ON[3 * v + 1], z = ON[3 * v + 2], l = Math.Sqrt(x * x + y * y + z * z);
                if (l > 0 && double.IsFinite(l)) { ocn[3 * c] = x / l; ocn[3 * c + 1] = y / l; ocn[3 * c + 2] = z / l; }
            }
        // per original triangle: its normal (the mean of its corner normals, unit) and its area
        var fs = new double[3 * om]; var area = new double[om];
        for (int t = 0; t < om; t++)
        {
            for (int k = 0; k < 3; k++) fs[3 * t + k] = ocn[9 * t + k] + ocn[9 * t + 3 + k] + ocn[9 * t + 6 + k];
            int a = OT[3 * t], b = OT[3 * t + 1], c = OT[3 * t + 2];
            double ux = OV[3 * b] - OV[3 * a], uy = OV[3 * b + 1] - OV[3 * a + 1], uz = OV[3 * b + 2] - OV[3 * a + 2];
            double vx = OV[3 * c] - OV[3 * a], vy = OV[3 * c + 1] - OV[3 * a + 1], vz = OV[3 * c + 2] - OV[3 * a + 2];
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            area[t] = Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
        }
        Normalize(fs);
        var index = new TriIndex(OV, OT);
        var near = new List<int>();
        double reach = PartProxy.Limits.TransferReach * radius;
        for (int t = 0; t < m; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
            double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
            double fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx, fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (!(fl > 0)) continue;
            fx /= fl; fy /= fl; fz /= fl;
            for (int s = 0; s < 3; s++)
            {
                int v = T[3 * t + s];
                // (1) the closest original point, same-facing faces first
                if (index.Closest(V[3 * v], V[3 * v + 1], V[3 * v + 2], reach, fx, fy, fz, SameFacing) is not { } hit) continue;
                // (2) the original's normal there
                double nx = 0, ny = 0, nz = 0, px = 0, py = 0, pz = 0;
                double[] w = { hit.U, hit.V, hit.W };
                for (int k = 0; k < 3; k++)
                {
                    int q = 9 * hit.Tri + 3 * k, ov = OT[3 * hit.Tri + k];
                    nx += w[k] * ocn[q]; ny += w[k] * ocn[q + 1]; nz += w[k] * ocn[q + 2];
                    px += w[k] * OV[3 * ov]; py += w[k] * OV[3 * ov + 1]; pz += w[k] * OV[3 * ov + 2];
                }
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!(nl > 0)) continue;
                nx /= nl; ny /= nl; nz /= nl;
                // (3) averaged with the original's triangles around that point (those within the crease of it)
                near.Clear();
                index.Near(px, py, pz, radius, near);
                double sx = 0, sy = 0, sz = 0;
                foreach (int f in near)
                {
                    if (fs[3 * f] * nx + fs[3 * f + 1] * ny + fs[3 * f + 2] * nz < cos) continue;
                    sx += area[f] * fs[3 * f]; sy += area[f] * fs[3 * f + 1]; sz += area[f] * fs[3 * f + 2];
                }
                double sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                if (sl > 0) { nx += sx / sl; ny += sy / sl; nz += sz / sl; }
                nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!(nl > 0)) continue;
                nx /= nl; ny /= nl; nz /= nl;
                // (4) within the crease of the proxy face, else the hull's corner normal stays
                if (nx * fx + ny * fy + nz * fz < cos) continue;
                (o[9 * t + 3 * s], o[9 * t + 3 * s + 1], o[9 * t + 3 * s + 2]) = (nx, ny, nz);
            }
        }
        return o;
    }

    /// <summary>The preference for same-facing faces (dot of the unit normals) of the hull's projection onto the original
    /// (<see cref="Hull"/>) and of the normal transfer (<see cref="TransferNormals"/>): one value for both.</summary>
    internal const double SameFacing = 0.2;

    /// <summary>The authored normals of the sources, merged as <see cref="Merge"/> merges the positions (zero for a source
    /// without), or null when no source has any.</summary>
    internal static double[]? MergeNormals(IList<Source> sources)
    {
        if (!sources.Any(s => s.N != null && s.N.Length == s.V.Length)) return null;
        var n = new double[sources.Sum(s => s.V.Length)];
        int at = 0;
        foreach (var s in sources)
        {
            if (s.N != null && s.N.Length == s.V.Length) Array.Copy(s.N, 0, n, at, s.V.Length);
            at += s.V.Length;
        }
        return n;
    }


    internal static void Normalize(double[] n)
    {
        for (int v = 0; v < n.Length / 3; v++)
        {
            double l = Math.Sqrt(n[3 * v] * n[3 * v] + n[3 * v + 1] * n[3 * v + 1] + n[3 * v + 2] * n[3 * v + 2]);
            if (l > 0) { n[3 * v] /= l; n[3 * v + 1] /= l; n[3 * v + 2] /= l; }
        }
    }

    /// <summary>Area-weighted vertex normals (unit length).</summary>
    public static double[] VertexNormals(double[] V, int[] T)
    {
        var n = new double[V.Length];
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
            double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
            double fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx;
            foreach (int v in new[] { a, b, c }) { n[3 * v] += fx; n[3 * v + 1] += fy; n[3 * v + 2] += fz; }
        }
        for (int v = 0; v < n.Length / 3; v++)
        {
            double l = Math.Sqrt(n[3 * v] * n[3 * v] + n[3 * v + 1] * n[3 * v + 1] + n[3 * v + 2] * n[3 * v + 2]);
            if (l > 0) { n[3 * v] /= l; n[3 * v + 1] /= l; n[3 * v + 2] /= l; }
        }
        return n;
    }
}
