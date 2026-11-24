using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SceneKit;

/// <summary>A pass parameter: key, label, default value, type (bool, int, double, string or a choice list).</summary>
public sealed record PassParam(string Key, string Label, object Default, Type Type, string[]? Choices = null);

public sealed record PassSpec(string Name, string Label, string Group, bool Safe, string Help, PassParam[] Params, Func<OptContext, string> Fn)
{
    /// <summary>Rearranges the scene graph but keeps the picture (and every triangle); not "Safe" because instancing / names change.</summary>
    public bool Structure { get; init; }
}

/// <summary>State shared by the passes of one run: parameters, scope and an incrementally kept parent map.</summary>
public sealed class OptContext
{
    public readonly Scene Scene;
    public readonly Dictionary<string, object> Params;
    public readonly List<Node>? Targets;
    public readonly Action<string> Progress;
    public readonly bool IncludeExternals;
    Dictionary<Node, List<Node>>? parents;
    public Dictionary<Node, (GeomData? source, string note)> Sources = new(ReferenceEqualityComparer.Instance);
    /// <summary>A pass's own verdict per new node (cards: the measured leaf area); <see cref="Optimizer.Run"/> puts it on the
    /// node's change. Reset per pass.</summary>
    public Dictionary<Node, QualityReport> Verdicts = new(ReferenceEqualityComparer.Instance);
    /// <summary>key=value lines a pass leaves for the run record (cards.*). Reset per pass.</summary>
    public List<string> Record = new();

    public OptContext(Scene scene, Dictionary<string, object>? prms = null, List<Node>? targets = null, Action<string>? progress = null)
    {
        Scene = scene;
        Params = prms ?? new Dictionary<string, object>();
        Targets = targets is { Count: > 0 } ? targets : null;
        Progress = progress ?? (_ => { });
        IncludeExternals = !Params.TryGetValue("include_externals", out var ie) || Convert.ToBoolean(ie);
    }

    public void MadeFrom(Node newNode, GeomData? source, string note = "") => Sources[newNode] = (source, note);

    /// <summary>Children plus the content of loaded external files (when externals are included).</summary>
    public IReadOnlyList<Node> Kids(Node n)
    {
        if (!IncludeExternals || n.ExternalNodes.Count == 0) return n.Children;
        var l = new List<Node>(n.Children);
        foreach (var en in n.ExternalNodes) l.AddRange(en.Children);
        return l;
    }

    /// <summary>True when the caller set this parameter (as pass.key or the bare key).</summary>
    public bool Has(string pass, string key) => Params.ContainsKey($"{pass}.{key}") || Params.ContainsKey(key);

    public object P(string pass, string key)
    {
        var spec = Optimizer.Passes[pass];
        var dflt = spec.Params.FirstOrDefault(p => p.Key == key)?.Default;
        if (Params.TryGetValue($"{pass}.{key}", out var v)) return v;
        if (Params.TryGetValue(key, out v)) return v;
        return dflt!;
    }

    public double PDouble(string pass, string key) => Convert.ToDouble(P(pass, key), System.Globalization.CultureInfo.InvariantCulture);
    public int PInt(string pass, string key) => (int)Convert.ToDouble(P(pass, key), System.Globalization.CultureInfo.InvariantCulture);
    public bool PBool(string pass, string key) => P(pass, key) is bool b ? b : Convert.ToBoolean(P(pass, key));
    public string PStr(string pass, string key) => Convert.ToString(P(pass, key), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    public Dictionary<Node, List<Node>> Parents
    {
        get
        {
            if (parents == null)
            {
                parents = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
                foreach (var n in Scene.IterNodes(externals: IncludeExternals))
                {
                    if (n.Kind == Kind.External) continue;   // the root of an external file stays the root of that file
                    foreach (var c in n.Children)
                    {
                        if (!parents.TryGetValue(c, out var l)) parents[c] = l = new List<Node>(1);
                        l.Add(n);
                    }
                }
            }
            return parents;
        }
    }

    public List<Node> ParentsOf(Node n) => Parents.TryGetValue(n, out var l) ? l : new List<Node>();

    public void Invalidate() => parents = null;

    public bool Replace(Node parent, Node old, IEnumerable<Node> newList) =>
        ReplaceMany(parent, new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance) { [old] = newList.ToList() });

