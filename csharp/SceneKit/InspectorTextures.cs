namespace SceneKit;

/// <summary>Texture views of the inspector: units that apply at a node (TX2) and the textures table (TX3).</summary>
public static partial class InspectorModel
{
    /// <summary>One texture unit that applies at a node. InheritedFrom: the ancestor whose state set binds it (null: the
    /// node's own state set). HasTexCoords: a geometry has the texcoord set for the unit (or a TexGen makes them); any
    /// other node: no geometry below drawing with the unit lacks it. State: see <see cref="TextureTracking.StateText"/>.</summary>
    public sealed record TexUnitInfo(int Unit, Texture Tex, Node? InheritedFrom, bool HasTexCoords, string State);

    /// <summary>The effective texture units at a node (own state set merged over the inherited ones), ascending.</summary>
    public static List<TexUnitInfo> TextureUnits(Report rep, Node n)
    {
        var ix = rep.TexIndex;
        bool texGen = ix.TexGen(n);
        var below = ix.Subtree(n);
        return ix.Effective(n).Select(b => new TexUnitInfo(b.Unit, b.Tex, ReferenceEquals(b.From, n) ? null : b.From,
            texGen || (n.Kind == Kind.Geometry && n.Geom != null
                ? TextureTracking.HasTexCoords(n.Geom, b.Unit)
                : (below.NoTexCoords & TextureTracking.Bit(b.Unit)) == 0),
            TextureTracking.StateText(b.Tex))).ToList();
    }

    /// <summary>One line of the textures table: every Texture object that resolves to the same file (Texs; the first one is Tex).
    /// Unit: the distinct units joined by ",". Users: nodes whose own state set holds one of them (double click selects them);
    /// Drawables: geometries drawing with one (inherited too); NoTexCoords: of those, the ones without the unit's texcoord
    /// set; all three summed over the group. Files: distinct files the users are saved in. Problems: "" when fine, else
    /// e.g. "missing image; 2 geometries without texcoord set 1; Large texture ...".</summary>
    public sealed record TextureRow(Texture Tex, string File, string Path, string Unit, int Width, int Height, string Size, string Format,
        long FileBytes, long GpuBytes, int Users, int Drawables, int NoTexCoords, string State, string Problems, List<Node> UserNodes,
        IReadOnlyList<Texture> Texs, int Files)
    {
        /// <summary>Labels of the texture's companion images ("Classes", "IR"), "" without.</summary>
        public string Companions => SceneKit.Companions.Labels(Tex);
    }

    /// <summary>One line per companion image of a texture: "Classes companion ({stem}_Classes.png): path  512x512 sgi" (baked
    /// ones: "written next to it when saving").</summary>
    public static List<string> CompanionLines(Texture t)
    {
        var o = new List<string>();
        if (t.Companions == null) return o;
        foreach (var (pat, c) in t.Companions)
        {
            string size = c.Width != 0 ? $"{c.Width}x{c.Height}" + (c.Fmt.Length > 0 ? $" {c.Fmt}" : "") : "size unknown";
            string where = c.Baked != null && (c.Path == null || !File.Exists(c.Path)) ? $"{c.File} (baked, written next to it when saving)" : c.Path ?? c.File;
            string diff = c.Baked == null && t.Width != 0 && c.Width != 0 && (c.Width != t.Width || c.Height != t.Height) && t.Resize == null ? "  (other size than the texture)" : "";
            o.Add($"{SceneKit.Companions.Label(pat)} companion ({pat}): {where}  [{size}]{diff}");
        }
        return o;
    }

    /// <summary>All textures of the scene, one row per image file (textures without a path stay one row each), biggest GPU
    /// memory first. <paramref name="filter"/> matches file, path, format and state (case-insensitive substring);
    /// <paramref name="problemsOnly"/> keeps rows with problems.</summary>
    public static List<TextureRow> TextureTable(Report rep, string? filter = null, bool problemsOnly = false)
    {
        var ix = rep.TexIndex;
        var owner = Selection.OwnerFiles(rep);
        var groups = new List<List<Texture>>();
        var byPath = new Dictionary<string, List<Texture>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in rep.Textures)
        {
            if (string.IsNullOrEmpty(t.Path)) { groups.Add(new List<Texture> { t }); continue; }
            if (!byPath.TryGetValue(t.Path, out var g)) groups.Add(byPath[t.Path] = g = new List<Texture>());
            g.Add(t);
        }
        var o = new List<TextureRow>();
        foreach (var (g, i) in groups.Select((g, i) => (g, i)).OrderBy(x => -x.g.Sum(t => t.GpuBytes)).ThenBy(x => x.i))
        {
            var t = g[0];
            var users = g.SelectMany(x => rep.TexUsers.TryGetValue(x, out var u) ? u : new List<Node>()).Distinct(ReferenceEqualityComparer.Instance).Cast<Node>().ToList();
            var problems = new List<string>();
            int noUv = 0, drawables = 0;
            foreach (var x in g)
            {
                var nu = ix.WithoutTexCoords(x);
                noUv += nu.Count;
                drawables += ix.Drawables(x).Count;
                if (TextureTracking.IsMissing(x) && !problems.Contains("missing image")) problems.Add("missing image");
                if (nu.Count > 0) problems.Add($"{nu.Count} geometr{(nu.Count == 1 ? "y" : "ies")} without texcoord set {x.Unit}");
                foreach (var f in ix.FindingsOf(x))
                    if (f.Code is not ("missing_texture" or "missing_texcoords") && !problems.Contains(f.Title)) problems.Add(f.Title);
            }
            var states = g.Select(TextureTracking.StateText).ToList();
            var files = new HashSet<string>();
            foreach (var n in users) if (owner.TryGetValue(n, out var f)) files.Add(f);
            var row = new TextureRow(t, t.Label, t.Path ?? "", string.Join(",", g.Select(x => x.Unit).Distinct().Order()), t.Width, t.Height,
                t.Width != 0 ? $"{t.Width}x{t.Height}" : "?", t.Fmt, t.FileBytes, g.Sum(x => x.GpuBytes), users.Count,
                drawables, noUv, states.FirstOrDefault(s => s != "ok") ?? states[0], string.Join("; ", problems), users, g, files.Count);
            if (problemsOnly && row.Problems.Length == 0) continue;
            if (!string.IsNullOrEmpty(filter) && !new[] { row.File, row.Path, row.Format, row.State, row.Companions }.Any(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;
            o.Add(row);
        }
        return o;
    }
}
