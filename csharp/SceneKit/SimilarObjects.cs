using System.Text;
using System.Text.RegularExpressions;

namespace SceneKit;

/// <summary>How closely copies of an object match (strongest first).</summary>
public enum Similarity
{
    /// <summary>The same node (already shared), or nodes around the same shared content.</summary>
    Instanced,
    /// <summary>The same vertex data in the object's own frame: only state and placement differ.</summary>
    Identical,
    /// <summary>The same shape up to a rotation and translation (vertices pre-transformed).</summary>
    Rigid,
    /// <summary>Only the search criteria match.</summary>
    Similar,
}

/// <summary>What makes objects "similar" (each criterion can be switched off) and the tolerances of the checks.</summary>
public sealed class SimilarCriteria
{
    /// <summary>Normalised names match (case, file extension, trailing numbers like _12, .001, #3, (2) ignored).</summary>
    public bool Name;
    /// <summary>Size matches within <see cref="SizeTolerance"/> (largest and RMS distance from the centre, in the object's own frame: rotation does not matter).</summary>
    public bool Size = true;
    /// <summary>Drawables, triangles, vertices and primitive modes match, and the shape (sorted distances from the centre) within <see cref="ShapeTolerance"/>.</summary>
    public bool Topology = true;
    /// <summary>Textures / material match too (otherwise they only split a group into variants).</summary>
    public bool Texture;
    /// <summary>Relative size tolerance.</summary>
    public double SizeTolerance = 0.02;
    /// <summary>Largest difference of the normalised distance profile.</summary>
    public double ShapeTolerance = 0.03;
    /// <summary>Rigid copies: largest vertex deviation after the best rigid fit, relative to the object's radius.</summary>
    public double RigidTolerance = 0.002;
    /// <summary>Objects with more vertices are not compared vertex by vertex (no size / shape / rigid check, only identical data).</summary>
    public int MaxCheckVerts = 200_000;
}

/// <summary>One object of a <see cref="SimilarGroup"/>.</summary>
public sealed class SimilarMember
{
    public Node Node = null!;
    /// <summary>'root/child/...' along the first parent.</summary>
    public string Path = "";
    /// <summary>World matrix of the object's own frame (its parents' matrices times its own), along the first parent.</summary>
    public double[] World = Mat.Identity();
    /// <summary>World centre of the object's bounding box.</summary>
    public double[] Position = new double[3];
    /// <summary>How often the node is drawn (shared nodes count once as a member).</summary>
    public long Instances = 1;
    /// <summary>Triangles of one instance.</summary>
    public long Tris;
    /// <summary>Textures / material of every drawable (equal keys look the same) and a short label (texture file names).</summary>
    public string VariantKey = "", VariantLabel = "";
    /// <summary>Index into <see cref="SimilarGroup.Variants"/>.</summary>
    public int Variant;
    /// <summary>Shape cluster: members with the same index are instanced / identical / rigid copies of the cluster's first member.</summary>
    public int Shape;
    /// <summary>Relation to the first member of its shape cluster. That member itself: <see cref="Similarity.Instanced"/>
    /// in the first cluster, <see cref="Similarity.Similar"/> in the others (it only matches the group by the criteria).</summary>
    public Similarity Match;
    /// <summary>World transform that maps the first member of the shape cluster onto this one (identity for that member).</summary>
    public double[] Fit = Mat.Identity();
}

/// <summary>Members of a group that use the same textures / material.</summary>
public sealed class SimilarVariant
{
    public string Key = "", Label = "";
    public List<SimilarMember> Members = new();
    public int Count => Members.Count;
}

/// <summary>Objects that match the criteria, reported at the highest level where they still match.</summary>
public sealed class SimilarGroup
{
    public string Name = "";
    public List<SimilarMember> Members = new();
    /// <summary>Texture / material variants, most used first.</summary>
    public List<SimilarVariant> Variants = new();
    /// <summary>Weakest match in the group (<see cref="Similarity.Similar"/> when there is more than one shape).</summary>
    public Similarity Class;
    /// <summary>Number of shape clusters.</summary>
    public int Shapes;
    /// <summary>What differs, e.g. "2 variants: 12x tree.rgb, 4x tree_autumn.rgb; vertices pre-transformed (rigid copies)".</summary>
    public string Differences = "";
    /// <summary>Triangles of the first member / of all members times their instances.</summary>
    public long TrisEach, TrisTotal;
    public IEnumerable<Node> Nodes => Members.Select(m => m.Node);
    /// <summary>Drawn copies (instances of all members).</summary>
    public long Copies => Members.Sum(m => m.Instances);
    /// <summary>"instanced", "identical", "rigid" or "similar".</summary>
    public string ClassLabel => Class.ToString().ToLowerInvariant();
}

