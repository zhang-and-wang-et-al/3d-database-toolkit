using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SceneKit;

/// <summary>R3-C10: a named set of passes with their values (a profile). Built-ins are embedded and read-only; user
/// profiles are JSON files in <see cref="Profiles.UserDir"/>. A run records <see cref="Tag"/> (name@version).</summary>
public sealed class Profile
{
    public string Name = "";
    public int Version = 1;
    public string Class = "", Use = "", Description = "", SelectHint = "";
    /// <summary>Allowed change per LOD switch (pixels); written to generate_lod.pixel_error when the profile ticks generate_lod.</summary>
    public double? PixelError;
    /// <summary>pass → (key → value); a value is converted to its <see cref="PassParam.Type"/>.</summary>
    public Dictionary<string, Dictionary<string, object>> Passes = new();
    public bool BuiltIn;
    /// <summary>The user file it was read from (null for built-ins and unsaved profiles).</summary>
    public string? Path;
    public string Tag => $"{Name}@{Version}";
}

/// <summary>The passes and parameters of a run composed from --profile / --set in argument order.</summary>
public sealed record ComposedRun(List<string> Passes, Dictionary<string, object> Params, string? ProfileTag, List<string> Warnings);

public static class Profiles
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The embedded profiles, in the order they are listed.</summary>
    static readonly string[] EmbeddedFiles =
        { "tree-ground.json", "tree-aerial.json", "tree-far.json", "clutter.json", "tree-lod.json", "building-far.json", "building-box.json", "vehicle-far.json" };

    /// <summary>The optimiser presets as profiles: (preset key, name, class, use, description, select hint).</summary>
    static readonly (string key, string name, string cls, string use, string desc, string hint)[] PresetProfiles =
    {
        ("safe", "Safe", "any", "lossless clean-up",
            "Keeps the look and every triangle: removes empty nodes, shares duplicates, welds and cleans vertices, merges small geometries and reorders them for the vertex cache.",
            "Works on the whole scene or the selection."),
        ("aggressive", "Aggressive", "any", "smaller and faster",
            "Changes the look: on top of Safe it simplifies meshes, flattens transforms, re-centres far-away geometry and downscales large textures.",
            "Works on the whole scene or the selection; check the result before saving."),
        ("lod", "LOD", "any", "LOD",
            "Heavy geometry keeps full detail close up and gets simplified levels that are drawn when it is far away.",
            "Select the heavy objects, or leave the selection empty for every heavy geometry."),
    };

    /// <summary>%APPDATA%\SceneInspector\profiles: the user's own profiles, one JSON file each.</summary>
    public static string UserDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SceneInspector", "profiles");

    /// <summary>The 8 embedded profiles plus Safe, Aggressive and LOD made from <see cref="Optimizer.Presets"/> (version 1,
    /// no values, no pixel target). Fresh objects on every call.</summary>
    public static IReadOnlyList<Profile> BuiltIn() => LoadBuiltIn(null);

    static List<Profile> LoadBuiltIn(List<string>? warnings)
    {
        var o = new List<Profile>();
        var asm = typeof(Profiles).Assembly;
        var names = asm.GetManifestResourceNames();
        foreach (var file in EmbeddedFiles)
        {
            var res = names.FirstOrDefault(n => n.EndsWith(".Profiles." + file, StringComparison.Ordinal));
            if (res == null) { warnings?.Add($"built-in {file}: missing from the build; skipped"); continue; }
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            var (p, w) = Parse(r.ReadToEnd(), "built-in " + file);
            warnings?.AddRange(w);
            if (p == null) continue;
            p.BuiltIn = true;
            o.Add(p);
        }
        foreach (var (key, name, cls, use, desc, hint) in PresetProfiles)
        {
            if (!Optimizer.Presets.TryGetValue(key, out var passes)) continue;
            var p = new Profile { Name = name, Version = 1, Class = cls, Use = use, Description = desc, SelectHint = hint, BuiltIn = true };
            foreach (var pass in passes) p.Passes[pass] = new Dictionary<string, object>();
            o.Add(p);
        }
        return o;
    }

    /// <summary>Built-ins, then the user's profiles (files sorted by name). Broken files and invalid values are skipped with
    /// a warning; a user profile named like a built-in is skipped too.</summary>
    public static (List<Profile> Profiles, List<string> Warnings) LoadAll(string? userDir = null)
    {
        var warnings = new List<string>();
        var all = LoadBuiltIn(warnings);
        userDir ??= UserDir;
        if (!Directory.Exists(userDir)) return (all, warnings);
        foreach (var file in Directory.GetFiles(userDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var p = ReadFile(file, warnings);
            if (p == null) continue;
            var same = all.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                warnings.Add($"{file}: skipped, the name '{p.Name}' is already taken by {(same.BuiltIn ? "a built-in profile" : same.Path)}");
                continue;
            }
            all.Add(p);
        }
        return (all, warnings);
    }

    static Profile? ReadFile(string file, List<string> warnings)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception exc) { warnings.Add($"{file}: skipped, cannot be read ({exc.Message})"); return null; }
        var (p, w) = Parse(text, file);
        warnings.AddRange(w);
        if (p == null) return null;
        p.BuiltIn = false;
        p.Path = file;
        return p;
    }

    /// <summary>Reads one profile. Lenient: unknown passes or keys and values of the wrong type or outside the choices become
    /// warnings and are skipped (the pass default is used); invalid JSON returns null with a warning naming <paramref name="source"/>.</summary>
    public static (Profile? Profile, List<string> Warnings) Parse(string json, string source)
    {
        var warnings = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException exc) { warnings.Add($"{source}: skipped, not valid JSON ({exc.Message})"); return (null, warnings); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { warnings.Add($"{source}: skipped, a profile is a JSON object"); return (null, warnings); }
            var p = new Profile();
            bool named = false;
            foreach (var prop in root.EnumerateObject())
            {
                var v = prop.Value;
                switch (prop.Name)
                {
                    case "name":
                        if (v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0) { p.Name = v.GetString()!.Trim(); named = true; }
                        break;
                    case "version":
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ver) && ver >= 1) p.Version = ver;
                        else warnings.Add($"{source}: version 1 is used: '{v.GetRawText()}' is not a whole number from 1");
                        break;
                    case "class": p.Class = Text(v, "class"); break;
                    case "use": p.Use = Text(v, "use"); break;
                    case "description": p.Description = Text(v, "description"); break;
                    case "select_hint": p.SelectHint = Text(v, "select_hint"); break;
                    case "pixel_error":
                        if (v.ValueKind == JsonValueKind.Null) p.PixelError = null;
                        else if (v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0 && double.IsFinite(v.GetDouble())) p.PixelError = v.GetDouble();
                        else warnings.Add($"{source}: no pixel target is used: pixel_error '{v.GetRawText()}' is not a positive number");
                        break;
                    case "passes":
                        if (v.ValueKind != JsonValueKind.Object) { warnings.Add($"{source}: no passes are used: 'passes' is not an object"); break; }
                        foreach (var pass in v.EnumerateObject()) ReadPass(p, pass, source, warnings);
                        break;
                    default:
                        warnings.Add($"{source}: '{prop.Name}' is not known; skipped");
                        break;
                }
            }
            if (!named)
            {
                p.Name = System.IO.Path.GetFileNameWithoutExtension(source);
                warnings.Add($"{source}: the file name '{p.Name}' is used: the profile has no \"name\"");
            }
            return (p, warnings);

            string Text(JsonElement e, string what)
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString()!;
                if (e.ValueKind != JsonValueKind.Null) warnings.Add($"{source}: '{what}' is left empty: it is not text");
                return "";
            }
        }
    }

    static void ReadPass(Profile p, JsonProperty pass, string source, List<string> warnings)
    {
        if (!Optimizer.Passes.TryGetValue(pass.Name, out var spec)) { warnings.Add($"{source}: pass '{pass.Name}' is not known; skipped"); return; }
        if (pass.Value.ValueKind != JsonValueKind.Object) { warnings.Add($"{source}: pass '{pass.Name}' runs with its defaults: its values are not an object"); p.Passes[pass.Name] = new(); return; }
        var values = new Dictionary<string, object>();
        foreach (var kv in pass.Value.EnumerateObject())
        {
            var pp = spec.Params.FirstOrDefault(x => x.Key == kv.Name);
            if (pp == null) { warnings.Add($"{source}: {pass.Name}.{kv.Name} is not known; skipped"); continue; }
            var (val, why) = FromJson(kv.Value, pp);
            if (val == null) { warnings.Add($"{source}: {pass.Name}.{kv.Name} uses its default ({Str(pp.Default)}): {why}"); continue; }
            values[pp.Key] = val;
        }
        p.Passes[pass.Name] = values;
    }

    static (object? value, string why) FromJson(JsonElement e, PassParam pp)
    {
        string raw = e.GetRawText();
        if (pp.Type == typeof(bool))
            return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? (e.GetBoolean(), "") : (null, $"{raw} is not true or false");
        if (pp.Type == typeof(int))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return ((int)d, "");
            return (null, $"{raw} is not a whole number");
        }
        if (pp.Type == typeof(double))
            return e.ValueKind == JsonValueKind.Number && double.IsFinite(e.GetDouble()) ? (e.GetDouble(), "") : (null, $"{raw} is not a number");
        if (e.ValueKind != JsonValueKind.String) return (null, $"{raw} is not text");
        var s = e.GetString()!;
        if (pp.Choices != null && Array.IndexOf(pp.Choices, s) < 0) return (null, $"'{s}' is not one of {string.Join(", ", pp.Choices)}");
        return (s, "");
    }

    /// <summary>A value converted to the parameter's type, or null when it does not fit (the caller then uses the default).</summary>
    static object? Coerce(object? v, PassParam pp)
    {
        if (v == null) return null;
        try
        {
            if (pp.Type == typeof(bool)) return v is bool b ? b : v is string s ? bool.Parse(s.Trim()) : Convert.ToBoolean(v, Inv);
            if (pp.Type == typeof(int))
            {
                double d = Convert.ToDouble(v, Inv);
                return d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;
            }
            if (pp.Type == typeof(double)) { double d = Convert.ToDouble(v, Inv); return double.IsFinite(d) ? d : null; }
            var str = Convert.ToString(v, Inv) ?? "";
            return pp.Choices != null && Array.IndexOf(pp.Choices, str) < 0 ? null : str;
        }
        catch (Exception exc) when (exc is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    static string Str(object v) => v is bool b ? (b ? "true" : "false") : Convert.ToString(v, Inv) ?? "";

    /// <summary>A profile by name (built-in or user) or by the path of a JSON file; null when there is none.</summary>
    public static Profile? Find(string nameOrPath, string? userDir, List<string> warnings)
    {
        if (File.Exists(nameOrPath)) return ReadFile(System.IO.Path.GetFullPath(nameOrPath), warnings);
        var (all, w) = LoadAll(userDir);
        warnings.AddRange(w);
        return all.FirstOrDefault(p => p.Name == nameOrPath) ?? all.FirstOrDefault(p => string.Equals(p.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ticks exactly the profile's passes and writes every parameter of those passes: the profile's value where it
    /// has one, else the default. replace_cards writes the eleven rows of its layout (so the rows, now explicit, keep the
    /// layout); generate_lod.pixel_error is the profile's pixel target when it has one.</summary>
    public static void Apply(Profile p, ISet<string> passes, IDictionary<string, object> prms) => Apply(p, passes, prms, null);

    /// <summary>As <see cref="Apply(Profile, ISet{string}, IDictionary{string, object})"/>; a layout name that is not known adds a warning.
    /// A stored replace_cards row only counts as explicit when it differs from the row of the profile's layout, so a profile
    /// saved from the editor (every value stored) keeps its named layout.</summary>
    public static void Apply(Profile p, ISet<string> passes, IDictionary<string, object> prms, List<string>? warnings)
    {
        passes.Clear();
        foreach (var (pass, values) in p.Passes)
        {
            if (!Optimizer.Passes.TryGetValue(pass, out var spec)) continue;
            passes.Add(pass);
            var done = new HashSet<string>();
            if (pass == "replace_cards")
            {
                string layout = values.TryGetValue("layout", out var l) ? Str(l) : "custom";
                CardLayout baseL;
                try { baseL = layout == "custom" || layout.Trim().Length == 0 ? CardLayout.Default : CardLayout.Named(layout.Trim()); }
                catch (ArgumentException)
                {
                    warnings?.Add($"{p.Name}: replace_cards.layout '{layout}' is not a known layout; custom is used");
                    layout = "custom"; baseL = CardLayout.Default;
                }
                var baseRows = baseL.Rows();
                var rows = values.Where(kv => CardLayout.RowKeys.Contains(kv.Key) && !SameRow(kv.Key, kv.Value, baseRows[kv.Key]))
                                 .ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var (k, v) in CardLayout.Resolve(layout, rows).Rows()) { prms[$"{pass}.{k}"] = v; done.Add(k); }
                prms[$"{pass}.layout"] = layout;
                done.Add("layout");
            }
            foreach (var pp in spec.Params)
            {
                if (done.Contains(pp.Key)) continue;
                prms[$"{pass}.{pp.Key}"] = values.TryGetValue(pp.Key, out var v) ? v : pp.Default;
            }
        }
        if (p.PixelError is double pe && passes.Contains("generate_lod")) prms["generate_lod.pixel_error"] = pe;
    }

    /// <summary>True when a stored row value is the same as the layout's row, compared the way the editor formats it.</summary>
    static bool SameRow(string key, object stored, object baseline) => NormRow(key, stored) == NormRow(key, baseline);

    static string NormRow(string key, object v)
    {
        string s = (v is bool b ? (b ? "true" : "false") : Convert.ToString(v, Inv) ?? "").Trim().ToLowerInvariant();
        if (key == "heights")
        {
            if (s.Length == 0 || s == "auto") return "auto";
            var parts = s.Split(',').Select(x => double.TryParse(x.Trim(), System.Globalization.NumberStyles.Float, Inv, out var d) ? d.ToString("R", Inv) : x.Trim());
            return string.Join(", ", parts);
        }
        return double.TryParse(s, System.Globalization.NumberStyles.Float, Inv, out var n) ? n.ToString("R", Inv) : s;
    }

    /// <summary>A profile from the editor's state: every value of every ticked pass, not only the changed ones, so a later
    /// change of a default does not change what the profile does.</summary>
    public static Profile FromEditor(string name, IEnumerable<string> passes, IReadOnlyDictionary<string, object> prms, string cls = "", string use = "", string hint = "")
    {
        var p = new Profile { Name = name.Trim(), Version = 1, Class = cls, Use = use, SelectHint = hint };
        foreach (var pass in passes.Distinct())
        {
            if (!Optimizer.Passes.TryGetValue(pass, out var spec)) continue;
            var values = new Dictionary<string, object>();
            foreach (var pp in spec.Params)
                values[pp.Key] = (prms.TryGetValue($"{pass}.{pp.Key}", out var v) ? Coerce(v, pp) : null) ?? pp.Default;
            p.Passes[pass] = values;
        }
        return p;
    }

    static bool IsBuiltInName(string name) => BuiltIn().Any(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the profile to &lt;user dir&gt;\&lt;sanitised name&gt;.json and returns the path. Built-in names are refused.</summary>
    public static string Save(Profile p, string? userDir = null)
    {
        if (IsBuiltInName(p.Name)) throw new InvalidOperationException("built-in profiles are read-only; save as a new name");
        if (p.Name.Trim().Length == 0) throw new ArgumentException("a profile needs a name");
        userDir ??= UserDir;
        Directory.CreateDirectory(userDir);
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var file = new string(p.Name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (file.Length == 0) file = "profile";
        // Windows device names (CON, NUL, COM1 ..., with or without an extension) would write to the device, not a file
        var stem = file.Split('.')[0].TrimEnd(' ');
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException($"\"{p.Name.Trim()}\" is a Windows device name and cannot be a file name; choose another name");
        var path = System.IO.Path.Combine(userDir, file + ".json");
        // another profile that sanitises to the same file name (A/B and A_B, or case only) must not be overwritten silently
        if (File.Exists(path))
        {
            string? other = null;
            try { other = Parse(File.ReadAllText(path, Encoding.UTF8), path).Profile?.Name; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (other != null && !string.Equals(other, p.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"the file name is already used by the profile \"{other}\"; choose another name");
        }
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, ToJson(p), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        p.Path = path;
        p.BuiltIn = false;
        return path;
    }

    /// <summary>Deletes a user profile's file. Built-in profiles cannot be deleted.</summary>
    public static void Delete(Profile p)
    {
        if (p.BuiltIn || p.Path == null) throw new InvalidOperationException("built-in profiles are read-only; only your own profiles can be deleted");
        File.Delete(p.Path);
    }

    /// <summary>The profile as JSON: name, version, class, use, description, select hint, pixel target, then the passes with
    /// their values in the registered order of the keys.</summary>
    public static string ToJson(Profile p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteNumber("version", p.Version);
            w.WriteString("class", p.Class);
            w.WriteString("use", p.Use);
            w.WriteString("description", p.Description);
            w.WriteString("select_hint", p.SelectHint);
            if (p.PixelError is double pe) w.WriteNumber("pixel_error", pe);
            w.WriteStartObject("passes");
            foreach (var (pass, values) in p.Passes)
            {
                w.WriteStartObject(pass);
                var order = Optimizer.Passes.TryGetValue(pass, out var spec) ? spec.Params.Select(x => x.Key).ToList() : new List<string>();
                foreach (var (k, v) in values.OrderBy(kv => order.IndexOf(kv.Key) is var i && i >= 0 ? i : int.MaxValue))
                {
                    w.WritePropertyName(k);
                    switch (v)
                    {
                        case bool b: w.WriteBooleanValue(b); break;
                        case int i: w.WriteNumberValue(i); break;
                        case long l: w.WriteNumberValue(l); break;
                        case double d: w.WriteNumberValue(d); break;
                        case float f: w.WriteNumberValue(f); break;
                        default: w.WriteStringValue(Convert.ToString(v, Inv)); break;
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>Applies --profile / --set in argument order: a --set after a --profile overrides it, one before it is
    /// overridden when the profile ticks that pass. The passes are the union of the profiles' passes in pipeline order.
    /// An unknown profile or a malformed --set throws <see cref="ArgumentException"/>.</summary>
    public static ComposedRun Compose(IReadOnlyList<(string Kind, string Value)> ops, Func<string, object> parseValue, string? userDir = null)
    {
        var passes = new List<string>();
        var prms = new Dictionary<string, object>();
        var tags = new List<string>();
        var warnings = new List<string>();
        foreach (var (kind, value) in ops)
        {
            if (kind == "profile")
            {
                var p = Find(value, userDir, warnings) ?? throw new ArgumentException($"profile '{value}' not found (see --list-profiles)");
                var ticked = new HashSet<string>();
                Apply(p, ticked, prms, warnings);
                foreach (var pass in p.Passes.Keys) if (ticked.Contains(pass) && !passes.Contains(pass)) passes.Add(pass);
                tags.Add(p.Tag);
            }
            else if (kind == "set")
            {
                int eq = value.IndexOf('=');
                if (eq < 0) throw new ArgumentException($"--set needs PASS.KEY=VALUE, got '{value}'");
                prms[value[..eq].Trim()] = parseValue(value[(eq + 1)..].Trim());
            }
            else throw new ArgumentException($"unknown operation '{kind}' (profile or set)");
        }
        return new ComposedRun(Optimizer.PipelineSort(passes), prms, tags.Count == 0 ? null : string.Join(" + ", tags), warnings.Distinct().ToList());
    }
}
