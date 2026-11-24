namespace SceneKit;

/// <summary>The display logic of the inspector window without any UI toolkit (port of the text building parts of
/// inspector_gui.py / inspector_preview.py), so it can be tested on every platform. The WPF window only shows it.</summary>
public static partial class InspectorModel
{
    public static readonly (string label, string mode)[] Orders =
    {
        ("cost (biggest first)", "cost"), ("scene order", "scene"), ("triangles", "tris"), ("memory", "bytes"), ("draw calls", "draws"),
        ("visible triangles", "vis"), ("vertices", "verts"), ("geometry memory", "geo"), ("texture memory", "tex"),
        ("instances", "inst"), ("name", "name"), ("findings (worst first)", "findings"),
    };

    /// <summary>Name sorts A-Z first; every other mode biggest / worst first.</summary>
    public static bool AscendingByDefault(string mode) => mode == "name";

    public static readonly Dictionary<string, string> SevMark = new() { ["high"] = "●", ["medium"] = "●", ["low"] = "○", ["info"] = "·" };
    public static readonly Dictionary<string, string> SevColor = new() { ["high"] = "#b3261e", ["medium"] = "#a15c00", ["low"] = "#4a6fa5", ["info"] = "#666666" };
    public static readonly Dictionary<string, string> VerdictColor = new() { ["ok"] = "#1f7a3a", ["check"] = "#a15c00", ["damaged"] = "#b3261e", ["skipped"] = "#555555" };
    public static readonly Dictionary<string, string> VerdictText = new()
    {
        ["ok"] = "OK - looks unchanged", ["check"] = "CHECK - visible changes possible",
        ["damaged"] = "LIKELY DAMAGED - look at it before saving", ["skipped"] = "NOTHING SIMPLIFIED",
    };
    /// <summary>Passes whose results are quality-checked.</summary>
    public static readonly HashSet<string> ShapePasses = new() { "simplify", "generate_lod", "clean", "weld" };
    public const int UndoLevels = 10;

    /// <summary>The parameter line of a run record. With a profile: "profile=Name@Version, " and every parameter of the passes
    /// (defaults too), so editing the profile later leaves the record's meaning alone; without: the changed parameters only.
    /// The record keys of the results (cards.*) follow.</summary>
    public static string RecordParams(string? profileTag, IEnumerable<string> passes, Dictionary<string, object> prms, IEnumerable<PassResult>? results = null)
    {
        var parts = new List<string>();
        var names = passes.ToList();
        if (profileTag != null) { parts.Add("profile=" + profileTag); parts.AddRange(Optimizer.AllParams(names, prms)); }
        else parts.AddRange(Optimizer.NonDefaultParams(names, prms));
        if (results != null) parts.AddRange(results.SelectMany(r => r.Record));
        return string.Join(", ", parts);
    }

    /// <summary>Names a saved profile may not take: the preset entries of the box (they would be hidden and never selectable).</summary>
    public static bool IsReservedProfileName(string name) =>
        name.Trim() is var n && (n.Length == 0 || new[] { "safe", "aggressive", "lod", "none", "custom" }.Contains(n, StringComparer.OrdinalIgnoreCase)
            || Optimizer.Presets.Keys.Contains(n, StringComparer.OrdinalIgnoreCase));

    /// <summary>An exception message as a sentence: first letter upper case, closing full stop; empty stays empty.</summary>
    public static string Sentence(string message)
    {
        string m = (message ?? "").Trim();
        if (m.Length == 0) return "";
        m = char.ToUpperInvariant(m[0]) + m[1..];
        return m.EndsWith('.') ? m : m + ".";
    }

    /// <summary>What the preset box shows for a profile: its name; "Custom (from X)" once a field changed; else "Custom".</summary>
    public static string ProfileBoxLabel(Profile? active, Profile? from) =>
        active != null ? active.Name : from != null ? $"Custom (from {from.Name})" : "Custom";

    /// <summary>World bounding diagonal (metres) of everything below a node: what "% of size" refers to.</summary>
    public static double NodeDiagonal(Node node, double[]? start) => Optimizer.Diagonal(Optimizer.WorldBox(node, start ?? Mat.Identity()));

    /// <summary>Status line after "Apply" of a previewed simplify: the counts and the preview's verdict; says so when the
    /// applied result differs from the preview.</summary>
    public static string AppliedLine(long before, long after, string verdict, long? previewAfter = null)
    {
        string head = $"Applied: {PyFormat.Int(before)} → {PyFormat.Int(after)} triangles";
        if (previewAfter is { } pa && pa != after) return $"{head} (the preview had {PyFormat.Int(pa)}), see Review";
        string word = verdict switch { "ok" => "OK", "check" => "CHECK", "damaged" => "LIKELY DAMAGED", "skipped" => "NOTHING SIMPLIFIED", _ => verdict.ToUpperInvariant() };
        return $"{head}, {word}";
    }

    public static string PlusPercent(double x)
    {
        var s = PyFormat.Percent(x, 0);
        return s.StartsWith('-') ? s : "+" + s;
    }

    /// <summary>Text bar: "███▌   42.0%".</summary>
    public static string Bar(double share, int width = 6)
    {
        if (double.IsNaN(share)) share = 0;
        share = Math.Max(0.0, Math.Min(1.0, share));
        int full = (int)(share * width);
        double rest = share * width - full;
        const string parts = " ▏▎▍▌▋▊▉";
        string s = new string('█', full) + (full < width ? parts[(int)(rest * 8)].ToString() : "");
        return s.PadRight(width) + " " + (PyFormat.Fixed(share * 100, 1) + "%").PadLeft(5);
    }

    // ------------------------------------------------------------------ findings per node

    /// <summary>Severity per node (ancestors get the worst one below them), findings per node, count below.</summary>
    public sealed class FindingIndex
    {
        public readonly Dictionary<Node, string> NodeSev = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Node, List<Finding>> NodeFindings = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Node, int> Below = new(ReferenceEqualityComparer.Instance);

        public FindingIndex(Report rep)
        {
            var order = Report.SeverityOrder;
            foreach (var f in rep.Findings)
            {
                if (f.Severity == "info") continue;
                // every ancestor along every parent chain, each once per finding (not once per node it covers)
                var ancestors = new HashSet<Node>(ReferenceEqualityComparer.Instance);
                var todo = new Stack<Node>();
                foreach (var n in f.Nodes)
                {
                    Add(n, f);
                    if (!NodeSev.TryGetValue(n, out var own) || order[f.Severity] < order[own]) NodeSev[n] = f.Severity;
                    todo.Push(n);
                }
                var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
                while (todo.Count > 0)
                {
                    var cur = todo.Pop();
                    if (!rep.Parents.TryGetValue(cur, out var ps)) continue;
                    foreach (var p in ps)
                        if (seen.Add(p)) { ancestors.Add(p); todo.Push(p); }
                }
                foreach (var a in ancestors)
                {
                    if (!NodeSev.TryGetValue(a, out var old) || order[f.Severity] < order[old]) NodeSev[a] = f.Severity;
                    Below[a] = Below.GetValueOrDefault(a) + 1;
                }
            }
            foreach (var f in rep.Findings)
                if (f.Severity == "info")
                    foreach (var n in f.Nodes) Add(n, f);
        }

