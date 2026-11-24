using System.Diagnostics;

namespace SceneKit;

public sealed class LoadOptions
{
    public bool FollowProxy = true;     // load ProxyNode / FLT external references
    public bool FollowPaged;            // load PagedLOD child files (can be thousands)
    public int MaxDepth = 64;           // nesting of external files (a file referenced again is loaded once)
    public int MaxFiles = 100_000;
    public string? Osgconv;             // path of osgconv, "" to disable, null to look on PATH
    public Action<string> Progress = _ => { };
    public List<string>? CompanionPatterns;   // naming rules of companion images (null: Companions.DefaultPatterns)
    /// <summary>Ordered roots of a layered load (full paths): the master is under <c>Layers[0]</c>, <c>Layers[^1]</c> is the real
    /// database folder. A reference is looked up under every layer in order (same relative folder), first hit wins.</summary>
    public List<string>? Layers;
    /// <summary>Layered load: the database's master file the loaded state stands for (e.g. "db/master.ive" for a checkpoint
    /// whose master is "master.osgt"); null: the master's place under the database folder.</summary>
    public string? Origin;
}

/// <summary>Load / save scenes (OSG text natively, other OSG formats through osgconv) and their external files.</summary>
public static class SceneIO
{
    public static readonly HashSet<string> NativeExts = new() { ".osgt", ".osg", ".flt" };
    public static readonly HashSet<string> SaveNative = new() { ".osgt", ".osg" };

    /// <summary>The OpenFlight reader (replaceable, e.g. for tests).</summary>
    public static Func<string, Scene>? FltReader = FltRead.Read;

    public static string? FindOsgconv(string? hint = null)
    {
        if (hint == "") return null;
        foreach (var cand in new[] { hint, Environment.GetEnvironmentVariable("OSGCONV_PATH"), "osgconv" })
        {
            if (string.IsNullOrEmpty(cand)) continue;
            if (File.Exists(cand)) return cand;
            var found = Which(cand);
            if (found != null) return found;
        }
        return null;
    }

