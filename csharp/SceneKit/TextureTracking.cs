namespace SceneKit;

/// <summary>The texture units a geometry draws with (own and inherited state merged) and whether the geometry has the
/// matching texcoord set for each; for a replaced subtree the union over its geometries (a unit counts as having
/// texcoords only when every geometry using it has them). Used to notice passes that drop units (T6).</summary>
public sealed class TexSignature
{
    public readonly record struct Entry(int Unit, bool HasTexCoords);

    /// <summary>Units in ascending order.</summary>
    public readonly IReadOnlyList<Entry> Units;

    public TexSignature(IEnumerable<Entry> units) { Units = units.OrderBy(e => e.Unit).ToList(); }

    public static readonly TexSignature Empty = new(Array.Empty<Entry>());

    /// <summary>Signature of a geometry drawn with the (merged) state <paramref name="effective"/>.</summary>
    public static TexSignature Of(GeomData? g, StateSet? effective)
    {
        if (effective == null || effective.Textures.Count == 0) return Empty;
        return new TexSignature(effective.Textures.Select(t => t.Unit).Distinct().Select(u => new Entry(u, TextureTracking.HasTexCoords(g, u))));
    }

    /// <summary>Union of several signatures (null when there are none).</summary>
    public static TexSignature? Union(IEnumerable<TexSignature> sigs)
    {
        Dictionary<int, bool>? o = null;
        foreach (var s in sigs)
        {
            o ??= new();
            foreach (var e in s.Units) o[e.Unit] = (o.TryGetValue(e.Unit, out var has) ? has : true) && e.HasTexCoords;
        }
        return o == null ? null : new TexSignature(o.Select(kv => new Entry(kv.Key, kv.Value)));
    }

    public bool Has(int unit) => Units.Any(e => e.Unit == unit);

    /// <summary>"0 1(no uv)".</summary>
    public override string ToString() => string.Join(" ", Units.Select(e => e.Unit + (e.HasTexCoords ? "" : "(no uv)")));
}

/// <summary>Effective (inherited) texture state: OSG merges state sets down the path, nearer ones override textures per
/// unit. Paths follow the first parent, like the optimiser does.</summary>
public static class TextureTracking
{
    /// <summary>The geometry has a non-empty texcoord array for the unit.</summary>
    public static bool HasTexCoords(GeomData? g, int unit) => g != null && g.TexCoords.TryGetValue(unit, out var a) && a.Count > 0;

    /// <summary>No image to draw: not found on disk, not embedded and not baked by the optimiser.</summary>
    public static bool IsMissing(Texture t) => !t.Exists && !t.Embedded && t.Baked == null;

    /// <summary>"ok", "MISSING", "embedded", "baked" or "resize -> WxH" (the size it is written with).</summary>
    public static string StateText(Texture t)
    {
        if (t.Baked != null) return "baked";
        if (t.Embedded) return "embedded";
        if (!t.Exists) return "MISSING";
        if (t.Resize is var (w, h)) return $"resize -> {w}x{h}";
        return "ok";
    }

    /// <summary>"0 1" for a unit mask (bit u = unit u).</summary>
    public static string UnitList(ulong mask) =>
        string.Join(" ", Enumerable.Range(0, 64).Where(u => (mask >> u & 1) != 0).Select(u => u.ToString()));

    internal static ulong Bit(int unit) => unit is >= 0 and < 64 ? 1UL << unit : 0;

    /// <summary><paramref name="parent"/> (an already merged state) with <paramref name="own"/> on top; the same
    /// merge as the optimiser's MergedState. Returns <paramref name="own"/> itself when nothing is inherited.</summary>
    public static StateSet? Merge(StateSet? parent, StateSet? own)
    {
        if (own == null) return parent;
        if (parent == null) return own;
        var m = new StateSet();
        Apply(m, parent);
        Apply(m, own);
        return m;
    }

    static void Apply(StateSet m, StateSet st)
    {
        if (st.Diffuse != null) { m.Diffuse = st.Diffuse; m.MaterialName = st.MaterialName; }
        else if (st.MaterialName.Length > 0) m.MaterialName = st.MaterialName;
        foreach (var t in st.Textures) { m.Textures.RemoveAll(x => x.Unit == t.Unit); m.Textures.Add(t); }
        m.Transparent |= st.Transparent;
        foreach (var (k, v) in st.Modes) m.Modes[k] = v;
        foreach (var s in st.Summary) if (!m.Summary.Contains(s)) m.Summary.Add(s);
    }

