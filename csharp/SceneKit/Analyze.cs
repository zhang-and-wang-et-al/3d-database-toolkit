using System.Text;

namespace SceneKit;

public sealed class NodeStats
{
    public long Tris, Verts, Lines, Points, Draws, VisTris, Geoms, Nodes, GeoBytes, TexBytes;
    public (double[] lo, double[] hi)? BBox;
    public int Depth;
    public long Bytes => GeoBytes + TexBytes;
}

/// <summary>Facts about one unique geometry.</summary>
public sealed class GeoInfo
{
    public Node Node;
    public long Tris, Lines, Points, Verts, VertexBytes, IndexBytes, Draws, Instances;
    public (double[] lo, double[] hi)? BBox;
    public double Size, WorldSize, MaxCoord;
    public long DupVertices, Degenerate, Unused;
    public double? Acmr;
    public string? Key;
    public List<string> Modes = new(), Kinds = new(), IndexTypes = new(), DoubleArrays = new();
    public bool ConstColor, UnderLod;
    public GeoInfo(Node n) { Node = n; }
    public long Bytes => VertexBytes + IndexBytes;
}

public sealed class Finding
{
    public string Code, Severity, Title, Message;
    public List<Node> Nodes;
    public string? Fix;
    public long SavingBytes, SavingTris, SavingDraws;
    public List<Texture> Textures;
    public List<Node>? Scope;   // nodes the fix should run on (default: Nodes)

    public Finding(string code, string severity, string title, string message, IEnumerable<Node>? nodes = null, string? fix = null,
        long savingBytes = 0, long savingTris = 0, long savingDraws = 0, IEnumerable<Texture>? textures = null, IEnumerable<Node>? scope = null)
    {
        Code = code; Severity = severity; Title = title; Message = message;
        Nodes = nodes?.ToList() ?? new List<Node>(); Fix = fix;
        SavingBytes = savingBytes; SavingTris = savingTris; SavingDraws = savingDraws;
        Textures = textures?.ToList() ?? new List<Texture>();
        Scope = scope?.ToList();
    }

    public List<Node> FixTargets => Scope ?? Nodes;
}

public sealed class Report
{
    public static readonly Dictionary<string, object> Defaults = new()
    {
        ["heavy_tris"] = 100_000L, ["heavy_share"] = 0.25, ["density_factor"] = 25.0, ["density_min_tris"] = 2_000L,
        ["large_texture"] = 2048L, ["many_drawables"] = 8L, ["small_drawable_tris"] = 1_000L, ["many_primsets"] = 16L,
        ["large_coord"] = 1e5, ["deep"] = 32L, ["weld_min_saving"] = 0.2, ["max_check_verts"] = 3_000_000L,
        ["acmr_min_tris"] = 10_000L, ["acmr_bad"] = 1.0,
    };
    public static readonly Dictionary<string, int> SeverityOrder = new() { ["high"] = 0, ["medium"] = 1, ["low"] = 2, ["info"] = 3 };

    public readonly Scene Scene;
    public readonly Dictionary<Node, NodeStats> Stats = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Node, long> Instances = new(ReferenceEqualityComparer.Instance);
    public readonly Dictionary<Node, List<Node>> Parents = new(ReferenceEqualityComparer.Instance);
    public readonly List<GeoInfo> Geoms = new();
    public readonly Dictionary<Node, GeoInfo> GeoInfoOf = new(ReferenceEqualityComparer.Instance);
    public readonly List<Texture> Textures = new();
    public readonly Dictionary<Texture, List<Node>> TexUsers = new(ReferenceEqualityComparer.Instance);
    public List<Finding> Findings = new();
    public readonly Dictionary<string, object> Settings = new(Defaults);
    public int NodeCount, MaxDepth;

    public Report(Scene scene) { Scene = scene; }

    TextureIndex? texIndex;
    /// <summary>Effective texture units, unit masks per subtree, state set users (built on first use).</summary>
    public TextureIndex TexIndex => texIndex ??= new TextureIndex(this);

    public double S(string k) => Convert.ToDouble(Settings[k]);

    public NodeStats Total => Scene.Root != null && Stats.TryGetValue(Scene.Root, out var s) ? s : new NodeStats();
    public NodeStats Stat(Node n) => Stats.TryGetValue(n, out var s) ? s : new NodeStats();

    /// <summary>'root/child/...' for the first path to the node.</summary>
    public string PathOf(Node node)
    {
        var parts = new List<string>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        Node? cur = node;
        while (cur != null && seen.Add(cur))
        {
            parts.Add(cur.Name.Length > 0 ? cur.Name : OsgReader.Short(cur.Cls));
            cur = Parents.TryGetValue(cur, out var ps) && ps.Count > 0 ? ps[0] : null;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>All nodes (externals included), in the Python traversal order.</summary>
    public IEnumerable<Node> AllNodes()
    {
        if (Scene.Root == null) yield break;
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new List<Node> { Scene.Root };
        while (stack.Count > 0)
        {
            var n = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!seen.Add(n)) continue;
            yield return n;
            stack.AddRange(n.AllChildren());
        }
    }

    public Dictionary<string, object?> Summary()
    {
        var t = Total;
        var bb = t.BBox;
        List<double>? size = bb != null ? bb.Value.hi.Zip(bb.Value.lo, (h, l) => h - l).ToList() : null;
        var ss = new HashSet<StateSet>(ReferenceEqualityComparer.Instance);
        foreach (var n in AllNodes()) if (n.StateSet != null) ss.Add(n.StateSet);
        return new Dictionary<string, object?>
        {
            ["file"] = Scene.Path, ["format"] = Scene.Fmt, ["version"] = Scene.Version, ["nodes"] = NodeCount,
            ["unique_geometries"] = Geoms.Count, ["geometry_instances"] = t.Geoms,
            ["triangles"] = t.Tris, ["visible_triangles"] = t.VisTris, ["vertices"] = t.Verts, ["lines"] = t.Lines,
            ["points"] = t.Points, ["draw_calls"] = t.Draws, ["geometry_bytes"] = t.GeoBytes, ["texture_bytes"] = t.TexBytes,
            ["textures"] = Textures.Count, ["statesets"] = ss.Count, ["max_depth"] = MaxDepth, ["size"] = size,
            ["findings"] = SeverityOrder.Keys.ToDictionary(s => s, s => Findings.Count(f => f.Severity == s)),
        };
    }
}

public static class Analyzer
{
    /// <summary>Sibling LOD levels that are not drawn up close, or null when there are none. OpenFlight (and osgconv
    /// output of it) stores every level as its own LOD node with one child next to the other levels: levels with the
    /// same centre form a group, and only the level(s) with the smallest switch-out distance of a group count.</summary>
    /// <summary>An OpenFlight LOD record: its one range applies to all its children (OSG's loader puts them under one
    /// group), unlike an osg::LOD, which has a range per child.</summary>
    public static bool OneRangeForAll(Node n) => n.Kind == Kind.Lod && n.Cls == "FLT LOD";

