namespace SceneKit;

/// <summary>Companion (sidecar) images: files an image generator finds next to a texture by a naming rule instead of a
/// reference in the scene, e.g. the infrared image 'tree_ir.rgb' of 'tree.rgb'. A pattern names the companion from the
/// texture's path with the tokens {stem} (file name without extension), {ext} (extension with the dot) and {dir} (the
/// texture's folder). A pattern without {dir} is relative to the texture's folder and may name a sub-folder
/// ("ir/{stem}{ext}") or its own extension ("{stem}_ir.int"). Matching is case-insensitive.
/// <para>Companions are found when a scene is loaded and whenever the patterns change (<see cref="SetPatterns"/>). Baking
/// (cards, billboard, proxy, rebake, LOD far levels) bakes a companion atlas with the layout of the new atlas, and saving
/// writes it next to the atlas under the same pattern; resizing resizes the companions with their texture.</para></summary>
public static class Companions
{
    public static readonly string[] DefaultPatterns = { "{stem}_Classes.png", "{stem}_ir{ext}", "{stem}_IR{ext}" };

    /// <summary>Patterns from a list in one string (separated by ';', ',' or new lines); empty entries dropped, duplicates
    /// (ignoring case) once. An empty text gives no patterns.</summary>
    public static List<string> Parse(string text)
    {
        var o = new List<string>();
        foreach (var p in text.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!o.Contains(p, StringComparer.OrdinalIgnoreCase)) o.Add(p);
        return o;
    }

    /// <summary>The patterns as one line ("{stem}_ir{ext}; ir/{stem}{ext}").</summary>
    public static string Format(IEnumerable<string> patterns) => string.Join("; ", patterns);

    /// <summary>Why a pattern cannot be used (null: fine): it needs {stem} and must not name the texture itself.</summary>
    public static string? Check(string pattern)
    {
        if (!pattern.Contains("{stem}", StringComparison.Ordinal)) return $"companion pattern '{pattern}' has no {{stem}}";
        if (pattern is "{stem}{ext}" or "{dir}/{stem}{ext}") return $"companion pattern '{pattern}' names the texture itself";
        foreach (var tok in System.Text.RegularExpressions.Regex.Matches(pattern, @"\{[^}]*\}").Select(m => m.Value))
            if (tok is not ("{stem}" or "{ext}" or "{dir}")) return $"companion pattern '{pattern}': unknown token {tok} (use {{stem}}, {{ext}}, {{dir}})";
        return null;
    }

    /// <summary>The companion path of an image path under a pattern (no file system access).</summary>
    public static string Apply(string pattern, string imagePath)
    {
        string p = imagePath.Replace('\\', '/');
        int slash = p.LastIndexOf('/');
        string dir = slash >= 0 ? imagePath[..slash] : "", name = slash >= 0 ? imagePath[(slash + 1)..] : imagePath;
        string ext = System.IO.Path.GetExtension(name), stem = name[..(name.Length - ext.Length)];
        string r = pattern.Replace("{stem}", stem, StringComparison.Ordinal).Replace("{ext}", ext, StringComparison.Ordinal);
        r = pattern.Contains("{dir}", StringComparison.Ordinal) ? r.Replace("{dir}", dir, StringComparison.Ordinal)
            : dir.Length == 0 ? r : System.IO.Path.Combine(dir, r);
        return System.IO.Path.DirectorySeparatorChar == '\\' ? r.Replace('/', '\\') : r;
    }