/// <summary>Finds copies of the same object under different nodes (S1): candidates are bucketed by cheap keys (node
/// statistics, names), compared by size and shape, then clustered into identical / rigid copies (vertex correspondence
/// by index, Kabsch fit on world positions).</summary>
public static class SimilarObjects
{
    static readonly Regex Ext = new(@"\.[a-z][a-z0-9]{0,4}$", RegexOptions.Compiled);
    static readonly Regex Trailing = new(@"(\s*\(\d+\)|[\s._#-]*\d+)$", RegexOptions.Compiled);

    /// <summary>Lower case, without file extension and trailing numbers / separators ("Tree_12.flt", "tree.001",
    /// "tree #3", "tree (2)" -> "tree").</summary>
    public static string NormalizeName(string name)
    {
        var s = name.Trim().ToLowerInvariant();
        for (string prev = ""; prev != s;)
        {
            prev = s;
            s = Ext.Replace(s, "");
            s = Trailing.Replace(s, "");
        }
        return s.TrimEnd(' ', '.', '_', '#', '-');
    }

    /// <summary>Taller than this (m) a group is no foliage: billboard cards would be visibly wrong.</summary>
    public const double FoliageMaxHeight = 40;

    /// <summary>A plant word at the start of the name or after a separator or capitalised inside a CamelCase name ("oak_tree", "Tree_01", "OakTree"; not "street_lamp", "ambush").</summary>
    public static readonly Regex FoliageName = new(@"(?:(?<![A-Za-z0-9])(?i:tree|bush|plant|veg|shrub|foliage|palm|hedge|grass)|(?<=[a-z])(?:Tree|Bush|Plant|Veg|Shrub|Foliage|Palm|Hedge|Grass))", RegexOptions.Compiled);

