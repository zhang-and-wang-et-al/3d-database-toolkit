using System.Text;

namespace SceneKit;

public sealed class SaveCopyOptions
{
    public bool IncludeUnchanged = true;
    public string? MasterName;                  // file name of the master in the copy; null = source name (native extension)
    public string? Osgconv;
    public Action<string> Progress = _ => { };
    public IReadOnlyList<string>? HistoryLines; // "passes run this session" for the manifest (window: history entries)
    public Action<string, string>? CopyFile;    // (src, dst): every byte copy goes through it; null = File.Copy(src, dst, true)
}

/// <summary>The written master, every file of the copy (full path, kind: "changed" | "copied" | "generated" | "external")
/// and the manifest.</summary>
public sealed record SaveCopyResult(string Master, IReadOnlyList<(string path, string kind)> Files, string Manifest);

/// <summary>The folder of the opened database (the real one when the database is layered).</summary>
public static class DatabaseRoot
{
    static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    static string Dir(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    public static string Of(Scene scene) =>
        Dir(scene.Layers is { Count: > 0 } layers ? layers[^1] : Path.GetDirectoryName(Path.GetFullPath(scene.ConvertedFrom ?? scene.Path))!);

    internal static bool Same(string a, string b) => string.Equals(Dir(a), Dir(b), Cmp);

    /// <summary>True when <paramref name="path"/> lies below the folder <paramref name="dir"/> (not the folder itself).</summary>
    internal static bool Under(string dir, string path)
    {
        string d = Dir(dir);
        if (!Path.EndsInDirectorySeparator(d)) d += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(d, Cmp);
    }

    /// <summary>A path under a non-root layer becomes the same relative path under the root; any other path is returned as is.</summary>
    public static string BasePath(Scene scene, string path)
    {
        if (scene.Layers is not { Count: > 1 } layers) return path;
        foreach (var layer in layers.Take(layers.Count - 1))
            if (Under(layer, path)) return Path.Combine(Of(scene), Path.GetRelativePath(Dir(layer), Path.GetFullPath(path)));
        return path;
    }

    /// <summary>A loaded external file must be written when it was modified or was read from a non-root layer.
    /// Unmodified files outside the database folder are not changed (Save Copy carries them as "_external" copies).</summary>
    public static bool IsChanged(Scene scene, Scene sub) =>
        SceneIO.SceneModified(sub) || (scene.Layers is { Count: > 1 } layers && layers.Take(layers.Count - 1).Any(l => Under(l, sub.Path)));

    /// <summary>The database's master file (full path), also for a scene restored from a checkpoint (whose master lies in the
    /// checkpoint folder and may have another extension, see <see cref="Scene.Origin"/>).</summary>
    public static string Master(Scene scene) => Path.GetFullPath(scene.Origin ?? BasePath(scene, scene.ConvertedFrom ?? scene.Path));

    /// <summary>Files referenced by the scene and its loaded external files (external files, loaded or not; texture images and
    /// their companions), as resolved; <c>resized</c>: the image is written downscaled, the file itself is not referenced.
    /// Baked and embedded images are left out.</summary>
    internal static IEnumerable<(string path, bool resized)> References(Scene scene)
    {
        foreach (var sc in scene.Externals.Values.Prepend(scene))
        {
            if (sc?.Root == null) continue;
            foreach (var n in sc.IterNodes())
            {
                foreach (var en in n.ExternalNodes) if (en.External != null) yield return (en.External, false);
                if (n.StateSet == null) continue;
                foreach (var t in n.StateSet.Textures)
                {
                    if (t.Embedded || t.Baked != null) continue;
                    if (t.Path != null) yield return (t.Path, t.Resize != null);
                    foreach (var c in t.Companions?.Values ?? Enumerable.Empty<Texture>())
                        if (c.Path != null) yield return (c.Path, t.Resize != null);
                }
            }
        }
    }

    /// <summary>Layered scene: existing files the scene references that were found in a non-root layer and are not loaded
    /// scenes (images, their companions and ".attr" files, external files not loaded; e.g. a checkpoint's "&lt;stem&gt;_textures"
    /// or "_external" copies), each once. They belong at <see cref="BasePath"/> under the root.</summary>
    internal static List<string> LayerReferences(Scene scene)
    {
        var o = new List<string>();
        if (scene.Layers is not { Count: > 1 }) return o;
        var loaded = new HashSet<string>(scene.Externals.Where(kv => kv.Value?.Root != null).Select(kv => Path.GetFullPath(kv.Key)), PathComparer);
        var seen = new HashSet<string>(PathComparer);
        foreach (var (raw, resized) in References(scene))
        {
            if (resized || !File.Exists(raw)) continue;
            string p = Path.GetFullPath(raw);
            if (loaded.Contains(p) || PathComparer.Equals(Path.GetFullPath(BasePath(scene, p)), p) || !seen.Add(p)) continue;
            o.Add(p);
            if (File.Exists(p + ".attr") && seen.Add(p + ".attr")) o.Add(p + ".attr");
        }
        return o;
    }
}

/// <summary>Save Copy to Folder: a self-contained copy of the database in another folder. Unchanged files are copied
/// byte for byte, changed files are written, references stay relative inside the copy; files outside the database folder
/// go to "_external/" (their own references are not followed). The opened database is never written.</summary>
public static class SaveCopy
{
    public const string ManifestName = "SAVE_MANIFEST.txt";

