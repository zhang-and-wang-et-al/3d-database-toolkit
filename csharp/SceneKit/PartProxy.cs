namespace SceneKit;

/// <summary>What a part is for the per-part proxy: dropped (tiny), a convex hull or decimated original (small, thin) or a
/// voxel hull (bulk). Set by the classification; part discovery leaves every part bulk.</summary>
internal enum PartClass { Tiny, Small, Thin, Bulk }

/// <summary>How a part's proxy was made: a voxel hull, a convex hull, the decimated original, or the original as it is.
/// JSON names: voxel, convex, decimated, kept.</summary>
internal enum PartMethod { Voxel, Convex, Decimated, Kept }

/// <summary>Per-part proxies (car package). Step 1, part discovery: below a target, every moving node (the
/// remove_occluded rule, <see cref="Optimizer.Moving"/>) is gathered on its own in its own frame, every switch child
/// (switched-off ones too) separately; the rest is split into connected components (units), and units that lie within
/// the closing gap of each other are clustered into parts, so body panels become one body. Step 2 classifies the parts;
/// step 3 builds a proxy per part (<see cref="BuildParts"/>) and bakes one atlas for all of them (<see cref="BakeAtlas"/>).</summary>
public static partial class PartProxy
{
    /// <summary>One drawn geometry: positions (3 per vertex of <paramref name="G"/>) in its owner's frame, the triangles of
    /// <see cref="Optimizer.Normalized"/>, the state that applies (merged down the path) and the path from the target down
    /// to the geometry (both included).</summary>
    internal sealed record Piece(Node Geom, GeomData G, double[] V, int[] T, StateSet? State, Node[] Path, double[]? World = null);

    /// <summary>One connected component of a piece: its triangles (numbers in the piece's T), named after its geode or FLT
    /// group.</summary>
    internal sealed class Unit
    {
        public int Piece;
        public int[] Tris = Array.Empty<int>();
        public string Name = "";
        public Node? FltGroup;
    }

    /// <summary>A cluster of units: its own vertices (owner frame) and triangles, the piece of each triangle, its class and
    /// its bounding box sides (A &gt;= B &gt;= C) and diagonal. A/B/C are bounding-box sides; <see cref="Extents"/> (PCA) is what the thin rule uses.</summary>
    internal sealed class Part
    {
        public string Name = "";
        public List<Unit> Units = new();
        public double[] V = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public int[] PieceOfTri = Array.Empty<int>();
        public PartClass Class = PartClass.Bulk;
        public double A, B, C, Diag;
    }

    /// <summary>What one owner (the target, or a moving node) holds. <see cref="Frame"/> maps the owner's frame into the
    /// target's parent frame (for the target: its own matrix, as its pieces already have it applied).</summary>
    internal sealed class Assembly
    {
        public Node Owner = null!;
        public double[] Frame = Mat.Identity();
        public List<Piece> Pieces = new();
        public List<Part> Parts = new();
        public List<MovingPart> Moving = new();
        public int TinyDropped;
        /// <summary>Set by <see cref="Classify"/>; <see cref="BuildParts"/> does not classify an assembly twice.</summary>
        internal bool Classified;
        public int LodAlternativesDropped;
        public double Diag;

        /// <summary>The static pieces of this owner, then those of each moving part, depth first.</summary>
        public IEnumerable<Piece> AllPieces()
        {
            foreach (var p in Pieces) yield return p;
            foreach (var m in Moving)
                foreach (var p in m.Inner.AllPieces()) yield return p;
        }
    }

    /// <summary>A moving node below an owner (for a switch: one child of it, <see cref="SwitchChild"/> &gt;= 0), the static
    /// matrices between the owner and the node (identity when there are none) and what it holds.</summary>
    internal sealed class MovingPart
    {
        public Node Node = null!;
        public int SwitchChild = -1;
        public double[] StaticAbove = Mat.Identity();
        public Assembly Inner = null!;
        /// <summary>An animated group (a sequence, an FLT animated group, any moving node that is neither a switch nor a
        /// transform): its children are frames of an animation, so they are neither merged into one proxy nor split; the
        /// node is kept as it is. <see cref="Inner"/> holds its pieces (they count as drawn and are seen) but no parts.</summary>
        public bool Animated;
    }

    /// <summary>The tolerance Decimate welds positions with.</summary>
    internal static double WeldTolerance(double diag) => diag * 1e-9;

    /// <summary>How often discovery ran (tests of the estimate).</summary>
    internal static int DiscoverCalls;

    /// <summary>Parts and moving parts below a target: its pieces in the target's parent frame (its matrix applied, as
    /// DrawnBelow does), each moving node's in its own frame (its matrix not applied). The target is never a moving part
    /// of itself.</summary>
    internal static Assembly Discover(Node target, double gap) => Discover(target, _ => gap);

    /// <summary><see cref="Discover(Node, double)"/> with the gap taken from the gathered pieces (before clustering).</summary>
    static Assembly Discover(Node target, Func<Assembly, double> gapOf)
    {
        Interlocked.Increment(ref DiscoverCalls);
        var asm = Gather(target, new[] { target }, Array.Empty<Node>(), target.Matrix != null ? (double[])target.Matrix.Clone() : Mat.Identity(), 0);
        // the object diagonal D is that of all pieces of the target, the moving ones placed through their frames (in their
        // own frames they would stretch the box from the origin to the object); a unit smaller than SmallShareOfD of it
        // never joins a cluster
        var (_, diag) = ObjectExtent(asm);
        double d = double.IsFinite(diag) ? diag : 0;
        Cluster(asm, gapOf(asm), d);
        return asm;
    }

    /// <summary>Fills Parts and Diag of an assembly and of every moving part below it.</summary>
    static void Cluster(Assembly asm, double gap, double objectDiag)
    {
        var r = FromPieces(asm.Pieces, asm.Owner, asm.Frame, gap, SmallRule(asm.Pieces, objectDiag));
        (asm.Parts, asm.Diag) = (r.Parts, r.Diag);
        foreach (var m in asm.Moving.Where(m => !m.Animated)) Cluster(m.Inner, gap, objectDiag);   // an animated group is kept as it is
    }

