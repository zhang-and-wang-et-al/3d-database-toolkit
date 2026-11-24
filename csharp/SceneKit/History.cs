namespace SceneKit;

/// <summary>One geometry that an operation created or changed.</summary>
public sealed class Change
{
    public string PassName;
    public Node Node;
    public GeomData? Before, After;
    public string Kind;      // "changed" | "new" | "lod level"
    public string Note;
    public QualityReport? Quality;
    /// <summary>Effective (merged, own + inherited) state before / after: the preview draws each side with its own.</summary>
    public StateSet? StateBefore, StateAfter;
    /// <summary>Texture units before / after (before of a replaced subtree: the union over it); null when unknown.</summary>
    public TexSignature? UnitsBefore, UnitsAfter;

    public Change(string passName, Node node, GeomData? before, GeomData? after, string kind, string note = "")
    {
        PassName = passName; Node = node; Before = before; After = after; Kind = kind; Note = note;
    }

    public (long trisBefore, long trisAfter, int vertsBefore, int vertsAfter) Counts() =>
        (Before?.Counts().tris ?? 0, After?.Counts().tris ?? 0, Before?.NumVertices ?? 0, After?.NumVertices ?? 0);

    /// <summary>"texture unit 1 dropped" etc., or null when no unit was lost.</summary>
    public string? UnitDelta => History.UnitDelta(UnitsBefore, UnitsAfter);
}

/// <summary>Undo snapshot: per node the attributes passes and edits change; geometry and array state; document swaps.
/// Vertex arrays are not copied - passes replace arrays, they never write into them.</summary>
public sealed class Snapshot
{
    public readonly Scene Scene;
    public readonly string Label;
    readonly Node? root;
    readonly List<(Node n, Node[] children, GeomData? geom, StateSet? ss, double[]? matrix, (double, double)[]? ranges,
        bool[]? values, long mask, string[]? dirty, string name, string[] files)> nodes = new();
    Dictionary<Node, int>? index;
    readonly Dictionary<GeomData, GeomData> geoms = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<VArray, (string type, double[] data, int comps, string binding)> arrays = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<StateSet, (Texture[] textures, string[] summary)> statesets = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Texture, Texture> textures = new(ReferenceEqualityComparer.Instance);
    public readonly List<(Stmt stmt, object? block)> DomSwaps = new();
    readonly Dictionary<string, Node?> subRoots = new();

    /// <summary>When the step was done (for the history list).</summary>
    public DateTime Time = DateTime.Now;
    /// <summary>Triangles and draw calls of the scene when the snapshot was taken (shared geometry counted once).</summary>
    public readonly long Tris, Draws;
    /// <summary>What the step was about, for the history list and the Undo / Redo texts (set by the caller after the step).</summary>
    public string Scope = "", Params = "";
    /// <summary>Files the step touched and how long it took.</summary>
    public int Files;
    public double Seconds;

    public Snapshot(Scene scene, string label = "")
    {
        Scene = scene; Label = label; root = scene.Root;
        foreach (var n in scene.IterNodes(externals: true))
        {
            if (n.Geom != null && !geoms.ContainsKey(n.Geom))
            {
                if (n.Kind == Kind.Geometry) { Tris += n.Geom.Counts().tris; Draws += n.Geom.DrawCalls; }
                geoms[n.Geom] = n.Geom.ShallowCopy();
                foreach (var (_, a) in n.Geom.Arrays()) arrays.TryAdd(a, (a.Type, a.Data, a.Comps, a.Binding));
            }
            nodes.Add((n, n.Children.ToArray(), n.Geom, n.StateSet, (double[]?)n.Matrix?.Clone(), n.Ranges?.ToArray(),
                n.SwitchValues?.ToArray(), n.NodeMask, n.Dirty.Count > 0 ? n.Dirty.ToArray() : null, n.Name, n.Files.ToArray()));
            var ss = n.StateSet;
            if (ss != null && !statesets.ContainsKey(ss))
            {
                statesets[ss] = (ss.Textures.ToArray(), ss.Summary.ToArray());   // remove unit edits both
                foreach (var t in ss.Textures) textures.TryAdd(t, t.ShallowCopy());
            }
        }
        foreach (var (p, sub) in scene.Externals) if (sub != null) subRoots[p] = sub.Root;
    }