        void Add(Node n, Finding f)
        {
            if (!NodeFindings.TryGetValue(n, out var l)) NodeFindings[n] = l = new List<Finding>();
            l.Add(f);
        }

        public List<Finding> Of(Node n) => NodeFindings.TryGetValue(n, out var l) ? l : new List<Finding>();
    }

    public static string SummaryLine(Report rep)
    {
        var s = rep.Summary();
        var fc = (Dictionary<string, int>)s["findings"]!;
        long L(string k) => Convert.ToInt64(s[k]);
        return $"{Analyzer.HumanCount(L("triangles"))} triangles ({Analyzer.HumanCount(L("visible_triangles"))} visible)   " +
               $"{Analyzer.HumanCount(L("vertices"))} vertices   {PyFormat.Int(L("draw_calls"))} draws   " +
               $"geometry {Analyzer.HumanBytes(L("geometry_bytes"))}   textures {Analyzer.HumanBytes(L("texture_bytes"))}   " +
               $"{PyFormat.Int(L("nodes"))} nodes   findings: {fc["high"]} high, {fc["medium"]} medium, {fc["low"]} low";
    }

    // ------------------------------------------------------------------ tree

    /// <summary>Children of n in the given order (see Orders). reverse flips the mode's default direction; equal keys
    /// keep scene order in both directions. idx is needed for "findings". sticky: children present in it keep that order first
    /// (a tree that must not jump after a run), the rest follow in the normal order.</summary>
    public static List<Node> SortedChildren(Report rep, Node n, string mode, bool reverse = false, FindingIndex? idx = null,
        IReadOnlyDictionary<Node, int>? sticky = null)
    {
        var sorted = SortedByMode(rep, n, mode, reverse, idx);
        if (sticky == null || sticky.Count == 0) return sorted;
        return sorted.Where(sticky.ContainsKey).OrderBy(c => sticky[c]).Concat(sorted.Where(c => !sticky.ContainsKey(c))).ToList();
    }

    static List<Node> SortedByMode(Report rep, Node n, string mode, bool reverse, FindingIndex? idx)
    {
        var kids = n.AllChildren().ToList();
        if (mode == "scene") return kids;
        if (mode == "name")
        {
            var named = kids.Select((c, i) => (c, i, k: c.Name.Length > 0 ? c.Name : c.Cls));
            var byName = reverse ? named.OrderByDescending(x => x.k, StringComparer.OrdinalIgnoreCase)
                                 : named.OrderBy(x => x.k, StringComparer.OrdinalIgnoreCase);
            return byName.ThenBy(x => x.i).Select(x => x.c).ToList();
        }
        double Key(Node c) => mode == "findings" ? FindingKey(idx, c) : Metric(rep, c, mode);
        // Python sorted(..., reverse=True) is stable: equal keys keep their order
        var keyed = kids.Select((c, i) => (c, i, k: Key(c)));
        var sorted = reverse ? keyed.OrderBy(x => x.k) : keyed.OrderByDescending(x => x.k);
        return sorted.ThenBy(x => x.i).Select(x => x.c).ToList();
    }

    /// <summary>Worst severity at or below the node first, then the number of findings.</summary>
    static double FindingKey(FindingIndex? idx, Node c)
    {
        if (idx == null) return 0;
        double sev = idx.NodeSev.TryGetValue(c, out var s) ? 3 - Report.SeverityOrder[s] : 0;   // high 3 .. low 1, none 0
        return sev * 1e9 + idx.Of(c).Count + idx.Below.GetValueOrDefault(c);
    }

    /// <summary>One tree row. Units: texture unit badge (see <see cref="TextureIndex.Badge"/>): "0 1", "⚠" appended when
    /// a unit's image is missing or a geometry lacks its texcoord set; for groups the union of the units used below.</summary>
    public sealed record Row(string Text, string Share, string Tris, string Vis, string Verts, string Draws, string Geo, string Tex,
        string Inst, string Notes, string? Severity, bool External, bool Hidden, bool HasChildren, string Units = "");

    static readonly Dictionary<string, string> Icons = new()
    {
        [Kind.Geometry] = "▲ ", [Kind.Geode] = "◆ ", [Kind.Transform] = "↻ ", [Kind.Lod] = "◎ ", [Kind.PagedLod] = "◎ ",
        [Kind.Switch] = "⇄ ", [Kind.Proxy] = "⇗ ", [Kind.External] = "📄 ",
    };

    public static Row RowFor(Report rep, FindingIndex idx, Node n) => RowFor(rep, idx, new[] { n });

    /// <summary>The row of a folded chain: the last node is the row's node; severity and findings of every chain node count as
    /// the row's own (the nodes above the last one are not rows of their own).</summary>
    public static Row RowFor(Report rep, FindingIndex idx, IReadOnlyList<Node> chain)
    {
        var n = chain[^1];
        var st = rep.Stat(n);
        var tot = rep.Total;
        double share = Math.Max(st.Tris / (double)Math.Max(tot.Tris, 1), st.Bytes / (double)Math.Max(tot.Bytes, 1));
        long inst = rep.Instances.GetValueOrDefault(n, 1);
        var notes = new List<string>();
        var fs = chain.SelectMany(idx.Of).OrderBy(f => Report.SeverityOrder.GetValueOrDefault(f.Severity, 9)).ToList();   // the worst first
        if (fs.Count > 0) notes.Add($"{SevMark[fs[0].Severity]} " + fs[0].Title + (fs.Count > 1 ? $" (+{fs.Count - 1})" : ""));
        else if (idx.Below.TryGetValue(n, out var below) && below > 0) notes.Add($"{below} finding(s) inside");
        if (n.Kind == Kind.External) notes.Add($"external: {n.External}");
        notes.AddRange(n.Notes.Take(2));
        if (n.NodeMask == 0) notes.Add("hidden (node mask 0)");
        if (n.Ranges is { Count: > 0 } && (n.Kind == Kind.Lod || n.Kind == Kind.PagedLod))
            notes.Add("ranges " + string.Join(", ", n.Ranges.Take(4).Select(r => $"{PyFormat.G(r.min)}-{PyFormat.G(r.max)}")));
        return new Row(Icons.GetValueOrDefault(n.Kind, "") + ReportText.NodeLabel(n), Bar(share), Analyzer.HumanCount(st.Tris),
            Analyzer.HumanCount(st.VisTris), Analyzer.HumanCount(st.Verts), st.Draws.ToString(), Analyzer.HumanBytes(st.GeoBytes),
            Analyzer.HumanBytes(st.TexBytes), inst > 1 ? inst.ToString() : "", string.Join(" | ", notes),
            chain.Select(c => idx.NodeSev.TryGetValue(c, out var sv) ? sv : null).OfType<string>().MinBy(sv => Report.SeverityOrder[sv]),
            n.Kind == Kind.External, n.NodeMask == 0, n.AllChildren().Count > 0,
            rep.TexIndex.Badge(n));
    }