    /// <summary>"&lt;parent of the database folder&gt;/&lt;its name&gt;_optimized"; for a database at a drive root (no parent)
    /// "&lt;temp&gt;/database_optimized".</summary>
    public static string DefaultFolder(Scene scene)
    {
        string root = DatabaseRoot.Of(scene);
        string? parent = Path.GetDirectoryName(root);
        return parent == null ? Path.Combine(Path.GetTempPath(), "database_optimized") : Path.Combine(parent, Path.GetFileName(root) + "_optimized");
    }

    static bool Skipped(string rel)
    {
        string top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return top.StartsWith("_backup_", StringComparison.OrdinalIgnoreCase) || top.EndsWith("_optimized", StringComparison.OrdinalIgnoreCase);
    }

    public static SaveCopyResult ToFolder(Scene scene, string folder, SaveCopyOptions? options = null)
    {
        var opt = options ?? new SaveCopyOptions();
        string root = DatabaseRoot.Of(scene);
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (DatabaseRoot.Same(folder, root) || DatabaseRoot.Under(root, folder) || DatabaseRoot.Under(folder, root))
            throw new ArgumentException("the copy must go outside the opened database folder", nameof(folder));
        // referenced files outside the root, collected before writing (the writers repoint baked textures at their output);
        // the copy must not land on top of them
        var outside = OutsideReferences(scene, root);
        if (outside.Any(p => DatabaseRoot.Under(folder, p)))
            throw new ArgumentException("the folder holds files the database references; choose another folder", nameof(folder));
        Directory.CreateDirectory(folder);

        var externalNames = new Dictionary<string, string>(DatabaseRoot.PathComparer);   // source outside the root -> name in _external
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Dest(string p)
        {
            p = Path.GetFullPath(p);
            // already in the copy: what the writers just generated there (baked / downscaled textures in "<stem>_textures")
            if (DatabaseRoot.Under(folder, p)) return p;
            p = DatabaseRoot.BasePath(scene, p);
            if (DatabaseRoot.Under(root, p)) return Path.Combine(folder, Path.GetRelativePath(root, p));
            if (!externalNames.TryGetValue(p, out var name))
            {
                string stem = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
                name = stem + ext;
                for (int i = 2; !usedNames.Add(name); i++) name = $"{stem}_{i}{ext}";
                externalNames[p] = name;
            }
            return Path.Combine(folder, "_external", name);
        }

        var kinds = new Dictionary<string, string>(DatabaseRoot.PathComparer);   // file in the copy -> kind
        void Copy(string src, string dst, string kind)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (opt.CopyFile != null) opt.CopyFile(src, dst);
            else File.Copy(src, dst, true);
            kinds[dst] = kind;
        }