    public int NodeCount => nodes.Count;

    public void Restore()
    {
        var sc = Scene;
        sc.Root = root;
        foreach (var (p, r) in subRoots) if (sc.Externals.TryGetValue(p, out var sub) && sub != null) sub.Root = r;
        foreach (var (n, children, geom, ss, matrix, ranges, values, mask, dirty, name, files) in nodes)
        {
            n.Children = children.ToList();
            n.Geom = geom;                     // the original object (keeps sharing intact)
            n.StateSet = ss;
            n.Matrix = (double[]?)matrix?.Clone();
            n.Ranges = ranges?.ToList();
            n.SwitchValues = values?.ToList();
            n.NodeMask = mask;
            n.Dirty = dirty != null ? new HashSet<string>(dirty) : new HashSet<string>();
            n.Name = name;
            n.Files = files.ToList();
        }
        foreach (var (g, saved) in geoms) g.SetFrom(saved);   // undo in-place edits of geometry objects
        foreach (var (a, st) in arrays) { a.Type = st.type; a.Data = st.data; a.Comps = st.comps; a.Binding = st.binding; }
        foreach (var (ss, (tex, summary)) in statesets) { ss.Textures = tex.ToList(); ss.Summary = summary.ToList(); }
        foreach (var (t, saved) in textures) t.SetFrom(saved);
        for (int i = DomSwaps.Count - 1; i >= 0; i--) DomSwaps[i].stmt.Block = DomSwaps[i].block;
    }

    /// <summary>The geometry data a node had when the snapshot was taken (a frozen copy).</summary>
    public GeomData? GeometryBefore(Node node)
    {
        index ??= nodes.Select((x, i) => (x.n, i)).ToDictionary(x => x.n, x => x.i, (IEqualityComparer<Node>)ReferenceEqualityComparer.Instance);
        if (!index.TryGetValue(node, out var i) || nodes[i].geom == null) return null;
        return geoms[nodes[i].geom!];
    }
}

public static class History
{
    /// <summary>One geometry node: its data object, content signature, frozen copy, effective state and texture units.</summary>
    public sealed record GeoState(Node Node, GeomData Geom, string Sig, GeomData Frozen, StateSet? State = null, TexSignature? Units = null);

    /// <summary>GeometryState result; also remembers the first parent of every node (to find replaced subtrees).</summary>
    public sealed class GeoStates : Dictionary<Node, GeoState>
    {
        public GeoStates() : base(ReferenceEqualityComparer.Instance) { }
        public Node? Root;
        public readonly Dictionary<Node, Node> FirstParent = new(ReferenceEqualityComparer.Instance);
        public bool Contains(Node n) => ReferenceEquals(n, Root) || FirstParent.ContainsKey(n);
    }

    /// <summary>{node: (geometry object, content signature, frozen copy, effective state, units)} for all geometry nodes
    /// (externals included).</summary>
    public static GeoStates GeometryState(Scene scene)
    {
        var o = new GeoStates { Root = scene.Root };
        var copies = new Dictionary<GeomData, GeomData>(ReferenceEqualityComparer.Instance);
        foreach (var (n, parent, merged) in TextureTracking.WithState(scene))
        {
            if (parent != null) o.FirstParent[n] = parent;
            if (n.Kind != Kind.Geometry || n.Geom == null) continue;
            if (!copies.TryGetValue(n.Geom, out var c)) copies[n.Geom] = c = n.Geom.ShallowCopy();
            o[n] = new GeoState(n, n.Geom, Sig(n.Geom), c, merged, TexSignature.Of(n.Geom, merged));
        }
        return o;
    }