    /// <summary>The single-child chain the tree folds into one row: start, then each only child as long as that child has no
    /// geometry, has children itself (a leaf is never folded away) and has one parent (a shared node, e.g. the root of a
    /// shared external file, stays its own row). A LOD, paged LOD, switch or hidden node (node mask 0) is never folded: its
    /// ranges / state show only on its own row. The last node is the row's node. Returns [start] when nothing folds.</summary>
    public static List<Node> FoldChain(Node start, Dictionary<Node, List<Node>> parents)
    {
        static bool Keeps(Node n) => n.Kind is Kind.Lod or Kind.PagedLod or Kind.Switch || n.NodeMask == 0;
        var chain = new List<Node> { start };
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance) { start };
        var cur = start;
        while (cur.Geom == null && !Keeps(cur) && cur.AllChildren() is [var only] && only.Geom == null && !Keeps(only) && only.AllChildren().Count > 0
               && (parents.GetValueOrDefault(only)?.Count ?? 0) <= 1 && seen.Add(only))
        {
            chain.Add(only);
            cur = only;
        }
        return chain;
    }

    /// <summary>Root -> node chain along the first parents (null when the node is not below the root).</summary>
    public static List<Node>? ChainTo(Report rep, Node target)
    {
        var chain = new List<Node>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        Node? cur = target;
        while (cur != null && seen.Add(cur))
        {
            chain.Add(cur);
            cur = rep.Parents.TryGetValue(cur, out var ps) && ps.Count > 0 ? ps[0] : null;
        }
        chain.Reverse();
        return chain.Count > 0 && ReferenceEquals(chain[0], rep.Scene.Root) ? chain : null;
    }

    // ------------------------------------------------------------------ details

    static string G4(double v) => PyFormat.G(v, 4, group: true);

    public static string FixLabel(string? fix) => fix != null && Optimizer.Passes.TryGetValue(fix, out var p) ? p.Label : fix ?? "";

    public static string Details(Report rep, FindingIndex idx, Node n)
    {
        var st = rep.Stat(n);
        var L = new List<string> { $"{n.Cls}  {PyRepr(n.Name)}", $"path:      {rep.PathOf(n)}", $"kind:      {n.Kind}" };
        var sel = Selection.Describe(rep, rep.Scene, new[] { n });
        L.Add($"file:      {sel.File}");
        L.Add($"drawn at:  {sel.Places} place(s) in {sel.Files} file(s)");
        long inst = rep.Instances.GetValueOrDefault(n, 1);
        int parents = rep.Parents.TryGetValue(n, out var ps) ? ps.Count : 0;
        L.Add($"instances: {inst} (parents: {parents})");
        if (n.NodeMask == 0) L.Add("node mask: 0 (hidden)");
        else if (n.NodeMask != 0xFFFFFFFFL) L.Add($"node mask: 0x{n.NodeMask:x}");
        L.Add("");
        L.Add("subtree:   " + $"{PyFormat.Int(st.Tris)} triangles ({PyFormat.Int(st.VisTris)} worst case visible), {PyFormat.Int(st.Verts)} vertices");
        L.Add($"           {PyFormat.Int(st.Draws)} draw calls, {PyFormat.Int(st.Geoms)} drawables, {PyFormat.Int(st.Nodes)} nodes");
        L.Add($"           geometry {Analyzer.HumanBytes(st.GeoBytes)}, textures {Analyzer.HumanBytes(st.TexBytes)}");
        if (st.BBox is var (lo, hi))
            L.Add("bounds:    " + string.Join(" x ", Enumerable.Range(0, 3).Select(k => G4(hi[k] - lo[k]))) + "   min " + string.Join(", ", lo.Select(G4)));
        if (n.Matrix != null)
        {
            L.Add("");
            L.Add("matrix (column vectors):");
            for (int r = 0; r < 4; r++)
                L.Add("   " + string.Join(" ", Enumerable.Range(0, 4).Select(c => PyFormat.G(n.Matrix[r * 4 + c], 5).PadLeft(12))));
        }
        if (n.Ranges is { Count: > 0 })
        {
            L.Add("");
            L.Add($"LOD ranges ({n.RangeMode}):");
            for (int i = 0; i < n.Ranges.Count; i++) L.Add($"   child {i}: {PyFormat.G(n.Ranges[i].min)} - {PyFormat.G(n.Ranges[i].max)}");
        }
        if (n.SwitchValues != null) L.Add("switch values: " + string.Join(" ", n.SwitchValues.Select(v => v ? "on" : "off")));
        if (n.Files.Count > 0) L.Add("files: " + string.Join(", ", n.Files.Select(f => f.Length > 0 ? f : "\"\"")));
        if (n.Kind == Kind.External) L.Add($"external file: {n.External}");
        var g = n.Geom;
        if (g != null)
        {
            rep.GeoInfoOf.TryGetValue(n, out var gi);
            L.Add("");
            L.Add("GEOMETRY" + (g.Optimizable ? "" : "   (read-only for the optimizer)"));
            var (t, l, p) = g.Counts();
            L.Add($"   {PyFormat.Int(g.NumVertices)} vertices, {PyFormat.Int(t)} triangles, {PyFormat.Int(l)} lines, {PyFormat.Int(p)} points, {g.DrawCalls} draw call(s)");
            L.Add($"   vertex data {Analyzer.HumanBytes(g.VertexBytes)}, index data {Analyzer.HumanBytes(g.IndexBytes)}");
            foreach (var (name, a) in g.Arrays())
                L.Add($"   {name,-14} {a.Type,-12} {a.Binding,-8} {PyFormat.Int(a.Count),9}  {Analyzer.HumanBytes(a.NBytes),9}");
            L.Add("   primitive sets:");
            foreach (var pr in g.Prims.Take(12))
            {
                long cnt = pr.Kind == "elements" ? pr.Indices?.Length ?? 0 : pr.Kind == "lengths" ? pr.Lengths?.Length ?? 0 : pr.Count;
                string what = pr.Kind switch
                {
                    "elements" => $"{pr.IndexType} {PyFormat.Int(cnt)} indices",
                    "lengths" => $"DrawArrayLengths {PyFormat.Int(cnt)} runs",
                    _ => $"DrawArrays first {pr.First} count {PyFormat.Int(cnt)}",
                };
                L.Add($"     {pr.Mode,-15} {what}" + (pr.Instances != 0 ? $" x{pr.Instances} instances" : ""));
            }
            if (g.Prims.Count > 12) L.Add($"     ... {g.Prims.Count - 12} more");
            if (gi != null)
            {
                var extra = new List<string>();
                if (gi.DupVertices != 0) extra.Add($"{PyFormat.Int(gi.DupVertices)} duplicate vertices");
                if (gi.Degenerate != 0) extra.Add($"{PyFormat.Int(gi.Degenerate)} degenerate triangles");
                if (gi.Unused != 0) extra.Add($"{PyFormat.Int(gi.Unused)} unused vertices");
                if (gi.Acmr != null) extra.Add($"ACMR {PyFormat.Fixed(gi.Acmr.Value, 2)}");
                if (extra.Count > 0) L.Add("   checks: " + string.Join(", ", extra));
            }
            foreach (var note in g.Notes) L.Add($"   note: {note}");
            var codes = FltFaceCodes.Describe(n);
            if (codes.Count > 0)
            {
                L.Add("");
                L.Add("OPENFLIGHT FACE CODES");
                foreach (var c in codes) L.Add($"   {c}");
            }
        }
        var ss = n.StateSet;
        if (ss != null)
        {
            L.Add("");
            int users = rep.TexIndex.StateSetUsers(ss);
            L.Add($"STATE SET (used by {users} node(s))" + (ss.Transparent ? "  transparent" : ""));
            foreach (var s in ss.Summary) L.Add($"   {s}");
            foreach (var kv in ss.Modes) L.Add($"   {kv.Key} {kv.Value}");
            foreach (var tx in ss.Textures)
            {
                string state = !(tx.Exists || tx.Embedded) ? "MISSING" : tx.Width != 0 ? $"{tx.Width}x{tx.Height} {tx.Fmt}" : "size unknown";
                L.Add($"   texture unit {tx.Unit}: {tx.Label}  [{state}]  GPU {Analyzer.HumanBytes(tx.GpuBytes)}");
                if (!string.IsNullOrEmpty(tx.Path)) L.Add($"      {tx.Path}");
                foreach (var line in CompanionLines(tx)) L.Add($"      {line}");
            }
        }
        var units = TextureUnits(rep, n);
        if (units.Count > 0)
        {
            L.Add("");
            L.Add("TEXTURES (effective: own + inherited)");
            foreach (var u in units)
            {
                var tx = u.Tex;
                string size = tx.Width != 0 ? $"{tx.Width}x{tx.Height}" + (tx.Fmt.Length > 0 ? $" {tx.Fmt}" : "") : "size unknown";
                string from = u.InheritedFrom == null ? "own" : $"from {rep.PathOf(u.InheritedFrom)}";
                L.Add($"   unit {u.Unit}: {tx.Label}  [{size}]  {u.State}  uv {(u.HasTexCoords ? "ok" : "MISSING")}  {from}  GPU {Analyzer.HumanBytes(tx.GpuBytes)}");
                foreach (var line in CompanionLines(tx)) L.Add($"      {line}");
            }
        }
        foreach (var note in n.Notes) L.Add($"note: {note}");
        var fs = idx.Of(n);
        if (fs.Count > 0)
        {
            L.Add("");
            L.Add("FINDINGS");
            foreach (var f in fs)
            {
                L.Add($"   [{f.Severity}] {f.Title}");
                L.Add($"      {f.Message}");
                if (f.Fix != null) L.Add($"      fix: {FixLabel(f.Fix)}");
            }
        }
        return string.Join("\n", L);
    }

    /// <summary>Python repr() of a str (quotes as Python chooses them).</summary>
    public static string PyRepr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new System.Text.StringBuilder().Append(q);
        foreach (char c in s)
        {
            if (c == q || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c == 0x7f) sb.Append($"\\x{(int)c:x2}");
            else sb.Append(c);
        }
        return sb.Append(q).ToString();
    }

    // ------------------------------------------------------------------ findings tab

    public static string Saving(Finding f)
    {
        var sav = new List<string>();
        if (f.SavingBytes != 0) sav.Add(Analyzer.HumanBytes(f.SavingBytes));
        if (f.SavingTris != 0) sav.Add($"{Analyzer.HumanCount(f.SavingTris)} tris");
        if (f.SavingDraws != 0) sav.Add($"{f.SavingDraws} draws");
        return string.Join(", ", sav);
    }

    public static string FindingText(Report rep, Finding f)
    {
        var txt = $"{f.Title}\n\n{f.Message}\n";
        if (f.Nodes.Count > 0) txt += $"\nAffects {f.Nodes.Count} node(s), e.g. " + string.Join(", ", f.Nodes.Take(5).Select(rep.PathOf));
        if (f.Fix != null && Optimizer.Passes.TryGetValue(f.Fix, out var p)) txt += $"\n\nFix: {p.Label} - {p.Help}";
        return txt;
    }

    /// <summary>Nodes a fix runs on (null: the whole scene, for findings without nodes).</summary>
    public static List<Node>? FixTargets(IEnumerable<Finding> findings)
    {
        var fl = findings.ToList();
        var nodes = fl.SelectMany(x => x.FixTargets).ToList();
        if (nodes.Count == 0 || !fl.All(x => x.FixTargets.Count > 0)) return null;
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        return nodes.Where(seen.Add).ToList();
    }

    // ------------------------------------------------------------------ optimisation log

    static readonly string[] NoOpKeys = { "triangles", "vertices", "draw_calls", "nodes", "geometry_bytes", "texture_bytes", "unique_geometries" };

    /// <summary>Null when the run changed anything (changes, removed nodes, edit signature or totals); otherwise why nothing changed:
    /// the first non-empty message of a pass without error, else a generic reason.</summary>
    public static string? NothingChanged(List<PassResult> results, Dictionary<string, object?> before, Dictionary<string, object?> after,
        long editsBefore, long editsAfter)
    {
        if (results.Any(r => r.Changes.Count > 0 || r.RemovedNodes > 0) || editsBefore != editsAfter) return null;
        foreach (var k in NoOpKeys)
            if (before.TryGetValue(k, out var b) && after.TryGetValue(k, out var a) && Convert.ToInt64(b) != Convert.ToInt64(a)) return null;
        return results.FirstOrDefault(r => r.Error == null && !string.IsNullOrWhiteSpace(r.Message))?.Message ?? "the passes found nothing to do";
    }

    public static List<string> LogResults(List<PassResult> results, Dictionary<string, object?> before, Dictionary<string, object?> after,
        IReadOnlyList<string>? prms = null)
    {
        var L = new List<string> { new string('-', 60) };
        var prev = results.Count > 0 ? results[0].TotalsBefore : null;
        foreach (var r in results)
        {
            string head = $"{r.Label}  [{PyFormat.Fixed(r.Seconds, 2)} s]";
            if (r.Error != null)
            {
                var lines = r.Error.Split('\n');
                L.Add($"{head}: FAILED {lines[0]}");
                foreach (var line in lines.Skip(1)) L.Add("      " + line);
                continue;
            }
            L.Add($"{head}: {r.Message}");
            var mine = prms?.Where(x => x.StartsWith(r.Name + ".")).ToList();
            if (mine is { Count: > 0 }) L.Add("      parameters: " + string.Join(", ", mine));
            var details = new List<string>();
            if (r.Changes.Count > 0)
            {
                var kinds = new List<(string kind, int n)>();
                foreach (var c in r.Changes)
                {
                    int i = kinds.FindIndex(k => k.kind == c.Kind);
                    if (i < 0) kinds.Add((c.Kind, 1)); else kinds[i] = (c.Kind, kinds[i].n + 1);
                }
                details.Add(string.Join(", ", kinds.Select(k => $"{PyFormat.Int(k.n)} geometr{(k.n == 1 ? "y" : "ies")} {k.kind}")));
            }
            if (r.RemovedNodes != 0) details.Add($"{PyFormat.Int(r.RemovedNodes)} nodes removed");
            if (r.Totals != null && prev != null)
            {
                var d = r.Totals.Where(kv => kv.Value != prev[kv.Key])
                    .Select(kv => $"{kv.Key.Replace('_', ' ')} {PyFormat.Int(prev[kv.Key])}->{PyFormat.Int(kv.Value)}").ToList();
                if (d.Count > 0) details.Add(string.Join("; ", d));
            }
            if (details.Count > 0) L.Add("      " + string.Join(" | ", details));
            else if (r.Changes.Count == 0) L.Add("      (nothing changed)");
            prev = r.Totals ?? prev;
        }
        foreach (var (key, label, isBytes) in new[] { ("triangles", "triangles", false), ("visible_triangles", "visible", false),
                     ("vertices", "vertices", false), ("draw_calls", "draw calls", false), ("geometry_bytes", "geometry", true),
                     ("texture_bytes", "textures", true), ("nodes", "nodes", false) })
        {
            long b = Convert.ToInt64(before[key]), a = Convert.ToInt64(after[key]);
            string F(long v) => isBytes ? Analyzer.HumanBytes(v) : PyFormat.Int(v);
            string ch = b != 0 && a != b ? $"  ({PlusPercent((a - b) / (double)b)})" : "";
            L.Add($"   {label,-11} {F(b),12} -> {F(a),12}{ch}");
        }
        return L;
    }

    // ------------------------------------------------------------------ quality

    /// <summary>Worst verdict, maximum deviations, summed edge counts and the distinct warnings of several checks.</summary>
    public static QualityReport Aggregate(IReadOnlyList<QualityReport> quals)
    {
        var o = new QualityReport();
        if (quals.Count == 0) return o;
        var rank = new Dictionary<string, int> { ["ok"] = 0, ["check"] = 1, ["damaged"] = 2 };
        var worst = quals[0];
        foreach (var q in quals) if (rank.GetValueOrDefault(q.Verdict) > rank.GetValueOrDefault(worst.Verdict)) worst = q;
        o.Verdict = worst.Verdict;
        o.MaxDevPct = quals.Max(q => q.MaxDevPct);
        o.MeanDevPct = quals.Average(q => q.MeanDevPct);
        o.FlippedPct = quals.Max(q => q.FlippedPct);
        o.BBoxChangePct = quals.Max(q => q.BBoxChangePct);
        o.AreaLossPct = quals.Max(q => q.AreaLossPct);
        o.BoundaryBefore = quals.Sum(q => q.BoundaryBefore);
        o.BoundaryAfter = quals.Sum(q => q.BoundaryAfter);
        o.NonManifoldBefore = quals.Sum(q => q.NonManifoldBefore);
        o.NonManifoldAfter = quals.Sum(q => q.NonManifoldAfter);
        foreach (var q in quals) foreach (var w in q.Warnings) if (!o.Warnings.Contains(w)) o.Warnings.Add(w);
        // texture: numbers of the measured parts; a note only when no part was measured and all parts say the same
        var measured = quals.Where(q => q.TexShiftP95 != null).ToList();
        if (measured.Count > 0)
        {
            o.TexShiftMean = measured.Average(q => q.TexShiftMean ?? 0);
            o.TexShiftP95 = measured.Max(q => q.TexShiftP95);
        }
        o.UvStretch = quals.Max(q => q.UvStretch);
        o.UvFlipped = quals.Sum(q => q.UvFlipped);
        if (measured.Count == 0 && quals.All(q => q.TextureNote == quals[0].TextureNote)) o.TextureNote = quals[0].TextureNote;
        foreach (var q in quals) foreach (var n in q.Notes) if (!o.Notes.Contains(n)) o.Notes.Add(n);
        return o;
    }

    /// <summary>The verdict line under the preview: the verdict (shape OK only, when the texture was rebaked or ignored;
    /// with the texture note in parentheses otherwise), the warnings, then the notes. One colon at most.</summary>
    public static string VerdictLine(QualityReport q)
    {
        string v = q.Verdict, vt = VerdictText.GetValueOrDefault(v, v);
        bool okRebake = v == "ok" && q.TextureNote is "textures rebaked" or "textures ignored";
        string text = okRebake ? $"SHAPE OK - {q.TextureNote}: compare the textured view"
            : q.TextureNote is "textures rebaked" or "textures ignored" or "no texture" or "texture size unknown" ? $"{vt} ({q.TextureNote})"
            : vt;
        var rest = q.Warnings.Concat(q.Notes).ToList();
        if (rest.Count > 0) text += (okRebake ? ";  " : ":  ") + string.Join(";  ", rest);
        return text;
    }

    /// <summary>(info line, verdict line) under the 3D preview.</summary>
    public static (string info, string verdict) QualityText(string title, QualityReport? q, (long b, long a, long vb, long va)? counts = null)
    {
        string head = title;
        if (counts is var (b, a, vb, va))
            head = $"{title}: triangles {PyFormat.Int(b)} -> {PyFormat.Int(a)}{(b != 0 ? $" ({PlusPercent((a - b) / (double)b)})" : "")}, " +
                   $"vertices {PyFormat.Int(vb)} -> {PyFormat.Int(va)}";
        if (q == null) return (head, "");
        var details = new List<string>
        {
            $"max deviation {PyFormat.Fixed(q.MaxDevPct, 3)} %", $"mean {PyFormat.Fixed(q.MeanDevPct, 4)} %",
            $"open edges {PyFormat.Int(q.BoundaryBefore)} -> {PyFormat.Int(q.BoundaryAfter)}",
            $"non-manifold {PyFormat.Int(q.NonManifoldBefore)} -> {PyFormat.Int(q.NonManifoldAfter)}",
            $"flipped {PyFormat.Fixed(q.FlippedPct, 2)} %", $"outline change {PyFormat.Fixed(q.BBoxChangePct, 2)} %",
        };
        string tex = "";
        if (q.TexShiftP95 is double p95)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            tex = $", texture shift {(q.TexShiftMean ?? 0).ToString("0.##", inv)} / {p95.ToString("0.##", inv)} texels (95 %)";
            if (q.UvStretch is double s) tex += $", stretch {s.ToString("0.##", inv)}×";
        }
        return (head + "\n" + string.Join(", ", details) + " (deviation in % of the object size)" + tex, VerdictLine(q));
    }

    // ------------------------------------------------------------------ changes tab

    public static List<Change> SortChanges(IEnumerable<Change> changes)
    {
        var rank = new Dictionary<string, int> { ["damaged"] = 0, ["check"] = 1, ["ok"] = 2 };
        return changes.Select((c, i) => (c, i)).OrderBy(x => ChangeVerdict(x.c) is string v ? rank.GetValueOrDefault(v, 3) : 3)
            .ThenBy(x => ShapePasses.Contains(x.c.PassName) ? 0 : 1)
            .ThenBy(x => -Math.Abs(x.c.Counts().trisBefore - x.c.Counts().trisAfter))
            .ThenBy(x => -Math.Abs(x.c.Counts().vertsBefore - x.c.Counts().vertsAfter))
            .ThenBy(x => -x.c.Counts().trisBefore).ThenBy(x => x.i).Select(x => x.c).ToList();
    }

    /// <summary>Verdict of a change for the list: the quality verdict, raised to "check" when a texture unit was dropped
    /// or lost its texcoords; null while nothing is known.</summary>
    public static string? ChangeVerdict(Change c)
    {
        var v = c.Quality?.Verdict;
        if (c.UnitDelta != null && v is null or "ok" or "skipped") return "check";
        return v;
    }

    public static string ChangeCheckText(Change c)
    {
        var delta = c.UnitDelta;
        var text = ChangeCheckTextShape(c);
        return delta != null ? $"{delta}; {text}" : text;
    }

    static string ChangeCheckTextShape(Change c)
    {
        var q = c.Quality;
        // cards: the measured verdict first, the old line stays as the detail
        if (q != null && c.PassName == "replace_cards")
            return (q.Warnings.Count > 0 ? string.Join("; ", q.Warnings) : q.SummaryLine()) + "; " + ReplacedLine(c);
        if (q != null) return q.Warnings.Count > 0 ? string.Join("; ", q.Warnings) : q.SummaryLine();
        if (c.Before == null) return "new geometry" + (c.Note.Length > 0 ? $" ({c.Note})" : "");
        if (c.PassName == "remove_occluded")   // the per-geometry check would call every removed hidden part damage
            return "not checked per geometry (only parts nobody sees were removed): compare before / after in the 3D preview";
        if (c.PassName is "replace_cards" or "replace_proxy")   // cards and hulls are meant to differ in shape
            return ReplacedLine(c);
        return ShapePasses.Contains(c.PassName) ? "checking ..." : "not checked (shape unchanged by this pass)";
    }

    static string ReplacedLine(Change c) =>
        "replaced" + (c.Note.Length > 0 ? $" ({c.Note})" : "") + ": compare before / after in the textured 3D preview";

    /// <summary>Quality check that never throws (an error becomes a 'check' verdict with the message).</summary>
    /// <summary>Shape and texture check (<paramref name="ss"/> = the merged state set before the change,
    /// <paramref name="seams"/> = the simplify seams mode the change ran with).</summary>
    public static QualityReport SafeCompare(GeomData? before, GeomData? after, StateSet? ss = null, string seams = "keep")
    {
        QualityReport q;
        try { q = Quality.Compare(before, after); }
        catch (Exception exc) { return new QualityReport { Verdict = "check", Warnings = { $"check failed: {exc.Message}" } }; }
        try { Quality.CompareTextured(q, before, after, ss, seams); }
        catch (Exception exc)
        {
            q.Warnings.Add($"texture check failed: {exc.Message}");
            if (q.Verdict == "ok") q.Verdict = "check";
        }
        return q;
    }

    // ------------------------------------------------------------------ try simplify

    public sealed class TryResult
    {
        public List<SoftRender.Mesh> Before = new(), After = new();
        public QualityReport Quality = new();
        public long TrisBefore, TrisAfter;
        public long VertsBefore => Before.Sum(m => (long)m.NumVerts);
        public long VertsAfter => After.Sum(m => (long)m.NumVerts);
        /// <summary>The parameters the try ran with (normalized: Fast, rough always keeps a share).</summary>
        public SimplifyParams Params = new();
        /// <summary>World bounding diagonal of the node (metres): what "% of size" refers to.</summary>
        public double Diag;
        /// <summary>Distinct reasons why geometries were left as they are.</summary>
        public List<string> Reasons = new();
        /// <summary>Names of the parts that lost over 90 % of their area (drawn red in the heat map).</summary>
        public List<string> Gone = new();
        /// <summary>The geometry nodes the try simplified (each once): Apply from the preview runs on exactly these.</summary>
        public IReadOnlyList<Node> Simplified = Array.Empty<Node>();
        /// <summary>Within: the node-level limit in metres the try ran with (for a relative target: the % of the node's
        /// diagonal, which caps each part's own share); 0 for Keep %.</summary>
        public double LimitM;
        /// <summary>The simplified geometries the try reached only through an external reference: Apply drops them when
        /// "Include externals" is off by then.</summary>
        public IReadOnlyList<Node> SimplifiedInExternals = Array.Empty<Node>();
        /// <summary>The try ran without externals and left geometry inside external references out.</summary>
        public bool ExternalsSkipped;
    }

    /// <summary>Targets and parameters for "Apply as previewed": the geometries the try simplified (not the node, whose
    /// coarser LOD levels, switched-off children and hidden parts the try never showed) and, for a relative target, the
    /// previewed node-level limit as max_error, so the pass caps each part's share exactly as the try did. With
    /// include_externals false, the geometries the try reached only through an external reference are dropped (the
    /// pass's scope would skip them; as direct targets they would not be).</summary>
    public static (List<Node> Targets, Dictionary<string, object> Params) ApplyArgs(TryResult r, IReadOnlyDictionary<string, object> prms)
    {
        var o = new Dictionary<string, object>(prms);
        if (r.Params.Within && r.Params.Error.Relative && r.LimitM > 0) o["simplify.max_error"] = r.LimitM;
        bool withExt = !prms.TryGetValue("include_externals", out var ie) || Convert.ToBoolean(ie);
        var targets = r.Simplified.ToList();
        if (!withExt && r.SimplifiedInExternals.Count > 0)
        {
            var drop = new HashSet<Node>(r.SimplifiedInExternals, ReferenceEqualityComparer.Instance);
            targets.RemoveAll(drop.Contains);
        }
        return (targets, o);
    }

    /// <summary>"3 parts almost gone: wheel_fl, wheel_fr, antenna" (first 5 names, then ", …").</summary>
    public static string GoneLine(IReadOnlyList<string> names) =>
        $"{names.Count} part{(names.Count == 1 ? "" : "s")} almost gone: {string.Join(", ", names.Take(5))}{(names.Count > 5 ? ", …" : "")}";

    /// <summary>"gap or overlap of 3 cm between 'window' and 'body'" (the measure is unsigned).</summary>
    public static string CrackLine(Crack c) => $"gap or overlap of {ErrorTarget.Distance(c.Gap)} between '{c.A}' and '{c.B}'";

    /// <summary>Where the tried node sits: <see cref="Start"/> = the matrix from the scene root to its parent (as the
    /// preview computes it); null = its own frame, metres assumed. <see cref="Neighbours"/> = unselected geometries around
    /// it in world coordinates (<see cref="Selection.Touching"/>), checked for new gaps but never changed.
    /// <see cref="Overlays"/> = the unselected geometries over its xy box (<see cref="Selection.Overlapping"/>), the
    /// overlay candidates; null = the neighbours. <see cref="IncludeExternals"/> = the Optimize tab's "Include externals":
    /// off, the try leaves geometry inside external references out, as the pass does.</summary>
    public sealed record TryContext(double[]? Start = null, IReadOnlyList<(Node Node, double[] World, StateSet? Ss)>? Neighbours = null,
        IReadOnlyList<(Node Node, double[] World, StateSet? Ss)>? Overlays = null, bool IncludeExternals = true);

    /// <summary>The context the preview tries a node in: placed at <paramref name="start"/>, with the geometries around it
    /// from the Report's world bounds (crack neighbours capped, overlay candidates all).</summary>
    public static TryContext TryContextFor(Report rep, Node node, double[]? start, bool includeExternals = true) =>
        new(start, Selection.Touching(rep, new[] { node }), Selection.Overlapping(rep, new[] { node }), includeExternals);

    /// <summary>Simplify every geometry below a node on copies (the scene is not changed) and check the result. Uses the
    /// same parameters and the same world-to-local error conversion as the simplify pass, so Apply gives the same
    /// triangles. The meshes stay in the node's own frame.</summary>
    public static TryResult? TrySimplify(Node node, SimplifyParams p, TryContext? ctx = null)
    {
        p = p.Normalized();
        static bool Simplifiable(Node n) => n.Geom is { Optimizable: true } g && g.Counts().tris >= 4;
        // "Include externals" off: geometry inside external references is left out, as the pass's scope leaves it out
        bool withExt = ctx?.IncludeExternals ?? true;
        var allItems = SoftRender.GeometryItems(node, 20000).Where(x => Simplifiable(x.node)).ToList();
        var items = withExt ? allItems : SoftRender.GeometryItems(node, 20000, externals: false).Where(x => Simplifiable(x.node)).ToList();
        // the geometries reachable without passing an external reference
        var local = withExt
            ? SoftRender.GeometryItems(node, int.MaxValue, externals: false).Select(x => x.node).ToHashSet(ReferenceEqualityComparer.Instance)
            : items.Select(x => x.node).ToHashSet(ReferenceEqualityComparer.Instance);
        bool externalsSkipped = !withExt && allItems.Any(x => !local.Contains(x.node));
        if (items.Count == 0) return null;
        var strat = Strategies.Of(p.Strategy);
        string method = p.Method, seams = strat.Seams;
        double ratio = p.Within ? 0 : p.Ratio;
        var start = ctx?.Start ?? Mat.Identity();
        // world bounding diagonal of ALL geometry below the node: what "% of size" refers to (the pass uses the same box)
        double diag = Optimizer.Diagonal(Optimizer.WorldBox(node, start));
        // each geometry gets the limit at its largest place below the node, so it holds there (as in the pass)
        var maxScale = new Dictionary<Node, double>(ReferenceEqualityComparer.Instance);
        foreach (var (n, w, _) in SoftRender.GeometryItems(node, int.MaxValue, start))
            maxScale[n] = Math.Max(maxScale.GetValueOrDefault(n), Mat.MaxScale(w));
        double limitM = p.Within ? p.Error.Metres(diag) : 0;
        var res = new TryResult { Params = p, Diag = diag, LimitM = limitM, ExternalsSkipped = externalsSkipped };
        var simplified = new List<Node>();
        var simplifiedSet = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var quals = new List<QualityReport>();
        var reasons = new List<(string reason, int n)>();
        var worst = new Dictionary<string, (double dev, int n)>();
        var cache = new Dictionary<string, RawImage?>();
        var neighbours = ctx?.Neighbours ?? Array.Empty<(Node Node, double[] World, StateSet? Ss)>();
        bool checkCracks = items.Count > 1 || neighbours.Count > 0;
        var parts = new List<Part>();
        // terrain under overlays keeps its vertices, as in the pass: candidates are the neighbours and the other
        // geometries of the selection itself (a tile's group node with its roads), each a candidate for every other
        // (KeepFor skips the tile's own geometry)
        // (Fast, rough cannot keep vertices: the overlays are still looked for, so the verdict can say so)
        var placed = p.ProtectOverlays
            ? (ctx?.Overlays ?? neighbours).Select(x => (node: x.Node, world: x.World))
                .Concat(SoftRender.GeometryItems(node, 20000, start).Select(x => (x.node, x.world)))
                .Where(x => x.node.Geom is { } g && g.Counts().tris > 0).Select(x => new Overlays.Placed(x.node, x.world)).ToList()
            : null;
        bool overlayKept = false;
        var overlayNotes = new List<string>();
        for (int idx = 0; idx < items.Count; idx++)
        {
            var (n, w, ss) = items[idx];
            double scale = maxScale.TryGetValue(n, out var ms) ? ms : Mat.MaxScale(Mat.Mul(start, w));
            // "% of size": each part's own size, at most the node's (the same helper as the pass)
            double partM = p.Within && p.Error.Relative ? Optimizer.PartLimit(p.Error.Value, limitM, n.Geom!, scale) : limitM;
            double maxError = partM * (scale > 0 && double.IsFinite(scale) ? 1.0 / scale : 1.0);
            int found = 0, copies = 0;
            var keep = placed is { Count: > 0 } ? Overlays.KeepFor(n.Geom!, new[] { Mat.Mul(start, w) }, placed, out found, out copies) : null;
            if (copies > 0) overlayNotes.Add(Overlays.CopyNote);
            if (method == "fast")
            {
                if (found > 0) overlayNotes.Add(Overlays.FastNote);
                keep = null;
            }
            Optimizer.SimplifyAttempt Run(double me)
            {
                var inf = new Optimizer.SimplifyInfo();
                if (seams == "rebake")
                {
                    var rb = Optimizer.Rebake(n.Geom!, ss, n.Name, ratio, Math.Clamp(p.RebakeSize, 16, 8192), p.Crease, p.Borders, p.Parts, me, inf, cache, keep: keep);
                    return new(rb?.g, rb?.ss, inf);
                }
                return new(Optimizer.SimplifyGeom(n.Geom!, new[] { ratio }, method, minTris: 4, seams: seams, info: inf, borders: p.Borders, parts: p.Parts,
                    maxError: me, crease: p.Crease, keep: keep, prune: p.Prune)[0], null, inf);
            }
            // the same measure-and-tighten as the pass, so Try and Apply give the same triangles
            // (a limit that cannot be met leaves the geometry unchanged, with the reason in Reasons)
            var at = p.Within ? Optimizer.SimplifyWithin(n.Geom!, maxError, maxError > 0 ? partM / maxError : 1.0, Run) : Run(maxError);
            var info = at.Info;
            GeomData? nw = at.Geom;
            StateSet? baked = at.State;
            var mb = SoftRender.MeshFromGeom(n.Geom, w, ss);
            if (mb != null) res.Before.Add(mb);
            res.TrisBefore += n.Geom!.Counts().tris;
            if (nw == null)
            {
                string why = info.Reason ?? "not simplifiable";
                int i = reasons.FindIndex(r => r.reason == why);
                if (i < 0) reasons.Add((why, 1)); else reasons[i] = (why, reasons[i].n + 1);
                Optimizer.NoteWorst(worst, why, info);
                if (mb != null) res.After.Add(mb);
                res.TrisAfter += n.Geom.Counts().tris;
                if (checkCracks) parts.Add(new Part(Selection.Nm(n), n.Geom, n.Geom, Mat.Mul(start, w)));
                continue;
            }
            overlayKept |= keep != null;   // only for geometries that changed
            if (simplifiedSet.Add(n)) simplified.Add(n);
            var q = SafeCompare(n.Geom, nw, ss, seams);
            if (q.AreaLossPct > Quality.GoneAreaLoss)
            {
                // one line for all gone parts instead of a warning per part; the heat map draws the part red
                res.Gone.Add(Selection.Nm(n));
                q.Warnings.RemoveAll(x => x.StartsWith("almost gone", StringComparison.Ordinal));
                if (q.VertexDev != null) q.VertexDev = Enumerable.Repeat(1e9, q.VertexDev.Length).ToArray();
            }
            quals.Add(q);
            if (checkCracks) parts.Add(new Part(Selection.Nm(n), n.Geom, nw, Mat.Mul(start, w)));
            var ma = SoftRender.MeshFromGeom(nw, w, baked ?? ss, q.VertexDev);
            if (ma != null) res.After.Add(ma);
            res.TrisAfter += nw.Counts().tris;
        }
        res.Quality = Aggregate(quals);
        if (quals.Count == 0) res.Quality.Verdict = "skipped";
        if (res.Gone.Count > 0) res.Quality.Warnings.Add(GoneLine(res.Gone));
        if (checkCracks && quals.Count > 0)
        {
            // new gaps between the parts and their unchanged neighbours, against the error target (Keep %: the OK deviation)
            foreach (var (nb, wb, _) in neighbours)
                if (nb.Geom != null) parts.Add(new Part(Selection.Nm(nb), nb.Geom, nb.Geom, wb));
            double allowed = p.Within ? limitM : Quality.OkDev / 100.0 * diag;
            try
            {
                if (Quality.Cracks(parts) is { } crack && crack.Gap > allowed)
                {
                    if (res.Quality.Verdict == "ok") res.Quality.Verdict = "check";
                    res.Quality.Warnings.Add(CrackLine(crack));
                }
            }
            catch (Exception exc)
            {
                res.Quality.Warnings.Add($"crack check failed: {exc.Message}");
                if (res.Quality.Verdict == "ok") res.Quality.Verdict = "check";
            }
        }
        foreach (var (reason, c) in reasons) res.Quality.Warnings.Add($"{c} geometr{(c == 1 ? "y" : "ies")} skipped: {reason}{Optimizer.WorstNote(worst, reason)}");
        if (overlayKept && quals.Count > 0) res.Quality.Notes.Add("overlay contact kept");
        foreach (var note in overlayNotes.Distinct()) res.Quality.Notes.Add(note);
        // as the pass says it: meshoptimizer keeps tile edges where Match would thin them (rebake: the standard simplifier)
        if (method is "meshopt" or "sloppy" && p.Borders == "match" && seams != "rebake") res.Quality.Notes.Add(Optimizer.MeshoptMatchNote);
        res.Reasons = reasons.Select(r => r.reason).ToList();
        res.Simplified = simplified;
        res.SimplifiedInExternals = simplified.Where(n => !local.Contains(n)).ToList();
        return res;
    }

    static readonly double[] MetreLadder = { 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1 };
    static readonly double[] PercentLadder = { 0.1, 0.2, 0.5, 1, 2, 5 };

    /// <summary>After a dead end (nothing simplified, and not just because the parts are tiny): the next setting to try,
    /// with the button text. Null when something was simplified or there is nothing further to suggest.</summary>
    public static (string Button, SimplifyParams Next)? NextStep(SimplifyParams p, TryResult r)
    {
        if (r.TrisAfter < r.TrisBefore) return null;
        if (r.Reasons.Count > 0 && r.Reasons.All(x => x.StartsWith("too small", StringComparison.Ordinal))) return null;
        if (p.Strategy == SimplifyStrategy.KeepTexturesExact)
            return ($"Try “{Strategies.Of(SimplifyStrategy.LetEdgesMerge).Label}”", p with { Strategy = SimplifyStrategy.LetEdgesMerge });
        if (p.Within)
        {
            double next = (p.Error.Relative ? PercentLadder : MetreLadder).FirstOrDefault(v => v > p.Error.Value * (1 + 1e-9));
            if (next > 0)
            {
                var t = new ErrorTarget(next, p.Error.Relative);
                return ($"Try “Within {(t.Relative ? PyFormat.G(next, 3) + " % of size" : ErrorTarget.Distance(next))}”", p with { Error = t });
            }
        }
        if (p.Strategy == SimplifyStrategy.LetEdgesMerge)
            return ($"Try “{Strategies.Of(SimplifyStrategy.NewTexture).Label}”", p with { Strategy = SimplifyStrategy.NewTexture });
        return null;
    }

    /// <summary>"Nothing simplified: " and the distinct reasons.</summary>
    public static string NothingLine(TryResult r) =>
        "Nothing simplified: " + (r.Reasons.Count > 0 ? string.Join("; ", r.Reasons) : "no collapse stayed within the target");

    /// <summary>The unit simplify distances are in: always metres; names the file's own unit when it was converted.</summary>
    public static string UnitNote(Scene s)
    {
        if (!s.Info.TryGetValue("unit_scale", out var v) || Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) == 1.0) return "metres";
        string unit = s.Info.TryGetValue("unit_name", out var u) ? Convert.ToString(u, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
        if (unit.Length == 0 && s.Info.TryGetValue("units", out u)) unit = Convert.ToString(u, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return $"metres (the file stores {(unit.Length > 0 ? unit : "another unit")}; converted on load)";
    }

    /// <summary>Billboard All / Cross Cards All: the run's mode, shared cards, and every layout row at its default, so
    /// the Optimize tab's layout choices cannot change what these buttons have always built. <c>planes</c> and
    /// <c>texture</c> stay as the editor has them; <c>planes</c> too when the layout row is custom, but after a named layout
    /// (which set the editor's planes) it goes back to 4, today's crossed cards.</summary>
    public static void SimCardsParams(Dictionary<string, object> prms, string mode)
    {
        var picked = prms.TryGetValue("replace_cards.layout", out var lv) ? Convert.ToString(lv, System.Globalization.CultureInfo.InvariantCulture)?.Trim() : null;
        if (!string.IsNullOrEmpty(picked) && !picked.Equals("custom", StringComparison.OrdinalIgnoreCase)) prms["replace_cards.planes"] = 4;
        prms["replace_cards.mode"] = mode;
        prms["replace_cards.share"] = true;
        prms["replace_cards.layout"] = "custom";
        prms["replace_cards.horizontal"] = 0;
        prms["replace_cards.heights"] = "auto";
        prms["replace_cards.slab"] = "full";
        prms["replace_cards.faces"] = "both";
        prms["replace_cards.back"] = "own";
        prms["replace_cards.box_bottom"] = false;
        prms["replace_cards.cells"] = "equal";
        prms["replace_cards.padding"] = "8";
        prms["replace_cards.alpha_mips"] = "renderer";
    }

    /// <summary>The "with ..." part of the Cards All confirmation: "ground cards, 16 triangles each".</summary>
    public static string CardsAllWith(CardLayout l) => $"{l.Label} cards, {l.Triangles} triangles each";

    /// <summary>The line under a similar-objects finding when the active profile sets a replace_cards layout; else null.</summary>
    public static string? CardsFindingNote(Finding f, Profile? active)
    {
        if (f.Fix != "replace_cards" || active == null) return null;
        if (!active.Passes.TryGetValue("replace_cards", out var rows) || !rows.TryGetValue("layout", out var v)) return null;
        var layout = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(layout) ? null : $"{active.Name} suggests {layout} cards";
    }
}