    /// <summary>One owner: walks <paramref name="roots"/> (each path prefixed with <paramref name="above"/>, the path from
    /// the target down to them), stopping at moving nodes, and recurses into those.</summary>
    static Assembly Gather(Node owner, IReadOnlyList<Node> roots, Node[] above, double[] frame, int lodDropped)
    {
        var pieces = new List<Piece>();
        var stopped = new List<(Node node, double[] world, Node[] path)>();
        foreach (var root in roots)
        {
            if (root.NodeMask == 0) continue;
            if (!ReferenceEquals(root, owner) && Optimizer.Moving(root)) { stopped.Add((root, Mat.Identity(), new[] { root })); continue; }
            var drawn = SoftRender.GeometryPaths(root, null, Optimizer.Moving, stopped, out int dropped);
            lodDropped += dropped;
            foreach (var (n, w, _, path) in drawn)
            {
                var (g, _) = Optimizer.Normalized(n.Geom!);
                var T = g.Triangles();
                int nv = g.NumVertices;
                if (g.Vertices == null || T.Length == 0 || T.Any(i => i < 0 || i >= nv)) continue;
                var V = new double[3 * nv];
                int cs = g.Vertices.Comps;
                for (int i = 0; i < nv; i++)
                {
                    double x = g.Vertices.Data[i * cs], y = cs > 1 ? g.Vertices.Data[i * cs + 1] : 0, z = cs > 2 ? g.Vertices.Data[i * cs + 2] : 0;
                    (V[3 * i], V[3 * i + 1], V[3 * i + 2]) = Mat.Apply(w, x, y, z);
                }
                var full = above.Concat(path).ToArray();
                pieces.Add(new Piece(n, g, V, T, Optimizer.MergedState(full.Select(p => p.StateSet)), full, w));
            }
        }
        var asm = new Assembly { Owner = owner, Frame = frame, Pieces = pieces };
        asm.LodAlternativesDropped = lodDropped;
        foreach (var (s, _, path) in stopped)
        {
            var full = above.Concat(path).ToArray();   // target ... s
            int at = Array.IndexOf(full, owner);
            var staticAbove = SoftRender.PathMatrix(full.Skip(at + 1).Take(full.Length - at - 2));
            var inner = Mat.Mul(Mat.Mul(frame, staticAbove), s.Matrix ?? Mat.Identity());
            if (s.Kind == Kind.Switch)
            {
                var kids = s.AllChildren();
                for (int i = 0; i < kids.Count; i++)
                    asm.Moving.Add(new MovingPart { Node = s, SwitchChild = i, StaticAbove = staticAbove, Inner = Gather(s, new[] { kids[i] }, full, inner, 0) });
            }
            else if (s.Matrix != null)
            {
                // its children, so its own matrix stays out (the walk applies the start node's matrix); LOD siblings as the walk
                IReadOnlyList<Node> kids = s.AllChildren();
                int hidden = 0;
                if (Analyzer.HiddenLodSiblings(kids) is bool[] h) { hidden = h.Count(x => x); kids = kids.Where((_, i) => !h[i]).ToList(); }
                asm.Moving.Add(new MovingPart { Node = s, StaticAbove = staticAbove, Inner = Gather(s, kids, full, inner, hidden), Animated = AnimatedGroup(s) });
            }
            // neither a switch nor a transform (a sequence, an FLT animated group): kept as it is, its pieces gathered for the counts
            else asm.Moving.Add(new MovingPart { Node = s, StaticAbove = staticAbove, Inner = Gather(s, new[] { s }, full[..^1], inner, 0), Animated = AnimatedGroup(s) });
        }
        return asm;
    }

    /// <summary>A moving node whose children are frames of an animation, kept as it is (<see cref="MovingPart.Animated"/>):
    /// neither a switch nor a DOF, and a sequence, a multi-switch, an FLT animated group or a moving node without a
    /// matrix (an update callback on a plain group).</summary>
    internal static bool AnimatedGroup(Node n) =>
        n.Kind != Kind.Switch && n.Cls != "FLT DOF" && OsgReader.Short(n.Cls) != "DOFTransform"
        && (n.Notes.Contains("animated group") || OsgReader.Short(n.Cls) is "Sequence" or "MultiSwitch" || n.Matrix == null);