    public bool RemoveMany(Node parent, IEnumerable<Node> olds)
    {
        var map = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
        foreach (var o in olds) map[o] = new List<Node>();
        return ReplaceMany(parent, map);
    }

    /// <summary>Replace several children of one parent in one pass and keep the parent map up to date.</summary>
    public bool ReplaceMany(Node parent, Dictionary<Node, List<Node>> mapping)
    {
        var counts = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        if (parents != null)
            foreach (var c in parent.Children)
                if (mapping.ContainsKey(c)) counts[c] = counts.GetValueOrDefault(c) + 1;
        if (!Optimizer.ReplaceChildren(parent, mapping)) return false;
        var P = parents;
        if (P == null || parent.Kind == Kind.External) return true;
        foreach (var (old, newList) in mapping)
        {
            int times = counts.GetValueOrDefault(old);
            if (times == 0) continue;
            foreach (var nc in newList) Register(nc, parent, times);
            if (P.TryGetValue(old, out var lst))
            {
                for (int t = 0; t < times; t++)
                {
                    int j = lst.FindIndex(p => ReferenceEquals(p, parent));
                    if (j >= 0) lst.RemoveAt(j);
                }
                if (lst.Count == 0) Detach(old);
            }
        }
        return true;
    }

    void Register(Node node, Node parent, int times)
    {
        var P = parents!;
        var stack = new Stack<(Node n, Node p, int t)>();
        stack.Push((node, parent, times));
        while (stack.Count > 0)
        {
            var (n, p, t) = stack.Pop();
            bool known = P.ContainsKey(n);
            if (!P.TryGetValue(n, out var l)) P[n] = l = new List<Node>(1);
            for (int i = 0; i < t; i++) l.Add(p);
            if (!known && n.Kind != Kind.External) foreach (var c in n.Children) stack.Push((c, n, 1));
        }
    }

    void Detach(Node node)
    {
        var P = parents!;
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            P.Remove(n);
            if (n.Kind == Kind.External) continue;
            foreach (var c in n.Children)
            {
                if (!P.TryGetValue(c, out var l)) continue;
                l.RemoveAll(p => ReferenceEquals(p, n));
                if (l.Count == 0) stack.Push(c);
            }
        }
    }

    /// <summary>Unique nodes in scope (targets' subtrees or the whole scene; external file content when included).
    /// External-file marker nodes themselves are never returned.</summary>
    public List<Node> ScopeNodes()
    {
        var roots = Targets ?? new List<Node> { Scene.Root! };
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var o = new List<Node>();
        var stack = new List<Node>(roots);
        while (stack.Count > 0)
        {
            var n = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!seen.Add(n)) continue;
            if (n.Kind == Kind.External)
            {
                if (IncludeExternals) stack.AddRange(n.Children);
                continue;
            }
            o.Add(n);
            stack.AddRange(Kids(n));
        }
        return o;
    }

    public List<Node> Geometries(long minTris = 0)
    {
        var o = new List<Node>();
        foreach (var n in ScopeNodes())
            if (n.Kind == Kind.Geometry && n.Geom != null && n.Geom.Optimizable && !MeshOps.Instanced(n.Geom))
            {
                if (minTris != 0 && n.Geom.Counts().tris < minTris) continue;
                o.Add(n);
            }
        return o;
    }
}

public sealed class PassResult
{
    public string Name, Label, Message;
    public string? Error;
    public double Seconds;
    public List<Change> Changes = new();
    public int RemovedNodes;
    public Dictionary<string, long>? Totals, TotalsBefore;
    /// <summary>key=value lines for the run record (replace_cards: cards.layout, cards.coverage ...).</summary>
    public List<string> Record = new();
    public PassResult(string name, string message, string? error = null)
    {
        Name = name; Label = Optimizer.Passes.TryGetValue(name, out var s) ? s.Label : name; Message = message; Error = error;
    }
}

public static partial class Optimizer
{
    static readonly HashSet<string> PlainKeys = new()
    {
        "Name", "DataVariance", "NodeMask", "CullingActive", "Children", "Drawables", "Matrix", "ReferenceFrame", "StateSet",
        "name", "nodeMask", "cullingActive", "num_children", "num_drawables", "referenceFrame", "Position",
        "Attitude", "Scale", "PivotPoint", "position", "attitude", "scale", "pivotPoint", "InitialBound",
    };