    /// <summary>Cards suit the group: its tallest member is below <see cref="FoliageMaxHeight"/> and the name says plant
    /// (group or any member) or a texture below a member is see-through (texels with alpha, see
    /// <see cref="Optimizer.SeeThroughTexture"/>) / its state set is transparent. An opaque RGBA texture does not count.</summary>
    public static bool FoliageLike(Report rep, SimilarGroup g)
    {
        double tallest = double.NaN;
        foreach (var m in g.Members)
            if (rep.Stat(m.Node).BBox is { } bb) tallest = double.IsNaN(tallest) ? bb.hi[2] - bb.lo[2] : Math.Max(tallest, bb.hi[2] - bb.lo[2]);
        if (double.IsNaN(tallest) || tallest >= FoliageMaxHeight) return false;
        if (FoliageName.IsMatch(g.Name) || g.Members.Any(m => FoliageName.IsMatch(m.Node.Name))) return true;
        var seeThrough = new Dictionary<Texture, bool>(ReferenceEqualityComparer.Instance);   // each texture is scanned once per group
        foreach (var m in g.Members)
        {
            var stack = new Stack<Node>();
            stack.Push(m.Node);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.Kind == Kind.Geometry && n.Geom != null)
                    foreach (var b in rep.TexIndex.Effective(n))
                        if (b.From.StateSet?.Transparent == true || SeeThroughOnce(seeThrough, b.Tex)) return true;
                foreach (var c in n.AllChildren()) stack.Push(c);
            }
        }
        return false;
    }

    static bool SeeThroughOnce(Dictionary<Texture, bool> memo, Texture t)
    {
        if (!memo.TryGetValue(t, out bool r)) memo[t] = r = Optimizer.SeeThroughTexture(t);
        return r;
    }

    /// <summary>Similar objects of a scene (analysed without findings first).</summary>
    public static List<SimilarGroup> Find(Scene scene, SimilarCriteria? criteria = null, Action<string>? progress = null) =>
        Find(Analyzer.Analyze(scene, deepChecks: false, findings: false), criteria, progress);

    /// <summary>Similar objects of an analysed scene, the most triangles first. Shared nodes are one member with N
    /// instances; groups whose members all lie inside members of another group are dropped.</summary>
    public static List<SimilarGroup> Find(Report rep, SimilarCriteria? criteria = null, Action<string>? progress = null)
    {
        var c = criteria ?? new SimilarCriteria();
        var root = rep.Scene.Root;
        if (root == null || !(c.Name || c.Size || c.Topology || c.Texture)) return new List<SimilarGroup>();
        var parents = rep.Parents;
        var cand = new List<Node>();
        var isCand = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var n in rep.Scene.IterNodes(externals: true))   // pre-order: members in scene order
            if (!ReferenceEquals(n, root) && IsUnitKind(n) && rep.Stat(n).Tris > 0) { cand.Add(n); isCand.Add(n); }
        // a node inside a wrapper (a transform around one geode, a geode with one drawable) is the same object as the
        // wrapper; a wrapper around a shared node only places that node, which is one member with N instances
        bool Wrapper(Node p) => isCand.Contains(p) && p.AllChildren().Count == 1;
        var units = cand.Where(n => !(parents.TryGetValue(n, out var ps) && ps.Count == 1 && Wrapper(ps[0]))
                                    && !(Wrapper(n) && WrapsShared(n, parents, isCand))).ToList();

        var buckets = new Dictionary<string, List<Node>>();
        foreach (var n in units)
        {
            string name = c.Name ? NormalizeName(MeaningfulName(n)) : "";
            if (c.Name && name.Length == 0) continue;
            var key = new StringBuilder();
            if (c.Topology) { var s = rep.Stat(n); key.Append(s.Geoms).Append('/').Append(s.Tris).Append('/').Append(s.Verts); }
            key.Append('|').Append(name);
            if (!buckets.TryGetValue(key.ToString(), out var l)) buckets[key.ToString()] = l = new List<Node>();
            l.Add(n);
        }
        progress?.Invoke("comparing similar objects ...");
        var caches = new Caches();
        var parts = new List<List<Unit>>();
        foreach (var b in buckets.Values)
        {
            if (b.Count < 2 && rep.Instances.GetValueOrDefault(b[0], 1) < 2) continue;
            var us = b.Select(n => Build(n, parents, c.MaxCheckVerts)).ToList();
            foreach (var part in Refine(us, c)) parts.Add(part);
        }
        // members in scene order; members inside other members of the same group: keep the outer one
        var index = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < units.Count; i++) index[units[i]] = i;
        for (int i = 0; i < parts.Count; i++)
        {
            var set = new HashSet<Node>(parts[i].Select(u => u.N), ReferenceEqualityComparer.Instance);
            parts[i] = parts[i].Where(u => !Ancestors(u.N, parents).Any(set.Contains)).OrderBy(u => index[u.N]).ToList();
        }
        parts = parts.Where(p => p.Count >= 2 || p.Count == 1 && rep.Instances.GetValueOrDefault(p[0].N, 1) >= 2).ToList();
        // highest level only: a group whose members all lie inside members of other groups is dropped
        var groupOf = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < parts.Count; i++) foreach (var u in parts[i]) groupOf.TryAdd(u.N, i);
        var keep = parts.Where((p, i) => !p.All(u => Ancestors(u.N, parents).Any(a => groupOf.TryGetValue(a, out int gi) && gi != i))).ToList();
        var o = keep.Select(p => BuildGroup(rep, p, c, caches)).ToList();
        return o.Select((g, i) => (g, i)).OrderBy(x => -x.g.TrisTotal).ThenBy(x => x.i).Select(x => x.g).ToList();
    }

    // ------------------------------------------------------------------------------ copies (for the passes)

    /// <summary>Nodes grouped into sets of copies that look the same: same structure, same textures / material per
    /// drawable and instanced, identical or rigid shape. Every node is in one set; Fit maps the set's first node onto
    /// the copy (world, along the first parent).</summary>
    internal sealed record CopySet(Node Rep, List<(Node Node, double[] Fit, Similarity Match)> Copies);

    internal static List<CopySet> Copies(IReadOnlyDictionary<Node, List<Node>> parents, IEnumerable<Node> nodes, SimilarCriteria? criteria = null)
    {
        var c = criteria ?? new SimilarCriteria();
        var caches = new Caches();
        var o = new List<CopySet>();
        var reps = new Dictionary<string, List<(Unit u, CopySet set)>>();
        foreach (var n in nodes.Distinct(ReferenceEqualityComparer.Instance).Cast<Node>())
        {
            var u = Build(n, parents, c.MaxCheckVerts);
            string key = u.Struct + "\u0001" + u.Counts + "\u0001" + u.VariantKey;
            if (!reps.TryGetValue(key, out var l)) reps[key] = l = new List<(Unit, CopySet)>();
            CopySet? found = null;
            foreach (var (r, set) in l)
            {
                if (Related(r.N, n, parents)) continue;
                var (m, fit) = Compare(r, u, c, caches);
                if (m is null or Similarity.Similar) continue;
                set.Copies.Add((n, fit!, m.Value));
                found = set;
                break;
            }
            if (found != null) continue;
            var ns = new CopySet(n, new List<(Node, double[], Similarity)>());
            l.Add((u, ns));
            o.Add(ns);
        }
        return o;
    }

    /// <summary>True when <paramref name="a"/> put where <paramref name="b"/> is would be drawn with the same textures /
    /// material as <paramref name="b"/> (the state above b applies to a's content).</summary>
    internal static bool SameLook(IReadOnlyDictionary<Node, List<Node>> parents, Node a, Node b)
    {
        var above = StatesAbove(b, parents);
        var ua = Build(a, parents, 0, above);
        var ub = Build(b, parents, 0);
        return ua.VariantKey == ub.VariantKey;
    }

    // ------------------------------------------------------------------------------ units

    sealed class Caches
    {
        public readonly Dictionary<GeomData, string> Content = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<GeomData, string> Topo = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A candidate object: its drawables relative to its own frame, structure, size and look.</summary>
    sealed class Unit
    {
        public readonly Node N;
        public double[] W = Mat.Identity();
        public readonly List<(Node g, double[] rel)> Draw = new();
        public string Struct = "", Counts = "", VariantKey = "", VariantLabel = "";
        public double[]? Local;
        public double[] Center = new double[3];
        public double RMax, RRms;
        public double[]? Profile;
        public Unit(Node n) { N = n; }
    }

    static bool IsUnitKind(Node n) => n.Kind switch
    {
        Kind.Geometry => n.Geom != null,
        Kind.Geode or Kind.Transform => true,
        Kind.Group or Kind.Lod or Kind.Switch => Optimizer.HasMeaningfulName(n) && !n.VirtualRoot,
        _ => false,
    };

    /// <summary>A chain of single children from the node reaches a shared candidate.</summary>
    static bool WrapsShared(Node n, IReadOnlyDictionary<Node, List<Node>> parents, HashSet<Node> isCand)
    {
        for (int depth = 0; n.AllChildren().Count == 1 && depth < 100; depth++)
        {
            n = n.AllChildren()[0];
            if (isCand.Contains(n) && parents.TryGetValue(n, out var ps) && ps.Count > 1) return true;
        }
        return false;
    }

    static string MeaningfulName(Node n) => Optimizer.HasMeaningfulName(n) ? n.Name : "";

    static bool IsBillboard(Node n) => n.Kind == Kind.Geode && n.Cls.EndsWith("Billboard", StringComparison.Ordinal) && n.Center is { Length: >= 3 };

    static double[] Translate(double[] c) { var t = Mat.Identity(); t[3] = c[0]; t[7] = c[1]; t[11] = c[2]; return t; }

    /// <summary>First-parent chain above a node (nearest first).</summary>
    static IEnumerable<Node> Ancestors(Node n, IReadOnlyDictionary<Node, List<Node>> parents)
    {
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance) { n };
        for (var cur = n; parents.TryGetValue(cur, out var ps) && ps.Count > 0 && seen.Add(ps[0]);)
        {
            cur = ps[0];
            yield return cur;
        }
    }

    static bool Related(Node a, Node b, IReadOnlyDictionary<Node, List<Node>> parents) =>
        Ancestors(a, parents).Any(x => ReferenceEquals(x, b)) || Ancestors(b, parents).Any(x => ReferenceEquals(x, a));

    /// <summary>State sets from the root down to the node's parent (along the first parent).</summary>
    static List<StateSet?> StatesAbove(Node n, IReadOnlyDictionary<Node, List<Node>> parents)
    {
        var l = Ancestors(n, parents).Select(a => a.StateSet).ToList();
        l.Reverse();
        return l;
    }

    sealed record Link(Node N, Link? Up);

    /// <summary>Every drawable below the node (all LOD levels and switch children: copies must match completely), with
    /// its matrix relative to the node's own frame; the look per drawable; positions when there are at most
    /// <paramref name="cap"/> vertices.</summary>
    static Unit Build(Node n, IReadOnlyDictionary<Node, List<Node>> parents, int cap, List<StateSet?>? above = null)
    {
        var u = new Unit(n);
        var pw = SoftRender.ParentMatrix(n, parents);
        u.W = n.Matrix != null ? Mat.Mul(pw, n.Matrix) : pw;
        var st = new StringBuilder();
        var paths = new List<Link>();
        // the structure leaves out the wrappers on top (single children of groups / geodes / transforms): a transform
        // around a copy and a pre-transformed copy are the same object
        var stack = new Stack<(Node x, double[] rel, Link? up, int depth, bool wrap)>();
        stack.Push((n, IsBillboard(n) ? Translate(n.Center!) : Mat.Identity(), null, 0, true));
        while (stack.Count > 0)
        {
            var (x, rel, up, depth, wrap) = stack.Pop();
            var here = new Link(x, up);
            var kids = x.AllChildren();
            wrap = wrap && kids.Count == 1 && x.Kind is Kind.Group or Kind.Geode or Kind.Transform;
            if (wrap) st.Append(x.NodeMask == 0 ? "h;" : "");
            else st.Append(x.Kind).Append(':').Append(kids.Count).Append(x.NodeMask == 0 ? ":h" : "").Append(x.Matrix != null ? ":m" : "");
            if (!wrap)
            {
                if (x.Ranges != null) foreach (var (a, b) in x.Ranges) st.Append(':').Append(PyFormat.Repr(a)).Append('-').Append(PyFormat.Repr(b));
                if (x.SwitchValues != null) foreach (var v in x.SwitchValues) st.Append(v ? '1' : '0');
                st.Append(';');
            }
            if (x.Kind == Kind.Geometry)
            {
                if (x.Geom != null) { u.Draw.Add((x, rel)); paths.Add(here); }
                continue;
            }
            if (depth > 200) continue;
            for (int i = kids.Count - 1; i >= 0; i--)
            {
                var k = kids[i];
                var r = k.Matrix != null ? Mat.Mul(rel, k.Matrix) : rel;
                if (IsBillboard(k)) r = Mat.Mul(r, Translate(k.Center!));
                stack.Push((k, r, here, depth + 1, wrap));
            }
        }
        u.Struct = st.ToString();
        u.Counts = string.Join(";", u.Draw.Select(d =>
        {
            var g = d.g.Geom!;
            var (t, l, p) = g.Counts();
            return $"{g.NumVertices}/{t}/{l}/{p}/{string.Join(",", g.Prims.Select(x => x.Mode).Distinct().OrderBy(x => x, StringComparer.Ordinal))}";
        }));
        // look: the state that applies to every drawable
        above ??= StatesAbove(n, parents);
        var keys = new StringBuilder();
        var files = new List<string>();
        var mats = new List<string>();
        foreach (var link in paths)
        {
            var chain = new List<StateSet?>();
            for (var l = link; l != null; l = l.Up) chain.Add(l.N.StateSet);
            chain.Reverse();
            var m = Optimizer.MergedState(above.Concat(chain));
            keys.Append(StateKey(m)).Append('\n');
            if (m == null) continue;
            foreach (var t in m.Textures.OrderBy(t => t.Unit))
            {
                string f = System.IO.Path.GetFileName(t.File.Length > 0 ? t.File : t.Path ?? "");
                if (f.Length > 0 && !files.Contains(f)) files.Add(f);
            }
            if (m.MaterialName.Length > 0 && !mats.Contains(m.MaterialName)) mats.Add(m.MaterialName);
        }
        u.VariantKey = keys.ToString();
        u.VariantLabel = files.Count > 0 ? string.Join(" + ", files) : mats.Count > 0 ? string.Join(" + ", mats) : "untextured";
        if (cap > 0) Positions(u, cap);
        return u;
    }

    static string StateKey(StateSet? m)
    {
        if (m == null) return "-";
        var sb = new StringBuilder();
        foreach (var t in m.Textures.OrderBy(t => t.Unit))
            sb.Append(t.Unit).Append(':').Append(t.Baked != null ? "baked#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t.Baked) : t.Path ?? t.File).Append(';');
        sb.Append('|').Append(m.MaterialName).Append('|');
        if (m.Diffuse != null) foreach (var d in m.Diffuse) sb.Append(PyFormat.Repr(d)).Append(',');
        sb.Append('|');
        foreach (var kv in m.Modes.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        return sb.Append('|').Append(m.Transparent).ToString();
    }

    /// <summary>Vertex positions in the object's own frame (drawable after drawable), centre, radii and the sorted
    /// distance profile (9 quantiles of the distance from the centroid / the largest one).</summary>
    static void Positions(Unit u, int cap)
    {
        long nv = u.Draw.Sum(d => (long)d.g.Geom!.NumVertices);
        if (nv == 0 || nv > cap) return;
        var P = new double[3 * nv];
        int o = 0;
        foreach (var (g, rel) in u.Draw)
        {
            var v = g.Geom!.Vertices;
            if (v == null) continue;
            int vc = v.Comps;
            for (int i = 0; i < v.Count; i++)
            {
                double x = v.Data[i * vc], y = vc > 1 ? v.Data[i * vc + 1] : 0, z = vc > 2 ? v.Data[i * vc + 2] : 0;
                (P[o], P[o + 1], P[o + 2]) = Mat.Apply(rel, x, y, z);
                o += 3;
            }
        }
        if (P.Any(x => !double.IsFinite(x))) return;
        u.Local = P;
        int n = P.Length / 3;
        double cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < n; i++) { cx += P[3 * i]; cy += P[3 * i + 1]; cz += P[3 * i + 2]; }
        cx /= n; cy /= n; cz /= n;
        var dist = new double[n];
        double s2 = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = P[3 * i] - cx, dy = P[3 * i + 1] - cy, dz = P[3 * i + 2] - cz;
            dist[i] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            s2 += dist[i] * dist[i];
        }
        Array.Sort(dist);
        u.RMax = dist[^1];
        u.RRms = Math.Sqrt(s2 / n);
        u.Profile = new double[9];
        for (int k = 0; k < 9; k++) u.Profile[k] = u.RMax > 0 ? dist[(int)Math.Round(k / 8.0 * (n - 1))] / u.RMax : 0;
        // world bounding box centre
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (int i = 0; i < n; i++)
        {
            var (x, y, z) = Mat.Apply(u.W, P[3 * i], P[3 * i + 1], P[3 * i + 2]);
            lo[0] = Math.Min(lo[0], x); lo[1] = Math.Min(lo[1], y); lo[2] = Math.Min(lo[2], z);
            hi[0] = Math.Max(hi[0], x); hi[1] = Math.Max(hi[1], y); hi[2] = Math.Max(hi[2], z);
        }
        for (int k = 0; k < 3; k++) u.Center[k] = (lo[k] + hi[k]) / 2;
    }

    /// <summary>World centre from the drawables' bounding boxes (objects over the vertex cap).</summary>
    static double[] BoxCenter(Unit u)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var (g, rel) in u.Draw)
        {
            if (g.Geom!.BBox() is not { } bb) continue;
            var w = Mat.Mul(u.W, rel);
            for (int c = 0; c < 8; c++)
            {
                double x = (c & 1) == 0 ? bb.lo[0] : bb.hi[0], y = bb.lo.Length > 1 ? ((c & 2) == 0 ? bb.lo[1] : bb.hi[1]) : 0, z = bb.lo.Length > 2 ? ((c & 4) == 0 ? bb.lo[2] : bb.hi[2]) : 0;
                var (px, py, pz) = Mat.Apply(w, x, y, z);
                lo[0] = Math.Min(lo[0], px); lo[1] = Math.Min(lo[1], py); lo[2] = Math.Min(lo[2], pz);
                hi[0] = Math.Max(hi[0], px); hi[1] = Math.Max(hi[1], py); hi[2] = Math.Max(hi[2], pz);
            }
        }
        return double.IsFinite(lo[0]) ? new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 } : new[] { u.W[3], u.W[7], u.W[11] };
    }

    /// <summary>Splits a bucket by the exact keys of the switched-on criteria, then by size (sorted sweep) and shape.</summary>
    static IEnumerable<List<Unit>> Refine(List<Unit> us, SimilarCriteria c)
    {
        foreach (var part in us.GroupBy(u => (c.Topology ? u.Counts : "") + "\u0001" + (c.Texture ? u.VariantKey : "")).Select(g => g.ToList()))
        {
            if (!(c.Size || c.Topology)) { yield return part; continue; }
            var unchecked_ = part.Where(u => u.Local == null).ToList();
            if (unchecked_.Count > 0) yield return unchecked_;
            var sorted = part.Where(u => u.Local != null).OrderBy(u => u.RRms).ToList();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i + 1;
                if (c.Size) while (j < sorted.Count && sorted[j].RRms <= sorted[i].RRms * (1 + c.SizeTolerance) + 1e-12) j++;
                else j = sorted.Count;
                var clusters = new List<List<Unit>>();
                for (int k = i; k < j; k++)
                {
                    var u = sorted[k];
                    var home = clusters.FirstOrDefault(cl => Close(cl[0], u, c));
                    if (home != null) home.Add(u); else clusters.Add(new List<Unit> { u });
                }
                foreach (var cl in clusters) yield return cl;
                i = j;
            }
        }
    }

    static bool Close(Unit a, Unit b, SimilarCriteria c)
    {
        if (c.Size)
        {
            double t = c.SizeTolerance;
            if (Math.Abs(a.RMax - b.RMax) > t * Math.Max(a.RMax, b.RMax) + 1e-12) return false;
            if (Math.Abs(a.RRms - b.RRms) > t * Math.Max(a.RRms, b.RRms) + 1e-12) return false;
        }
        if (c.Topology && a.Profile != null && b.Profile != null)
            for (int k = 0; k < a.Profile.Length; k++) if (Math.Abs(a.Profile[k] - b.Profile[k]) > c.ShapeTolerance) return false;
        return true;
    }

    // ------------------------------------------------------------------------------ comparing two objects

    static bool SameMatrix(double[] a, double[] b)
    {
        for (int i = 0; i < 16; i++) if (Math.Abs(a[i] - b[i]) > 1e-9 * Math.Max(1.0, Math.Abs(a[i]))) return false;
        return true;
    }

    static string ContentKey(GeomData g, Caches cache) => cache.Content.TryGetValue(g, out var k) ? k : cache.Content[g] = g.ContentKey();

    /// <summary>Primitive sets with their index arrays (same index arrays = same triangles over the same vertex numbers).</summary>
    static string TopoKey(GeomData g, Caches cache)
    {
        if (cache.Topo.TryGetValue(g, out var k)) return k;
        ulong h = 1469598103934665603UL;
        void Add(long v) { h = (h ^ (ulong)v) * 1099511628211UL; }
        Add(g.NumVertices);
        foreach (var p in g.Prims)
        {
            Add(p.Mode.GetHashCode(StringComparison.Ordinal)); Add(p.Kind.Length); Add(p.First); Add(p.Count);
            if (p.Lengths != null) { Add(p.Lengths.Length); foreach (var x in p.Lengths) Add(x); }
            if (p.Indices != null) { Add(p.Indices.Length); foreach (var x in p.Indices) Add(x); }
        }
        return cache.Topo[g] = h.ToString("x16");
    }

    /// <summary>How b matches a and the world transform that maps a onto b; (null, null) when it is not a copy.</summary>
    static (Similarity? m, double[]? fit) Compare(Unit a, Unit b, SimilarCriteria c, Caches cache)
    {
        if (a.Struct != b.Struct || a.Draw.Count != b.Draw.Count) return (null, null);
        bool sameRel = true, sameNode = true, sameData = true, sameTopo = true;
        for (int i = 0; i < a.Draw.Count; i++)
        {
            var (ga, ra) = a.Draw[i];
            var (gb, rb) = b.Draw[i];
            if (!SameMatrix(ra, rb)) sameRel = false;
            if (!ReferenceEquals(ga, gb)) sameNode = false;
            if (!sameNode && sameData && !ReferenceEquals(ga.Geom, gb.Geom) && ContentKey(ga.Geom!, cache) != ContentKey(gb.Geom!, cache)) sameData = false;
            if (sameTopo && TopoKey(ga.Geom!, cache) != TopoKey(gb.Geom!, cache)) sameTopo = false;
        }
        if (sameRel && (sameNode || sameData) && Mat.Invert(a.W) is { } inv)
            return (sameNode ? Similarity.Instanced : Similarity.Identical, Mat.Mul(b.W, inv));
        if (!sameTopo || a.Local == null || b.Local == null || a.Local.Length != b.Local.Length) return (null, null);
        var fit = Kabsch(a, b, c.RigidTolerance);
        return fit != null ? (Similarity.Rigid, fit) : (null, null);
    }

    /// <summary>Best rigid transform (rotation + translation) from a's world positions onto b's, vertex i onto vertex i
    /// (Horn's quaternion method), or null when some vertex stays further away than tol x b's radius.</summary>
    static double[]? Kabsch(Unit a, Unit b, double tol)
    {
        int n = a.Local!.Length / 3;
        var A = new double[3 * n];
        var B = new double[3 * n];
        double maxAbs = 0;
        var ca = new double[3]; var cb = new double[3];
        for (int i = 0; i < n; i++)
        {
            (A[3 * i], A[3 * i + 1], A[3 * i + 2]) = Mat.Apply(a.W, a.Local[3 * i], a.Local[3 * i + 1], a.Local[3 * i + 2]);
            (B[3 * i], B[3 * i + 1], B[3 * i + 2]) = Mat.Apply(b.W, b.Local![3 * i], b.Local[3 * i + 1], b.Local[3 * i + 2]);
            for (int k = 0; k < 3; k++) { ca[k] += A[3 * i + k]; cb[k] += B[3 * i + k]; maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(A[3 * i + k]), Math.Abs(B[3 * i + k]))); }
        }
        for (int k = 0; k < 3; k++) { ca[k] /= n; cb[k] /= n; }
        var S = new double[9];
        double rb = 0;
        for (int i = 0; i < n; i++)
        {
            double ax = A[3 * i] - ca[0], ay = A[3 * i + 1] - ca[1], az = A[3 * i + 2] - ca[2];
            double bx = B[3 * i] - cb[0], by = B[3 * i + 1] - cb[1], bz = B[3 * i + 2] - cb[2];
            S[0] += ax * bx; S[1] += ax * by; S[2] += ax * bz;
            S[3] += ay * bx; S[4] += ay * by; S[5] += ay * bz;
            S[6] += az * bx; S[7] += az * by; S[8] += az * bz;
            rb = Math.Max(rb, Math.Sqrt(bx * bx + by * by + bz * bz));
        }
        double sxx = S[0], sxy = S[1], sxz = S[2], syx = S[3], syy = S[4], syz = S[5], szx = S[6], szy = S[7], szz = S[8];
        var N = new double[,]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
        };
        var q = LargestEigenvector(N);
        var R = Mat.QuatToMatrix(q[1], q[2], q[3], q[0]);
        var F = Mat.Identity();
        for (int r = 0; r < 3; r++)
        {
            for (int k = 0; k < 3; k++) F[r * 4 + k] = R[r, k];
            F[r * 4 + 3] = cb[r] - (R[r, 0] * ca[0] + R[r, 1] * ca[1] + R[r, 2] * ca[2]);
        }
        double limit = tol * rb + 1e-6 * maxAbs + 1e-12;
        for (int i = 0; i < n; i++)
        {
            var (x, y, z) = Mat.Apply(F, A[3 * i], A[3 * i + 1], A[3 * i + 2]);
            double dx = x - B[3 * i], dy = y - B[3 * i + 1], dz = z - B[3 * i + 2];
            if (dx * dx + dy * dy + dz * dz > limit * limit) return null;
        }
        return F;
    }

    /// <summary>Eigenvector of the largest eigenvalue of a symmetric 4x4 matrix (cyclic Jacobi).</summary>
    static double[] LargestEigenvector(double[,] a)
    {
        var m = (double[,])a.Clone();
        var v = new double[4, 4];
        for (int i = 0; i < 4; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = 0;
            for (int p = 0; p < 4; p++) for (int q = p + 1; q < 4; q++) off += m[p, q] * m[p, q];
            if (off < 1e-30) break;
            for (int p = 0; p < 4; p++)
                for (int q = p + 1; q < 4; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-300) continue;
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double cs = 1 / Math.Sqrt(t * t + 1), sn = t * cs;
                    for (int k = 0; k < 4; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = cs * mkp - sn * mkq; m[k, q] = sn * mkp + cs * mkq;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = cs * mpk - sn * mqk; m[q, k] = sn * mpk + cs * mqk;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = cs * vkp - sn * vkq; v[k, q] = sn * vkp + cs * vkq;
                    }
                }
        }
        int best = 0;
        for (int i = 1; i < 4; i++) if (m[i, i] > m[best, best]) best = i;
        return new[] { v[0, best], v[1, best], v[2, best], v[3, best] };
    }

    // ------------------------------------------------------------------------------ groups

    static SimilarGroup BuildGroup(Report rep, List<Unit> us, SimilarCriteria c, Caches cache)
    {
        var g = new SimilarGroup();
        var reps = new List<Unit>();
        foreach (var u in us)
        {
            var m = new SimilarMember
            {
                Node = u.N, Path = rep.PathOf(u.N), World = u.W, Position = u.Local != null ? u.Center : BoxCenter(u),
                Instances = rep.Instances.GetValueOrDefault(u.N, 1), Tris = rep.Stat(u.N).Tris, VariantKey = u.VariantKey, VariantLabel = u.VariantLabel,
            };
            double[]? found = null;
            for (int k = 0; k < reps.Count && found == null; k++)
            {
                var (sim, fit) = Compare(reps[k], u, c, cache);
                if (fit == null) continue;
                m.Shape = k; m.Match = sim!.Value; found = fit;
            }
            if (found == null) { m.Shape = reps.Count; m.Match = Similarity.Instanced; found = Mat.Identity(); reps.Add(u); }
            m.Fit = found;
            g.Members.Add(m);
        }
        g.Shapes = reps.Count;
        // the first member of every other shape cluster only matches the group by the criteria
        foreach (var m in g.Members) if (m.Shape > 0 && ReferenceEquals(reps[m.Shape].N, m.Node)) m.Match = Similarity.Similar;
        var byKey = new Dictionary<string, SimilarVariant>();
        foreach (var m in g.Members)
        {
            if (!byKey.TryGetValue(m.VariantKey, out var v)) byKey[m.VariantKey] = v = new SimilarVariant { Key = m.VariantKey, Label = m.VariantLabel };
            v.Members.Add(m);
        }
        g.Variants = byKey.Values.Select((v, i) => (v, i)).OrderBy(x => -x.v.Count).ThenBy(x => x.i).Select(x => x.v).ToList();
        for (int i = 0; i < g.Variants.Count; i++) foreach (var m in g.Variants[i].Members) m.Variant = i;
        g.Class = g.Members.Count == 1 ? Similarity.Instanced : g.Shapes > 1 ? Similarity.Similar : g.Members.Max(m => m.Match);
        g.TrisEach = g.Members[0].Tris;
        g.TrisTotal = g.Members.Sum(m => m.Tris * m.Instances);
        g.Name = g.Members.Select(m => NormalizeName(MeaningfulName(m.Node))).Where(x => x.Length > 0)
            .GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? GroupName(g.Members[0].Node);
        g.Differences = Differences(g, c);
        return g;
    }

    static string GroupName(Node n)
    {
        var own = NormalizeName(MeaningfulName(n));
        if (own.Length > 0) return own;
        var stack = new Stack<Node>(n.AllChildren().Reverse());
        int seen = 0;
        while (stack.Count > 0 && seen++ < 10_000)
        {
            var x = stack.Pop();
            var nm = NormalizeName(MeaningfulName(x));
            if (nm.Length > 0) return nm;
            foreach (var k in x.AllChildren().Reverse()) stack.Push(k);
        }
        return OsgReader.Short(n.Cls);
    }

    static string Differences(SimilarGroup g, SimilarCriteria c)
    {
        var parts = new List<string>();
        if (g.Class == Similarity.Instanced)
            parts.Add(g.Members.Count == 1 ? $"already shared: one node drawn {g.Members[0].Instances}x" : $"already shared: {g.Members.Count} places draw the same content");
        if (g.Variants.Count > 1) parts.Add($"{g.Variants.Count} variants: " + string.Join(", ", g.Variants.Select(v => $"{v.Count}x {v.Label}")));
        if (g.Shapes > 1) parts.Add($"{g.Shapes} shapes (criteria match only)");
        if (g.Members.Any(m => m.Match == Similarity.Rigid)) parts.Add("vertices pre-transformed (rigid copies)");
        int stored = g.Members.GroupBy(m => m.Shape).Where(x => x.Any(m => m.Match == Similarity.Identical)).Sum(x => x.Count(m => m.Match is Similarity.Identical or Similarity.Instanced));
        if (stored > 1) parts.Add($"same vertex data stored {stored} times");
        if (!c.Name)
        {
            var names = g.Members.Select(m => NormalizeName(MeaningfulName(m.Node))).Where(s => s.Length > 0).Distinct().ToList();
            if (names.Count > 1) parts.Add("names: " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", ..." : ""));
        }
        return string.Join("; ", parts);
    }
}