    /// <summary>What a change lost in texture units: "texture unit 1 dropped", "texture units 1, 2 lost their
    /// texcoords"; null when nothing was lost (or a side is unknown).</summary>
    public static string? UnitDelta(TexSignature? before, TexSignature? after)
    {
        if (before == null || after == null) return null;
        var dropped = before.Units.Where(e => !after.Has(e.Unit)).Select(e => e.Unit).ToList();
        var lost = before.Units.Where(e => e.HasTexCoords && after.Units.Any(a => a.Unit == e.Unit && !a.HasTexCoords)).Select(e => e.Unit).ToList();
        var parts = new List<string>();
        if (dropped.Count > 0) parts.Add(dropped.Count == 1 ? $"texture unit {dropped[0]} dropped" : $"texture units {string.Join(", ", dropped)} dropped");
        if (lost.Count > 0) parts.Add(lost.Count == 1 ? $"texture unit {lost[0]} lost its texcoords" : $"texture units {string.Join(", ", lost)} lost their texcoords");
        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    /// <summary>Cheap identity of geometry data (array objects + primitive index arrays).</summary>
    static string Sig(GeomData g)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (_, a) in g.Arrays()) sb.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a.Data)).Append(',');
        foreach (var p in g.Prims)
        {
            sb.Append(p.Indices != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Indices).ToString() : $"{p.First}/{p.Count}");
            sb.Append(p.Mode).Append(';');
        }
        return sb.ToString();
    }

    public static List<Change> Diff(string passName, Dictionary<Node, GeoState> before, Dictionary<Node, GeoState> after,
        Dictionary<Node, (GeomData? source, string note)>? sources = null)
    {
        var o = new List<Change>();
        Dictionary<GeomData, GeoState>? byGeom = null;
        var subtree = new Dictionary<Node, List<GeoState>>(ReferenceEqualityComparer.Instance);
        foreach (var (n, st) in after)
        {
            Change ch;
            if (!before.TryGetValue(n, out var old))
            {
                var (src, note) = sources != null && sources.TryGetValue(n, out var s) ? s : (null, "");
                ch = new Change(passName, n, src, st.Geom, note.StartsWith("LOD", StringComparison.Ordinal) ? "lod level" : "new", note);
                // what it was made from: the source geometry's node, else the geometry the pass removed from the subtree it sits in
                GeoState? from = null;
                if (src != null)
                {
                    if (byGeom == null)
                    {
                        byGeom = new(ReferenceEqualityComparer.Instance);
                        foreach (var x in before.Values) byGeom.TryAdd(x.Geom, x);
                    }
                    byGeom.TryGetValue(src, out from);
                }
                var gone = from == null ? RemovedBelow(n, before, after, subtree) : null;
                if (from != null) { ch.UnitsBefore = from.Units; ch.StateBefore = TextureTracking.Copy(from.State); }
                else if (gone is { Count: > 0 })
                {
                    ch.UnitsBefore = TexSignature.Union(gone.Select(x => x.Units ?? TexSignature.Empty));
                    ch.StateBefore = TextureTracking.Copy(gone[0].State);
                }
            }
            else if (!ReferenceEquals(old.Geom, st.Geom) || old.Sig != st.Sig || UnitDelta(old.Units, st.Units) != null)
            {
                ch = new Change(passName, n, old.Frozen, st.Geom, "changed",
                    ReferenceEquals(old.Geom, st.Geom) && old.Sig == st.Sig ? "texture state changed" : "");
                ch.UnitsBefore = old.Units;
                ch.StateBefore = TextureTracking.Copy(old.State);
            }
            else continue;
            ch.UnitsAfter = st.Units;
            ch.StateAfter = TextureTracking.Copy(st.State);
            o.Add(ch);
        }
        return o;
    }

    /// <summary>Geometry the pass removed from the subtree a new node sits in: below its nearest ancestor that existed
    /// before (first parents). Empty when the state carries no parent information.</summary>
    static List<GeoState> RemovedBelow(Node n, Dictionary<Node, GeoState> before, Dictionary<Node, GeoState> after,
        Dictionary<Node, List<GeoState>> cache)
    {
        if (before is not GeoStates b || after is not GeoStates a) return new List<GeoState>();
        Node? anchor = null;
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        for (Node? cur = a.FirstParent.GetValueOrDefault(n); cur != null && seen.Add(cur); cur = a.FirstParent.GetValueOrDefault(cur))
            if (b.Contains(cur)) { anchor = cur; break; }
        if (anchor == null) return new List<GeoState>();
        if (cache.Count == 0)
        {
            // once per diff: every removed geometry under each of its former ancestors
            cache[b.Root ?? anchor] = new List<GeoState>();
            foreach (var (g, st) in b)
            {
                if (a.ContainsKey(g)) continue;
                var seen2 = new HashSet<Node>(ReferenceEqualityComparer.Instance);
                for (Node? cur = b.FirstParent.GetValueOrDefault(g); cur != null && seen2.Add(cur); cur = b.FirstParent.GetValueOrDefault(cur))
                {
                    if (!cache.TryGetValue(cur, out var l)) cache[cur] = l = new List<GeoState>();
                    l.Add(st);
                }
            }
        }
        return cache.TryGetValue(anchor, out var r) ? r : new List<GeoState>();
    }

    public static int RemovedCount(Dictionary<Node, GeoState> before, Scene scene)
    {
        var alive = new HashSet<Node>(scene.IterNodes(externals: true), ReferenceEqualityComparer.Instance);
        return before.Keys.Count(k => !alive.Contains(k));
    }

    /// <summary>Triangles, vertices, draw calls, geometries, nodes (shared geometry counted once), texture units (summed
    /// over the geometries: the units each draws with, inherited ones included) and textures (distinct objects).</summary>
    public static Dictionary<string, long> QuickTotals(Scene scene)
    {
        long tris = 0, verts = 0, draws = 0, geos = 0, nodes = 0, units = 0;
        var seen = new HashSet<GeomData>(ReferenceEqualityComparer.Instance);
        var textures = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var (n, _, merged) in TextureTracking.WithState(scene))
        {
            nodes++;
            if (n.StateSet != null) textures.UnionWith(n.StateSet.Textures);
            if (n.Kind == Kind.Geometry && n.Geom != null && seen.Add(n.Geom))
            {
                geos++;
                tris += n.Geom.Counts().tris;
                verts += n.Geom.NumVertices;
                draws += n.Geom.DrawCalls;
                if (merged != null) units += merged.Textures.Select(t => t.Unit).Distinct().Count();
            }
        }
        return new()
        {
            ["triangles"] = tris, ["vertices"] = verts, ["draw_calls"] = draws, ["geometries"] = geos, ["nodes"] = nodes,
            ["texture_units"] = units, ["textures"] = textures.Count,
        };
    }

    /// <summary>One line for an Undo / Redo tooltip, the history list and the save manifest.</summary>
    public static string Describe(UndoHistory.Entry e)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        string pct = e.TrisBefore != 0 ? InspectorModel.PlusPercent((e.TrisAfter - e.TrisBefore) / (double)e.TrisBefore) : "±0 %";
        var sb = new System.Text.StringBuilder(e.Label);
        if (e.Scope.Length > 0) sb.Append(" on ").Append(e.Scope);
        sb.Append(": triangles ").Append(PyFormat.Int(e.TrisBefore)).Append(" → ").Append(PyFormat.Int(e.TrisAfter)).Append(" (").Append(pct).Append(')');
        if (e.Files > 0) sb.Append(", ").Append(e.Files.ToString(ic)).Append(" file(s)");
        sb.Append(", ").Append(e.Seconds.ToString("0.0", ic)).Append(" s");
        if (e.Params.Length > 0) sb.Append("; ").Append(e.Params);
        return sb.ToString();
    }

    /// <summary>Node to the absolute path of the file whose tree holds it (master nodes map to the master path; a node shared by
    /// several externals keeps its first owner).</summary>
    public static Dictionary<Node, string> OwnerFiles(Scene scene)
    {
        var owner = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes()) owner.TryAdd(n, scene.Path);
        foreach (var (p, sub) in scene.Externals)
            if (sub != null) foreach (var n in sub.IterNodes()) owner.TryAdd(n, p);
        return owner;
    }

    /// <summary>Number of distinct files (master + externals) that own at least one of the nodes.</summary>
    public static int FilesOf(Scene scene, IEnumerable<Node> nodes)
    {
        var owner = OwnerFiles(scene);
        var files = new HashSet<string>();
        foreach (var n in nodes) if (owner.TryGetValue(n, out var f)) files.Add(f);
        return files.Count;
    }

    /// <summary>Grows with every edit: the global edit counter (<see cref="Node.Edits"/>: every mark, also of a key that is
    /// already dirty, and every texture edit) plus the edited textures (a pass that edits anything raises it).</summary>
    public static long EditSignature(Scene scene)
    {
        long sig = Node.Edits;
        var seen = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes(externals: true))
        {
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (seen.Add(t) && (t.Resize != null || t.Baked != null || t.Repointed || t.SamplingEdited)) sig++;
        }
        return sig;
    }

    /// <summary>'a/b/c' path of a node using a parent map.</summary>
    public static string NodePath(Node node, IReadOnlyDictionary<Node, List<Node>> parents, int limit = 8)
    {
        var names = new List<string>();
        Node? n = node;
        for (int i = 0; i < limit && n != null; i++)
        {
            names.Add(n.Name.Length > 0 ? n.Name : OsgReader.Short(n.Cls));
            n = parents.TryGetValue(n, out var ps) && ps.Count > 0 ? ps[0] : null;
        }
        names.Reverse();
        return string.Join("/", names);
    }
}

