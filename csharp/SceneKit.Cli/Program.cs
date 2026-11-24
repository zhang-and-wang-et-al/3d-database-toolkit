using System.Diagnostics;
using SceneKit;

static int Usage()
{
    Console.Error.WriteLine("""
usage: sceneinspect FILE [--json PATH] [--depth N] [--min-share PCT] [--top N] [--no-externals] [--paged]
                         [--osgconv PATH] [--fast] [-o OUT|FOLDER] [--optimize PASSES|PRESET] [--set PASS.KEY=VALUE]
                         [--profile NAME|PATH]   (repeatable; applied in order with --set; implies --optimize with the profile's passes)
                         [--select NAME[,NAME]] [--keep-externals] [--overwrite-externals] [--in-place] [--timing]
                         [--companions "PATTERN[;PATTERN]"]   (companion images, default "{stem}_Classes.png; {stem}_ir{ext}; {stem}_IR{ext}")
       -o FILE writes one file; -o FOLDER (an existing folder, a trailing / or \, or no extension) writes a self-contained copy of the database into FOLDER
       --in-place backs up every overwritten file to <folder>/_backup_<timestamp>/ first
       sceneinspect --list-passes
       sceneinspect --list-profiles   (built-in and your own profiles in %APPDATA%\SceneInspector\profiles)
       sceneinspect resave IN OUT        (load and save unchanged)
""");
    return 2;
}