        // 1. the unchanged database: the root, then the other layers (the layer the master was read from last, so it wins)
        if (opt.IncludeUnchanged)
            foreach (var layer in new[] { root }.Concat(Enumerable.Reverse(scene.Layers?.SkipLast(1).ToList() ?? new List<string>())))
            {
                var files = Directory.EnumerateFiles(layer, "*", SearchOption.AllDirectories).Where(f => !Skipped(Path.GetRelativePath(layer, f))).ToList();
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 200 == 0) opt.Progress($"copying {i + 1} of {files.Count} files …");
                    Copy(files[i], Dest(files[i]), "copied");
                }
            }

        // 2. the files to write: changed ones, those outside the root, and every file that references one
        var plan = new Dictionary<string, (string src, Scene sub, string target)>();
        foreach (var (src, sub) in SceneIO.ChangedExternals(scene, sub => DatabaseRoot.IsChanged(scene, sub)))
        {
            string target = Dest(src), ext = OsgWrite.NativeExt(sub);
            if (!string.Equals(Path.GetExtension(target), ext, StringComparison.OrdinalIgnoreCase)) target = Path.ChangeExtension(target, ext);
            plan[SceneIO.Key(src)] = (src, sub, target);
        }

        // 3. referenced files outside the root
        opt.Progress("copying files from outside the database …");
        foreach (var p in outside)
        {
            if (plan.ContainsKey(SceneIO.Key(p))) continue;
            string dst = Dest(p);
            Copy(p, dst, "external");
            if (File.Exists(p + ".attr")) Copy(p + ".attr", dst + ".attr", "external");
        }

        // 3b. without the unchanged files no layer is mirrored: referenced files that exist only in a non-root layer (a
        // restored checkpoint's "<stem>_textures" or "_external" copies) go to their place under the copy's root
        if (!opt.IncludeUnchanged)
            foreach (var p in DatabaseRoot.LayerReferences(scene))
                if (!plan.ContainsKey(SceneIO.Key(p))) Copy(p, Dest(p), "copied");

        // 4. write the changed files and the master
        opt.Progress($"writing {plan.Count + 1} changed file(s) …");
        string masterPath = Path.Combine(folder, opt.MasterName ?? Path.GetFileNameWithoutExtension(scene.Path) + OsgWrite.NativeExt(scene));
        SceneIO.WriteFiles(scene, masterPath, plan, opt.Osgconv, Dest);
        string master = Path.GetFullPath(masterPath);
        foreach (var (_, _, target) in plan.Values) kinds[Path.GetFullPath(target)] = "changed";
        kinds[master] = "changed";

        // 5. what the writers created besides (downscaled / baked textures) and the manifest
        string manifest = Path.Combine(folder, ManifestName);
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            if (!kinds.ContainsKey(f) && !DatabaseRoot.PathComparer.Equals(f, manifest)) kinds[f] = "generated";
        opt.Progress($"writing {ManifestName} …");
        var list = kinds.Select(kv => (path: kv.Key, kind: kv.Value, rel: Path.GetRelativePath(folder, kv.Key).Replace('\\', '/')))
            .OrderBy(x => x.rel, StringComparer.Ordinal).ToList();
        File.WriteAllText(manifest, Manifest(scene, opt, list.Select(x => (x.rel, x.kind))));
        return new SaveCopyResult(master, list.Select(x => (x.path, x.kind)).ToList(), manifest);
    }

    /// <summary>Existing files referenced by the scene or its loaded external files that lie outside the root: external
    /// files (loaded or not), texture images and their companions, each once.</summary>
    static List<string> OutsideReferences(Scene scene, string root)
    {
        var seen = new HashSet<string>(DatabaseRoot.PathComparer);
        var o = new List<string>();
        foreach (var (raw, _) in DatabaseRoot.References(scene))
        {
            if (!File.Exists(raw)) continue;
            string p = Path.GetFullPath(raw);
            if (!DatabaseRoot.Under(root, DatabaseRoot.BasePath(scene, p)) && seen.Add(p)) o.Add(p);
        }
        return o;
    }

    static string Manifest(Scene scene, SaveCopyOptions opt, IEnumerable<(string rel, string kind)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Source: {scene.Path}");
        sb.AppendLine($"Saved: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Include unchanged files: {(opt.IncludeUnchanged ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("Passes run this session:");
        if (opt.HistoryLines is { Count: > 0 } history) foreach (var l in history) sb.AppendLine(l);
        else sb.AppendLine("(none recorded)");
        sb.AppendLine();
        sb.AppendLine("Files:");
        foreach (var (rel, kind) in files) sb.AppendLine($"{kind,-9} {rel}");
        if (!opt.IncludeUnchanged)
        {
            sb.AppendLine();
            sb.AppendLine("Unchanged files are not included. Copy this folder over a copy of the original database before loading it.");
        }
        return sb.ToString();
    }
}

/// <summary>Files Save Over will overwrite (full paths), the folder their backup goes to (not created yet), the existing
/// "&lt;stem&gt;_textures" folders next to them, the files it copies into the root from a restored checkpoint's layers
/// (source, target) and the changed external files it cannot write in place (another format than the native one).</summary>
public sealed record SaveOverPlan(IReadOnlyList<string> Files, string BackupFolder, IReadOnlyList<string> Folders,
    IReadOnlyList<(string src, string dst)> Copies, IReadOnlyList<string> Unsupported);

public sealed record SaveOverResult(string BackupFolder, IReadOnlyList<string> Written);