/// <summary>Undo / redo of scene edits as snapshots (the inspector's history). Push a snapshot taken before every
/// change; a new step clears the redo steps. Each kept step keeps the scene structure of its time alive, so the number
/// of steps is capped (<see cref="InspectorModel.UndoLevels"/>, 3 for scenes over 200k nodes); dropped steps are counted.</summary>
public sealed class UndoHistory
{
    /// <summary>One step for the history list: triangles / draw calls after minus before; Undone = on the redo side.</summary>
    public sealed record Entry(string Label, DateTime Time, long TrisDelta, long DrawsDelta, bool Undone,
        string Scope = "", string Params = "", int Files = 0, double Seconds = 0, long TrisBefore = 0, long TrisAfter = 0);

    readonly List<Snapshot> undo = new(), redo = new();

    /// <summary>Oldest steps dropped because of the cap since the last <see cref="Clear"/>.</summary>
    public int Dropped { get; private set; }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoLabel => undo.Count > 0 ? undo[^1].Label : null;
    public string? RedoLabel => redo.Count > 0 ? redo[^1].Label : null;
    /// <summary>"Undo simplify on 3 nodes: triangles ..." for the last done step; null when there is none.</summary>
    public string? UndoText => Entries().LastOrDefault(e => !e.Undone) is { } e ? "Undo " + History.Describe(e) : null;
    /// <summary>"Redo ..." for the next undone step; null when there is none.</summary>
    public string? RedoText => redo.Count > 0 && Entries().FirstOrDefault(e => e.Undone) is { } e ? "Redo " + History.Describe(e) : null;
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;

