namespace SceneKit;

/// <summary>What the inspector shows for the current selection: name, kind, owning file, how often it is drawn.
/// For several nodes, Files counts their owning files and <see cref="SharedPlaces"/> the places of the shared ones.</summary>
public sealed record SelectionInfo(string Name, string Kind, string File, int Count, long Places, int Files)
{
    /// <summary>A node drawn at more than one place or whose file is referenced by more than one file.</summary>
    public bool Shared => Count > 1 ? SharedPlaces > 0 : Places > 1 || Files > 1;

    /// <summary>Set for several nodes: the places of the shared ones (0 when none is shared).</summary>
    public long SharedPlaces { get; init; }

    /// <summary>Set for several nodes: the first two names.</summary>
    internal string[] Names { get; init; } = Array.Empty<string>();

    public string Text
    {
        get
        {
            if (Count == 0) return "the whole scene";
            if (Count == 1)
                return $"'{Name}' ({Kind}) in {File}" + (Shared ? $", drawn at {Places} place(s) in {Files} file(s)" : "");
            string first = Names.Length > 0 ? Names[0] : Name, second = Names.Length > 1 ? Names[1] : "";
            return $"{Count} nodes: '{first}', '{second}'{(Count > 2 ? ", …" : "")} in {Files} file(s)" + (Shared ? $", drawn at {SharedPlaces} places" : "");
        }
    }

    /// <summary>Short form for the Run button.</summary>
    public string Short => Count == 0 ? "the whole scene"
        : Count == 1 ? $"'{Name}' in {File}, {Places} place(s)" : $"{Count} nodes in {Files} file(s)";
}

public static class Selection
{
    sealed class Index
    {
        public Dictionary<Node, string> Owner = new();
        public Dictionary<string, HashSet<string>> Referrers = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Report, Index> Indexes = new();

    /// <summary>How many indexes were built (tests check that Describe reuses one per Report).</summary>
    [ThreadStatic] internal static int IndexBuilds;

    static Index Build(Scene scene)
    {
        IndexBuilds++;
        var ix = new Index { Owner = History.OwnerFiles(scene) };
        // stand-in node's owner file per referenced external path
        foreach (var (p, sub) in scene.Externals.Prepend(new(scene.Path, (Scene?)scene)))
            if (sub != null)
                foreach (var n in sub.IterNodes())
                    foreach (var en in n.ExternalNodes)
                        if (en.Kind == Kind.External && !string.IsNullOrEmpty(en.External))
                        {
                            if (!ix.Referrers.TryGetValue(en.External, out var set)) ix.Referrers[en.External] = set = new(StringComparer.OrdinalIgnoreCase);
                            set.Add(p);
                        }
        return ix;
    }

    /// <summary>Node to owning file, from the per-Report cached index (built on first use, shared with <see cref="Describe"/>).</summary>
    public static IReadOnlyDictionary<Node, string> OwnerFiles(Report rep) => Indexes.GetValue(rep, r => Build(r.Scene)).Owner;

    /// <summary>Every drawn geometry of the scene with its world matrix, state set and world bounds (the local box's eight
    /// corners through the matrix).</summary>
    sealed record Placed(Node Node, double[] World, StateSet? Ss, double[] Lo, double[] Hi);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Report, List<Placed>> WorldBounds = new();

    static List<Placed> BuildBounds(Report rep)
    {
        var outl = new List<Placed>();
        if (rep.Scene.Root == null) return outl;
        var local = new Dictionary<Node, (double[] lo, double[] hi)?>(ReferenceEqualityComparer.Instance);
        foreach (var (n, w, ss) in SoftRender.GeometryItems(rep.Scene.Root))
        {
            if (!local.TryGetValue(n, out var box)) local[n] = box = n.Geom?.BBox();
            if (box is not { } b || b.lo.Length < 2) continue;
            double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            for (int c = 0; c < 8; c++)
            {
                var (x, y, z) = Mat.Apply(w, (c & 1) == 0 ? b.lo[0] : b.hi[0], (c & 2) == 0 ? b.lo[1] : b.hi[1],
                    b.lo.Length > 2 ? ((c & 4) == 0 ? b.lo[2] : b.hi[2]) : 0);
                lo[0] = Math.Min(lo[0], x); lo[1] = Math.Min(lo[1], y); lo[2] = Math.Min(lo[2], z);
                hi[0] = Math.Max(hi[0], x); hi[1] = Math.Max(hi[1], y); hi[2] = Math.Max(hi[2], z);
            }
            outl.Add(new Placed(n, w, ss, lo, hi));
        }
        return outl;
    }