    public static bool[]? HiddenLodSiblings(IReadOnlyList<Node> kids)
    {
        if (kids.Count < 2) return null;
        Dictionary<(double, double, double), double>? nearest = null;
        static (double, double, double) Key(Node n) => n.Center is { Length: >= 3 } c ? (c[0], c[1], c[2]) : (double.NaN, 0, 0);
        static bool Level(Node n) => n.Kind == Kind.Lod && (n.Children.Count <= 1 || OneRangeForAll(n)) && n.Ranges is { Count: > 0 };
        int levels = 0;
        foreach (var k in kids)
        {
            if (!Level(k)) continue;
            levels++;
            nearest ??= new();
            var key = Key(k);
            double near = k.Ranges![0].min;
            nearest[key] = nearest.TryGetValue(key, out var m) ? Math.Min(m, near) : near;
        }
        if (levels < 2 || nearest!.Count == levels) return null;   // every group has a single level
        var hidden = new bool[kids.Count];
        bool any = false;
        for (int i = 0; i < kids.Count; i++)
            if (Level(kids[i]) && kids[i].Ranges![0].min > nearest[Key(kids[i])]) { hidden[i] = true; any = true; }
        return any ? hidden : null;
    }

    public static string HumanBytes(double n)
    {
        foreach (var unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (Math.Abs(n) < 1024 || unit == "GB")
                return unit == "B" ? $"{PyFormat.Fixed(n, 0)} {unit}" : $"{PyFormat.Fixed(n, 1)} {unit}";
            n /= 1024.0;
        }
        return $"{PyFormat.Fixed(n, 1)} GB";
    }

    public static string HumanCount(long n)
    {
        if (n >= 1_000_000) return $"{PyFormat.Fixed(n / 1e6, 2)}M";
        if (n >= 10_000) return $"{PyFormat.Fixed(n / 1e3, 1)}k";
        return n.ToString();
    }

    static double MatScale(double[]? m)
    {
        if (m == null) return 1.0;
        double best = 0;
        for (int c = 0; c < 3; c++)
        {
            double s = Math.Sqrt(m[c] * m[c] + m[4 + c] * m[4 + c] + m[8 + c] * m[8 + c]);
            if (s > best || double.IsNaN(s)) best = double.IsNaN(best) ? best : s;
        }
        return best == 0 ? 1.0 : best;
    }

    static (double[] lo, double[] hi)? TransformBBox((double[] lo, double[] hi)? bb, double[]? m)
    {
        if (bb == null || m == null) return bb;
        var (lo, hi) = bb.Value;
        var olo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var ohi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var x in new[] { lo[0], hi[0] })
            foreach (var y in new[] { lo[1], hi[1] })
                foreach (var z in new[] { lo[2], hi[2] })
                {
                    var (px, py, pz) = Mat.Apply(m, x, y, z);
                    Upd(olo, ohi, 0, px); Upd(olo, ohi, 1, py); Upd(olo, ohi, 2, pz);
                }
        return (olo, ohi);
    }

    static void Upd(double[] lo, double[] hi, int i, double v)
    {
        // numpy min / max propagate NaN
        if (double.IsNaN(v) || double.IsNaN(lo[i])) { lo[i] = double.NaN; hi[i] = double.NaN; return; }
        if (v < lo[i]) lo[i] = v;
        if (v > hi[i]) hi[i] = v;
    }

    static (double[] lo, double[] hi)? Union((double[] lo, double[] hi)? a, (double[] lo, double[] hi)? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        var lo = new double[3]; var hi = new double[3];
        for (int i = 0; i < 3; i++)
        {
            lo[i] = double.IsNaN(a.Value.lo[i]) || double.IsNaN(b.Value.lo[i]) ? double.NaN : Math.Min(a.Value.lo[i], b.Value.lo[i]);
            hi[i] = double.IsNaN(a.Value.hi[i]) || double.IsNaN(b.Value.hi[i]) ? double.NaN : Math.Max(a.Value.hi[i], b.Value.hi[i]);
        }
        return (lo, hi);
    }

    static (double[] lo, double[] hi)? Pad3((double[] lo, double[] hi)? bb)
    {
        if (bb == null || bb.Value.lo.Length == 3) return bb;
        double[] P(double[] v) { var o = new double[3]; for (int i = 0; i < Math.Min(3, v.Length); i++) o[i] = v[i]; return o; }
        return (P(bb.Value.lo), P(bb.Value.hi));
    }

    sealed class IdSet { public Dictionary<object, long> Ids = new(ReferenceEqualityComparer.Instance); public long Sum; }

    static IdSet AddId(IdSet? acc, object key, long size)
    {
        acc ??= new IdSet();
        if (acc.Ids.TryAdd(key, size)) acc.Sum += size;
        return acc;
    }

    static IdSet? MergeIds(IdSet? acc, IdSet? other, bool mayTake)
    {
        if (other == null || other.Ids.Count == 0) return acc;
        if (acc == null)
        {
            if (mayTake) return other;
            return new IdSet { Ids = new Dictionary<object, long>(other.Ids, ReferenceEqualityComparer.Instance), Sum = other.Sum };
        }
        if (mayTake && other.Ids.Count > acc.Ids.Count) (acc, other) = (other, acc);
        foreach (var (k, size) in other.Ids) if (acc.Ids.TryAdd(k, size)) acc.Sum += size;
        return acc;
    }

    /// <summary>Analyse a scene. <paramref name="findings"/> = false only computes the statistics.</summary>
    public static Report Analyze(Scene scene, Dictionary<string, object>? settings = null, Action<string>? progress = null,
        bool deepChecks = true, bool findings = true)
    {
        progress ??= _ => { };
        var rep = new Report(scene);
        if (settings != null) foreach (var kv in settings) rep.Settings[kv.Key] = kv.Value;
        var root = scene.Root;
        if (root == null) return rep;

        // ---- topology: post order, parents
        var order = new List<Node>();
        var state = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        var depthOf = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance) { [root] = 0 };
        var stack = new List<(Node n, int d)> { (root, 0) };
        while (stack.Count > 0)
        {
            var (n, d) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            state.TryGetValue(n, out int st);
            if (st == 2) continue;
            if (st == 1) { state[n] = 2; order.Add(n); continue; }
            state[n] = 1;
            stack.Add((n, d));
            foreach (var c in n.AllChildren())
            {
                if (!rep.Parents.TryGetValue(c, out var pl)) rep.Parents[c] = pl = new List<Node>(1);
                pl.Add(n);
                if (!state.ContainsKey(c)) { depthOf[c] = d + 1; stack.Add((c, d + 1)); }
            }
        }
        rep.NodeCount = order.Count;
        rep.MaxDepth = depthOf.Count > 0 ? depthOf.Values.Max() : 0;
        progress($"{rep.NodeCount} nodes");