    public static readonly Dictionary<string, string[]> Presets = new()
    {
        ["safe"] = new[] { "remove_empty", "share_duplicates", "weld", "clean", "merge_geometries", "compact_arrays", "vertex_cache" },
        ["aggressive"] = new[] { "remove_empty", "remove_redundant", "share_duplicates", "flatten_transforms", "weld", "clean",
            "merge_geometries", "compact_arrays", "recenter", "simplify", "vertex_cache", "downscale_textures" },
        ["lod"] = new[] { "share_duplicates", "weld", "clean", "generate_lod" },
    };

    public static readonly string[] PipelineOrder =
    {
        "remove_hidden", "remove_occluded", "replace_cards", "replace_proxy", "remove_empty", "remove_redundant", "share_duplicates", "make_instances", "flatten_transforms",
        "weld", "clean", "triangulate", "merge_geometries", "compact_arrays", "float_arrays", "recenter",
        "simplify", "generate_lod", "vertex_cache", "downscale_textures",
    };

    public static List<string> PipelineSort(IEnumerable<string> names)
    {
        var rank = PipelineOrder.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        return names.Select((n, i) => (n, i)).OrderBy(x => rank.GetValueOrDefault(x.n, rank.Count)).ThenBy(x => x.i).Select(x => x.n).ToList();
    }

    public static readonly Dictionary<string, PassSpec> Passes = new();

    /// <summary>"simplify.ratio=0.25" for every pass parameter given in <paramref name="prms"/> that differs from its default; sorted.</summary>
    public static List<string> NonDefaultParams(IEnumerable<string> passes, Dictionary<string, object>? prms)
    {
        var o = new List<string>();
        if (prms == null) return o;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        static string Str(object v, System.Globalization.CultureInfo ic) => v is bool b ? (b ? "true" : "false") : Convert.ToString(v, ic) ?? "";
        foreach (var name in passes.Distinct())
        {
            if (!Passes.TryGetValue(name, out var spec)) continue;
            foreach (var pp in spec.Params)
            {
                var key = $"{name}.{pp.Key}";
                if (prms.TryGetValue(key, out var v) && v != null && Str(v, ic) != Str(pp.Default, ic)) o.Add($"{key}={Str(v, ic)}");
            }
        }
        o.Sort(StringComparer.Ordinal);
        return o;
    }

    /// <summary>"replace_cards.texture=512" for every parameter of these passes, given in <paramref name="prms"/> or its default;
    /// sorted ordinal. The record of a profile run: it names every value, so a later default change cannot alter its meaning.</summary>
    public static List<string> AllParams(IEnumerable<string> passes, Dictionary<string, object>? prms)
    {
        var o = new List<string>();
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        static string Str(object v, System.Globalization.CultureInfo ic) => v is bool b ? (b ? "true" : "false") : Convert.ToString(v, ic) ?? "";
        foreach (var name in passes.Distinct())
        {
            if (!Passes.TryGetValue(name, out var spec)) continue;
            foreach (var pp in spec.Params)
            {
                var key = $"{name}.{pp.Key}";
                object v = prms != null && prms.TryGetValue(key, out var given) && given != null ? given : pp.Default;
                o.Add($"{key}={Str(v, ic)}");
            }
        }
        o.Sort(StringComparer.Ordinal);
        return o;
    }

    static void Reg(string name, string label, string group, bool safe, string help, Func<OptContext, string> fn, params PassParam[] prms) =>
        Passes[name] = new PassSpec(name, label, group, safe, help, prms, fn);

    static readonly HashSet<string> FltPlain = new() { "FLT Header", "FLT Group", "FLT Object", "FLT Faces", "FLT Mesh", "FLT Matrix", "FLT Instance Reference" };
    static readonly Dictionary<string, string> OsgClassForKind = new()
    {
        [Kind.Group] = "Group", [Kind.Geode] = "Geode", [Kind.Transform] = "MatrixTransform", [Kind.Lod] = "LOD",
        [Kind.Switch] = "Switch", [Kind.Proxy] = "ProxyNode", [Kind.PagedLod] = "PagedLOD", [Kind.Geometry] = "Geometry",
    };