    /// <summary>Steps kept for a scene of the snapshot's size.</summary>
    public static int Levels(Snapshot snap) => snap.NodeCount < 200_000 ? InspectorModel.UndoLevels : 3;

    /// <summary>Record a step (the snapshot is the state before it). Clears the redo steps; drops the oldest over the cap.</summary>
    public void Push(Snapshot snap)
    {
        undo.Add(snap);
        redo.Clear();
        int levels = Levels(snap);
        if (undo.Count > levels)
        {
            Dropped += undo.Count - levels;
            undo.RemoveRange(0, undo.Count - levels);
        }
    }

    /// <summary>Restore the state before the last step; returns that step (null when there is none).</summary>
    public Snapshot? Undo()
    {
        if (undo.Count == 0) return null;
        var snap = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add(Inverse(snap));
        snap.Restore();
        return snap;
    }

    /// <summary>Do the last undone step again; returns it (null when there is none).</summary>
    public Snapshot? Redo()
    {
        if (redo.Count == 0) return null;
        var snap = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(Inverse(snap));
        snap.Restore();
        return snap;
    }

    /// <summary>A snapshot of the current state that undoes restoring <paramref name="snap"/> (document swaps included).</summary>
    static Snapshot Inverse(Snapshot snap)
    {
        var back = new Snapshot(snap.Scene, snap.Label)
            { Time = snap.Time, Scope = snap.Scope, Params = snap.Params, Files = snap.Files, Seconds = snap.Seconds };
        foreach (var (stmt, _) in snap.DomSwaps) back.DomSwaps.Add((stmt, stmt.Block));
        return back;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        Dropped = 0;
    }