/// <summary>Save Over: overwrite the opened database in place, but only after a copy of every file it touches (and of the
/// master) went to "&lt;database folder&gt;/_backup_&lt;yyyyMMdd_HHmmss&gt;/" (same relative layout). Changed externals whose format
/// differs from their native one cannot be written in place: the plan lists them and <see cref="Run"/> refuses.</summary>
public static class SaveOver
{
    public static SaveOverPlan Plan(Scene scene)
    {
        string root = DatabaseRoot.Of(scene), master = DatabaseRoot.Master(scene);
        var files = new List<string>();
        var unsupported = new List<string>();
        if (DatabaseRoot.IsChanged(scene, scene) && DatabaseRoot.Under(root, master)) files.Add(master);
        foreach (var (src, sub) in SceneIO.ChangedExternals(scene, s => DatabaseRoot.IsChanged(scene, s)))
        {
            string target = Path.GetFullPath(DatabaseRoot.BasePath(scene, src));
            if (!DatabaseRoot.Under(root, target)) continue;
            if (string.Equals(Path.GetExtension(src), OsgWrite.NativeExt(sub), StringComparison.OrdinalIgnoreCase)) files.Add(target);
            else if (DatabaseRoot.IsChanged(scene, sub)) unsupported.Add(target);   // an unchanged referrer needs no rewrite in place
        }
        var folders = files.Select(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f) + "_textures"))
            .Where(Directory.Exists).Distinct(DatabaseRoot.PathComparer).ToList();

        // a restored checkpoint: the files the written ones reference that exist only in its layers go into the root
        var copies = new List<(string, string)>();
        var targets = new HashSet<string>(files, DatabaseRoot.PathComparer);
        foreach (var p in DatabaseRoot.LayerReferences(scene))
        {
            string dst = Path.GetFullPath(DatabaseRoot.BasePath(scene, p));
            if (DatabaseRoot.Under(root, dst) && targets.Add(dst)) copies.Add((p, dst));
        }

        string backup = Path.Combine(root, "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        string free = backup;
        for (int i = 2; Directory.Exists(free); i++) free = $"{backup}_{i}";
        return new SaveOverPlan(files, free, folders, copies, unsupported);
    }

    /// <summary>Why <see cref="Run"/> refuses a plan with <see cref="SaveOverPlan.Unsupported"/> files.</summary>
    public static string UnsupportedMessage(SaveOverPlan plan) =>
        $"{plan.Unsupported.Count} changed external file(s) in another format cannot be written in place: " +
        string.Join(", ", plan.Unsupported.Select(Path.GetFileName)) + ". Use Save Copy to Folder instead.";

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public static SaveOverResult Run(Scene scene, SaveOverPlan plan, string? osgconv = null, Action<string>? progress = null)
    {
        if (plan.Unsupported.Count > 0) throw new NotSupportedException(UnsupportedMessage(plan));
        if (plan.Files.Count == 0 && plan.Folders.Count == 0 && plan.Copies.Count == 0)
            throw new InvalidOperationException("nothing to save: the scene has no changes");
        string root = DatabaseRoot.Of(scene), backup = plan.BackupFolder;
        string masterTarget = DatabaseRoot.Master(scene);

        progress?.Invoke($"backing up to {backup} …");
        Directory.CreateDirectory(backup);
        void Backup(string f)
        {
            if (!File.Exists(f)) return;
            string dst = Path.Combine(backup, Path.GetRelativePath(root, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
        foreach (var f in plan.Files) Backup(f);
        foreach (var (_, dst) in plan.Copies) Backup(dst);
        foreach (var d in plan.Folders) CopyDir(d, Path.Combine(backup, Path.GetRelativePath(root, d)));
        // the master always (also when only other files change): the "before this session's save over" entry loads it, so its
        // references resolve to the backed-up files first
        if (DatabaseRoot.Under(root, masterTarget)) Backup(masterTarget);

        // the write plan: the changed externals, in place; the master only when it is in plan.Files
        var files = new HashSet<string>(plan.Files, DatabaseRoot.PathComparer);
        var write = new Dictionary<string, (string src, Scene sub, string target)>();
        foreach (var (src, sub) in SceneIO.ChangedExternals(scene, s => DatabaseRoot.IsChanged(scene, s)))
        {
            string target = Path.GetFullPath(DatabaseRoot.BasePath(scene, src));
            if (files.Contains(target)) write[SceneIO.Key(src)] = (src, sub, target);
        }
        string? master = files.Contains(masterTarget) ? masterTarget : null;
        // a restored (layered) scene: references are written relative to the database, not to the checkpoint folder
        Func<string, string>? dest = scene.Layers != null ? p => DatabaseRoot.BasePath(scene, p) : null;

        progress?.Invoke($"writing {plan.Files.Count} file(s) …");
        var written = new List<string>();
        try
        {
            foreach (var (src, dst) in plan.Copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
            }
            SceneIO.WriteFiles(scene, master, write, osgconv, dest);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            int n = (scene.SavedExternals?.Count ?? 0);
            throw new IOException($"{exc.Message}. Files already backed up to {backup}; {n} of {plan.Files.Count} written", exc);
        }
        written.AddRange(plan.Copies.Select(c => c.dst));
        written.AddRange(scene.SavedExternals ?? new List<string>());
        if (master != null) written.Add(master);
        return new SaveOverResult(backup, written);
    }
}