    /// <summary>The OSG class a node is (or will be written as): FLT records become plain OSG nodes.</summary>
    public static string OsgClass(Node n) => n.Cls.StartsWith("FLT ", StringComparison.Ordinal) ? OsgClassForKind.GetValueOrDefault(n.Kind, "Group") : OsgReader.Short(n.Cls);

    internal static readonly Regex AutoFltName = new("^[A-Za-z]{1,4}[0-9]+$", RegexOptions.Compiled);

    /// <summary>OpenFlight tools name every node automatically (g12, o145, ...); those names carry no meaning.</summary>
    public static bool HasMeaningfulName(Node n) =>
        n.Name.Length > 0 && !(n.Cls.StartsWith("FLT ", StringComparison.Ordinal) && AutoFltName.IsMatch(n.Name));

    /// <summary>No callbacks / user data / class specific state we would lose by removing or merging the node.</summary>
    public static bool IsPlain(Node n)
    {
        if (n.Cls.StartsWith("FLT ", StringComparison.Ordinal))
            return FltPlain.Contains(n.Cls) && !n.Notes.Any(x => x is "animated group" or "shadow object");
        if (n.Src is not Obj src) return n.Kind is Kind.Group or Kind.Geode or Kind.Transform or Kind.Geometry;
        if (OsgReader.Short(n.Cls) is not ("Group" or "Geode" or "MatrixTransform" or "PositionAttitudeTransform")) return false;
        foreach (var s in src.Stmts)
        {
            if (!PlainKeys.Contains(s.Key)) return false;
            if (s.Key == "DataVariance" && s.Toks.Length > 1 && s.Toks[1] == "DYNAMIC") return false;
            if (s.Key is "ReferenceFrame" or "referenceFrame" && s.Toks.Length > 1 && s.Toks[1].Contains("ABSOLUTE")) return false;
        }
        return true;
    }