    /// <summary>Units and parts of an owner's pieces (no moving parts): each piece is welded by position
    /// (<see cref="WeldTolerance"/>) and split into connected components; units that are not small and lie within
    /// <paramref name="gap"/> of each other (bounding boxes first, then the closest triangle distance from sample vertices)
    /// form one part. A small unit (<paramref name="isSmall"/>; null: none is) never joins and is a part of its own.</summary>
    internal static Assembly FromPieces(List<Piece> pieces, Node owner, double[] frame, double gap, Func<Unit, bool>? isSmall = null)
    {
        var asm = new Assembly { Owner = owner, Frame = frame, Pieces = pieces };
        var lo = new double[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new double[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var p in pieces) Grow(p.V, p.T, lo, hi);
        if (pieces.Count == 0) return asm;
        asm.Diag = Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi[c] - lo[c]) * (hi[c] - lo[c])));
        double q = WeldTolerance(asm.Diag > 0 ? asm.Diag : 1.0);
        var ctr = Enumerable.Range(0, 3).Select(c => (lo[c] + hi[c]) / 2).ToArray();

        // ---------------------------------------------------------------- units: connected components per piece
        var units = new List<Unit>();
        for (int pi = 0; pi < pieces.Count; pi++)
        {
            var p = pieces[pi];
            int nv = p.V.Length / 3, m = p.T.Length / 3;
            var weld = new Dictionary<(long, long, long), int>();
            var pos = new int[nv];
            for (int i = 0; i < nv; i++)
            {
                var key = ((long)Math.Round((p.V[3 * i] - ctr[0]) / q), (long)Math.Round((p.V[3 * i + 1] - ctr[1]) / q), (long)Math.Round((p.V[3 * i + 2] - ctr[2]) / q));
                if (!weld.TryGetValue(key, out int id)) weld[key] = id = weld.Count;
                pos[i] = id;
            }
            var uf = new UnionFind(weld.Count);
            for (int t = 0; t < m; t++) { uf.Join(pos[p.T[3 * t]], pos[p.T[3 * t + 1]]); uf.Join(pos[p.T[3 * t]], pos[p.T[3 * t + 2]]); }
            var byRoot = new Dictionary<int, List<int>>();
            for (int t = 0; t < m; t++)
            {
                int r = uf.Find(pos[p.T[3 * t]]);
                if (!byRoot.TryGetValue(r, out var l)) byRoot[r] = l = new List<int>();
                l.Add(t);
            }
            int at = Array.IndexOf(p.Path, owner);
            var below = p.Path.Skip(at + 1).ToArray();
            var group = below.LastOrDefault(x => x.Cls == "FLT Group");
            var geode = below.LastOrDefault(x => x.Kind == Kind.Geode);
            string name = geode?.Name is { Length: > 0 } gn ? gn : group?.Name is { Length: > 0 } fn ? fn : p.Geom.Name;
            foreach (var tris in byRoot.Values.OrderBy(l => l[0]))   // in the order of their first triangle
                units.Add(new Unit { Piece = pi, Tris = tris.ToArray(), Name = name, FltGroup = group });
        }

        // ---------------------------------------------------------------- clusters: units within the gap
        int nu = units.Count;
        var ulo = new double[nu][]; var uhi = new double[nu][];
        for (int u = 0; u < nu; u++)
        {
            ulo[u] = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            uhi[u] = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            Grow(pieces[units[u].Piece].V, UnitT(pieces, units[u]), ulo[u], uhi[u]);
        }
        var small = units.Select(u => isSmall?.Invoke(u) ?? false).ToArray();
        var index = new TriIndex?[nu];
        TriIndex Index(int u) => index[u] ??= new TriIndex(pieces[units[u].Piece].V, UnitT(pieces, units[u]));
        var samples = new int[nu][];
        bool Near(int a, int b)   // a sample vertex of b (at most about 2000) within the gap of a's triangles
        {
            var V = pieces[units[b].Piece].V;
            var verts = samples[b] ??= UnitT(pieces, units[b]).Distinct().ToArray();
            int step = Math.Max(1, verts.Length / 2000);
            var ix = Index(a);
            for (int i = 0; i < verts.Length; i += step)
                if (ix.Closest(V[3 * verts[i]], V[3 * verts[i] + 1], V[3 * verts[i] + 2], gap) != null) return true;
            return false;
        }
        var cl = new UnionFind(nu);
        for (int a = 0; a < nu; a++)
        {
            if (small[a]) continue;
            for (int b = a + 1; b < nu; b++)
            {
                if (small[b] || cl.Find(a) == cl.Find(b)) continue;
                bool apart = false;
                for (int c = 0; c < 3 && !apart; c++) apart = ulo[a][c] > uhi[b][c] + gap || ulo[b][c] > uhi[a][c] + gap;
                if (apart) continue;
                if (Near(a, b) || Near(b, a)) cl.Join(a, b);
            }
        }

        // ---------------------------------------------------------------- parts
        var clusters = new Dictionary<int, List<int>>();
        for (int u = 0; u < nu; u++)
        {
            int r = cl.Find(u);
            if (!clusters.TryGetValue(r, out var l)) clusters[r] = l = new List<int>();
            l.Add(u);
        }
        foreach (var members in clusters.Values.OrderBy(l => l[0]))   // in the order of their first unit
        {
            var part = new Part { Units = members.Select(u => units[u]).ToList() };
            part.Name = part.Units.OrderByDescending(u => u.Tris.Length).First().Name;
            var map = new Dictionary<(int, int), int>();
            var V = new List<double>(); var T = new List<int>(); var src = new List<int>();
            foreach (var u in part.Units)
            {
                var p = pieces[u.Piece];
                foreach (int t in u.Tris)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int v = p.T[3 * t + k];
                        if (!map.TryGetValue((u.Piece, v), out int id))
                        {
                            map[(u.Piece, v)] = id = V.Count / 3;
                            V.Add(p.V[3 * v]); V.Add(p.V[3 * v + 1]); V.Add(p.V[3 * v + 2]);
                        }
                        T.Add(id);
                    }
                    src.Add(u.Piece);
                }
            }
            (part.V, part.T, part.PieceOfTri) = (V.ToArray(), T.ToArray(), src.ToArray());
            var plo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var phi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            Grow(part.V, part.T, plo, phi);
            var sides = Enumerable.Range(0, 3).Select(c => phi[c] - plo[c]).OrderByDescending(x => x).ToArray();
            (part.A, part.B, part.C) = (sides[0], sides[1], sides[2]);
            part.Diag = Math.Sqrt(sides.Sum(x => x * x));
            asm.Parts.Add(part);
        }
        return asm;
    }

    /// <summary>The thresholds of the classification, in one place. None of them is a pass option.</summary>
    internal static class Limits
    {
        /// <summary>A part with a diagonal of at most this many e is tiny.</summary>
        public const double TinyDiagInE = 2;
        /// <summary>The same in cells, in keep mode (e = 0).</summary>
        public const double TinyInCells = 2;
        /// <summary>A part with a diagonal below this share of the object's diagonal D is small.</summary>
        public const double SmallShareOfD = 0.25;
        /// <summary>A part whose smallest extent is below this many e is thin ...</summary>
        public const double ThinInE = 3;
        /// <summary>... or below this many cells, whichever is more.</summary>
        public const double ThinInCells = 2;
        /// <summary>The proxy estimate may be this many times off before it warns.</summary>
        public const double EstimateWarnFactor = 3;
        /// <summary>The proxy estimate gives up above this many triangles below the selection ("Selection too large to
        /// estimate"): discovery would stall the window, and the number would be a guess anyway.</summary>
        public const long EstimateMaxTris = 2_000_000;
        /// <summary>The fewest and most triangles the estimate gives a bulk part.</summary>
        public const int EstimateMinBulkTris = 12, EstimateMaxBulkTris = 20_000;
        /// <summary>The proxy may take this many times the estimated build time before it warns.</summary>
        public const double BuildTimeFactor = 3;
        /// <summary>Views a part is measured from.</summary>
        public const int MeasureViews = 42;
        /// <summary>Surface samples a part is measured with.</summary>
        public const int MeasureSamples = 20000;
        /// <summary>A small part with this many triangles or fewer is kept as it is.</summary>
        public const int SmallPartTris = 40;
        /// <summary>The fewest triangles the keep-mode budget gives a part ...</summary>
        public const int MinPartTris = 8;
        /// <summary>... and a voxel hull.</summary>
        public const int MinHullTris = 12;
        /// <summary>The decimated original of a small part is measured and, while too far, the run repeated: at most this many runs ...</summary>
        public const int MaxDecimateAttempts = 4;
        /// <summary>... each with maxError x this x tol / measured deviation.</summary>
        public const double DecimateTighten = 0.9;
        /// <summary>A cell counts as coarser than e when it is more than e x (1 + this) (rounding is not coarser).</summary>
        public const double CellSlack = 1e-9;
        /// <summary>Normals from the original: the closest original point is searched within this many radii (max(e, cell)).</summary>
        public const double TransferReach = 3;
        /// <summary>Within: a bulk part's voxel hull asks for cells of this share of the attempt's error (e / 2 at first,
        /// finer with each tighter attempt), up to the voxels (the upper limit).</summary>
        public const double WithinCellShare = 0.5;
        /// <summary>Within: after this many seconds of an object's build no further voxel hull attempt runs; the remaining
        /// bulk parts take the decimated original or are kept ("time budget reached after {n} s").</summary>
        public const double WithinBudgetSeconds = 120;
    }

    /// <summary>The extents of a mesh along its principal axes (a &gt;= b &gt;= c): the axes come from a PCA of the
    /// area-weighted triangle surface (second moment), the extents from the vertices projected on them.</summary>
    internal static (double a, double b, double c) Extents(double[] V, int[] T)
    {
        int m = T.Length / 3;
        if (m == 0) return (0, 0, 0);
        // second moment of the surface, area weighted: per triangle A/12 * (sum p p^T + (sum p)(sum p)^T), so that the two
        // triangles of a quad do not bias the axes (their centroids alone lie on one diagonal)
        var mean = new double[3];
        var cov = new double[3, 3];
        double sum = 0;
        for (int t = 0; t < m; t++)
        {
            var p = new double[3][];
            for (int k = 0; k < 3; k++) p[k] = new[] { V[3 * T[3 * t + k]], V[3 * T[3 * t + k] + 1], V[3 * T[3 * t + k] + 2] };
            double ux = p[1][0] - p[0][0], uy = p[1][1] - p[0][1], uz = p[1][2] - p[0][2];
            double vx = p[2][0] - p[0][0], vy = p[2][1] - p[0][1], vz = p[2][2] - p[0][2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double area = Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
            sum += area;
            for (int r = 0; r < 3; r++)
            {
                double s = p[0][r] + p[1][r] + p[2][r];
                mean[r] += area * s / 3;
                for (int c = 0; c < 3; c++)
                    cov[r, c] += area / 12 * (p[0][r] * p[0][c] + p[1][r] * p[1][c] + p[2][r] * p[2][c] + s * (p[0][c] + p[1][c] + p[2][c]));
            }
        }
        if (sum <= 0) return BoxExtents(V, T);
        for (int r = 0; r < 3; r++) mean[r] /= sum;
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) cov[r, c] = cov[r, c] / sum - mean[r] * mean[c];
        var axes = Jacobi(cov);   // columns: eigenvectors
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (int v in T)
            for (int a = 0; a < 3; a++)
            {
                double d = V[3 * v] * axes[0, a] + V[3 * v + 1] * axes[1, a] + V[3 * v + 2] * axes[2, a];
                lo[a] = Math.Min(lo[a], d); hi[a] = Math.Max(hi[a], d);
            }
        var e = Enumerable.Range(0, 3).Select(a => hi[a] - lo[a]).OrderByDescending(x => x).ToArray();
        return (e[0], e[1], e[2]);
    }

    /// <summary>The extents along the coordinate axes, for a mesh without area.</summary>
    static (double a, double b, double c) BoxExtents(double[] V, int[] T)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Grow(V, T, lo, hi);
        var e = Enumerable.Range(0, 3).Select(c => hi[c] - lo[c]).OrderByDescending(x => x).ToArray();
        return (e[0], e[1], e[2]);
    }

    /// <summary>Eigenvectors (as columns) of a symmetric 3x3 matrix (cyclic Jacobi).</summary>
    static double[,] Jacobi(double[,] s)
    {
        var a = (double[,])s.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            if (off < 1e-300) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    double th = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = (th >= 0 ? 1 : -1) / (Math.Abs(th) + Math.Sqrt(th * th + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - sn * akq; a[k, q] = sn * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - sn * aqk; a[q, k] = sn * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - sn * vkq; v[k, q] = sn * vkp + c * vkq;
                    }
                }
        }
        return v;
    }

    /// <summary>Classifies the parts of an assembly and of every moving part below it (the parts come from
    /// <see cref="Discover"/>, which applied the small rule): tiny (diagonal &lt;= 2 e), small (diagonal below
    /// <see cref="Limits.SmallShareOfD"/> of <paramref name="objectDiag"/>), thin (smallest extent below max(2 cells, 3 e))
    /// or bulk. In keep mode the caller passes e = 0 and only cells count. A tiny part within e (keep mode: 2 cells) of
    /// another part is dropped and counted in <see cref="Assembly.TinyDropped"/>; a tiny part that touches nothing stays.</summary>
    internal static void Classify(Assembly asm, double e, double cell, double objectDiag)
    {
        double tiny = e > 0 ? Limits.TinyDiagInE * e : Limits.TinyInCells * cell;
        double thin = Math.Max(Limits.ThinInCells * cell, Limits.ThinInE * e);
        double touch = e > 0 ? e : 2 * cell;
        foreach (var p in asm.Parts)
        {
            if (p.Diag <= tiny) p.Class = PartClass.Tiny;
            else if (p.Diag < Limits.SmallShareOfD * objectDiag) p.Class = PartClass.Small;
            else p.Class = Extents(p.V, p.T).c < thin ? PartClass.Thin : PartClass.Bulk;
        }
        var drop = new List<Part>();
        var index = new Dictionary<Part, TriIndex>();   // one per non-tiny part, built when first needed
        foreach (var p in asm.Parts.Where(x => x.Class == PartClass.Tiny))
        {
            var verts = p.T.Distinct().ToArray();
            int step = Math.Max(1, verts.Length / 2000);
            foreach (var o in asm.Parts.Where(x => x.Class != PartClass.Tiny))
            {
                if (!index.TryGetValue(o, out var ix)) index[o] = ix = new TriIndex(o.V, o.T);
                bool near = false;
                for (int i = 0; i < verts.Length && !near; i += step)
                    near = ix.Closest(p.V[3 * verts[i]], p.V[3 * verts[i] + 1], p.V[3 * verts[i] + 2], touch) != null;
                if (near) { drop.Add(p); break; }
            }
        }
        foreach (var p in drop) asm.Parts.Remove(p);
        asm.TinyDropped = drop.Count;
        asm.Classified = true;
        foreach (var m in asm.Moving) Classify(m.Inner, e, cell, objectDiag);
    }

    // ------------------------------------------------------------------------------------------------ step 3: building

    /// <summary>One part's proxy, in its owner's frame. <see cref="Cell"/> is the voxel cell it was built with (kept: the
    /// object's cell); the bake searches the original within 3 cells.</summary>
    internal sealed class PartResult
    {
        public Part Part = null!;
        public PartMethod Method;
        public double[] V = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public double DevM = double.NaN;
        public double Cell;
        /// <summary>Decimated: the part vertex (in <see cref="Part.V"/>) each vertex comes from (Decimate's Kept), which
        /// carries the original's texture coordinates and colours; else null.</summary>
        public int[]? From;
        /// <summary>Decimated with normals = original: the corner normals from the original (<see cref="Proxy.TransferNormals"/>,
        /// set by <see cref="BakeAtlas"/>, in the layout of <see cref="Proxy.CornerNormals"/>); else null (normals from the
        /// triangles).</summary>
        public double[]? Corner;
    }

    internal sealed class BuildOptions
    {
        public int TextureSize = 512;
        public ExtraFill Fill = ExtraFill.White;
        public ExtraFill ClassFill = ExtraFill.ClassFill(0);
        public Action<string>? Progress;
        public double WorldScale = 1;
        // read from Tasks 9-11 on (within mode, normals from the original, per-part bake distance); ErrorMetres is 0 in keep mode
        public ProxyNormals Normals = ProxyNormals.Hull;
        public double Crease = 45;
        public double ErrorMetres = 0;
        public double Cell = 0;
        /// <summary>The ground rule of the build: full, above (the hidden-triangle run sees from above the horizon only) or
        /// cut (as above, and the down-facing bottom at <see cref="Plane"/> is cut off every proxy).</summary>
        public ProxyGround Ground = ProxyGround.Full;
        /// <summary>The ground plane in the target's parent frame (<see cref="GroundOf"/>); null with ground cut: the
        /// object's lowest point, its parent frame taken as the world.</summary>
        public GroundPlane? Plane;
        /// <summary>Why <see cref="BuildParts"/> returned null (it has no result to hold the notes then).</summary>
        public List<string> Notes = new();
        /// <summary>Within: the wall-clock budget of the object (seconds from the start of <see cref="BuildParts"/>) after
        /// which no further voxel hull attempt runs; <see cref="Limits.WithinBudgetSeconds"/> unless a test sets it.</summary>
        public double WithinBudgetSeconds = Limits.WithinBudgetSeconds;
    }

    /// <summary>One output geometry: the static one (<see cref="Owner"/> null) or one per moving part, in its owner's
    /// frame. <see cref="PartOfTri"/> is the number of each triangle's part in the owner's <see cref="ObjectResult.Static"/>.</summary>
    internal sealed class ProxyMesh
    {
        public string Name = "";
        public MovingPart? Owner;
        public double[] V = Array.Empty<double>(), N = Array.Empty<double>(), UV = Array.Empty<double>();
        public int[] T = Array.Empty<int>();
        public int[] PartOfTri = Array.Empty<int>();
    }

    /// <summary>The proxy of one owner: its static parts and, per moving part, what was built below it (null: nothing).
    /// <see cref="Meshes"/> (filled by <see cref="BakeAtlas"/>), <see cref="Report"/> and <see cref="Notes"/> are those of
    /// the whole object and only set on the result <see cref="BuildParts"/> returns.</summary>
    internal sealed class ObjectResult
    {
        public Assembly Asm = null!;
        public List<PartResult> Static = new();
        public List<(MovingPart M, ObjectResult? Inner)> Moving = new();
        public List<ProxyMesh> Meshes = new();
        public ProxyReport Report = new();
        public List<string> Notes = new();
        /// <summary>The cell of the object's grid (its extent over the voxels) the build used; the result line names distances in it.</summary>
        public double Cell;
    }

    /// <summary>Builds the per-part proxy of an object in keep mode: <paramref name="sources"/> holds one source per piece
    /// in <see cref="Assembly.AllPieces"/> order. The parts are classified here on the object's grid (its extent / voxels;
    /// the caller discovers with a gap of 2 cells) and must not be classified beforehand (the call classifies, which drops
    /// tiny parts from <paramref name="asm"/>; an already classified assembly is not classified again).
    /// <see cref="ProxyParams.Ratio"/> of the object's triangles are split over the bulk parts (moving ones included) by
    /// surface area, at least <see cref="Limits.MinHullTris"/> per hull. Every bulk part gets a voxel hull of its own sources
    /// on the object's cell, not its own: a part's resolution is ceil(its largest bounding box side / object cell), clamped
    /// to 8 .. <see cref="ProxyParams.Voxels"/>. A bulk part without a hull keeps its triangles. Small, thin and floating
    /// tiny parts get <see cref="BuildSmall"/> (a convex hull, the decimated original or the original as it is). One unit,
    /// one cluster and no moving parts: today's single hull. The ground (<see cref="BuildOptions.Ground"/>): with cut, every
    /// voxel hull, convex hull and decimated original is cut at <see cref="BuildOptions.Plane"/> (taken into each moving
    /// part's frame) within max(e, cell); then, with every ground, <see cref="RemoveHidden"/> drops the static proxy
    /// triangles nobody sees (above and cut: from above the horizon only). Null when there are no triangles, no part is left after
    /// dropping tiny ones, the object has no extent or every part kept its original (the reason goes to
    /// <see cref="BuildOptions.Notes"/>). <see cref="ProxyReport.BuildMs"/> excludes the bake, and the draw calls count the
    /// pieces and the output meshes; replace_proxy sets all three for the whole build after <see cref="BakeAtlas"/>. The
    /// triangles and draw calls of the report count drawn geometry only (<see cref="DrawnPiece"/>: below a switch child
    /// that is off nothing is drawn), before and after; the keep-mode budget is still ratio x every piece's triangles.</summary>
    internal static ObjectResult? BuildParts(Assembly asm, IReadOnlyList<Proxy.Source> sources, ProxyParams p, BuildOptions o)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pieces = asm.AllPieces().ToList();
        if (pieces.Count != sources.Count || pieces.Where((pc, i) => pc.T.Length != sources[i].T.Length).Any())
            throw new ArgumentException("one source per piece, in AllPieces order", nameof(sources));
        long before = sources.Sum(s => (long)s.T.Length / 3);
        if (before == 0) return null;
        var srcOf = SourceIndex(asm);

        // the object's grid: its extent in the target's parent frame over the voxels; within, cells of e (in the target's
        // parent frame: the world e over the largest scale of its world matrix), at most the voxels along the extent
        var (ext, diag) = ObjectExtent(asm);
        if (!(ext > 0)) { o.Notes.Add("object has no extent"); return null; }
        double eWorld = p.Within ? (o.ErrorMetres > 0 ? o.ErrorMetres : p.ErrorMetres()) : 0;
        if (!(eWorld > 0))
        {
            // within without a positive error (0, negative or NaN) builds as keep mode, and says so
            if (p.Within) o.Notes.Add("within needs max_error > 0");
            eWorld = 0;
        }
        double worldScale = o.WorldScale > 0 && double.IsFinite(o.WorldScale) ? o.WorldScale : 1;
        double e = eWorld / worldScale;
        double cell = ObjectCell(ext, e, p.Voxels);

        var r = new ObjectResult { Asm = asm, Cell = cell };
        // within: the visibility of the original, once per object, for every part and every attempt
        var within = e > 0 ? new WithinRun { E = e, Visible = VisibleOriginal(asm, o.Ground), SrcOf = srcOf, Watch = watch, BudgetSeconds = o.WithinBudgetSeconds } : null;
        if (e > 0 && cell > e * (1 + Limits.CellSlack)) r.Notes.Add(CoarseCellNote(cell * worldScale, eWorld));
        // the ground: cut needs the plane (in the target's parent frame) and tol = max(e, cell)
        var plane = o.Ground == ProxyGround.Cut ? o.Plane ?? GroundOf(asm, Mat.Identity()) : (GroundPlane?)null;
        double tol = Math.Max(e, cell);   // e from the zeroed eWorld (never NaN)
        if (asm.Moving.Count == 0 && asm.Parts.Count == 1 && asm.Parts[0].Units.Count == 1)
        {
            r.Notes.Add("1 part found, using the single hull");
            if (within != null)
                r.Static.Add(WithinPart(asm, asm.Parts[0], () => sources.ToList(), within, cell, p.Voxels, plane, tol, null, r.Notes, o.Progress));
            else
            {
                var h = Proxy.Hull(sources.ToList(), new Proxy.Options { TargetTris = (int)Math.Max(12, before * p.Ratio), Resolution = p.Voxels, Progress = o.Progress, Cut = plane, CutTolerance = tol });
                if (h is { CutWouldEmpty: true }) r.Notes.Add(UncutNote(asm.Parts[0]));
                r.Static.Add(Result(asm.Parts[0], h, cell));
            }
            if (within != null && r.Static[0].Method == PartMethod.Kept) return WithinFailed(r, within, o, eWorld, worldScale);
        }
        else
        {
            if (!asm.Classified) Classify(asm, e, cell, diag);
            if (!Owners(asm).Any(x => x.A.Parts.Count > 0)) { o.Notes.Add("no parts left after dropping tiny parts"); return null; }
            // the budget: ratio x the object's triangles, split by surface area (in the target's parent frame)
            var area = new Dictionary<Part, double>(ReferenceEqualityComparer.Instance);
            foreach (var (a, toParent) in Owners(asm))
                foreach (var part in a.Parts.Where(x => x.Class == PartClass.Bulk)) area[part] = Area(Place(part.V, toParent), part.T);
            double total = p.Ratio * before, sum = area.Values.Sum();
            // only bulk parts get a voxel hull and a share; small, thin and tiny parts are convex hulls or decimated originals
            var budget = area.ToDictionary(kv => kv.Key, kv => Math.Max(Limits.MinHullTris,
                (int)Math.Round(sum > 0 ? total * kv.Value / sum : total / area.Count)), (IEqualityComparer<Part>)ReferenceEqualityComparer.Instance);
            BuildOwner(r, sources, srcOf, budget, p, o, cell, plane, tol, null, within: within);
            if (within != null && AllResults(r).SelectMany(x => x.Static).All(x => x.Method == PartMethod.Kept))
                return WithinFailed(r, within, o, eWorld, worldScale);
            if (AllResults(r).SelectMany(x => x.Static).All(x => x.Method == PartMethod.Kept))
            {
                o.Notes.Add("no part could be replaced; left unchanged");
                return null;
            }
        }
        // step 8: the static proxy triangles nobody sees (inside another part's hull, or underneath with ground above / cut)
        RemoveHidden(r, o.Ground, o.Progress);
        // within: each part measured again on its final geometry
        if (within != null) Remeasure(r, within);

        // ---------------------------------------------------------------- report
        var all = AllResults(r).ToList();
        var parts = all.SelectMany(x => x.Static).ToList();
        var moving = AllMoving(r).ToList();
        var dev = parts.Where(x => !double.IsNaN(x.DevM)).Select(x => x.DevM).ToList();
        // the counts are of what is drawn: below a switch child that is off nothing is (ruling T16-d)
        var drawnParts = DrawnResults(r, true).Where(x => x.Drawn).SelectMany(x => x.R.Static).ToList();
        // the largest deviation over the parts (gaps between parts do not count), in world metres
        double? reached = dev.Count > 0 ? dev.Max() * worldScale : null;
        r.Report = new ProxyReport
        {
            Object = asm.Owner.Name,
            Parts = parts.Count,
            MovingParts = moving.Count,
            TinyDropped = Owners(asm).Sum(x => x.A.TinyDropped),
            PerPart = parts.Select(x => new ProxyReport.PartEntry
            {
                Name = x.Part.Name, Class = x.Part.Class.ToString().ToLowerInvariant(), Method = x.Method.ToString().ToLowerInvariant(),
                Tris = x.T.Length / 3, DevM = double.IsNaN(x.DevM) ? null : x.DevM * worldScale,
            }).ToList(),
            TrisBefore = pieces.Where(DrawnPiece).Sum(pc => (long)pc.T.Length / 3),
            TrisAfter = drawnParts.Sum(x => (long)x.T.Length / 3),
            DrawCallsBefore = pieces.Count(DrawnPiece),
            DrawCallsAfter = (r.Static.Count > 0 ? 1 : 0) + DrawnResults(r, true).Skip(1).Count(x => x.Drawn && x.R.Static.Count > 0),
            TargetM = eWorld,
            ReachedM = reached,
            Within = eWorld > 0 && reached is double rm && rm <= eWorld,
            Ground = ProxyParams.Name(o.Ground),
            Normals = ProxyParams.Name(p.Normals),
            BuildMs = watch.ElapsedMilliseconds,
        };
        return r;
    }

    /// <summary>The object's grid cell: its extent over the voxels; within, cells of e (in the target's parent frame), at
    /// most the voxels along the extent.</summary>
    internal static double ObjectCell(double ext, double e, int voxels) => e > 0
        ? ext / Math.Clamp(Math.Ceiling(ext / e), 16, Math.Max(16, Math.Min(512, voxels)))
        : ext / Math.Clamp(voxels, 16, 512);

    /// <summary>Within, every part kept its original: the object is left unchanged. The notes go to
    /// <see cref="BuildOptions.Notes"/>, ending with the failure line; best is the largest over the parts of the best
    /// deviation each reached.</summary>
    static ObjectResult? WithinFailed(ObjectResult r, WithinRun w, BuildOptions o, double eWorld, double worldScale)
    {
        var bests = w.Best.Values.Where(double.IsFinite).ToList();
        o.Notes.AddRange(r.Notes);
        o.Notes.Add(WithinFailedNote(eWorld, bests.Count > 0 ? bests.Max() * worldScale : double.PositiveInfinity));
        return null;
    }

    /// <summary>The largest side and the diagonal of the bounding box of every piece of an object, in the target's parent
    /// frame (moving pieces through their <see cref="Assembly.Frame"/>); not positive when it has no triangles.</summary>
    internal static (double Ext, double Diag) ObjectExtent(Assembly asm)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var (a, toParent) in Owners(asm))
            foreach (var pc in a.Pieces) Grow(Place(pc.V, toParent), pc.T, lo, hi);
        return (Enumerable.Range(0, 3).Max(c => hi[c] - lo[c]), Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi[c] - lo[c]) * (hi[c] - lo[c]))));
    }

    /// <summary>The proxies of an owner's parts (bulk: a voxel hull; small, thin and floating tiny: <see cref="BuildSmall"/>
    /// with e = 0, keep mode), then the results of its moving parts (recursively). With a ground <paramref name="plane"/>
    /// (in the target's parent frame; taken into the owner's frame through <paramref name="toParent"/>, its
    /// <see cref="Assembly.Frame"/>, null for the target) every voxel hull, convex hull and decimated original is cut at it
    /// within <paramref name="tol"/>.</summary>
    static void BuildOwner(ObjectResult r, IReadOnlyList<Proxy.Source> sources, Dictionary<Piece, int> srcOf, Dictionary<Part, int> budget,
        ProxyParams p, BuildOptions o, double cell, GroundPlane? plane, double tol, double[]? toParent, List<string>? notes = null, WithinRun? within = null)
    {
        notes ??= r.Notes;   // the notes go to the target's result
        // the plane in the owner's frame; tol stays metres of the target's parent frame (a scaled frame divides it)
        double scale = 1;
        var g = plane is { } pl && toParent != null ? Into(pl, toParent, out scale) : plane;
        double t = tol / scale;
        foreach (var part in r.Asm.Parts)
        {
            if (within != null)
            {
                // the voxels are the upper limit: the hull's cell follows the attempt's error (WithinCellShare), not the object's grid
                r.Static.Add(WithinPart(r.Asm, part, () => PartSources(r.Asm, part, sources, srcOf), within, cell, p.Voxels, g, t, toParent, notes, o.Progress));
                continue;
            }
            if (part.Class != PartClass.Bulk)
            {
                var small = BuildSmall(part, 0, cell);
                if (g is { } gs && !CutSmall(small, gs, t)) notes.Add(UncutNote(part));
                r.Static.Add(small);
                continue;
            }
            var h = Proxy.Hull(PartSources(r.Asm, part, sources, srcOf), new Proxy.Options
            {
                TargetTris = budget[part], Resolution = PartResolution(part, cell, p.Voxels), Progress = o.Progress, Cut = g, CutTolerance = t,
            });
            if (h is { CutWouldEmpty: true }) notes.Add(UncutNote(part));
            r.Static.Add(Result(part, h, cell));
        }
        foreach (var m in r.Asm.Moving)
        {
            ObjectResult? inner = null;
            if (!m.Animated && (m.Inner.Parts.Count > 0 || m.Inner.Moving.Count > 0))
            {
                inner = new ObjectResult { Asm = m.Inner };
                BuildOwner(inner, sources, srcOf, budget, p, o, cell, plane, tol, m.Inner.Frame, notes, within);
            }
            r.Moving.Add((m, inner));
        }
    }

    /// <summary>The note for a part whose proxy the ground cut would remove completely (it is kept uncut).</summary>
    static string UncutNote(Part part) => $"cut would remove the whole hull of {part.Name}; kept uncut";

    /// <summary>The voxels along a part's largest side so that its cell is the object's: ceil(side / cell), 8 .. <paramref name="voxels"/>.</summary>
    static int PartResolution(Part part, double objectCell, int voxels) =>
        (int)Math.Clamp(Math.Ceiling(part.A / objectCell), 8, Math.Max(8, voxels));

    /// <summary>A part's voxel hull, or its own triangles when it has none.</summary>
    static PartResult Result(Part part, Proxy.HullResult? h, double objectCell) => h != null
        ? new PartResult { Part = part, Method = PartMethod.Voxel, V = h.V, T = h.T, Cell = h.Cell }
        : new PartResult { Part = part, Method = PartMethod.Kept, V = part.V, T = part.T, Cell = objectCell };

    /// <summary>Whether a part's proxy goes into the atlas; a kept or decimated part is drawn with its original texture,
    /// with its own state set (<see cref="KeptGeometries"/>).</summary>
    internal static bool InAtlas(PartResult pr) => pr.Method is PartMethod.Voxel or PartMethod.Convex;

    /// <summary>Bakes one atlas for the results (each with its sources, as for <see cref="BuildParts"/>) and fills their
    /// <see cref="ObjectResult.Meshes"/> (parts <see cref="InAtlas"/> only; an owner without such parts gets no mesh): one
    /// unwrap of the combined mesh of every part (disjoint, so no chart crosses parts;
    /// moving parts taken into the target's parent frame, and back afterwards), one bake of the base texture and every
    /// layer from the originals of all parts. Normals per part, before the unwrap: with <see cref="BuildOptions.Normals"/>
    /// hull as <see cref="Proxy.Build"/> makes them; with original from the part's sources (<see cref="Proxy.TransferNormals"/>,
    /// radius max(e, the object's cell), <see cref="BuildOptions.Crease"/>), which decimated parts also get
    /// (<see cref="PartResult.Corner"/>, drawn by <see cref="KeptGeometries"/>). With more than one part in the atlas, each
    /// texel looks up the original of its own part only (a global part number over all owners, see
    /// <see cref="Bake.Input.PartOfProxyTri"/>), within 3 x (e &gt; 0 ? e : the object's cell); one part bakes as before.</summary>
    internal static (RawImage? Texture, Dictionary<LayerKey, RawImage> Extra) BakeAtlas(IReadOnlyList<ObjectResult> results,
        IReadOnlyList<IReadOnlyList<Proxy.Source>> sources, BuildOptions o)
    {
        if (results.Count != sources.Count) throw new ArgumentException("one source list per result", nameof(sources));
        var V = new List<double>(); var T = new List<int>();
        var own = new List<double>();        // positions in the owner's frame
        var corner = new List<double>();     // corner normals in the owner's frame
        var original = new List<Proxy.Source>();   // the parts' sources in the target's parent frame
        var spans = new List<(ProxyMesh mesh, int first, int count)>();
        var partOfProxy = new List<int>(); var partOfOriginal = new List<int>(); var maxDistOfPart = new List<double>();
        double cell = 0;
        bool transfer = o.Normals == ProxyNormals.Original;
        double ws = o.WorldScale > 0 && double.IsFinite(o.WorldScale) ? o.WorldScale : 1;
        double e = o.ErrorMetres > 0 && double.IsFinite(o.ErrorMetres) ? o.ErrorMetres / ws : 0;   // 0 in keep mode
        for (int i = 0; i < results.Count; i++)
        {
            var top = results[i];
            var srcOf = SourceIndex(top.Asm);
            top.Meshes.Clear();
            double radius = Math.Max(e, top.Cell);   // metres of the target's parent frame
            void Add(ObjectResult r, MovingPart? m, double[]? toParent, string name)
            {
                // the transfer runs in the owner's frame, which a scaled moving frame stretches: divide as BuildOwner does for the cut
                double rad = toParent == null ? radius : radius / Mat.MaxScale(toParent);
                // decimated parts are drawn with the original's attributes (KeptGeometries), their normals from here
                foreach (var pr in r.Static.Where(x => x.Method == PartMethod.Decimated))
                    pr.Corner = transfer ? Transfer(pr, PartSources(r.Asm, pr.Part, sources[i], srcOf), rad, o.Crease) : null;
                if (!r.Static.Any(InAtlas)) return;
                int first = T.Count / 3;
                var partOf = new List<int>();
                for (int k = 0; k < r.Static.Count; k++)
                {
                    var pr = r.Static[k];
                    if (!InAtlas(pr)) continue;
                    int b = V.Count / 3;
                    V.AddRange(Place(pr.V, toParent)); own.AddRange(pr.V);
                    T.AddRange(pr.T.Select(x => x + b));
                    var ps = PartSources(r.Asm, pr.Part, sources[i], srcOf);
                    corner.AddRange(transfer ? Transfer(pr, ps, rad, o.Crease) : Proxy.CornerNormals(pr.V, pr.T, 0.7));
                    partOf.AddRange(Enumerable.Repeat(k, pr.T.Length / 3));
                    cell = Math.Max(cell, pr.Cell);
                    // the global part number: its proxy triangles search its own sources only (bake in the target's parent frame)
                    int gp = maxDistOfPart.Count;
                    maxDistOfPart.Add(3 * (e > 0 ? e : top.Cell));
                    partOfProxy.AddRange(Enumerable.Repeat(gp, pr.T.Length / 3));
                    foreach (var s in ps)
                    {
                        partOfOriginal.AddRange(Enumerable.Repeat(gp, s.T.Length / 3));
                        if (toParent == null) { original.Add(s); continue; }
                        // placed copies drop N: it stays in the owner's frame and only the transfer (which uses the unplaced sources) reads it
                        var placed = s.With(Place(s.V, toParent), s.T);
                        placed.N = null;
                        original.Add(placed);
                    }
                }
                var mesh = new ProxyMesh { Name = name, Owner = m, PartOfTri = partOf.ToArray() };
                top.Meshes.Add(mesh);
                spans.Add((mesh, first, T.Count / 3 - first));
            }
            Add(top, null, null, top.Asm.Owner.Name + " proxy");
            foreach (var (m, inner) in AllMoving(top))
                if (inner != null) Add(inner, m, inner.Asm.Frame, (m.SwitchChild >= 0 ? $"{m.Node.Name} {m.SwitchChild}" : m.Node.Name) + " proxy");
        }
        if (T.Count == 0) return (null, new());

        o.Progress?.Invoke("proxy: unwrapping ...");
        var uw = Unwrap.Run(V.ToArray(), T.ToArray(), o.TextureSize);
        o.Progress?.Invoke("proxy: baking the texture ...");
        var (OV, OT, triSrc) = Proxy.Merge(original);
        // one part: no filter, today's distance; else a part without a usable distance of its own takes today's
        bool filter = maxDistOfPart.Count > 1;
        var md = maxDistOfPart.Select(d => d > 0 && double.IsFinite(d) ? d : cell * 3).ToArray();
        var (tex, extra) = Proxy.BakeSources(original, new TriIndex(OV, OT), OV, OT, triSrc, uw.V, uw.T, uw.UV, o.TextureSize, cell * 3, o.Fill, o.ClassFill,
            filter ? partOfProxy.ToArray() : null, filter ? partOfOriginal.ToArray() : null, filter ? md : null);

        // the unwrapped mesh back into the meshes (Unwrap keeps the triangle order), positions in the owner's frame
        foreach (var (mesh, first, count) in spans)
        {
            var id = new Dictionary<int, int>();
            var mv = new List<double>(); var muv = new List<double>(); var mn = new List<double>();
            mesh.T = new int[3 * count];
            for (int c = 0; c < 3 * count; c++)
            {
                int u = uw.T[3 * first + c];
                if (!id.TryGetValue(u, out int j))
                {
                    id[u] = j = id.Count;
                    int s = uw.Source[u];
                    mv.AddRange(new[] { own[3 * s], own[3 * s + 1], own[3 * s + 2] });
                    muv.AddRange(new[] { uw.UV[2 * u], uw.UV[2 * u + 1] });
                    mn.AddRange(new double[3]);
                }
                mesh.T[c] = j;
                for (int k = 0; k < 3; k++) mn[3 * j + k] += corner[3 * (3 * first + c) + k];
            }
            var nrm = mn.ToArray();
            Proxy.Normalize(nrm);
            (mesh.V, mesh.UV, mesh.N) = (mv.ToArray(), muv.ToArray(), nrm);
        }
        return (tex, extra);
    }

    /// <summary>A part's corner normals from its sources (in the owner's frame, as the part).</summary>
    static double[] Transfer(PartResult pr, List<Proxy.Source> sources, double radius, double crease)
    {
        var (OV, OT, _) = Proxy.Merge(sources);
        return Proxy.TransferNormals(pr.V, pr.T, OV, OT, Proxy.MergeNormals(sources), radius, crease);
    }

    /// <summary>A piece's authored normals in the frame of its positions (<see cref="Piece.V"/>), when its geometry has
    /// them per vertex; else null.</summary>
    internal static double[]? PieceNormals(Piece p)
    {
        if (p.G.Normals is not { Comps: 3, Binding: "vertex" } on || on.Count != p.V.Length / 3 || PieceLinear(p) is not { } lin) return null;
        var N = new double[p.V.Length];
        for (int j = 0; j < N.Length / 3; j++)
            for (int c = 0; c < 3; c++)
                N[3 * j + c] = lin[c] * on.Data[3 * j] + lin[3 + c] * on.Data[3 * j + 1] + lin[6 + c] * on.Data[3 * j + 2];
        Proxy.Normalize(N);
        return N;
    }

    /// <summary>The sources of a part: for each piece it has triangles of (in the order of its units), that piece's source
    /// with only those triangles.</summary>
    static List<Proxy.Source> PartSources(Assembly a, Part part, IReadOnlyList<Proxy.Source> sources, Dictionary<Piece, int> srcOf)
    {
        var order = new List<int>();
        var tris = new Dictionary<int, List<int>>();
        foreach (var u in part.Units)
        {
            if (!tris.TryGetValue(u.Piece, out var l)) { tris[u.Piece] = l = new List<int>(); order.Add(u.Piece); }
            l.AddRange(u.Tris);
        }
        return order.Select(pi =>
        {
            var s = sources[srcOf[a.Pieces[pi]]];
            var T = new int[3 * tris[pi].Count];
            for (int i = 0; i < tris[pi].Count; i++)
                for (int k = 0; k < 3; k++) T[3 * i + k] = s.T[3 * tris[pi][i] + k];
            return s.With(s.V, T);
        }).ToList();
    }

    /// <summary>The number of each piece's source (<see cref="Assembly.AllPieces"/> order).</summary>
    static Dictionary<Piece, int> SourceIndex(Assembly asm)
    {
        var d = new Dictionary<Piece, int>(ReferenceEqualityComparer.Instance);
        foreach (var pc in asm.AllPieces()) d[pc] = d.Count;
        return d;
    }

    /// <summary>The assembly and those of its moving parts (depth first), each with the matrix into the target's parent
    /// frame (null for the target, whose pieces are there already).</summary>
    static IEnumerable<(Assembly A, double[]? ToParent)> Owners(Assembly top)
    {
        yield return (top, null);
        foreach (var m in top.Moving)
            foreach (var (a, _) in Owners(m.Inner)) yield return (a, a.Frame);
    }

    /// <summary>The result and those below its moving parts, depth first.</summary>
    static IEnumerable<ObjectResult> AllResults(ObjectResult r) => new[] { r }.Concat(r.Moving.Where(x => x.Inner != null).SelectMany(x => AllResults(x.Inner!)));

    /// <summary>The result and those below its moving parts, depth first, each with whether it is drawn (no switch child
    /// that is off on the way down; <paramref name="drawn"/> for <paramref name="r"/> itself).</summary>
    static IEnumerable<(ObjectResult R, bool Drawn)> DrawnResults(ObjectResult r, bool drawn) =>
        new[] { (r, drawn) }.Concat(r.Moving.Where(x => x.Inner != null).SelectMany(x => DrawnResults(x.Inner!, drawn && Drawn(x.M))));

    /// <summary>Every moving part below the result, depth first.</summary>
    static IEnumerable<(MovingPart M, ObjectResult? Inner)> AllMoving(ObjectResult r) =>
        r.Moving.SelectMany(x => new[] { x }.Concat(x.Inner != null ? AllMoving(x.Inner) : Enumerable.Empty<(MovingPart, ObjectResult?)>()));

    /// <summary>Positions through a matrix (null: as they are).</summary>
    static double[] Place(double[] V, double[]? m)
    {
        if (m == null) return V;
        var o = new double[V.Length];
        for (int i = 0; i < V.Length / 3; i++) (o[3 * i], o[3 * i + 1], o[3 * i + 2]) = Mat.Apply(m, V[3 * i], V[3 * i + 1], V[3 * i + 2]);
        return o;
    }

    static double Area(double[] V, int[] T)
    {
        double s = 0;
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
            double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
            double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
            s += Math.Sqrt(x * x + y * y + z * z) / 2;
        }
        return s;
    }

    /// <summary>The triangles of a unit (vertex indices into its piece's V).</summary>
    static int[] UnitT(List<Piece> pieces, Unit u)
    {
        var T = pieces[u.Piece].T;
        var o = new int[3 * u.Tris.Length];
        for (int i = 0; i < u.Tris.Length; i++) { o[3 * i] = T[3 * u.Tris[i]]; o[3 * i + 1] = T[3 * u.Tris[i] + 1]; o[3 * i + 2] = T[3 * u.Tris[i] + 2]; }
        return o;
    }

    static void Grow(double[] V, int[] T, double[] lo, double[] hi)
    {
        foreach (int v in T)
            for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], V[3 * v + c]); hi[c] = Math.Max(hi[c], V[3 * v + c]); }
    }

    sealed class UnionFind
    {
        readonly int[] up;
        public UnionFind(int n) { up = new int[n]; for (int i = 0; i < n; i++) up[i] = i; }

        public int Find(int x)
        {
            while (up[x] != x) { up[x] = up[up[x]]; x = up[x]; }
            return x;
        }

        public void Join(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) up[Math.Max(a, b)] = Math.Min(a, b);
        }
    }
}