        // ---- instance counts and world scale, top-down
        var inst = new Dictionary<Node, long>(ReferenceEqualityComparer.Instance) { [root] = 1 };
        var scale = new Dictionary<Node, double>(ReferenceEqualityComparer.Instance) { [root] = MatScale(root.Matrix) };
        var underLod = new Dictionary<Node, bool>(ReferenceEqualityComparer.Instance) { [root] = false };
        for (int oi = order.Count - 1; oi >= 0; oi--)
        {
            var n = order[oi];
            long k = inst.GetValueOrDefault(n, 0);
            double s = scale.GetValueOrDefault(n, 1.0);
            bool lod = underLod.GetValueOrDefault(n, false) || n.Kind is Kind.Lod or Kind.PagedLod;
            foreach (var c in n.AllChildren())
            {
                inst[c] = inst.GetValueOrDefault(c, 0) + k;
                double cs = s * MatScale(c.Matrix);
                if (cs > scale.GetValueOrDefault(c, 0)) scale[c] = cs;
                underLod[c] = underLod.GetValueOrDefault(c, false) || lod;
            }
        }
        rep.Instances = inst;

        // ---- bottom-up statistics (shared memory counted once per subtree, linear time)
        var plainBytes = new Dictionary<Node, long>(ReferenceEqualityComparer.Instance);
        var gsets = new Dictionary<Node, IdSet>(ReferenceEqualityComparer.Instance);
        var tsets = new Dictionary<Node, IdSet>(ReferenceEqualityComparer.Instance);
        var pending = rep.Parents.ToDictionary(kv => kv.Key, kv => kv.Value.Count, (IEqualityComparer<Node>)ReferenceEqualityComparer.Instance);
        foreach (var n in order)
        {
            var st = new NodeStats { Nodes = 1 };
            long ownPlain = 0;
            IdSet? gset = null, tset = null;
            if (n.StateSet != null)
            {
                foreach (var t in n.StateSet.Textures)
                {
                    if (!rep.TexUsers.TryGetValue(t, out var users)) rep.TexUsers[t] = users = new List<Node>();
                    if (users.Count == 0) rep.Textures.Add(t);
                    users.Add(n);
                    tset = AddId(tset, t, t.GpuBytes);
                }
            }
            if (n.Kind == Kind.Geometry && n.Geom != null)
            {
                var g = n.Geom;
                var gi = new GeoInfo(n);
                var (tris, lines, points) = g.Counts();
                gi.Tris = tris; gi.Lines = lines; gi.Points = points;
                gi.Verts = g.NumVertices;
                gi.VertexBytes = g.VertexBytes;
                gi.IndexBytes = g.IndexBytes;
                gi.Draws = g.DrawCalls;
                gi.Instances = inst.GetValueOrDefault(n, 1);
                gi.BBox = Pad3(g.BBox());
                if (gi.BBox != null)
                {
                    var (lo, hi) = gi.BBox.Value;
                    double s2 = 0;
                    for (int i = 0; i < 3; i++) s2 += (hi[i] - lo[i]) * (hi[i] - lo[i]);
                    gi.Size = Math.Sqrt(s2);
                    gi.MaxCoord = lo.Concat(hi).Select(Math.Abs).Max();
                }
                gi.WorldSize = gi.Size * scale.GetValueOrDefault(n, 1.0);
                gi.Modes = g.Prims.Select(p => p.Mode).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                gi.Kinds = g.Prims.Select(p => p.Kind).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                gi.IndexTypes = g.Prims.Where(p => !string.IsNullOrEmpty(p.IndexType)).Select(p => p.IndexType!).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                gi.DoubleArrays = g.Arrays().Where(x => x.a.Type.EndsWith("dArray", StringComparison.Ordinal) || x.a.Type == "DoubleArray").Select(x => x.name).ToList();
                gi.UnderLod = underLod.GetValueOrDefault(n, false);
                rep.Geoms.Add(gi);
                rep.GeoInfoOf[n] = gi;
                st.Tris = tris; st.Lines = lines; st.Points = points;
                st.Verts = gi.Verts;
                st.Draws = gi.Draws;
                st.VisTris = n.NodeMask != 0 ? tris : 0;
                st.Geoms = 1;
                if (gi.Instances > 1) gset = AddId(gset, n, gi.Bytes);
                else ownPlain = gi.Bytes;
                st.BBox = gi.BBox;
            }
            var kids = n.AllChildren();
            var visChildren = new List<long>();
            var lodHidden = HiddenLodSiblings(kids);
            for (int i = 0; i < kids.Count; i++)
            {
                var c = kids[i];
                var cs = rep.Stats.TryGetValue(c, out var cst) ? cst : new NodeStats();
                st.Tris += cs.Tris; st.Verts += cs.Verts; st.Lines += cs.Lines; st.Points += cs.Points;
                st.Draws += cs.Draws; st.Geoms += cs.Geoms; st.Nodes += cs.Nodes;
                if (!(n.Kind == Kind.Switch && n.SwitchValues != null && i < n.SwitchValues.Count && !n.SwitchValues[i])
                    && lodHidden?[i] != true)
                    visChildren.Add(cs.VisTris);
                st.BBox = Union(st.BBox, TransformBBox(cs.BBox, c.Matrix));
                ownPlain += plainBytes.GetValueOrDefault(c, 0);
                bool last = pending.GetValueOrDefault(c, 0) == 1;
                gset = MergeIds(gset, gsets.GetValueOrDefault(c), last);
                tset = MergeIds(tset, tsets.GetValueOrDefault(c), last);
            }
            if (visChildren.Count > 0) st.VisTris += n.Kind is Kind.Lod or Kind.PagedLod && !OneRangeForAll(n) ? visChildren.Max() : visChildren.Sum();
            if (n.NodeMask == 0) st.VisTris = 0;
            plainBytes[n] = ownPlain;
            st.GeoBytes = ownPlain + (gset?.Sum ?? 0);
            st.TexBytes = tset?.Sum ?? 0;
            if (gset != null && gset.Ids.Count > 0) gsets[n] = gset;
            if (tset != null && tset.Ids.Count > 0) tsets[n] = tset;
            foreach (var c in kids)
            {
                int left = pending.GetValueOrDefault(c, 0) - 1;
                pending[c] = left;
                if (left <= 0) { gsets.Remove(c); tsets.Remove(c); plainBytes.Remove(c); }
            }
            st.Depth = depthOf.GetValueOrDefault(n, 0);
            rep.Stats[n] = st;
        }
        progress("statistics done");
        if (findings) FindingsOf(rep, deepChecks, progress);
        rep.Findings = rep.Findings
            .Select((f, i) => (f, i))
            .OrderBy(x => Report.SeverityOrder.GetValueOrDefault(x.f.Severity, 9))
            .ThenBy(x => -(x.f.SavingBytes + x.f.SavingTris * 40))
            .ThenBy(x => x.i)
            .Select(x => x.f).ToList();
        return rep;
    }

    static string Sev(double share, double high = 0.10, double medium = 0.02) => share >= high ? "high" : (share >= medium ? "medium" : "low");

    static void FindingsOf(Report rep, bool deep, Action<string> progress)
    {
        var F = rep.Findings;
        var tot = rep.Total;
        double totalTris = Math.Max(tot.Tris, 1), totalBytes = Math.Max(tot.Bytes, 1), totalDraws = Math.Max(tot.Draws, 1);
        double sceneDiag = 0;
        if (tot.BBox != null)
        {
            var (lo, hi) = tot.BBox.Value;
            sceneDiag = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (hi[i] - lo[i]) * (hi[i] - lo[i])));
        }
        var editable = rep.Geoms.Where(g => g.Node.Geom != null).ToList();

        // --- heavy geometry / LOD suggestions
        var heavy = editable.Where(g => g.Tris * g.Instances >= rep.S("heavy_tris")
                                        || (g.Tris * g.Instances >= 10_000 && g.Tris * g.Instances / totalTris >= rep.S("heavy_share")))
                            .Select((g, i) => (g, i)).OrderBy(x => -x.g.Tris * x.g.Instances).ThenBy(x => x.i).Select(x => x.g).ToList();
        foreach (var g in heavy.Take(25))
        {
            double share = g.Tris * g.Instances / totalTris;
            string instTxt = g.Instances > 1 ? $" (drawn {g.Instances}x)" : "";
            F.Add(new Finding("heavy_geometry", Sev(share, 0.25, 0.05), $"Heavy geometry: {HumanCount(g.Tris)} triangles{instTxt}",
                $"{rep.PathOf(g.Node)} holds {PyFormat.Percent(share, 0)} of all triangles. Simplify it (e.g. to 50 %) or, "
                + (g.UnderLod ? "it is already inside a LOD." : "add LOD levels so the full detail is only drawn up close."),
                new[] { g.Node }, g.UnderLod ? "simplify" : "generate_lod", savingTris: g.Tris * g.Instances / 2));
        }

        // --- over-tessellated
        var cands = editable.Where(g => g.Tris >= 50 && g.WorldSize > 0).ToList();
        if (sceneDiag > 0 && cands.Count >= 3)
        {
            var dens = cands.Select(g => g.Tris / Math.Pow(Math.Max(g.WorldSize / sceneDiag, 1e-9), 2)).ToArray();
            var sorted = dens.OrderBy(x => x).ToArray();
            double med = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            var ranked = cands.Zip(dens).Select((x, i) => (x.First, x.Second, i)).OrderBy(x => -x.Second).ThenBy(x => x.i).Take(30);
            foreach (var (g, d, _) in ranked)
            {
                if (g.Tris >= rep.S("density_min_tris") && med > 0 && d > rep.S("density_factor") * med)
                {
                    double rel = g.WorldSize / sceneDiag;
                    string relTxt = rel >= 0.001 ? PyFormat.Percent(rel, 1) : "<0.1%";
                    F.Add(new Finding("over_tessellated", Sev(g.Tris * g.Instances / totalTris, 0.1, 0.01),
                        $"Small but detailed: {HumanCount(g.Tris)} triangles at {relTxt} of the scene size",
                        $"{rep.PathOf(g.Node)} has {PyFormat.Fixed(d / med, 0, true)}x more triangles per area than a typical object in this scene. "
                        + "Simplifying it rarely shows.", new[] { g.Node }, "simplify", savingTris: (long)(g.Tris * 0.75) * g.Instances));
                }
            }
        }

        // --- duplicated geometry
        var groups = new Dictionary<string, List<GeoInfo>>();
        if (deep)
        {
            progress("looking for duplicate geometry ...");
            var ssIds = new Dictionary<StateSet, int>(ReferenceEqualityComparer.Instance);
            foreach (var g in editable)
            {
                if (g.Verts == 0 || g.Verts > rep.S("max_check_verts")) continue;
                var ss = g.Node.StateSet;
                string ssKey = ss == null ? "-" : (ssIds.TryGetValue(ss, out var sid) ? sid : (ssIds[ss] = ssIds.Count)).ToString();
                g.Key = g.Node.Geom!.ContentKey() + "|" + ssKey;
                if (!groups.TryGetValue(g.Key, out var l)) groups[g.Key] = l = new List<GeoInfo>();
                l.Add(g);
            }
        }
        // --- similar objects (copies under different nodes); duplicate geometry inside them is folded into their finding
        var similar = new List<(SimilarGroup g, Finding f)>();
        if (deep)
        {
            foreach (var sg in SimilarObjects.Find(rep, null, progress))
            {
                if (sg.Class == Similarity.Instanced || sg.Members.Count < 2) continue;
                double share = sg.TrisTotal / totalTris;
                if (share < 0.001 || similar.Count >= 25) continue;
                // copies of one shape and one variant become instances; otherwise cards / proxies replace them all at once
                var instanceable = sg.Members.Where(m => m.Match is Similarity.Identical or Similarity.Rigid
                    && sg.Members.Any(o => o.Shape == m.Shape && o.Variant == m.Variant && !ReferenceEquals(o, m))).ToList();
                bool inst = instanceable.Count > 0;
                if (!inst && sg.Members.All(m => m.Instances > 1)) continue;   // already instanced, one member per look
                bool foliage = !inst && SimilarObjects.FoliageLike(rep, sg);
                long saving = inst ? instanceable.Sum(m => rep.Stat(m.Node).GeoBytes) : 0;
                string what = sg.Class switch { Similarity.Identical => "identical copies", Similarity.Rigid => "rigid copies", _ => "similar objects" };
                var f = new Finding("similar_objects", share >= 0.05 ? "medium" : "low", $"{sg.Members.Count} {what} of '{sg.Name}' ({HumanCount(sg.TrisEach)} tris each)",
                    $"{rep.PathOf(sg.Members[0].Node)} and {sg.Members.Count - 1} more" + (sg.Differences.Length > 0 ? $" ({sg.Differences})" : "") + ". "
                    + (inst || (!foliage && sg.Class != Similarity.Similar) ? "Make instances keeps one geometry per variant and places the copies with transforms; billboard cards or a proxy are baked once per variant."
                            : foliage ? "Billboard cards or a proxy can replace all of them, baked once per look."
                            : "Simplify reduces each copy; cards are not suggested because the objects are not foliage-like (taller than 40 m or no alpha / plant name)."),
                    sg.Nodes, inst ? "make_instances" : foliage ? "replace_cards" : sg.Class == Similarity.Similar ? "simplify" : "make_instances", savingBytes: saving);
                similar.Add((sg, f));
                F.Add(f);
            }
        }
        foreach (var gs in groups.Values)
        {
            if (gs.Count < 2) continue;
            long saving = gs.Skip(1).Sum(g => g.Bytes);
            var inside = similar.FirstOrDefault(s => gs.All(g => s.g.Nodes.Any(m => ReferenceEquals(m, g.Node) || IsBelow(rep, g.Node, m))));
            if (inside.f != null)
            {
                if (!inside.f.Message.Contains("same vertex data")) inside.f.Message += $" The same vertex data is stored {gs.Count} times.";
                inside.f.SavingBytes = Math.Max(inside.f.SavingBytes, saving);
                continue;
            }
            F.Add(new Finding("duplicate_geometry", Sev(saving / totalBytes), $"{gs.Count} identical copies of a geometry ({HumanCount(gs[0].Tris)} tris each)",
                $"The same vertex data is stored {gs.Count} times. Sharing one copy saves {HumanBytes(saving)}.",
                gs.Select(g => g.Node), "share_duplicates", savingBytes: saving));
        }

        // --- per geometry data checks
        if (deep)
        {
            int small = editable.Count(g => g.Node.Geom!.Optimizable && g.Node.Geom.Prims.Count > 0 && g.Verts > 0 && g.Verts <= 4096);
            if (small > 0) progress($"checking {small:N0} small geometries ...");
        }
        foreach (var g in editable)
        {
            var n = g.Node;
            var geom = n.Geom!;
            if (!geom.Optimizable) continue;
            if (geom.Prims.Count == 0)
            {
                F.Add(new Finding("empty_geometry", "low", "Geometry without primitives", $"{rep.PathOf(n)} has {g.Verts} vertices but draws nothing.",
                    new[] { n }, "remove_empty", savingBytes: g.Bytes, scope: rep.Parents.GetValueOrDefault(n) ?? new List<Node>()));
                continue;
            }
            if (deep && g.Verts > 0 && g.Verts <= rep.S("max_check_verts"))
            {
                bool smallOne = g.Verts <= 4096;
                var ms = MeshOps.MeshStats(geom);
                if (ms != null)
                {
                    g.DupVertices = ms.DupVertices; g.Degenerate = ms.Degenerate; g.Unused = ms.Unused;
                }
                if (!(smallOne && ms != null) && g.Tris >= rep.S("acmr_min_tris") && g.Tris <= 400_000 && g.Kinds.Contains("elements"))
                    g.Acmr = MeshOps.Acmr(geom.Triangles());
                if (geom.Colors != null && geom.Colors.Binding == "vertex" && geom.Colors.Count > 1)
                {
                    var c = geom.Colors;
                    bool same = true;
                    for (int r = 1; r < c.Count && same; r++)
                        for (int j = 0; j < c.Comps; j++) if (c.Data[r * c.Comps + j] != c.Data[j]) { same = false; break; }
                    g.ConstColor = same;
                }
            }
            double shareDup = (double)g.DupVertices / Math.Max(g.Verts, 1);
            if (g.DupVertices != 0 && shareDup >= rep.S("weld_min_saving"))
            {
                double perVertex = (double)g.VertexBytes / Math.Max(g.Verts, 1);
                long saving = (long)(g.DupVertices * perVertex);
                bool unindexed = g.Kinds.Contains("arrays") || g.Kinds.Contains("lengths");
                F.Add(new Finding("duplicate_vertices", Sev(saving / totalBytes), $"{PyFormat.Percent(shareDup, 0)} duplicate vertices" + (unindexed ? " (not indexed)" : ""),
                    $"{rep.PathOf(n)}: {HumanCount(g.DupVertices)} of {HumanCount(g.Verts)} vertices are exact copies. "
                    + "Merging them (and drawing with an index list) saves memory and vertex work.", new[] { n }, "weld", savingBytes: saving));
            }
            if (g.Degenerate != 0)
                F.Add(new Finding("degenerate_triangles", g.Degenerate < 1000 ? "low" : "medium", $"{HumanCount(g.Degenerate)} degenerate triangles",
                    $"{rep.PathOf(n)}: triangles with zero area or a repeated corner are drawn for nothing.", new[] { n }, "clean",
                    savingTris: g.Degenerate * g.Instances));
            if (g.Unused != 0 && g.Unused >= Math.Max(16, g.Verts * 0.05))
                F.Add(new Finding("unused_vertices", "low", $"{HumanCount(g.Unused)} unused vertices", $"{rep.PathOf(n)}: vertices that no primitive references.",
                    new[] { n }, "clean", savingBytes: (long)(g.Unused * (double)g.VertexBytes / Math.Max(g.Verts, 1))));
            if (g.IndexTypes.Contains("DrawElementsUInt") && g.Verts <= 65536)
            {
                long idxCount = geom.Prims.Where(p => p.Kind == "elements").Sum(p => (long)(p.Indices?.Length ?? 0));
                F.Add(new Finding("index_type", "low", "32-bit indices for a small mesh", $"{rep.PathOf(n)}: {g.Verts} vertices fit in 16-bit indices.",
                    new[] { n }, "triangulate", savingBytes: idxCount * 2));
            }
            if (g.Draws >= rep.S("many_primsets"))
                F.Add(new Finding("many_primitive_sets", Sev(g.Draws / totalDraws, 0.2, 0.05), $"{g.Draws} draw calls in one geometry",
                    $"{rep.PathOf(n)} draws {geom.Prims.Count} primitive set(s) / strips one by one. One indexed triangle list draws it in a single call.",
                    new[] { n }, "triangulate", savingDraws: (g.Draws - 1) * g.Instances));
            else if (g.Modes.Any(m => m is "QUADS" or "QUAD_STRIP" or "POLYGON"))
                F.Add(new Finding("legacy_primitives", "info", "Quads / polygons",
                    $"{rep.PathOf(n)} uses {string.Join(", ", g.Modes.Where(m => m is "QUADS" or "QUAD_STRIP" or "POLYGON"))}, which modern (core profile) OpenGL cannot draw directly.",
                    new[] { n }, "triangulate"));
            if (g.Acmr != null && g.Acmr > rep.S("acmr_bad"))
                F.Add(new Finding("vertex_cache", "low", $"Poor vertex cache order (ACMR {PyFormat.Fixed(g.Acmr.Value, 2)})",
                    $"{rep.PathOf(n)}: reordering the triangles typically brings this to ~0.7 and makes drawing faster.", new[] { n }, "vertex_cache"));
            if (g.ConstColor)
                F.Add(new Finding("constant_colors", "low", "Per-vertex colour array with a single colour", $"{rep.PathOf(n)}: one overall colour is enough.",
                    new[] { n }, "compact_arrays", savingBytes: geom.Colors!.NBytes));
            if (g.MaxCoord >= rep.S("large_coord"))
                F.Add(new Finding("large_coordinates", "medium", $"Vertex coordinates up to {PyFormat.Fixed(g.MaxCoord, 0, true)}",
                    $"{rep.PathOf(n)} stores far-from-origin coordinates in its vertices. 32-bit floats only keep "
                    + $"~{PyFormat.G(g.MaxCoord * 6e-8, 3)} units precision there (jitter, z-fighting). Moving the offset into a MatrixTransform fixes it.",
                    new[] { n }, "recenter"));
            if (g.DoubleArrays.Count > 0)
            {
                long saving = geom.Arrays().Where(x => g.DoubleArrays.Contains(x.name)).Sum(x => x.a.NBytes / 2);
                F.Add(new Finding("double_arrays", "low", "Double precision vertex arrays",
                    $"{rep.PathOf(n)}: {string.Join(", ", g.DoubleArrays)} use 64-bit floats. GPUs draw 32-bit floats; re-center first if the coordinates are large.",
                    new[] { n }, "float_arrays", savingBytes: saving));
            }
        }

        // --- many small drawables with the same state under one parent
        foreach (var n in rep.AllNodes())
        {
            var geos = n.Children.Where(c => c.Kind == Kind.Geometry && c.Geom != null && c.Geom.Optimizable).ToList();
            geos.AddRange(n.Children.Where(c => c.Kind == Kind.Geode && c.Children.Count == 1 && c.Matrix == null
                && c.Children[0].Kind == Kind.Geometry && c.Children[0].Geom != null && c.Children[0].Geom!.Optimizable
                && (rep.Parents.GetValueOrDefault(c)?.Count ?? 0) == 1).Select(c => c.Children[0]));
            if (geos.Count < rep.S("many_drawables")) continue;
            var byState = new Dictionary<(object?, object?), List<Node>>();
            foreach (var c in geos)
            {
                if (!rep.GeoInfoOf.TryGetValue(c, out var gi) || gi.Tris >= rep.S("small_drawable_tris")) continue;
                var holder = rep.Parents.TryGetValue(c, out var ps) && ps.Count > 0 ? ps[0] : null;
                var hss = holder != null && !ReferenceEquals(holder, n) ? holder.StateSet : null;
                var key = ((object?)c.StateSet, (object?)hss);
                if (!byState.TryGetValue(key, out var l)) byState[key] = l = new List<Node>();
                l.Add(c);
            }
            foreach (var cs in byState.Values)
            {
                if (cs.Count >= rep.S("many_drawables"))
                    F.Add(new Finding("many_small_drawables", Sev(cs.Count / totalDraws, 0.2, 0.05), $"{cs.Count} small drawables with the same state",
                        $"{rep.PathOf(n)}: {cs.Count} geometries under 1k triangles each are drawn one by one. Merging them reduces draw calls.",
                        cs, "merge_geometries", savingDraws: cs.Count - 1, scope: new[] { n }));
            }
        }

        // --- textures
        var byPath = new Dictionary<string, List<Texture>>();
        foreach (var t in rep.Textures)
        {
            var users = rep.TexUsers.GetValueOrDefault(t) ?? new List<Node>();
            if (!t.Exists && !t.Embedded && t.File.Length > 0)
            {
                F.Add(new Finding("missing_texture", "high", $"Missing texture {t.File}",
                    $"Used by {users.Count} node(s); the image could not be found next to the scene.", users, textures: new[] { t }));
                continue;
            }
            if (t.Path != null) { if (!byPath.TryGetValue(t.Path, out var bl)) byPath[t.Path] = bl = new List<Texture>(); bl.Add(t); }
            if (Math.Max(t.Width, t.Height) > rep.S("large_texture"))
            {
                long saving = t.GpuBytes - t.GpuBytes / 4;
                F.Add(new Finding("large_texture", Sev(saving / totalBytes, 0.1, 0.02), $"Large texture {t.Width}x{t.Height} ({HumanBytes(t.GpuBytes)} on the GPU)",
                    $"{t.Label}: halving the resolution saves {HumanBytes(saving)}.", users, "downscale_textures", savingBytes: saving, textures: new[] { t }));
            }
            if (t.Width != 0 && t.Height != 0 && ((t.Width & (t.Width - 1)) != 0 || (t.Height & (t.Height - 1)) != 0))
                F.Add(new Finding("npot_texture", "info", $"Non power-of-two texture {t.Width}x{t.Height}",
                    $"{t.Label}: OpenSceneGraph rescales such images when loading (slower load, blurrier).", users, textures: new[] { t }));
            if (t.Embedded && t.FileBytes > 256 * 1024)
                F.Add(new Finding("embedded_texture", "info", $"Embedded image data ({HumanBytes(t.FileBytes)})",
                    $"{t.Label} is stored inside the scene file, which makes the text file large and slow to parse.", users, textures: new[] { t }));
        }
        foreach (var ts in byPath.Values)
        {
            if (ts.Count <= 1) continue;
            long saving = ts.Skip(1).Sum(t => t.GpuBytes);
            var users = ts.SelectMany(t => rep.TexUsers.GetValueOrDefault(t) ?? new List<Node>()).ToList();
            F.Add(new Finding("duplicate_texture", Sev(saving / totalBytes), $"Image loaded {ts.Count} times",
                $"{ts[0].Label} is referenced by {ts.Count} separate texture objects, each uploading its own copy.",
                users, "share_duplicates", savingBytes: saving, textures: ts));
        }

        // --- companion images (infrared ...) on only some parts of an object: baked together, the others get the fill
        CompanionFindings(rep, F);

        // --- texture units without texture coordinates (the unit samples one texel: a flat colour)
        foreach (var (t, geos) in rep.TexIndex.TexturesWithoutTexCoords())
        {
            string what = geos.Count == 1 ? "1 geometry" : $"{geos.Count} geometries";
            F.Add(new Finding("missing_texcoords", "medium", $"Texture unit {t.Unit} without texture coordinates",
                $"{t.Label} on unit {t.Unit} is drawn by {what} without texcoord set {t.Unit} (e.g. {rep.PathOf(geos[0])}), "
                + "so it shows a single texel of the image. Add texture coordinates for the unit or remove the texture unit.",
                geos, textures: new[] { t }));
        }

        // --- duplicate state sets
        var ssGroups = new Dictionary<string, HashSet<StateSet>>();
        foreach (var n in rep.AllNodes())
        {
            if (n.StateSet?.Src == null) continue;
            var sig = StateSetSignature(n.StateSet);
            if (!ssGroups.TryGetValue(sig, out var set)) ssGroups[sig] = set = new HashSet<StateSet>(ReferenceEqualityComparer.Instance);
            set.Add(n.StateSet);
        }
        int dupSs = ssGroups.Values.Where(v => v.Count > 1).Sum(v => v.Count - 1);
        if (dupSs >= 4)
            F.Add(new Finding("duplicate_statesets", "low", $"{dupSs} duplicate state sets",
                "Identical materials/texture settings exist as separate objects; sharing them lets OSG sort by state and switch state less often.",
                null, "share_duplicates"));

        // --- structure
        foreach (var n in rep.AllNodes())
        {
            if (!rep.Stats.TryGetValue(n, out var st)) continue;
            if (n.NodeMask == 0 && st.Tris != 0)
                F.Add(new Finding("hidden_subtree", Sev(st.Tris / totalTris), $"Hidden subtree ({HumanCount(st.Tris)} tris)",
                    $"{rep.PathOf(n)} has node mask 0: it is loaded but never drawn.", new[] { n }, "remove_hidden",
                    savingBytes: st.Bytes, savingTris: st.Tris, scope: rep.Parents.GetValueOrDefault(n) ?? new List<Node>()));
            if (n.Kind == Kind.Lod && n.Children.Count >= 2)
            {
                var ts = n.Children.Where(c => rep.Stats.ContainsKey(c)).Select(c => rep.Stats[c].Tris).ToList();
                if (ts.Count > 0 && ts.Max() > 1000 && ts.Min() >= 0.7 * ts.Max())
                    F.Add(new Finding("ineffective_lod", "medium", "LOD levels with almost the same detail",
                        $"{rep.PathOf(n)}: the coarsest level still has {PyFormat.Percent((double)ts.Min() / ts.Max(), 0)} of the triangles of the finest one.",
                        new[] { n }, null, savingTris: ts.Max() - Py.FloorDiv(ts.Min(), 2)));
            }
            if (n.Kind == Kind.Group && n.Children.Count == 0 && n.ExternalNodes.Count == 0 && !ReferenceEquals(n, rep.Scene.Root))
                F.Add(new Finding("empty_node", "info", "Empty group", $"{rep.PathOf(n)} has no children.", new[] { n }, "remove_empty",
                    scope: rep.Parents.GetValueOrDefault(n) ?? new List<Node>()));
            if (n.Kind == Kind.Geode && n.Children.Count == 0)
                F.Add(new Finding("empty_node", "info", "Empty geode", $"{rep.PathOf(n)} has no drawables.", new[] { n }, "remove_empty",
                    scope: rep.Parents.GetValueOrDefault(n) ?? new List<Node>()));
            if (n.Kind == Kind.Transform && n.Matrix != null && Mat.IsIdentity(n.Matrix) && n.Cls.EndsWith("Transform", StringComparison.Ordinal))
                F.Add(new Finding("identity_transform", "info", "Identity transform", $"{rep.PathOf(n)} does not move anything.", new[] { n }, "remove_redundant"));
            foreach (var en in n.ExternalNodes)
                if (en.Notes.Contains("file not found"))
                    F.Add(new Finding("missing_external", "high", $"Missing external file {en.Name}",
                        $"{rep.PathOf(n)} references {en.External}, which does not exist.", new[] { n }));
        }
        Collapse(F, new[] { "empty_node", "identity_transform", "npot_texture", "unused_vertices", "index_type", "duplicate_vertices",
            "constant_colors", "legacy_primitives", "vertex_cache", "double_arrays", "degenerate_triangles", "missing_texcoords" });
        var single = rep.AllNodes().Where(n => n.Kind == Kind.Group && n.Children.Count == 1 && n.StateSet == null && n.Matrix == null
            && n.Cls is "osg::Group" or "Group" && n.ExternalNodes.Count == 0 && !ReferenceEquals(n, rep.Scene.Root)
            && (rep.Parents.GetValueOrDefault(n)?.Count ?? 0) == 1).ToList();
        if (single.Count >= 4)
            F.Add(new Finding("redundant_groups", "info", $"{single.Count} groups with a single child",
                "They add traversal work without grouping anything.", single, "remove_redundant"));
        if (rep.MaxDepth > rep.S("deep"))
            F.Add(new Finding("deep_hierarchy", "info", $"Deep hierarchy ({rep.MaxDepth} levels)",
                "Every level costs a little traversal and culling time.", null, "remove_redundant"));
    }

    /// <summary>True when <paramref name="anc"/> is above <paramref name="n"/> along the first parent.</summary>
    static bool IsBelow(Report rep, Node n, Node anc)
    {
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance) { n };
        for (var cur = n; rep.Parents.TryGetValue(cur, out var ps) && ps.Count > 0 && seen.Add(ps[0]);)
        {
            cur = ps[0];
            if (ReferenceEquals(cur, anc)) return true;
        }
        return false;
    }

    static void Collapse(List<Finding> F, string[] codes)
    {
        foreach (var code in codes)
        {
            var items = F.Where(f => f.Code == code).ToList();
            if (items.Count <= 3) continue;
            F.RemoveAll(f => f.Code == code);
            var worst = items.Select((f, i) => (f, i)).OrderBy(x => Report.SeverityOrder.GetValueOrDefault(x.f.Severity, 9)).ThenBy(x => x.i).First().f;
            var nodes = items.SelectMany(f => f.Nodes).ToList();
            var tex = items.SelectMany(f => f.Textures).ToList();
            List<Node>? scope = items.Any(f => f.Scope != null) ? items.SelectMany(f => f.FixTargets).ToList() : null;
            F.Add(new Finding(code, worst.Severity, $"{items.Count}x {items[0].Title.Split(':')[0]}", $"{items.Count} places, e.g. {items[0].Message}",
                nodes, items[0].Fix, items.Sum(f => f.SavingBytes), items.Sum(f => f.SavingTris), items.Sum(f => f.SavingDraws), tex, scope));
        }
    }

    /// <summary>Everything that makes two state sets equivalent (object ids excluded).</summary>
    /// <summary>Objects whose parts draw textures with and without a companion image of the same pattern (on the same
    /// unit): baking them into one atlas (cards, billboard, proxy, rebake, LOD far level) gives the parts without one the
    /// extra_fill colour in the companion atlas. Reported at the smallest node that mixes them.</summary>
    static void CompanionFindings(Report rep, List<Finding> F)
    {
        var keys = rep.Textures.Where(t => t.Companions != null).SelectMany(t => t.Companions!.Keys.Select(p => (t.Unit, p))).Distinct().ToList();
        if (keys.Count == 0 || rep.Scene.Root == null) return;
        // per node: (unit, pattern) -> textures below with / without the companion
        var memo = new Dictionary<Node, Dictionary<(int, string), (HashSet<Texture> with, HashSet<Texture> without)>>(ReferenceEqualityComparer.Instance);
        var minimal = new Dictionary<(int, string), List<Node>>();
        var visiting = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        Dictionary<(int, string), (HashSet<Texture>, HashSet<Texture>)> Visit(Node n, int depth)
        {
            if (memo.TryGetValue(n, out var known)) return known;
            var agg = new Dictionary<(int, string), (HashSet<Texture> with, HashSet<Texture> without)>();
            if (depth > 400 || !visiting.Add(n)) return agg;
            (HashSet<Texture> with, HashSet<Texture> without) Slot((int, string) k)
            {
                if (!agg.TryGetValue(k, out var v)) agg[k] = v = (new HashSet<Texture>(ReferenceEqualityComparer.Instance), new HashSet<Texture>(ReferenceEqualityComparer.Instance));
                return v;
            }
            var mixedBelow = new HashSet<(int, string)>();
            foreach (var c in n.AllChildren())
                foreach (var (k, (w, wo)) in Visit(c, depth + 1))
                {
                    var v = Slot(k);
                    v.with.UnionWith(w); v.without.UnionWith(wo);
                    if (w.Count > 0 && wo.Count > 0) mixedBelow.Add(k);
                }
            if (n.Kind == Kind.Geometry && n.Geom != null)
                foreach (var b in rep.TexIndex.Effective(n))
                    foreach (var k in keys)
                    {
                        if (k.Unit != b.Unit || TextureTracking.IsMissing(b.Tex)) continue;
                        var v = Slot(k);
                        if (b.Tex.Companions?.ContainsKey(k.p) == true) v.with.Add(b.Tex); else v.without.Add(b.Tex);
                    }
            foreach (var (k, (w, wo)) in agg)
                if (w.Count > 0 && wo.Count > 0 && !mixedBelow.Contains(k))
                {
                    if (!minimal.TryGetValue(k, out var l)) minimal[k] = l = new List<Node>();
                    l.Add(n);
                }
            visiting.Remove(n);
            memo[n] = agg;
            return agg;
        }
        Visit(rep.Scene.Root, 0);
        foreach (var ((unit, pat), nodes) in minimal)
        {
            string label = Companions.Label(pat);
            var first = memo[nodes[0]][(unit, pat)];
            string Names(IEnumerable<Texture> ts) { var l = ts.Select(t => t.Label).Distinct().ToList(); return string.Join(", ", l.Take(3)) + (l.Count > 3 ? $" (+{l.Count - 3})" : ""); }
            var without = nodes.SelectMany(n => memo[n][(unit, pat)].without).Distinct(ReferenceEqualityComparer.Instance).Cast<Texture>().ToList();
            bool cls = Companions.IsClassMap(pat);
            string cnt = $"({nodes.Count} object{(nodes.Count == 1 ? "" : "s")})";
            F.Add(new Finding("companion_partial", "low", cls ? $"class maps on only some parts {cnt}" : $"{label} companions on only some parts {cnt}",
                $"{rep.PathOf(nodes[0])}: {Names(first.with)} {(first.with.Count == 1 ? "has" : "have")} {(cls ? "a class map" : $"an {label} companion")} ({pat}) on unit {unit}, " +
                $"{Names(first.without)} {(first.without.Count == 1 ? "does" : "do")} not" + (nodes.Count > 1 ? $" (and {nodes.Count - 1} more object(s) like it)" : "") +
                (cls ? ". Baked into one atlas (cards, billboard, proxy, rebake, LOD far level) the parts without get class_fill (class ID 0 by default) in the class-map atlas: add the missing class maps or set class_fill."
                     : $". Baked into one atlas (cards, billboard, proxy, rebake, LOD far level) the parts without get the extra_fill colour in the {label} atlas (white by default; in infrared white often means hot): " +
                       "add the missing companion images or choose the fill (black, luminance or a value 0-255)."),
                nodes, textures: without));
        }
    }

    public static string StateSetSignature(StateSet ss)
    {
        var sb = new StringBuilder();
        foreach (var t in ss.Textures) sb.Append(t.Unit).Append('\u0001').Append(t.Path ?? t.File).Append('\u0002');
        sb.Append('\u0003').Append(ss.MaterialName).Append('\u0003');
        if (ss.Diffuse != null) foreach (var d in ss.Diffuse) sb.Append(PyFormat.Repr(d)).Append(',');
        sb.Append('\u0003');
        foreach (var kv in ss.Modes.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        sb.Append('\u0003').Append(ss.Transparent).Append('\u0003');
        if (ss.Src is Obj o)
        {
            try
            {
                var sw = new StringWriter();
                new TextOut("osgt", sw).WriteStmts(o.Stmts, 0);
                foreach (var line in sw.ToString().Split('\n'))
                    if (!line.Contains("UniqueID")) sb.Append(line).Append('\n');
            }
            catch (Exception) { sb.Append("<none>"); }
        }
        return sb.ToString();
    }
}