    public static bool ReplaceChild(Node parent, Node old, IEnumerable<Node> newList) =>
        ReplaceChildren(parent, new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance) { [old] = newList.ToList() });

    /// <summary>Replace several children in one pass (an empty list removes the child); LOD ranges and switch values stay aligned.</summary>
    public static bool ReplaceChildren(Node parent, Dictionary<Node, List<Node>> mapping)
    {
        if (parent.Kind == Kind.External) return false;
        var o = new List<Node>(parent.Children.Count);
        var ranges = new List<(double, double)>();
        var values = new List<bool>();
        bool hasR = parent.Ranges != null, hasV = parent.SwitchValues != null;
        bool changed = false;
        for (int i = 0; i < parent.Children.Count; i++)
        {
            var c = parent.Children[i];
            IEnumerable<Node> items;
            if (mapping.TryGetValue(c, out var rep)) { items = rep; changed = true; } else items = new[] { c };
            foreach (var nc in items)
            {
                o.Add(nc);
                if (hasR && i < parent.Ranges!.Count) ranges.Add(parent.Ranges[i]);
                if (hasV && i < parent.SwitchValues!.Count) values.Add(parent.SwitchValues[i]);
            }
        }
        if (changed)
        {
            parent.Children = o;
            parent.Mark("children");
            if (hasR) { parent.Ranges = ranges; parent.Mark("ranges"); }
            if (hasV) parent.SwitchValues = values;
        }
        return changed;
    }

    static int ApplyReplacements(OptContext ctx, Dictionary<Node, Dictionary<Node, List<Node>>> todo)
    {
        int n = 0;
        foreach (var (p, mapping) in todo) if (ctx.ReplaceMany(p, mapping)) n += mapping.Count;
        return n;
    }

    static bool FixedChildCount(Node parent) => parent.Kind is Kind.Lod or Kind.PagedLod or Kind.Switch or Kind.Proxy;

    static void SetGeom(Node n, GeomData g) { n.Geom = g; n.Mark("geometry"); }

    static string Summ(long count, string what, string extra = "") => $"{count} {what}" + (extra.Length > 0 ? $" ({extra})" : "");

    // ------------------------------------------------------------------------------ geometry core

    public sealed class NormInfo
    {
        public int VertsBefore, VertsAfter, TrisBefore, TrisAfter, Degenerate, DuplicateTris;
    }

    /// <summary>Geometry as at most three indexed lists (TRIANGLES, LINES, POINTS), optionally welded / cleaned.</summary>
    public static (GeomData g, NormInfo info) Normalized(GeomData g, bool weld = false, double tol = 0.0, bool dropDegenerate = false,
        bool dropUnused = true, bool dedupeTris = false)
    {
        var tris = g.Triangles();
        var lines = g.Lines();
        var pts = g.Points();
        int nv = g.NumVertices;
        var keep = Enumerable.Range(0, nv).ToArray();
        var info = new NormInfo { VertsBefore = nv, TrisBefore = tris.Length / 3 };
        if (weld && nv > 0)
        {
            var (mat, k, _) = MeshOps.VertexMatrix(g);
            var (first, inv) = MeshOps.UniqueRows(mat, nv, k, tol);
            keep = first;
            tris = tris.Select(i => inv[i]).ToArray();
            lines = lines.Select(i => inv[i]).ToArray();
            pts = pts.Select(i => inv[i]).ToArray();
        }
        if (dropDegenerate && tris.Length > 0)
        {
            VArray? pos = g.Vertices == null ? null : Gather(g.Vertices, keep);
            var bad = MeshOps.DegenerateMask(pos, tris);
            info.Degenerate = bad.Count(b => b);
            var tl = new List<int>(tris.Length);
            for (int t = 0; t < bad.Length; t++) if (!bad[t]) { tl.Add(tris[3 * t]); tl.Add(tris[3 * t + 1]); tl.Add(tris[3 * t + 2]); }
            tris = tl.ToArray();
            if (lines.Length > 0)
            {
                var ll = new List<int>(lines.Length);
                for (int i = 0; i + 1 < lines.Length; i += 2) if (lines[i] != lines[i + 1]) { ll.Add(lines[i]); ll.Add(lines[i + 1]); }
                lines = ll.ToArray();
            }
        }
        if (dedupeTris && tris.Length > 0)
        {
            var seen = new HashSet<(int, int, int)>();
            var tl = new List<int>(tris.Length);
            int before = tris.Length / 3;
            for (int t = 0; t < before; t++)
                if (seen.Add(MeshOps.Canon(tris[3 * t], tris[3 * t + 1], tris[3 * t + 2]))) { tl.Add(tris[3 * t]); tl.Add(tris[3 * t + 1]); tl.Add(tris[3 * t + 2]); }
            info.DuplicateTris = before - tl.Count / 3;
            tris = tl.ToArray();
        }
        if (dropUnused)
        {
            var usedSet = new SortedSet<int>(tris.Concat(lines).Concat(pts));
            var used = usedSet.ToArray();
            var remap = new int[keep.Length];
            Array.Fill(remap, -1);
            for (int i = 0; i < used.Length; i++) remap[used[i]] = i;
            keep = used.Select(u => keep[u]).ToArray();
            tris = tris.Select(i => remap[i]).ToArray();
            lines = lines.Select(i => remap[i]).ToArray();
            pts = pts.Select(i => remap[i]).ToArray();
        }
        var o = Rebuild(g, keep, tris, lines, pts);
        info.VertsAfter = o.NumVertices;
        info.TrisAfter = tris.Length / 3;
        return (o, info);
    }

    public static VArray Gather(VArray a, int[] rows)
    {
        int c = a.Comps;
        var d = new double[rows.Length * c];
        for (int i = 0; i < rows.Length; i++) Array.Copy(a.Data, rows[i] * c, d, i * c, c);
        return a.CopyWith(d);
    }

    static void SetArray(GeomData o, string name, VArray a)
    {
        if (name == "vertices") o.Vertices = a;
        else if (name == "normals") o.Normals = a;
        else if (name == "colors") o.Colors = a;
        else if (name.StartsWith("texcoord", StringComparison.Ordinal)) o.TexCoords[int.Parse(name[8..])] = a;
        else o.Extra[name] = a;
    }

    /// <summary>New GeomData with the vertices <paramref name="keep"/> and the given index lists (new numbering).</summary>
    public static GeomData Rebuild(GeomData g, int[] keep, int[]? tris = null, int[]? lines = null, int[]? points = null)
    {
        var o = new GeomData { Notes = new List<string>(g.Notes) };
        int nv = g.NumVertices;
        foreach (var (name, a) in g.Arrays())
        {
            var na = a.Binding == "vertex" && a.Count == nv ? Gather(a, keep) : a.CopyWith((double[])a.Data.Clone());
            SetArray(o, name, na);
        }
        string it = MeshOps.SmallestIndexType(keep.Length);
        if (tris is { Length: > 0 }) o.Prims.Add(new Primitive("TRIANGLES", "elements", indices: tris, indexType: it));
        if (lines is { Length: > 0 }) o.Prims.Add(new Primitive("LINES", "elements", indices: lines, indexType: it));
        if (points is { Length: > 0 }) o.Prims.Add(new Primitive("POINTS", "elements", indices: points, indexType: it));
        return o;
    }

    // ------------------------------------------------------------------------------ runner

    /// <summary>Apply passes in order: (results, totals before, totals after). Every result carries its duration,
    /// the geometries it created / changed, removed nodes and the totals after it.</summary>
    public static (List<PassResult> results, Dictionary<string, object?> before, Dictionary<string, object?> after) Run(
        Scene scene, IEnumerable<string> names, Dictionary<string, object>? prms = null, List<Node>? targets = null,
        Action<string>? progress = null, bool track = true)
    {
        progress ??= _ => { };
        TextureFormats.Forget(scene);
        // scene setting: naming rules of companion images (infrared ...), "{stem}_ir{ext}; ir/{stem}{ext}"
        if (prms != null && prms.TryGetValue("companion_patterns", out var cp) && cp != null)
        {
            var wanted = Companions.Parse(Convert.ToString(cp, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            if (!wanted.SequenceEqual(scene.CompanionPatterns))
                foreach (var problem in Companions.SetPatterns(scene, wanted).problems)
                    if (!scene.Warnings.Contains(problem)) scene.Warnings.Add(problem);
        }
        var before = Analyzer.Analyze(scene, deepChecks: false, findings: false).Summary();
        var ctx = new OptContext(scene, prms, targets, progress);
        var results = new List<PassResult>();
        var state = track ? History.GeometryState(scene) : null;
        var totals = track ? History.QuickTotals(scene) : null;
        foreach (var name in names)
        {
            if (!Passes.TryGetValue(name, out var spec)) { results.Add(new PassResult(name, "", $"unknown pass '{name}'")); continue; }
            progress($"{spec.Label} ...");
            var sw = Stopwatch.StartNew();
            ctx.Sources = new(ReferenceEqualityComparer.Instance);
            ctx.Verdicts = new(ReferenceEqualityComparer.Instance);
            ctx.Record = new();
            PassResult r;
            try { r = new PassResult(name, spec.Fn(ctx)); }
            catch (Exception exc) { r = new PassResult(name, "", $"{exc.GetType().Name}: {exc.Message}\n{exc}"); }
            r.Seconds = sw.Elapsed.TotalSeconds;
            r.Record = ctx.Record;
            results.Add(r);
            ctx.Invalidate();
            if (track)
            {
                var newState = History.GeometryState(scene);
                r.Changes = History.Diff(name, state!, newState, ctx.Sources);
                foreach (var c in r.Changes)
                    if (ctx.Verdicts.TryGetValue(c.Node, out var q)) c.Quality = q;
                r.RemovedNodes = History.RemovedCount(state!, scene);
                r.TotalsBefore = totals;
                r.Totals = totals = History.QuickTotals(scene);
                state = newState;
            }
        }
        var after = Analyzer.Analyze(scene, deepChecks: false, findings: false).Summary();
        return (results, before, after);
    }

    public static string DefaultOutputPath(Scene scene)
    {
        // the source's own format: .flt stays .flt; scenes read through osgconv (.osgb, .ive, ...) are converted back
        var b = scene.ConvertedFrom ?? scene.Path;
        string ext = scene.ConvertedFrom != null && System.IO.Path.GetExtension(b).Length > 0 ? System.IO.Path.GetExtension(b).ToLowerInvariant() : OsgWrite.NativeExt(scene);
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(b) ?? "", System.IO.Path.GetFileNameWithoutExtension(b) + ".optimized" + ext);
    }
}