    /// <summary>A copy that does not change when the original's texture list is edited later.</summary>
    public static StateSet? Copy(StateSet? ss) => ss == null ? null : new StateSet
    {
        Textures = new List<Texture>(ss.Textures), MaterialName = ss.MaterialName, Diffuse = ss.Diffuse, Modes = new(ss.Modes),
        Transparent = ss.Transparent, Src = ss.Src, Summary = new(ss.Summary),
    };

    /// <summary>Every node once, depth first (the first path to a shared node wins), with the merged state that applies to
    /// it (its own state set included).</summary>
    public static IEnumerable<(Node n, Node? parent, StateSet? merged)> WithState(Scene scene, bool externals = true)
    {
        if (scene.Root == null) yield break;
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(Node n, Node? parent, StateSet? above)>();
        stack.Push((scene.Root, null, null));
        while (stack.Count > 0)
        {
            var (n, parent, above) = stack.Pop();
            if (!seen.Add(n)) continue;
            var merged = Merge(above, n.StateSet);
            yield return (n, parent, merged);
            var kids = externals ? n.AllChildren() : n.Children;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], n, merged));
        }
    }
}

/// <summary>Texture facts of an analysed scene, built once per report (<see cref="Report.TexIndex"/>): the effective
/// units of every node, unit masks per subtree, state set users and the geometries drawing each texture.</summary>
public sealed class TextureIndex
{
    /// <summary>One texture unit that applies at a node; From is the node whose state set binds it.</summary>
    public readonly record struct Bound(int Unit, Texture Tex, Node From);

    /// <summary>Unit masks of the geometries at / below a node (bit u = unit u): units drawn, units lacking their
    /// texcoord set on some geometry, units whose image is missing on some geometry.</summary>
    public readonly record struct Masks(ulong Units, ulong NoTexCoords, ulong Missing)
    {
        public bool Warn => (NoTexCoords | Missing) != 0;
    }

    sealed class Eff { public Bound[] Units = Array.Empty<Bound>(); public bool TexGen; }

    readonly Report rep;
    readonly Dictionary<Node, Eff> eff = new(ReferenceEqualityComparer.Instance);
    Dictionary<Node, Masks>? masks;
    Dictionary<StateSet, int>? ssUsers;
    Dictionary<Texture, (List<Node> geoms, List<Node> noUv)>? texGeoms;
    Dictionary<Texture, List<Finding>>? texFindings;
    static readonly Eff None = new();

    public TextureIndex(Report rep) { this.rep = rep; }

    Eff EffOf(Node n)
    {
        if (eff.TryGetValue(n, out var e)) return e;
        // walk up the first parents to a known node, then fill in top-down
        var chain = new List<Node>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        Eff above = None;
        for (Node? cur = n; cur != null && seen.Add(cur); cur = rep.Parents.TryGetValue(cur, out var ps) && ps.Count > 0 ? ps[0] : null)
        {
            if (eff.TryGetValue(cur, out var known)) { above = known; break; }
            chain.Add(cur);
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var c = chain[i];
            var ss = c.StateSet;
            if (ss == null) { eff[c] = above; continue; }
            var units = above.Units.ToList();
            foreach (var t in ss.Textures) { units.RemoveAll(b => b.Unit == t.Unit); units.Add(new Bound(t.Unit, t, c)); }
            above = new Eff
            {
                Units = units.OrderBy(b => b.Unit).ToArray(),
                TexGen = above.TexGen || ss.Summary.Any(s => s.StartsWith("TexGen", StringComparison.Ordinal)) || ss.Modes.Keys.Any(k => k.Contains("TEXTURE_GEN")),
            };
            eff[c] = above;
        }
        return above;
    }

    /// <summary>Texture units that apply at the node (own state set and inherited), ascending.</summary>
    public IReadOnlyList<Bound> Effective(Node n) => EffOf(n).Units;

    /// <summary>A TexGen is active at the node (texture coordinates are generated, no texcoord array needed).</summary>
    public bool TexGen(Node n) => EffOf(n).TexGen;

    static bool Drawn(Node n) => n.Kind == Kind.Geometry && n.Geom != null;

    Masks Own(Node n)
    {
        if (!Drawn(n)) return default;
        ulong u = 0, noUv = 0, miss = 0;
        var e = EffOf(n);
        foreach (var b in e.Units)
        {
            ulong bit = TextureTracking.Bit(b.Unit);
            u |= bit;
            if (!e.TexGen && !TextureTracking.HasTexCoords(n.Geom, b.Unit)) noUv |= bit;
            if (TextureTracking.IsMissing(b.Tex)) miss |= bit;
        }
        return new Masks(u, noUv, miss);
    }