    /// <summary>"3 oldest steps dropped (undo keeps the last 10)" once the cap removed steps, else null.</summary>
    public string? CapNote()
    {
        if (Dropped == 0) return null;
        int levels = undo.Count > 0 ? Levels(undo[^1]) : redo.Count > 0 ? Levels(redo[^1]) : InspectorModel.UndoLevels;
        return $"{Dropped} oldest step{(Dropped == 1 ? "" : "s")} dropped (undo keeps the last {levels})";
    }

    /// <summary>The steps oldest first: done ones, then undone ones (redo order last). Deltas compare the scene before and
    /// after each step; the current scene is counted once.</summary>
    public List<Entry> Entries()
    {
        var o = new List<Entry>();
        var scene = undo.Count > 0 ? undo[^1].Scene : redo.Count > 0 ? redo[^1].Scene : null;
        if (scene == null) return o;
        long curTris = 0, curDraws = 0;
        var seen = new HashSet<GeomData>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes(externals: true))
            if (n.Kind == Kind.Geometry && n.Geom != null && seen.Add(n.Geom)) { curTris += n.Geom.Counts().tris; curDraws += n.Geom.DrawCalls; }
        for (int i = 0; i < undo.Count; i++)
        {
            long aT = i + 1 < undo.Count ? undo[i + 1].Tris : curTris, aD = i + 1 < undo.Count ? undo[i + 1].Draws : curDraws;
            o.Add(new Entry(undo[i].Label, undo[i].Time, aT - undo[i].Tris, aD - undo[i].Draws, false,
                undo[i].Scope, undo[i].Params, undo[i].Files, undo[i].Seconds, undo[i].Tris, aT));
        }
        // redo[^1] is the next step to redo: it goes from the current state to its own
        for (int j = redo.Count - 1; j >= 0; j--)
        {
            long bT = j == redo.Count - 1 ? curTris : redo[j + 1].Tris, bD = j == redo.Count - 1 ? curDraws : redo[j + 1].Draws;
            o.Add(new Entry(redo[j].Label, redo[j].Time, redo[j].Tris - bT, redo[j].Draws - bD, true,
                redo[j].Scope, redo[j].Params, redo[j].Files, redo[j].Seconds, bT, redo[j].Tris));
        }
        return o;
    }
}