    /// <summary>A pattern or file name for class maps (Trian3D sensor classes, 0-15 in the red channel): its fixed text
    /// (the stem of a file name) ends with "_classes", ignoring case ("{stem}_Classes.png", "ground_classes.png").</summary>
    public static bool IsClassMap(string patternOrFile)
    {
        string s = patternOrFile.Replace("{stem}", " ").Replace("{ext}", " ").Replace("{dir}", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\.[A-Za-z0-9]{1,5}\s*$", "").TrimEnd();
        return s.EndsWith("_classes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Short label of a pattern for the UI: its fixed text without tokens, separators and extension, upper-case
    /// ("{stem}_ir{ext}" -> "IR", "ir/{stem}{ext}" -> "IR", "{stem}_nvg.rgb" -> "NVG"); "Classes" for class maps.</summary>
    public static string Label(string pattern)
    {
        if (IsClassMap(pattern)) return "Classes";
        string s = pattern.Replace("{stem}", " ").Replace("{ext}", " ").Replace("{dir}", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\.[A-Za-z0-9]{1,5}\s*$", "");   // its own extension
        s = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? pattern : string.Join("/", parts).ToUpperInvariant();
    }

    /// <summary>The existing file a path names, matched case-insensitively (the path as it is on disk), or null.</summary>
    public static string? FindFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows()) return Exact(path) ?? path;
                return path;
            }
            if (OperatingSystem.IsWindows()) return null;   // the file system ignores case already
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (dir == null) return null;
            if (!Directory.Exists(dir))
            {
                dir = FindDir(dir);
                if (dir == null) return null;
            }
            string name = System.IO.Path.GetFileName(path);
            return Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    static string? FindDir(string dir)
    {
        string? parent = System.IO.Path.GetDirectoryName(dir);
        if (parent == null) return null;
        if (!Directory.Exists(parent)) parent = FindDir(parent);
        if (parent == null) return null;
        string name = System.IO.Path.GetFileName(dir);
        return Directory.EnumerateDirectories(parent).FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The spelling of an existing file's name as it is on disk (Windows), or null.</summary>
    static string? Exact(string path)
    {
        string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (dir == null) return null;
        string name = System.IO.Path.GetFileName(path);
        var f = Directory.EnumerateFiles(dir, name).FirstOrDefault();
        return f;
    }

    /// <summary>Companion files of one image path: pattern -> a texture describing the file (path, size, channels, format;
    /// unit as <paramref name="unit"/>). Null when there is none.</summary>
    public static Dictionary<string, Texture>? Find(string imagePath, IEnumerable<string> patterns, int unit = 0)
    {
        string self = System.IO.Path.GetFullPath(imagePath);
        // (pattern, file on disk, the pattern spells the name exactly): one file found by several patterns ("_ir" and "_IR"
        // on Windows) belongs to the one that spells it as it is on disk, so baked companions are named the same way
        var hits = new List<(string pat, string file, bool exact)>();
        foreach (var pat in patterns)
        {
            if (Check(pat) != null) continue;
            string cand;
            try { cand = System.IO.Path.GetFullPath(Apply(pat, self)); } catch (ArgumentException) { continue; }
            if (string.Equals(cand, self, StringComparison.OrdinalIgnoreCase)) continue;
            var found = FindFile(cand);
            if (found != null) hits.Add((pat, found, string.Equals(System.IO.Path.GetFileName(found), System.IO.Path.GetFileName(cand), StringComparison.Ordinal)));
        }
        Dictionary<string, Texture>? o = null;
        foreach (var h in hits)
        {
            var same = hits.Where(x => string.Equals(x.file, h.file, StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = same.FirstOrDefault(x => x.exact);
            if (pick.pat == null) pick = same[0];
            if (pick.pat != h.pat) continue;
            var d = Describe(h.file, unit);
            if (IsClassMap(h.pat)) d.IsClassMap = true;
            (o ??= new Dictionary<string, Texture>())[h.pat] = d;
        }
        return o;
    }

    /// <summary>A texture object for a companion file (not part of any state set).</summary>
    public static Texture Describe(string path, int unit)
    {
        var info = Images.Probe(path);
        var t = new Texture
        {
            File = System.IO.Path.GetFileName(path), Path = path, Exists = true, Unit = unit, IsCompanion = true, IsClassMap = IsClassMap(System.IO.Path.GetFileName(path)),
            Width = info?.Width ?? 0, Height = info?.Height ?? 0, Channels = info?.Channels ?? 4, Fmt = info?.Fmt ?? "", Bpp = info?.Bpp,
        };
        try { t.FileBytes = new FileInfo(path).Length; } catch (IOException) { }
        return t;
    }

    /// <summary>Texture files of the scene (and its loaded external files), each once.</summary>
    static IEnumerable<Texture> SceneTextures(Scene scene)
    {
        var seen = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes(externals: true))
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (seen.Add(t)) yield return t;
    }

    /// <summary>Find the companions of every texture file with the scene's patterns (baked atlases keep theirs).
    /// Returns the number of textures with at least one companion.</summary>
    public static int Detect(Scene scene)
    {
        int n = 0;
        foreach (var t in SceneTextures(scene))
        {
            if (IsClassMap(t.File)) t.IsClassMap = true;
            if (t.Baked != null) { if (t.Companions != null) n++; continue; }
            t.Companions = t.Path != null && t.Exists && !t.Embedded ? Find(t.Path, scene.CompanionPatterns, t.Unit) : null;
            if (t.Companions != null) n++;
        }
        return n;
    }

    /// <summary>Use other patterns and find the companions again: (textures with companions, problems with the patterns).</summary>
    public static (int found, List<string> problems) SetPatterns(Scene scene, IEnumerable<string> patterns)
    {
        var list = patterns.ToList();
        var problems = list.Select(Check).OfType<string>().ToList();
        scene.CompanionPatterns = list.Where(p => Check(p) == null).ToList();
        return (Detect(scene), problems);
    }

    /// <summary>"IR" or "IR, NVG" for a texture's companions; "" without.</summary>
    public static string Labels(Texture t) => t.Companions is { Count: > 0 } c ? string.Join(", ", c.Keys.Select(Label).Distinct()) : "";
}