static int Run(string[] argv)
{
    if (argv.Length == 0) return Usage();
    ImageResizer.Current ??= new ManagedImageResizer();   // 'downscale_textures' for SGI / TGA / PNG textures
    if (argv[0] == "dom-roundtrip" && argv.Length >= 3)
    {
        var doc = OsgParser.ParseFile(argv[1]);
        using var w = new StreamWriter(argv[2], false, new System.Text.UTF8Encoding(false), 1 << 20);
        new TextOut(doc.Dialect, w).WriteStmts(doc.Stmts, 0);
        return 0;
    }
    if (argv[0] == "render" && argv.Length >= 3)
    {
        // render FILE OUT.ppm [WIDTH HEIGHT] [--paint material|textured|uvchecker] [--focus NAME[,NAME]]: the inspector preview of the whole
        // scene (shaded + wireframe; no wireframe when painted)
        var paint = SoftRender.Paint.Material;
        int pi = Array.IndexOf(argv, "--paint");
        if (pi >= 0 && pi + 1 < argv.Length)
        {
            paint = argv[pi + 1].ToLowerInvariant() switch
            {
                "textured" => SoftRender.Paint.Textured, "uvchecker" => SoftRender.Paint.UvChecker, _ => SoftRender.Paint.Material,
            };
            argv = argv.Take(pi).Concat(argv.Skip(pi + 2)).ToArray();
        }
        // --focus NAME[,NAME]: frame the camera on these nodes (the whole scene is still drawn)
        string[]? focus = null;
        int fi = Array.IndexOf(argv, "--focus");
        if (fi >= 0 && fi + 1 < argv.Length)
        {
            focus = argv[fi + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            argv = argv.Take(fi).Concat(argv.Skip(fi + 2)).ToArray();
        }
        var sc = SceneIO.Load(argv[1]);
        var meshes = SoftRender.Collect(sc.Root!);
        var frame = meshes;
        if (focus != null)
        {
            var parents = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
            foreach (var n in sc.IterNodes())
                foreach (var c in n.AllChildren())
                {
                    if (!parents.TryGetValue(c, out var l)) parents[c] = l = new List<Node>();
                    l.Add(n);
                }
            frame = sc.IterNodes().Where(n => focus.Contains(n.Name))
                .SelectMany(n => SoftRender.GeometryItems(n, start: SoftRender.ParentMatrix(n, parents)))
                .Select(x => SoftRender.MeshFromGeom(x.node.Geom, x.world, x.ss)).OfType<SoftRender.Mesh>().ToList();
            if (frame.Count == 0) { Console.Error.WriteLine($"--focus: no node named {string.Join(", ", focus)}"); return 2; }
        }
        int w = argv.Length > 4 ? int.Parse(argv[3]) : 800, h = argv.Length > 4 ? int.Parse(argv[4]) : 600;
        var sw0 = Stopwatch.StartNew();
        var (img, info) = SoftRender.Render(meshes, SoftRender.Camera.Fit(frame), w, h, wire: paint == SoftRender.Paint.Material, paint: paint);
        Console.Error.WriteLine($"{info.Triangles:N0} triangles, {info.Drawn:N0} drawn, wire {info.Wire}, {sw0.Elapsed.TotalMilliseconds:F0} ms");
        File.WriteAllBytes(argv[2], SoftRender.ToPpm(img, w, h));
        return 0;
    }
    if (argv[0] == "gui-text" && argv.Length >= 2)
    {
        // gui-text FILE: tree row + details text of every node as the inspector window shows them (for tests)
        var sc = SceneIO.Load(argv[1]);
        var grep = Analyzer.Analyze(sc);
        var idx = new InspectorModel.FindingIndex(grep);
        var o = new StringWriter();
        o.Write(InspectorModel.SummaryLine(grep) + "\n");
        foreach (var n in sc.IterNodes(externals: true))
        {
            var r = InspectorModel.RowFor(grep, idx, n);
            o.Write($"ROW {r.Text} | {r.Share} | {r.Tris} | {r.Vis} | {r.Verts} | {r.Draws} | {r.Geo} | {r.Tex} | {r.Inst} | {r.Notes} | {r.Severity}\n");
            o.Write(InspectorModel.Details(grep, idx, n) + "\n");
            o.Write("ORDER " + string.Join(",", InspectorModel.SortedChildren(grep, n, "cost").Select(c => c.Name)) + "\n");
        }
        foreach (var f in grep.Findings) o.Write($"FINDING {f.Title} | {InspectorModel.Saving(f)} | {InspectorModel.FixLabel(f.Fix)}\n{InspectorModel.FindingText(grep, f)}\n");
        Console.Out.Write(o.ToString());
        return 0;
    }
    if (argv[0] == "gui-log" && argv.Length >= 3)
    {
        // gui-log FILE PRESET: the optimisation log of the inspector window (for tests); timings zeroed
        var sc = SceneIO.Load(argv[1]);
        var (results, before, after) = Optimizer.Run(sc, Optimizer.Presets[argv[2]]);
        foreach (var r in results) r.Seconds = 0;
        Console.Out.Write(string.Join("\n", InspectorModel.LogResults(results, before, after)) + "\n");
        return 0;
    }
    if (argv[0] == "resave" && argv.Length >= 3)
    {
        var sc = SceneIO.Load(argv[1]);
        SceneIO.Save(sc, argv[2], externals: false);
        return 0;
    }
    string? file = null, json = null, osgconv = null, output = null;
    int depth = 6, top = 15;
    double minShare = 1.0;
    bool noExt = false, paged = false, fast = false, timing = false, keepExt = false, overwriteExt = false, inPlace = false, listPasses = false, listProfiles = false;
    string? optimize = null;
    string? select = null, companions = null;
    var ops = new List<(string Kind, string Value)>();   // --profile / --set in argument order
    for (int i = 0; i < argv.Length; i++)
    {
        string a = argv[i];
        string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{a} needs a value");
        switch (a)
        {
            case "--json": json = Next(); break;
            case "--depth": depth = int.Parse(Next()); break;
            case "--min-share": minShare = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--top": top = int.Parse(Next()); break;
            case "--no-externals": noExt = true; break;
            case "--paged": paged = true; break;
            case "--osgconv": osgconv = Next(); break;
            case "--fast": fast = true; break;
            case "--timing": timing = true; break;
            case "--optimize": optimize = Next(); break;
            case "--select": select = Next(); break;   // run the passes on the nodes with these names (comma separated)
            case "--set": ops.Add(("set", Next())); break;
            case "--profile": ops.Add(("profile", Next())); break;   // a profile name or a JSON file; a later --set overrides it
            case "--companions": companions = Next(); break;   // naming rules of companion (IR) images: tokens {stem} {ext} {dir}
            case "--keep-externals": keepExt = true; break;
            case "--overwrite-externals": overwriteExt = true; break;
            case "--in-place": inPlace = true; break;   // overwrite the opened database after a _backup_<timestamp> copy
            case "--list-passes": listPasses = true; break;
            case "--list-profiles": listProfiles = true; break;
            case "-o": case "--output": output = Next(); break;
            case "-h": case "--help": return Usage();
            default:
                if (a.StartsWith('-')) { Console.Error.WriteLine($"unknown option {a}"); return Usage(); }
                file = a; break;
        }
    }
    if (listPasses)
    {
        foreach (var spec in Optimizer.Passes.Values)
        {
            Console.Out.Write($"{spec.Name,-20} {spec.Label}{(spec.Safe ? "" : "   (changes the look / structure)")}\n");
            Console.Out.Write($"{"",-20} {spec.Help}\n");
            foreach (var prm in spec.Params)
                Console.Out.Write($"{"",-22}{spec.Name}.{prm.Key}={PyStr(prm.Default)}   {prm.Label}\n");
        }
        Console.Out.Write($"\nscene setting (--companions or --set companion_patterns=...): companion_patterns={Companions.Format(Companions.DefaultPatterns)}   naming rules of companion images (e.g. infrared) next to the textures; tokens {{stem}} {{ext}} {{dir}}, a sub-folder or an extension of their own allowed\n");
        Console.Out.Write("\npresets: " + string.Join("; ", Optimizer.Presets.Select(kv => $"{kv.Key} = {string.Join(",", kv.Value)}")) + "\n");
        return 0;
    }
    if (listProfiles)
    {
        var (profiles, warnings) = Profiles.LoadAll();
        foreach (var p in profiles)
        {
            Console.Out.Write($"{p.Tag,-24} {(p.BuiltIn ? "built-in" : "user"),-9} {p.Class} / {p.Use}\n");
            if (p.SelectHint.Length > 0) Console.Out.Write($"{"",-24} {p.SelectHint}\n");
            if (!p.BuiltIn) Console.Out.Write($"{"",-24} {p.Path}\n");
        }
        Console.Out.Write($"\nyour profiles: {Profiles.UserDir}\n");
        foreach (var w in warnings) Console.Out.Write($"warning: {w}\n");
        return 0;
    }
    if (file == null) return Usage();
    if (inPlace && output != null) { Console.Error.WriteLine("--in-place and -o exclude each other"); return 2; }
    var sw = Stopwatch.StartNew();
    var opts = new LoadOptions { FollowProxy = !noExt, FollowPaged = paged, Osgconv = osgconv, Progress = m => Console.Error.WriteLine(m),
        CompanionPatterns = companions != null ? Companions.Parse(companions) : null };
    Scene scene;
    try { scene = SceneIO.Load(file, opts); }
    catch (Exception exc) { Console.Error.WriteLine($"cannot read {file}: {exc.Message}"); return 1; }
    Console.Error.WriteLine($"loaded in {sw.Elapsed.TotalSeconds:F1}s");
    if (timing) Console.Error.WriteLine($"managed heap after load {GC.GetTotalMemory(true) / 1048576} MB, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");
    sw.Restart();
    var rep = Analyzer.Analyze(scene, deepChecks: !fast);
    if (timing) Console.Error.WriteLine($"analysed in {sw.Elapsed.TotalSeconds:F1}s, managed heap {GC.GetTotalMemory(true) / 1048576} MB");
    Console.Out.Write(ReportText.Text(rep, depth, minShare / 100.0, top));
    Console.Out.Write("\n");
    if (json != null)
    {
        File.WriteAllText(json, ReportText.Json(rep));
        Console.Error.WriteLine($"JSON report: {json}");
    }
    bool withProfile = ops.Any(x => x.Kind == "profile");
    if (optimize != null || withProfile)
    {
        var names = optimize == null ? new List<string>() : Optimizer.Presets.TryGetValue(optimize, out var pre) ? pre.ToList()
            : optimize.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var unknown = names.Where(n => !Optimizer.Passes.ContainsKey(n)).ToList();
        if (unknown.Count > 0) { Console.Error.WriteLine($"unknown pass(es): {string.Join(", ", unknown)} (see --list-passes)"); return 2; }
        ComposedRun composed;
        try { composed = Profiles.Compose(ops, ParseValue); }
        catch (ArgumentException exc) { Console.Error.WriteLine(exc.Message); return 2; }
        foreach (var w in composed.Warnings) Console.Error.WriteLine($"warning: {w}");
        // --profile alone runs the profile's passes; with --optimize both lists run, in pipeline order
        if (withProfile) names = Optimizer.PipelineSort(names.Concat(composed.Passes).Distinct());
        var prms = composed.Params;
        prms["include_externals"] = !keepExt;
        sw.Restart();
        List<Node>? targets = null;
        if (select != null)
        {
            var wanted = new HashSet<string>(select.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
            targets = scene.IterNodes(externals: true).Where(n => wanted.Contains(n.Name)).Distinct().ToList();
            Console.Error.WriteLine($"--select: {targets.Count} node(s)");
            if (targets.Count == 0) { Console.Error.WriteLine("--select matched no node"); return 2; }
        }
        var (results, before, after) = Optimizer.Run(scene, names, prms, targets, progress: m => Console.Error.WriteLine(m));
        if (timing)
        {
            foreach (var r in results) Console.Error.WriteLine($"   {r.Label,-48} {r.Seconds,6:F1}s");
            Console.Error.WriteLine($"optimised in {sw.Elapsed.TotalSeconds:F1}s (with the analysis before and after)");
        }
        var o = Console.Out;
        if (composed.ProfileTag != null) o.Write($"profile: {composed.ProfileTag}\n");
        o.Write("Optimisation:\n");
        foreach (var r in results)
            o.Write($"  {r.Label,-48} {(r.Error != null ? r.Error.Split('\n')[0] : r.Message)}\n");
        o.Write("\n");
        foreach (var (key, label, bytes) in new[] { ("triangles", "Triangles", false), ("visible_triangles", "Visible triangles", false),
                     ("vertices", "Vertices", false), ("draw_calls", "Draw calls", false), ("unique_geometries", "Geometries", false),
                     ("nodes", "Nodes", false), ("geometry_bytes", "Geometry memory", true), ("texture_bytes", "Texture memory", true) })
        {
            double b = Convert.ToDouble(before[key]), a = Convert.ToDouble(after[key]);
            string F(double v) => bytes ? Analyzer.HumanBytes(v) : PyFormat.Int((long)v);
            string change = b != 0 ? PlusPercent((a - b) / b) : "";
            o.Write($"  {label,-20} {F(b),14} -> {F(a),14}  {change}\n");
        }
        sw.Restart();
        if (inPlace) { int rc = SaveInPlace(scene, osgconv); if (rc != 0) return rc; }
        else if (output != null && IsFolderTarget(output)) { int rc = SaveToFolder(scene, output, osgconv); if (rc != 0) return rc; }
        else
        {
            output ??= Optimizer.DefaultOutputPath(scene);
            try { SceneIO.Save(scene, output, osgconv, overwriteExternals: overwriteExt); }
            catch (Exception exc) { Console.Error.WriteLine($"\ncould not save: {exc.Message}"); return 1; }
            o.Write($"\nsaved: {output}\n");
            foreach (var ext in scene.SavedExternals) o.Write($"       {ext}  (optimised copy of an external file)\n");
        }
        if (timing) Console.Error.WriteLine($"saved in {sw.Elapsed.TotalSeconds:F1}s");
    }
    else if (inPlace)
    {
        int rc = SaveInPlace(scene, osgconv);
        if (rc != 0) return rc;
    }
    else if (output != null)
    {
        sw.Restart();
        if (IsFolderTarget(output)) { int rc = SaveToFolder(scene, output, osgconv); if (rc != 0) return rc; }
        else
        {
            SceneIO.Save(scene, output, osgconv, overwriteExternals: overwriteExt);
            Console.Out.WriteLine($"saved: {output}");
        }
        if (timing) Console.Error.WriteLine($"saved in {sw.Elapsed.TotalSeconds:F1}s");
    }
    if (timing) Console.Error.WriteLine($"peak memory {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
    return 0;
}
// -o FOLDER: an existing directory, a trailing / or \, or no extension
static bool IsFolderTarget(string path) =>
    Directory.Exists(path) || path.EndsWith('/') || path.EndsWith('\\') || Path.GetExtension(path) == "";

static int SaveToFolder(Scene scene, string folder, string? osgconv)
{
    try
    {
        var res = SaveCopy.ToFolder(scene, folder, new SaveCopyOptions { Osgconv = osgconv, Progress = m => Console.Error.WriteLine(m) });
        Console.Out.WriteLine($"\nsaved copy: {Path.GetFullPath(folder)}");
        Console.Out.WriteLine($"manifest: {res.Manifest}");
        return 0;
    }
    catch (Exception exc) { Console.Error.WriteLine($"\ncould not save: {exc.Message}"); return 1; }
}

static int SaveInPlace(Scene scene, string? osgconv)
{
    try
    {
        var plan = SaveOver.Plan(scene);
        var res = SaveOver.Run(scene, plan, osgconv, m => Console.Error.WriteLine(m));
        Console.Out.WriteLine($"\nbackup: {res.BackupFolder}");
        foreach (var f in res.Written) Console.Out.WriteLine($"saved: {f}");
        return 0;
    }
    catch (InvalidOperationException exc) when (exc.Message.StartsWith("nothing to save")) { Console.Error.WriteLine(exc.Message); return 0; }   // a note, not a result: stdout stays clean
    catch (Exception exc) { Console.Error.WriteLine($"\ncould not save: {exc.Message}"); return 1; }
}

static object ParseValue(string v)
{
    var low = v.ToLowerInvariant();
    if (low is "true" or "yes" or "on") return true;
    if (low is "false" or "no" or "off") return false;
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    if (long.TryParse(v.Replace("_", ""), System.Globalization.NumberStyles.AllowLeadingSign, inv, out var l)) return l <= int.MaxValue && l >= int.MinValue ? (int)l : l;
    if (double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var d)) return d;
    return v;
}

static string PyStr(object o) => o switch
{
    bool b => b ? "True" : "False",
    double d => PyFormat.Repr(d),
    float f => PyFormat.Repr(f),
    _ => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "None",
};

// Python's f"{x:+.0%}"
static string PlusPercent(double x)
{
    var s = PyFormat.Percent(x, 0);
    return s.StartsWith('-') ? s : "+" + s;
}

return Run(args);