    /// <summary>Masks of the geometries at / below the node (a geometry: its own).</summary>
    public Masks Subtree(Node n)
    {
        if (masks == null)
        {
            masks = new(ReferenceEqualityComparer.Instance);
            if (rep.Scene.Root != null)
            {
                // post order over the whole graph
                var state = new Dictionary<Node, bool>(ReferenceEqualityComparer.Instance);
                var stack = new Stack<Node>();
                stack.Push(rep.Scene.Root);
                while (stack.Count > 0)
                {
                    var c = stack.Peek();
                    if (!state.TryGetValue(c, out var done))
                    {
                        state[c] = false;
                        foreach (var k in c.AllChildren()) if (!state.ContainsKey(k)) stack.Push(k);
                        continue;
                    }
                    stack.Pop();
                    if (done) continue;
                    state[c] = true;
                    var m = Own(c);
                    foreach (var k in c.AllChildren())
                        if (masks.TryGetValue(k, out var km)) m = new Masks(m.Units | km.Units, m.NoTexCoords | km.NoTexCoords, m.Missing | km.Missing);
                    masks[c] = m;
                }
            }
        }
        return masks.TryGetValue(n, out var r) ? r : Own(n);
    }

    /// <summary>Badge for the tree's Units column: geometry - the units it draws with (own + inherited); any other
    /// node - the union of the units the geometries below it draw with. "⚠" when one of them has no image or the
    /// geometry lacks the texcoord set. Empty when no texture applies.</summary>
    public string Badge(Node n)
    {
        var m = Subtree(n);
        if (m.Units == 0) return "";
        return TextureTracking.UnitList(m.Units) + (m.Warn ? " ⚠" : "");
    }

    /// <summary>Number of nodes whose own state set is <paramref name="ss"/>.</summary>
    public int StateSetUsers(StateSet ss)
    {
        if (ssUsers == null)
        {
            ssUsers = new(ReferenceEqualityComparer.Instance);
            foreach (var n in rep.AllNodes())
                if (n.StateSet != null) ssUsers[n.StateSet] = ssUsers.GetValueOrDefault(n.StateSet) + 1;
        }
        return ssUsers.GetValueOrDefault(ss);
    }

    void BuildTexGeoms()
    {
        if (texGeoms != null) return;
        texGeoms = new(ReferenceEqualityComparer.Instance);
        foreach (var n in rep.AllNodes())
        {
            if (!Drawn(n)) continue;
            var e = EffOf(n);
            foreach (var b in e.Units)
            {
                if (!texGeoms.TryGetValue(b.Tex, out var l)) texGeoms[b.Tex] = l = (new List<Node>(), new List<Node>());
                l.geoms.Add(n);
                if (!e.TexGen && !TextureTracking.HasTexCoords(n.Geom, b.Unit)) l.noUv.Add(n);
            }
        }
    }

    /// <summary>Geometry nodes drawing with the texture (through their own or an inherited state set).</summary>
    public List<Node> Drawables(Texture t) { BuildTexGeoms(); return texGeoms!.TryGetValue(t, out var l) ? l.geoms : new List<Node>(); }

    /// <summary>Geometry nodes drawing with the texture that have no texcoord set for its unit (and no TexGen).</summary>
    public List<Node> WithoutTexCoords(Texture t) { BuildTexGeoms(); return texGeoms!.TryGetValue(t, out var l) ? l.noUv : new List<Node>(); }

    /// <summary>Textures that some geometry draws without the matching texcoord set, in scene order.</summary>
    public List<(Texture tex, List<Node> geoms)> TexturesWithoutTexCoords()
    {
        BuildTexGeoms();
        return texGeoms!.Where(kv => kv.Value.noUv.Count > 0).Select(kv => (kv.Key, kv.Value.noUv)).ToList();
    }

    /// <summary>Findings (not "info") that name the texture.</summary>
    public List<Finding> FindingsOf(Texture t)
    {
        if (texFindings == null)
        {
            texFindings = new(ReferenceEqualityComparer.Instance);
            foreach (var f in rep.Findings)
                if (f.Severity != "info")
                    foreach (var tx in f.Textures.Distinct(ReferenceEqualityComparer.Instance).Cast<Texture>())
                    {
                        if (!texFindings.TryGetValue(tx, out var l)) texFindings[tx] = l = new List<Finding>();
                        l.Add(f);
                    }
        }
        return texFindings.TryGetValue(t, out var r) ? r : new List<Finding>();
    }
}
