using System.Text;
using System.Text.Json;

namespace SceneKit;

/// <summary>Text and JSON reports of an analysis (text identical to the Python version).</summary>
public static class ReportText
{
    public static string NodeLabel(Node n)
    {
        string cls = OsgReader.Short(n.Cls);
        return n.Name.Length > 0 ? $"{cls} '{n.Name}'" : cls;
    }

    static readonly string[] SceneExts = { ".flt", ".osgt", ".osgb", ".ive", ".osg" };

    /// <summary>Shortens a "/"-joined node path in the middle. Keeps the head up to the first distinguishing level (within the first three, a level with a scene-file extension, else one with a digit that is not an OpenFlight auto name like g1 / o145; the tile) and the last two levels; never cuts inside a level name.</summary>
    public static string MiddlePath(string path, int maxChars)
    {
        if (path.Length <= maxChars) return path;
        var lv = path.Split('/');
        if (lv.Length <= 4) return path;
        int d = Array.FindIndex(lv, 0, 3, l => SceneExts.Any(e => l.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
        if (d < 0) d = Array.FindIndex(lv, 0, 3, l => l.Any(char.IsDigit) && !Optimizer.AutoFltName.IsMatch(l));
        int head = d >= 0 ? d + 1 : 2;
        if (head + 2 >= lv.Length) return path;
        string tail = string.Join("/", lv[^2..]);
        string c1 = $"{string.Join("/", lv[..head])}/…/{tail}";
        if (c1.Length <= maxChars) return c1;
        string c2 = d >= 0 ? $"…/{lv[d]}/…/{tail}" : $"{lv[0]}/…/{tail}";
        if (c2.Length <= maxChars) return c2;
        return $"…/{tail}";
    }

    static string L(string s, int w) => s.Length >= w ? s : s + new string(' ', w - s.Length);
    static string R(string s, int w) => s.Length >= w ? s : new string(' ', w - s.Length) + s;

    /// <summary>Indented scene tree; subtrees below minShare of triangles and memory are folded.</summary>
    public static List<string> TreeLines(Report rep, int maxDepth = 6, double minShare = 0.01, int maxChildren = 25)
    {
        var tot = rep.Total;
        var outl = new List<string>();
        double tTris = Math.Max(tot.Tris, 1), tBytes = Math.Max(tot.Bytes, 1);

        void Walk(Node n, int depth, HashSet<Node> prefix)
        {
            var st = rep.Stat(n);
            double share = Math.Max(st.Tris / tTris, st.Bytes / tBytes);
            long inst = rep.Instances.GetValueOrDefault(n, 1);
            var extra = new List<string>();
            if (inst > 1) extra.Add($"shared x{inst}");
            if (n.Kind == Kind.External) extra.Add($"file {n.External}");
            if (n.Notes.Count > 0) extra.Add(string.Join("; ", n.Notes.Take(2)));
            outl.Add($"{new string(' ', 2 * depth)}{L(NodeLabel(n), Math.Max(10, 44 - 2 * depth))} "
                     + $"{R(Analyzer.HumanCount(st.Tris), 8)} tris {R(Analyzer.HumanCount(st.VisTris), 8)} vis {R(st.Draws.ToString(), 6)} draws "
                     + $"{R(Analyzer.HumanBytes(st.Bytes), 9)} {R(PyFormat.Percent(share, 1), 6)}" + (extra.Count > 0 ? $"  [{string.Join(", ", extra)}]" : ""));
            if (prefix.Contains(n)) return;
            var kids = n.AllChildren();
            if (kids.Count == 0) return;
            if (depth >= maxDepth)
            {
                outl.Add($"{new string(' ', 2 * (depth + 1))}... {kids.Count} children");
                return;
            }
            int shown = 0;
            var hidden = new List<Node>();
            var ranked = kids.Select((c, i) => (c, i)).OrderBy(x => -(rep.Stat(x.c).Tris / tTris + rep.Stat(x.c).Bytes / tBytes)).ThenBy(x => x.i).Select(x => x.c);
            var np = new HashSet<Node>(prefix, ReferenceEqualityComparer.Instance) { n };
            foreach (var c in ranked)
            {
                var cs = rep.Stat(c);
                if (Math.Max(cs.Tris / tTris, cs.Bytes / tBytes) < minShare || shown >= maxChildren) { hidden.Add(c); continue; }
                shown++;
                Walk(c, depth + 1, np);
            }
            if (hidden.Count > 0)
            {
                long ht = hidden.Sum(c => rep.Stat(c).Tris), hb = hidden.Sum(c => rep.Stat(c).Bytes);
                outl.Add($"{new string(' ', 2 * (depth + 1))}... {hidden.Count} smaller node(s): {Analyzer.HumanCount(ht)} tris, {Analyzer.HumanBytes(hb)}");
            }
        }

        if (rep.Scene.Root != null) Walk(rep.Scene.Root, 0, new HashSet<Node>(ReferenceEqualityComparer.Instance));
        return outl;
    }

    public static string Text(Report rep, int treeDepth = 6, double minShare = 0.01, int top = 15)
    {
        var s = rep.Summary();
        var sc = rep.Scene;
        var o = new List<string>();
        o.Add($"Scene:     {sc.Path}");
        if (sc.ConvertedFrom != null) o.Add("           (read through osgconv)");
        o.Add($"Format:    {sc.Fmt}" + (sc.Version is int v && v != 0 ? $" version {v}" : "")
              + (sc.Info.TryGetValue("generator", out var gen) && gen is string gs && gs.Length > 0 ? $", {gs}" : ""));
        foreach (var k in new[] { "units", "openflight_revision" })
            if (sc.Info.TryGetValue(k, out var val))
            {
                string label = k.Replace('_', ' ');
                label = char.ToUpperInvariant(label[0]) + label[1..].ToLowerInvariant() + ":";
                o.Add($"{L(label, 11)}{val ?? "None"}");
            }
        o.Add("");
        string I(string key) => PyFormat.Int(Convert.ToInt64(s[key]));
        o.Add($"Nodes:               {I("nodes")} (max depth {s["max_depth"]})");
        o.Add($"Geometries:          {I("unique_geometries")} unique, {I("geometry_instances")} drawn");
        o.Add($"Triangles:           {I("triangles")} (worst case visible: {I("visible_triangles")})");
        if (Convert.ToInt64(s["lines"]) != 0 || Convert.ToInt64(s["points"]) != 0) o.Add($"Lines / points:      {I("lines")} / {I("points")}");
        o.Add($"Vertices:            {I("vertices")}");
        o.Add($"Draw calls:          {I("draw_calls")}");
        o.Add($"Geometry memory:     {Analyzer.HumanBytes(Convert.ToInt64(s["geometry_bytes"]))}");
        o.Add($"Texture memory:      {Analyzer.HumanBytes(Convert.ToInt64(s["texture_bytes"]))} in {s["textures"]} texture(s) (GPU, with mipmaps)");
        if (s["size"] is List<double> size && size.Count > 0) o.Add("Size:                " + string.Join(" x ", size.Select(x => PyFormat.G(x, 3, true))));
        if (sc.Warnings.Count > 0)
        {
            o.Add("");
            o.Add("Warnings:");
            o.AddRange(sc.Warnings.Take(20).Select(w => $"  - {w}"));
        }
        o.Add("");
        o.Add("Scene tree (what the size is made of):");
        o.AddRange(TreeLines(rep, treeDepth, minShare));
        o.Add("");
        o.Add($"Top {top} geometries by triangles (x instances):");
        foreach (var g in rep.Geoms.Select((g, i) => (g, i)).OrderBy(x => -x.g.Tris * x.g.Instances).ThenBy(x => x.i).Take(top).Select(x => x.g))
            o.Add($"  {R(Analyzer.HumanCount(g.Tris * g.Instances), 8)} tris  {R(Analyzer.HumanBytes(g.Bytes), 9)}  "
                  + $"{R(g.Instances > 1 ? "x" + g.Instances : "", 5)}  {rep.PathOf(g.Node)}");
        if (rep.Textures.Count > 0)
        {
            o.Add("");
            o.Add($"Top {top} textures by GPU memory:");
            foreach (var t in rep.Textures.Select((t, i) => (t, i)).OrderBy(x => -x.t.GpuBytes).ThenBy(x => x.i).Take(top).Select(x => x.t))
            {
                string state = !t.Exists && !t.Embedded ? "MISSING" : (t.Width != 0 ? $"{t.Width}x{t.Height}" : "?");
                o.Add($"  {R(Analyzer.HumanBytes(t.GpuBytes), 9)}  {R(state, 11)}  {t.Label}");
            }
        }
        o.Add("");
        o.Add($"Findings ({rep.Findings.Count}):");
        foreach (var f in rep.Findings)
        {
            var sav = new List<string>();
            if (f.SavingBytes != 0) sav.Add(Analyzer.HumanBytes(f.SavingBytes));
            if (f.SavingTris != 0) sav.Add($"{Analyzer.HumanCount(f.SavingTris)} tris");
            if (f.SavingDraws != 0) sav.Add($"{f.SavingDraws} draws");
            o.Add($"  [{L(f.Severity.ToUpperInvariant(), 6)}] {f.Title}" + (sav.Count > 0 ? $"  (saves ~{string.Join(", ", sav)})" : ""));
            o.Add($"           {f.Message}");
            if (f.Fix != null) o.Add($"           fix: --optimize {f.Fix}");
        }
        return string.Join("\n", o) + "\n";
    }

    public static string Json(Report rep)
    {
        object NodeDict(Node n, int depth, HashSet<Node> seen)
        {
            var st = rep.Stat(n);
            var d = new Dictionary<string, object?>
            {
                ["class"] = n.Cls, ["name"] = n.Name, ["kind"] = n.Kind, ["tris"] = st.Tris, ["visible_tris"] = st.VisTris,
                ["vertices"] = st.Verts, ["draw_calls"] = st.Draws, ["geometry_bytes"] = st.GeoBytes,
                ["texture_bytes"] = st.TexBytes, ["instances"] = rep.Instances.GetValueOrDefault(n, 1),
            };
            if (n.Notes.Count > 0) d["notes"] = n.Notes;
            if (n.Kind == Kind.External) d["file"] = n.External;
            if (seen.Contains(n)) { d["shared_reference"] = true; return d; }
            var kids = n.AllChildren();
            if (kids.Count > 0 && depth < 64)
            {
                var ns = new HashSet<Node>(seen, ReferenceEqualityComparer.Instance) { n };
                d["children"] = kids.Select(c => NodeDict(c, depth + 1, ns)).ToList();
            }
            return d;
        }
        var outd = new Dictionary<string, object?>
        {
            ["summary"] = rep.Summary(),
            ["warnings"] = rep.Scene.Warnings,
            ["info"] = rep.Scene.Info,
            ["findings"] = rep.Findings.Select(f => new Dictionary<string, object?>
            {
                ["code"] = f.Code, ["severity"] = f.Severity, ["title"] = f.Title, ["message"] = f.Message, ["fix"] = f.Fix,
                ["saving_bytes"] = f.SavingBytes, ["saving_tris"] = f.SavingTris, ["saving_draws"] = f.SavingDraws,
                ["nodes"] = f.Nodes.Take(50).Select(rep.PathOf).ToList(), ["textures"] = f.Textures.Take(50).Select(t => t.Label).ToList(),
            }).ToList(),
            ["textures"] = rep.Textures.Select(t => new Dictionary<string, object?>
            {
                ["file"] = t.Label, ["path"] = t.Path, ["exists"] = t.Exists || t.Embedded, ["width"] = t.Width,
                ["height"] = t.Height, ["gpu_bytes"] = t.GpuBytes, ["embedded"] = t.Embedded,
            }).ToList(),
            ["tree"] = rep.Scene.Root != null ? NodeDict(rep.Scene.Root, 0, new HashSet<Node>(ReferenceEqualityComparer.Instance)) : null,
        };
        return JsonSerializer.Serialize(outd, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
    }
}