    /// <summary>The selection's geometries (to leave out) and its world box (each node where its first parent chain puts
    /// it, as the preview places it); null box for no vertices.</summary>
    static (HashSet<Node> below, double[] lo, double[] hi)? SelectionBox(Report rep, IReadOnlyList<Node> sel)
    {
        var below = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        double[] lo = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        double[] hi = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var s in sel)
        {
            foreach (var (n, _, _) in SoftRender.GeometryItems(s)) below.Add(n);
            if (Optimizer.WorldBox(s, SoftRender.ParentMatrix(s, rep.Parents)) is not { } b) continue;
            for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], b.lo[c]); hi[c] = Math.Max(hi[c], b.hi[c]); }
        }
        return double.IsFinite(lo[0]) ? (below, lo, hi) : null;
    }

    /// <summary>The geometries around a selection for the crack check (R2-D5 UX-09): geometries not below the selected
    /// nodes whose world bounds touch the selection's world bounds (within 1e-4 of its diagonal), at most
    /// <paramref name="max"/>. Those whose xy box crosses the selection's box boundary (a neighbouring tile) come before
    /// those inside it (props standing on it), so the cap drops the props first. The selection sits where its first
    /// parent chain puts it (as the preview places it). The world bounds of every geometry are built once per Report
    /// (like <see cref="OwnerFiles"/>).</summary>
    public static List<(Node Node, double[] World, StateSet? Ss)> Touching(Report rep, IReadOnlyList<Node> sel, int max = 64)
    {
        var res = new List<(Node, double[], StateSet?)>();
        if (sel.Count == 0 || max <= 0 || SelectionBox(rep, sel) is not var (below, lo, hi)) return res;
        double eps = 1e-4 * Optimizer.Diagonal((lo, hi));
        var inside = new List<(Node, double[], StateSet?)>();
        foreach (var p in WorldBounds.GetValue(rep, BuildBounds))
        {
            if (below.Contains(p.Node)) continue;
            bool touch = true;
            for (int c = 0; c < 3 && touch; c++) touch = p.Lo[c] <= hi[c] + eps && lo[c] <= p.Hi[c] + eps;
            if (!touch) continue;
            bool contained = true;
            for (int c = 0; c < 2 && contained; c++) contained = p.Lo[c] >= lo[c] - eps && p.Hi[c] <= hi[c] + eps;
            if (!contained)
            {
                res.Add((p.Node, p.World, p.Ss));
                if (res.Count >= max) return res;
            }
            else if (inside.Count < max) inside.Add((p.Node, p.World, p.Ss));
        }
        res.AddRange(inside.Take(max - res.Count));
        return res;
    }

    /// <summary>The overlay candidates of a selection (R2-D5 UX-10): every geometry not below the selected nodes whose
    /// world xy box overlaps the selection's (no cap: the road on a tile may come after hundreds of props, and the
    /// pass looks at every geometry of the scene). The overlay test itself skips those far above or below.</summary>
    public static List<(Node Node, double[] World, StateSet? Ss)> Overlapping(Report rep, IReadOnlyList<Node> sel)
    {
        var res = new List<(Node, double[], StateSet?)>();
        if (sel.Count == 0 || SelectionBox(rep, sel) is not var (below, lo, hi)) return res;
        foreach (var p in WorldBounds.GetValue(rep, BuildBounds))
            if (!below.Contains(p.Node) && p.Lo[0] <= hi[0] && lo[0] <= p.Hi[0] && p.Lo[1] <= hi[1] && lo[1] <= p.Hi[1])
                res.Add((p.Node, p.World, p.Ss));
        return res;
    }

    internal static string Nm(Node n) => n.Name.Length > 0 ? n.Name : OsgReader.Short(n.Cls);

    /// <summary>One line for the selection view: "{name}, {tris} triangles, {n} texture(s)" for one node, "{count} nodes,
    /// ..." for several, "Nothing selected" for none. Textures are counted once per file (case-insensitive; path-less
    /// ones by reference) over the state sets the geometries are drawn with.</summary>
    public static string Caption(Report rep, IReadOnlyList<Node> nodes)
    {
        if (nodes.Count == 0) return "Nothing selected";
        long tris = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unnamed = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var n in nodes)
        {
            tris += rep.Stat(n).Tris;
            foreach (var (_, _, ss) in SoftRender.GeometryItems(n))
                if (ss != null)
                    foreach (var t in ss.Textures)
                        if (!string.IsNullOrEmpty(t.Path)) paths.Add(t.Path); else unnamed.Add(t);
        }
        int texCount = paths.Count + unnamed.Count;
        string who = nodes.Count == 1 ? Nm(nodes[0]) : $"{nodes.Count} nodes";
        return $"{who}, {PyFormat.Int(tris)} triangles, {texCount} texture{(texCount == 1 ? "" : "s")}";
    }

    public static SelectionInfo Describe(Report rep, Scene scene, IReadOnlyList<Node> nodes)
    {
        if (nodes.Count == 0) return new SelectionInfo("", "", "", 0, 0, 0);
        var ix = Indexes.GetValue(rep, r => Build(r.Scene));
        var owner = ix.Owner;
        var referrers = ix.Referrers;

        // the files that reference a node's file (1 for the master)
        int Referring(string own) => string.Equals(own, scene.Path, StringComparison.OrdinalIgnoreCase) ? 1
            : referrers.TryGetValue(own, out var refs) && refs.Count > 0 ? refs.Count : 1;

        long places = 0, sharedPlaces = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            long inst = rep.Instances.GetValueOrDefault(n, 1);
            string own = owner.GetValueOrDefault(n, scene.Path);
            places += inst;
            files.Add(own);
            if (inst > 1 || Referring(own) > 1) sharedPlaces += inst;
        }
        // one node: the tiles that reference its file; several: their owning files
        int fileCount = nodes.Count == 1 ? Referring(owner.GetValueOrDefault(nodes[0], scene.Path)) : files.Count;

        var first = nodes[0];
        string file = System.IO.Path.GetFileName(owner.GetValueOrDefault(first, scene.Path));
        return new SelectionInfo(Nm(first), OsgReader.Short(first.Cls), file, nodes.Count, places, fileCount)
        {
            Names = nodes.Take(2).Select(Nm).ToArray(),
            SharedPlaces = nodes.Count > 1 ? sharedPlaces : 0,
        };
    }
}