    static string? Which(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".bat", ".cmd", "" } : new[] { "" };
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
            foreach (var e in exts)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var p = System.IO.Path.Combine(dir, exe + e);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    public static string OsgconvConvert(string exe, string src, string dst, int timeoutSec = 600)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(src);
        psi.ArgumentList.Add(dst);
        psi.Environment["OSG_OPTIMIZER"] = "OFF";
        if (!psi.Environment.ContainsKey("OSG_NOTIFY_LEVEL")) psi.Environment["OSG_NOTIFY_LEVEL"] = "WARN";
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } throw new TimeoutException("osgconv timed out"); }
        string output = (outTask.Result + errTask.Result).Trim();
        if (p.ExitCode != 0 || !File.Exists(dst) || new FileInfo(dst).Length == 0)
            throw new InvalidOperationException($"osgconv failed ({p.ExitCode}): {(output.Length > 400 ? output[^400..] : output)}");
        return output;
    }

    static Scene LoadFile(string path, LoadOptions opts)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".osgt" or ".osg")
        {
            opts.Progress($"parsing {System.IO.Path.GetFileName(path)} ({new FileInfo(path).Length / 1e6:N0} MB) ...");
            var doc = OsgParser.ParseFile(path, null, opts.Progress);
            var scene = new Scene(path, doc.Dialect);
            opts.Progress("building the scene ...");
            OsgReader.Read(doc, scene, opts.Progress);
            if (doc.Dialect == "osgt" && doc.Version != 0 && doc.Version < 147)
                scene.Warnings.Add($"old .osgt file version {doc.Version}: saving writes the current format (version 161)");
            return scene;
        }
        if (ext == ".flt")
        {
            if (FltReader == null) throw new NotSupportedException("OpenFlight reading is not available in this build");
            return FltReader(path);
        }
        var exe = FindOsgconv(opts.Osgconv) ?? throw new NotSupportedException(
            $"No built-in reader for '{ext}'. Install OpenSceneGraph (osgconv) to inspect this format.");
        string tmpdir = Directory.CreateTempSubdirectory("scenekit_").FullName;
        Scene sc;
        try
        {
            string tmp = System.IO.Path.Combine(tmpdir, System.IO.Path.GetFileNameWithoutExtension(path) + ".osgt");
            opts.Progress($"converting {System.IO.Path.GetFileName(path)} with osgconv ...");
            OsgconvConvert(exe, path, tmp);
            sc = LoadFile(tmp, opts);
        }
        finally { try { Directory.Delete(tmpdir, true); } catch (IOException) { } }
        sc.ConvertedFrom = path;
        sc.Path = path;
        sc.Fmt = ext.TrimStart('.');
        sc.Warnings.Insert(0, $"read through osgconv ({System.IO.Path.GetFileName(exe)}); textures are resolved relative to the original file");
        foreach (var n in sc.IterNodes())
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (!t.Embedded && !t.Exists) Images.FillTexture(t, System.IO.Path.GetDirectoryName(path)!);
        return sc;
    }

    /// <summary>Load a scene file and (optionally) its external references.</summary>
    public static Scene Load(string path, LoadOptions? options = null)
    {
        var opts = options ?? new LoadOptions();
        path = System.IO.Path.GetFullPath(path);
        opts.Progress($"reading {System.IO.Path.GetFileName(path)} ...");
        var scene = LoadFile(path, opts);
        if (opts.Layers is { Count: > 0 })
        {
            scene.Layers = opts.Layers.Select(l => System.IO.Path.GetFullPath(l)).ToList();
            scene.Origin = System.IO.Path.GetFullPath(opts.Origin ?? System.IO.Path.Combine(scene.Layers[^1],
                System.IO.Path.GetRelativePath(scene.Layers[0], scene.ConvertedFrom ?? scene.Path)));
            FillLayeredTextures(scene, scene.Layers);
        }
        LoadExternals(scene, opts);
        if (opts.CompanionPatterns != null) scene.CompanionPatterns = opts.CompanionPatterns.Where(p => Companions.Check(p) == null).ToList();
        Companions.Detect(scene);   // IR / sensor images next to the textures (tree_ir.rgb next to tree.rgb)
        return scene;
    }

    internal static string Key(string p) => OperatingSystem.IsWindows() ? p.ToLowerInvariant() : p;

    /// <summary>Layered load: the folders a reference from <paramref name="file"/> is looked up in after the file's own folder —
    /// the same relative folder under every layer, in layer order (the layer of the file is the first one it lies under).</summary>
    static List<string> LayerDirs(IReadOnlyList<string>? layers, string file)
    {
        var dirs = new List<string>();
        if (layers is not { Count: > 0 }) return dirs;
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!;
        var own = layers.FirstOrDefault(l => DatabaseRoot.Under(l, file));
        if (own == null) return dirs;
        string rel = System.IO.Path.GetRelativePath(own, dir);
        foreach (var l in layers)
        {
            string d = System.IO.Path.GetFullPath(System.IO.Path.Combine(l, rel));
            if (!DatabaseRoot.Same(d, dir)) dirs.Add(d);
        }
        return dirs;
    }

    /// <summary>Layered load: textures the reader did not find next to the file are looked up through the layers.</summary>
    static void FillLayeredTextures(Scene sc, IReadOnlyList<string>? layers)
    {
        if (layers is not { Count: > 0 } || sc.Root == null) return;
        string file = sc.ConvertedFrom ?? sc.Path;
        var dirs = LayerDirs(layers, file);
        if (dirs.Count == 0) return;
        foreach (var n in sc.IterNodes())
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (!t.Embedded && !t.Exists) Images.FillTexture(t, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!, dirs);
    }

    static void LoadExternals(Scene scene, LoadOptions opts)
    {
        var cache = new Dictionary<string, Scene?> { [Key(scene.Path)] = scene };
        int loaded = 0;
        void Visit(Scene sc, int depth)
        {
            var search = LayerDirs(scene.Layers, sc.Path).Append(System.IO.Path.GetDirectoryName(scene.Path)!).ToList();
            foreach (var n in sc.IterNodes().ToList())
            {
                if (n.Files.Count == 0) continue;
                bool follow = n.Kind == Kind.PagedLod ? opts.FollowPaged : opts.FollowProxy;
                var extNodes = new List<Node>();
                foreach (var f in n.Files)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    string baseDir = System.IO.Path.GetDirectoryName(sc.Path)!;
                    var p = Images.Resolve(f, baseDir, search);   // relative to the file, then through the layers, else to the master file
                    var en = new Node(Kind.External, "external file", System.IO.Path.GetFileName(f.Replace('\\', '/'))) { External = p ?? f };
                    if (p == null)
                    {
                        en.Notes.Add("file not found");
                        string msg = ReferenceEquals(sc, scene) ? $"external file not found: {f}" : $"external file not found: {f} (referenced from {System.IO.Path.GetFileName(sc.Path)})";
                        if (!scene.Warnings.Contains(msg)) scene.Warnings.Add(msg);   // the window shows the main scene's warnings
                    }
                    else if (follow && cache.TryGetValue(Key(p), out var known) && known?.Root != null)
                        en.Children = new List<Node> { known.Root };   // already loaded (from another reference): share it
                    else if (follow && depth < opts.MaxDepth && loaded < opts.MaxFiles)
                    {
                        string key = Key(p);
                        cache.TryGetValue(key, out var sub);
                        if (sub == null && !cache.ContainsKey(key))
                        {
                            cache[key] = null;
                            try
                            {
                                opts.Progress($"reading external {System.IO.Path.GetFileName(p)} ...");
                                sub = LoadFile(p, opts);
                                FillLayeredTextures(sub, scene.Layers);
                                loaded++;
                                cache[key] = sub;
                                scene.Externals[p] = sub;
                                Visit(sub, depth + 1);
                            }
                            catch (Exception exc)
                            {
                                en.Notes.Add($"could not be read: {exc.Message}");
                                scene.Externals[p] = null;
                            }
                        }
                        if (sub?.Root != null) en.Children = new List<Node> { sub.Root };
                    }
                    else
                    {
                        en.Notes.Add(n.Kind == Kind.PagedLod ? "not loaded (paged child)" : !follow ? "not loaded" : "not loaded (nesting limit)");
                        if (follow && depth >= opts.MaxDepth && !scene.Warnings.Contains($"external files nested deeper than {opts.MaxDepth} levels were not loaded"))
                            scene.Warnings.Add($"external files nested deeper than {opts.MaxDepth} levels were not loaded");
                    }
                    extNodes.Add(en);
                }
                if (extNodes.Count > 0) n.Externals = extNodes;
            }
        }
        Visit(scene, 0);
        if (loaded >= opts.MaxFiles) scene.Warnings.Add($"stopped loading external files after {opts.MaxFiles}");
    }

    static IEnumerable<Node> OwnNodes(Scene sc) => sc.Root != null ? sc.IterNodes() : Enumerable.Empty<Node>();

    /// <summary>True when the optimiser / editor changed anything in this file (not its external files).</summary>
    public static bool SceneModified(Scene sc)
    {
        foreach (var n in OwnNodes(sc))
        {
            if (n.Dirty.Count > 0) return true;
            if (n.StateSet != null && n.StateSet.Textures.Any(t => t.Resize != null || t.Baked != null || t.Repointed || t.SamplingEdited)) return true;
        }
        return false;
    }

    static HashSet<string> Referenced(Scene sc) =>
        OwnNodes(sc).SelectMany(n => n.ExternalNodes).Where(en => en.External != null).Select(en => Key(en.External!)).ToHashSet();

    /// <summary>Loaded external files that must be written as copies: changed ones and every file that references one.</summary>
    public static Dictionary<string, (string src, Scene sub, string target)> PlanExternalCopies(Scene scene, string outPath, bool overwrite = false)
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!,
            System.IO.Path.GetFileNameWithoutExtension(outPath) + "_files");
        var plan = new Dictionary<string, (string, Scene, string)>();
        var used = new HashSet<string>();
        foreach (var (p, sub) in ChangedExternals(scene, SceneModified))
        {
            string k = Key(p);
            string stem = System.IO.Path.GetFileNameWithoutExtension(p), ext = OsgWrite.NativeExt(sub);
            if (overwrite && string.Equals(System.IO.Path.GetExtension(p), ext, StringComparison.OrdinalIgnoreCase))
            {   // same format: replace the original in place, no copy folder
                plan[k] = (p, sub, System.IO.Path.GetFullPath(p));
                continue;
            }
            string name = stem + ext;
            int i = 2;
            while (used.Contains(name.ToLowerInvariant())) { name = $"{stem}_{i}{ext}"; i++; }
            used.Add(name.ToLowerInvariant());
            plan[k] = (p, sub, System.IO.Path.Combine(folder, name));
        }
        return plan;
    }

    /// <summary>Loaded external files that must be written: those <paramref name="changed"/> selects and every file that
    /// references one (directly or through other files), ordered by path.</summary>
    internal static List<(string src, Scene sub)> ChangedExternals(Scene scene, Func<Scene, bool> changed)
    {
        var subs = new Dictionary<string, (string, Scene)>();
        foreach (var (p, sub) in scene.Externals)
            if (sub?.Root != null) subs.TryAdd(Key(p), (p, sub));
        var hit = subs.Where(kv => changed(kv.Value.Item2)).Select(kv => kv.Key).ToHashSet();
        var refs = subs.ToDictionary(kv => kv.Key, kv => Referenced(kv.Value.Item2));
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var k in subs.Keys)
                if (!hit.Contains(k) && refs[k].Overlaps(hit)) { hit.Add(k); grew = true; }
        }
        return hit.OrderBy(x => x, StringComparer.Ordinal).Select(k => subs[k]).ToList();
    }

    static List<Node> PointReferences(Scene sc, string scOut, Dictionary<string, (string src, Scene sub, string target)> plan)
    {
        var touched = new List<Node>();
        string baseDir = System.IO.Path.GetDirectoryName(sc.Path)!;
        foreach (var n in OwnNodes(sc))
        {
            if (n.Files.Count == 0 || n.ExternalNodes.Count == 0) continue;
            var mapping = new Dictionary<string, string>();
            foreach (var f in n.Files)
            {
                var p = string.IsNullOrEmpty(f) ? null : Images.Resolve(f, baseDir);
                if (p != null && plan.TryGetValue(Key(p), out var hit))
                    mapping[f] = OsgWriterBase.RelPath(hit.target, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(scOut))!);
            }
            if (mapping.Count > 0) { n.FilesOut = mapping; touched.Add(n); }
        }
        return touched;
    }

    /// <summary>Write the scene; changed external files go to "&lt;output&gt;_files/" as copies,
    /// or with <paramref name="overwriteExternals"/> replace the original files in place.</summary>
    public static string Save(Scene scene, string path, string? osgconv = null, bool externals = true, bool overwriteExternals = false)
    {
        var plan = externals ? PlanExternalCopies(scene, path, overwriteExternals) : new();
        return WriteFiles(scene, path, plan, osgconv, null) ?? path;
    }

    /// <summary>Write the planned external files and then the scene to <paramref name="masterOut"/>, references pointing at
    /// the planned targets; <paramref name="dest"/> maps every other referenced file to its place in the output (Save Copy).</summary>
    internal static string? WriteFiles(Scene scene, string? masterOut, Dictionary<string, (string src, Scene sub, string target)> plan,
        string? osgconv, Func<string, string>? dest)
    {
        var touched = new List<Node>();
        scene.SavedExternals = new List<string>();
        try
        {
            if (masterOut != null) touched.AddRange(PointReferences(scene, masterOut, plan));
            foreach (var (_, sub, target) in plan.Values) touched.AddRange(PointReferences(sub, target, plan));
            foreach (var (_, sub, target) in plan.Values)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                OsgWrite.Write(sub, target, dest);
                scene.SavedExternals.Add(target);
            }
            return masterOut == null ? null : SaveMain(scene, masterOut, osgconv, dest);   // null: write only the planned externals (Save Over, master unchanged)
        }
        finally { foreach (var n in touched) n.FilesOut = null; }
    }

    static string SaveMain(Scene scene, string path, string? osgconv, Func<string, string>? dest)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        string native = OsgWrite.NativeExt(scene);
        if (ext == native || (ext == ".osgt" && scene.Doc == null)) return OsgWrite.Write(scene, path, dest);   // OpenFlight scenes can also be saved as .osgt
        var exe = FindOsgconv(osgconv) ?? throw new NotSupportedException($"Saving as '{ext}' needs OpenSceneGraph's osgconv; save as {native} instead.");
        string tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, $".{System.IO.Path.GetFileName(path)}.tmp{native}");
        try
        {
            OsgWrite.Write(scene, tmp, dest);
            OsgconvConvert(exe, tmp, path);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
        return path;
    }
}
