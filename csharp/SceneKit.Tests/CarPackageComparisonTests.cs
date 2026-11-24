using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SceneKit;
using Xunit;
using Xunit.Abstractions;

/// <summary>R3-C16: the car package bench (opt-in, SCENEKIT_BENCH=1). The split and the merged synthetic car at e = 2, 4
/// and 8 cm: row 1 the per-part proxy within e (replace_proxy per_part, voxels 512 as the upper limit), row 2 today's
/// single hull at the lowest ratio (2 to 50 %, voxels 128 and 256) whose deviation is at most e, row 3 meshopt keep, qem
/// keep and qem permissive at row 1's triangle count (no cap, on the drawn original merged into one mesh). One Markdown
/// table per fixture, then the spec's pass rules (ruling P19: 1-4 and 6-8 on the split car, 5-6 on the merged car),
/// collected and asserted at the end so the tables are always printed.</summary>
public class CarPackageComparisonTests
{
    readonly ITestOutputHelper output;
    public CarPackageComparisonTests(ITestOutputHelper output) => this.output = output;

    /// <summary>The targets: SCENEKIT_BENCH_TARGETS (comma-separated metres), default 0.02, 0.04, 0.08.</summary>
    static readonly double[] Es = ParseTargets();
    static double[] ParseTargets()
    {
        var raw = Environment.GetEnvironmentVariable("SCENEKIT_BENCH_TARGETS");
        if (string.IsNullOrWhiteSpace(raw)) return new[] { 0.02, 0.04, 0.08 };
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
    }
    /// <summary>The engine block of the split car stands 1 cm above the floor pan (ruling T16-a); the merged car keeps it
    /// on the floor (engineLift 0: a merged car is welded by construction, ruling T16-e).</summary>
    const double EngineLift = 0.01;
    static double LiftOf(bool split) => split ? EngineLift : 0;
    static (Scene sc, Node car) Car(bool split) => Meshes.SynthCar(split, engineLift: LiftOf(split));
    static string Cm(double e) => F(e * 100, Math.Abs(e * 100 - Math.Round(e * 100)) < 1e-9 ? 0 : 1);
    const int Row1Voxels = 512;
    static readonly int[] Row2Voxels = { 128, 256 };
    const int Views = 26, Raster = 256;

    /// <summary>The drawn original of a fixture in the world: merged positions, triangles, unit-0 UVs, the visibility of
    /// every triangle (moving pieces seen but hiding nothing, as VisibleOriginal), the class IDs seen and the class map.</summary>
    sealed class Original
    {
        public double[] V = null!; public int[] T = null!; public double[] UV = null!; public bool[] Vis = null!;
        public HashSet<int> SeenIds = new(); public HashSet<(int, int)> SourcePixels = new();
        public double LowZ; public long Tris => T.Length / 3;
        public double[] Ctr = null!; public double Radius;
        public List<Proxy.Source> Sources = null!;
        public RawImage ClassSrc = null!;                                   // the fixture's own source class map
        public Dictionary<string, HashSet<int>> PartIds = new();            // visible class IDs per part (geometry or group name below the car)
        public Node Car = null!;                                            // the car the original was taken from (rule 6 discovers its parts)
        public List<Node> Geoms = new(); public List<int> TriStart = new(); // the drawn geometries and the first triangle of each in T
        public int[] TriId = null!;                                         // the class ID at each visible triangle, -1 where unseen
        public List<bool[]>? Cover;                                         // the 26 coverage rasters of the original, computed once
    }

    /// <summary>One measured row.</summary>
    sealed class Row
    {
        public string Label = "", Voxels = "", Note = "";
        public long Tris, Verts; public int Draws, Textures, LineDraws = -1;
        public double Dev = double.NaN, IouMean = double.NaN, IouMin = double.NaN; public long Ms;
        public bool Never;
    }

    /// <summary>What a row-1 run left: the scene, the result line, the reached deviation of the line and whether it is
    /// within ("within e (reached r, ...)" rather than "within e asked, reached r after the cut").</summary>
    sealed class Run1
    {
        public Scene Sc = null!; public Node Car = null!; public string Line = ""; public double? Reached; public bool WithinForm;
        public int DrawAfter = -1, KeptDraws, MeasuredDraws = -1; public string KeptNames = "";
    }

    readonly List<string> failures = new();
    readonly List<(string fixture, double e, string rule, bool ok, string numbers)> rules = new();

    /// <summary>Progress with the time since the start, appended to the file SCENEKIT_BENCH_LOG names (when set).</summary>
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static void Log(string what)
    {
        if (Environment.GetEnvironmentVariable("SCENEKIT_BENCH_LOG") is { Length: > 0 } f)
            File.AppendAllText(f, $"{Clock.Elapsed.TotalSeconds,8:F1} s  {what}{Environment.NewLine}");
    }

    void Rule(string fixture, double e, string rule, bool ok, string numbers)
    {
        rules.Add((fixture, e, rule, ok, numbers));
        if (!ok) failures.Add($"{fixture}, e {Cm(e)} cm, rule {rule}: {numbers}");
    }

    static string Commit()
    {
        if (Environment.GetEnvironmentVariable("SCENEKIT_BENCH_COMMIT") is { Length: > 0 } c) return c;
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(10000)) return "unknown";
            return o.Length > 0 && p.ExitCode == 0 ? o : "unknown";
        }
        catch { return "unknown"; }
    }
    static string Cpu()
    {
        string name = "unknown CPU";
        try
        {
            if (OperatingSystem.IsWindows())
                name = (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string)?.Trim() ?? name;
        }
        catch { }
        return $"CPU {name}, {Environment.ProcessorCount} logical processors";
    }
    static string Ram()
    {
        try { return $"RAM {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F1} GiB"; } catch { return "RAM unknown"; }
    }
    static string BuildKind()
    {
#if DEBUG
        return "Debug build";
#else
        return "Release build";
#endif
    }

    [BenchFact]
    public void CarPackageComparison()
    {
        var total = Stopwatch.StartNew();
        output.WriteLine($"Car package bench on commit {Commit()}; {Cpu()}; {Ram()}; {BuildKind()}");
        output.WriteLine($"Fixtures: Meshes.SynthCar(true, engineLift: {F(EngineLift, 2)}) (the split car's engine block stands {F(EngineLift * 100, 0)} cm above the floor pan) and Meshes.SynthCar(false, engineLift: 0) (the merged car, welded by construction); targets e = {string.Join(", ", Es.Select(Cm))} cm (SCENEKIT_BENCH_TARGETS, default 0.02,0.04,0.08).");
        output.WriteLine($"Method: deviation = PartProxy.TwoWayVisible against the drawn " +
                         $"original (visibility from {PartProxy.Limits.MeasureViews} views), px at 100 m (1080 px, 45 deg); IoU over {Views} views " +
                         $"(Occlusion.SphereDirections), {Raster} px orthographic coverage; CDB = total vertices <= 128.");
        output.WriteLine("");
        Fixture("split", true);
        Fixture("merged", false);

        var sb = new StringBuilder();
        sb.AppendLine("### Pass rules");
        sb.AppendLine();
        sb.AppendLine("| Fixture | e | Rule | Result | Numbers |");
        sb.AppendLine("|---|---:|---|---|---|");
        foreach (var r in rules) sb.AppendLine($"| {r.fixture} | {F(r.e * 100, 0)} cm | {r.rule} | {(r.ok ? "pass" : "FAIL")} | {r.numbers} |");
        output.WriteLine(sb.ToString());
        output.WriteLine($"bench wall time {total.Elapsed.TotalSeconds:F0} s");
        Assert.True(failures.Count == 0, "failed rules:\n" + string.Join("\n", failures));
    }

    // ------------------------------------------------------------------------------------------------ one fixture

    void Fixture(string name, bool split)
    {
        Log($"{name}: original ...");
        var orig = OriginalOf(Car(split).car);
        Log($"{name}: original done");
        var hullDev = new Dictionary<(int pct, int vox), (double dev, long tris)>();
        var sb = new StringBuilder();
        sb.AppendLine($"### {(split ? "Split" : "Merged")} synthetic car (`Meshes.SynthCar({(split ? "true" : "false")}, engineLift: {F(LiftOf(split), 2)})`): {orig.Tris} drawn triangles, {orig.V.Length / 3} vertices");
        sb.AppendLine();
        sb.AppendLine("| e | Row | Variant | Voxels | Tris | Draw calls (measured / line) | Textures | Dev (m) | Dev (px @ 100 m) | IoU mean / min | Build ms | CDB | Note |");
        sb.AppendLine("|---:|---|---|---|---:|---:|---:|---:|---:|---|---:|---|---|");
        var row1Tris = new Dictionary<double, long>();
        foreach (double e in Es)
        {
            // ---------------------------------------------------------------- row 1: per part within e
            var (r1, run1) = PerPart(split, e, ProxyGround.Full, orig);
            row1Tris[e] = r1.Tris;
            // ---------------------------------------------------------------- row 2: today's single hull
            var r2 = SingleHull(split, e, orig, hullDev);
            // ---------------------------------------------------------------- row 3: the simplifiers at row 1's count
            var r3 = new[] { ("meshopt keep", "meshopt", "keep"), ("qem keep", "qem", "keep"), ("qem permissive", "qem", "permissive") }
                .Select(v => Simplified(v.Item1, v.Item2, v.Item3, r1.Tris, orig)).ToList();
            foreach (var (row, label) in new[] { (r1, "1"), (r2, "2") }.Concat(r3.Select(x => (x, "3"))))
                sb.AppendLine(Line(e, label, row));

            // ---------------------------------------------------------------- the rules
            if (split)
            {
                Rule(name, e, "1 reached <= e", run1.WithinForm && run1.Reached is double rr && rr <= e + 1e-12,
                    $"reached {(run1.Reached is double x ? F(x, 4) : "?")} m ({(run1.WithinForm ? "within" : "asked, reached after the cut")}); bench dev {F(r1.Dev, 4)} m");
                Rule(name, e, "2 row 1 tris <= row 2 tris", r2.Never || r1.Tris <= r2.Tris,
                    r2.Never ? $"row 1 {r1.Tris}; row 2 never reaches e" : $"row 1 {r1.Tris}, row 2 {r2.Tris}");
                string devs = $"row 1 {F(r1.Dev, 4)}; " + string.Join(", ", r3.Select(x => $"{x.Label} {F(x.Dev, 4)}"));
                if (e >= 0.04) Rule(name, e, "3 row 1 dev < every row 3", r3.All(x => r1.Dev < x.Dev), devs);
                else rules.Add((name, e, "3 (printed, not asserted)", true, devs));
                Structure(name, e, run1, orig, r1);
                ClassMap(name, e, run1, orig);
                Rule(name, e, $"8 build <= {PartProxy.Limits.BuildTimeFactor} x row 2", r1.Ms <= PartProxy.Limits.BuildTimeFactor * r2.Ms,
                    $"row 1 {r1.Ms} ms, row 2 {r2.Ms} ms ({(r2.Ms > 0 ? F((double)r1.Ms / r2.Ms, 2) : "?")} x){(r2.Never ? " (row 2 at its closest setting, it never reaches e)" : "")}");
            }
            else
            {
                bool single = run1.Line.Contains("1 part found, using the single hull");
                bool tenPct = !r2.Never && Math.Abs(r1.Tris - r2.Tris) <= 0.1 * r2.Tris;
                Rule(name, e, "5 single hull, tris within 10 % of row 2", single && tenPct,
                    $"{(single ? "\"1 part found, using the single hull\"" : "no single-hull note")}; row 1 {r1.Tris}, row 2 {(r2.Never ? "never reaches e" : r2.Tris.ToString(CultureInfo.InvariantCulture))}" +
                    (r2.Never ? "" : $" ({F(100.0 * (r1.Tris - r2.Tris) / r2.Tris, 1)} %)"));
                ClassMap(name, e, run1, orig);
            }
        }
        output.WriteLine(sb.ToString());

        if (split)
        {
            // rule 7: ground = cut at 4 cm
            double e = Es.Contains(0.04) ? 0.04 : Es.Max();   // 4 cm when it is a target, else the largest target
            var (cut, runCut) = PerPart(split, e, ProxyGround.Cut, orig);
            int down = 0;
            foreach (var (g, w, _) in SoftRender.GeometryItems(runCut.Sc.Root!))
            {
                var (V, T) = World(g.Geom!, w);
                for (int t = 0; t < T.Length / 3; t++)
                {
                    var n = Normal(V, T, t);
                    if (n[2] < -0.5 && Enumerable.Range(0, 3).All(k => Math.Abs(V[3 * T[3 * t + k] + 2] - orig.LowZ) <= e)) down++;
                }
            }
            Rule(name, e, "7 ground cut lowers tris, no down-facing triangle within e of the plane", cut.Tris < row1Tris[e] && down == 0,
                $"cut {cut.Tris} vs full {row1Tris[e]} tris; {down} down-facing triangles at the plane; cut dev {F(cut.Dev, 4)} m; line: {Within(runCut.Line)}");
        }
    }

    // ------------------------------------------------------------------------------------------------ rows

    (Row row, Run1 run) PerPart(bool split, double e, ProxyGround ground, Original orig)
    {
        var (sc, car) = Car(split);
        var prms = new Dictionary<string, object>
        {
            ["replace_proxy.parts"] = "per_part", ["replace_proxy.target"] = "within", ["replace_proxy.max_error"] = e,
            ["replace_proxy.voxels"] = Row1Voxels, ["replace_proxy.share"] = false, ["replace_proxy.ground"] = ProxyParams.Name(ground),
        };
        Log($"row 1 {(split ? "split" : "merged")} e {e} ground {ground} ...");
        var sw = Stopwatch.StartNew();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, prms, targets: new List<Node> { car });
        sw.Stop();
        Assert.Null(results[0].Error);
        string line = results[0].Message;
        Log($"  done in {sw.ElapsedMilliseconds} ms: {line}");
        var run = new Run1 { Sc = sc, Car = car, Line = line };
        // the ProxyReport is not exposed by Optimizer.Run: the reached deviation and the draw calls come from the line
        var m = Regex.Match(line, @"within [\d.eE+-]+ (?:mm|cm|m|km)( asked,)? \(?reached ([\d.eE+-]+) (mm|cm|m|km)");
        if (m.Success)
        {
            run.WithinForm = !m.Groups[1].Success;
            run.Reached = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * m.Groups[3].Value switch { "mm" => 0.001, "cm" => 0.01, "km" => 1000, _ => 1 };
        }
        var d = Regex.Match(line, @"draw calls [\d,]+ -> ([\d,]+)");
        if (d.Success) run.DrawAfter = int.Parse(d.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        var k = Regex.Match(line, @"(\d+) parts kept original or decimated, \+(\d+) draw calls \(([^)]*)\)");
        if (k.Success) { run.KeptDraws = int.Parse(k.Groups[2].Value, CultureInfo.InvariantCulture); run.KeptNames = k.Groups[3].Value; }
        var row = Measure($"per part, within e{(ground == ProxyGround.Cut ? ", ground cut" : "")}", sc.Root!, orig, sw.ElapsedMilliseconds);
        row.Voxels = $"{Row1Voxels} (limit)";
        run.MeasuredDraws = row.Draws;
        row.LineDraws = run.DrawAfter;
        row.Note = Within(line) + (k.Success ? $"; {k.Value}" : "");
        return (row, run);
    }

    /// <summary>The "within ..." fragment of a result line (or the line's start when there is none).</summary>
    static string Within(string line)
    {
        var m = Regex.Match(line, @"within [^;]*max over parts\)");
        return m.Success ? m.Value : line.Length > 160 ? line[..160] + " ..." : line;
    }

    Row SingleHull(bool split, double e, Original orig, Dictionary<(int pct, int vox), (double dev, long tris)> cache)
    {
        (double dev, long tris) Dev(int pct, int vox)
        {
            if (cache.TryGetValue((pct, vox), out var c)) return c;
            var t0 = Stopwatch.StartNew();
            var h = Proxy.Hull(orig.Sources, new Proxy.Options { TargetTris = (int)Math.Max(12, orig.Tris * pct / 100.0), Resolution = vox });
            var r = h == null ? (double.PositiveInfinity, 0L) : (PartProxy.TwoWayVisible(h.V, h.T, orig.V, orig.T, orig.Vis), (long)h.T.Length / 3);
            Log($"  hull {pct} % at {vox}: dev {r.Item1:F4}, {r.Item2} tris, {t0.ElapsedMilliseconds} ms");
            return cache[(pct, vox)] = r;
        }
        // per voxel count the lowest percentage within e (binary search over 2..50 %, the deviation falling with the ratio)
        (int pct, int vox)? best = null;
        foreach (int vox in Row2Voxels)
        {
            if (Dev(50, vox).dev > e) continue;
            int lo = 2, hi = 50;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Dev(mid, vox).dev <= e) hi = mid; else lo = mid + 1;
            }
            if (best == null || lo < best.Value.pct) best = (lo, vox);
        }
        bool never = best == null;
        // never: the closest setting tried (the lowest deviation) is built and measured, for the build time of rule 8
        var pick = best ?? cache.Where(x => x.Key.pct == 50).OrderBy(x => x.Value.dev).First().Key;
        var (sc, car) = Car(split);
        var prms = new Dictionary<string, object>
        {
            ["replace_proxy.parts"] = "whole", ["replace_proxy.target"] = "keep", ["replace_proxy.ratio"] = pick.pct / 100.0,
            ["replace_proxy.voxels"] = pick.vox, ["replace_proxy.share"] = false,
        };
        var sw = Stopwatch.StartNew();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, prms, targets: new List<Node> { car });
        sw.Stop();
        Assert.Null(results[0].Error);
        var row = Measure(never ? "single hull: never reaches e" : $"single hull, ratio {pick.pct} %", sc.Root!, orig, sw.ElapsedMilliseconds);
        row.Never = never;
        row.Voxels = pick.vox.ToString(CultureInfo.InvariantCulture);
        row.Note = never
            ? $"never reaches e (best at 50 %: {string.Join(", ", Row2Voxels.Select(v => $"{v} vox {F(cache[(50, v)].dev, 4)} m"))}); shown: 50 %, {pick.vox} vox"
            : $"search: {cache.Count} hulls so far for this car";
        return row;
    }

    Row Simplified(string label, string method, string seams, long target, Original orig)
    {
        var g = new GeomData { Vertices = new VArray(orig.V, 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(orig.UV, 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: orig.T.Length, indices: orig.T, indexType: "DrawElementsUInt"));
        var info = new Optimizer.SimplifyInfo { MeshoptMaxRelativeError = method == "meshopt" ? 0 : null };
        var sw = Stopwatch.StartNew();
        Log($"row 3 {label} ...");
        var r = Optimizer.SimplifyGeom(g, new[] { (double)target / orig.Tris }, method, minTris: 4, seams: seams, info: info)[0];
        sw.Stop();
        if (r == null) return new Row { Label = label, Note = "nothing: " + info.Reason, Ms = sw.ElapsedMilliseconds, Voxels = "-" };
        var geo = new Node(Kind.Geometry, "osg::Geometry", label) { Geom = r };
        var root = new Node(Kind.Group, "osg::Group", "root") { StateSet = new StateSet { Textures = { new Texture { File = "car.png", Unit = 0 } } }, Children = { new Node(Kind.Geode, "osg::Geode", label) { Children = { geo } } } };
        var row = Measure(label, root, orig, sw.ElapsedMilliseconds);
        row.Voxels = "-";
        row.Note = "no cap, on the drawn original merged into one mesh";
        return row;
    }

    /// <summary>Triangles, vertices, draw calls (the geometries drawn), unit-0 textures, deviation and IoU of everything
    /// drawn below <paramref name="root"/>, in the world.</summary>
    static Row Measure(string label, Node root, Original orig, long ms)
    {
        var V = new List<double>(); var T = new List<int>();
        int draws = 0;
        var tex = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (g, w, ss, path) in SoftRender.GeometryPaths(root))
        {
            var (gv, gt) = World(g.Geom!, w);
            if (gt.Length == 0) continue;
            draws++;
            int b = V.Count / 3;
            V.AddRange(gv); T.AddRange(gt.Select(x => x + b));
            // the unit-0 texture in effect: the nearest state set on the path (the geometry's own first) that has one
            var t0 = path.Reverse().Select(n => n.StateSet?.Textures.FirstOrDefault(t => t.Unit == 0 && !t.IsCompanion)).FirstOrDefault(t => t != null);
            if (t0 != null) tex.Add((object?)t0.Baked ?? t0);
        }
        var PV = V.ToArray(); var PT = T.ToArray();
        var (mean, min) = Iou(orig, PV, PT);
        return new Row
        {
            Label = label, Tris = PT.Length / 3, Verts = PV.Length / 3, Draws = draws, Textures = tex.Count, Ms = ms,
            Dev = PartProxy.TwoWayVisible(PV, PT, orig.V, orig.T, orig.Vis), IouMean = mean, IouMin = min,
        };
    }

    // ------------------------------------------------------------------------------------------------ rules 4 and 6

    void Structure(string name, double e, Run1 run, Original orig, Row r1)
    {
        var (_, fresh) = Car(true);
        var want = fresh.Children.Where(c => c.Cls == "FLT DOF").ToDictionary(c => c.Name, c => (double[])c.Matrix!.Clone());
        var dofs = run.Sc.IterNodes().Where(n => n.Cls == "FLT DOF").ToList();
        bool dofOk = dofs.Count == 4 && want.All(w => dofs.Count(d => d.Name == w.Key) == 1 && dofs.Single(d => d.Name == w.Key).Matrix!.SequenceEqual(w.Value));
        var sw = run.Sc.IterNodes().Where(n => n.Kind == Kind.Switch).ToList();
        bool swOk = sw.Count == 1 && sw[0].Name == "lights" && sw[0].Cls == "FLT Switch" && sw[0].SwitchValues!.SequenceEqual(new[] { true, false }) && sw[0].Children.Count == 2;
        bool kidsOk = swOk && sw[0].Children.All(c => SoftRender.GeometryItems(c).Any(x => x.node.Geom!.Triangles().Length > 0));
        Rule(name, e, "4a 4 DOFs and the switch survive (names, matrices, values)", dofOk && swOk, $"{dofs.Count} DOFs ({string.Join(", ", dofs.Select(x => x.Name))}), switches {sw.Count}");
        Rule(name, e, "4b both switch children have geometry", kidsOk, swOk ? string.Join(", ", sw[0].Children.Select(c => $"{c.Name}: {SoftRender.GeometryItems(c).Count} geometries")) : "no switch");
        // ruling T16-d: the line counts drawn geometry only, so the light state that is on (1), not both switch children
        int wantDraws = 1 + 4 + 1 + run.KeptDraws;
        bool named = run.KeptDraws == 0 || run.KeptNames.Trim().Length > 0;
        Rule(name, e, "4c draw calls 1 + 4 + 1 (the light that is on) + kept (named), measured = line", run.DrawAfter == wantDraws && run.MeasuredDraws == run.DrawAfter && named,
            $"line: draw calls -> {run.DrawAfter}, measured (geometries drawn) {run.MeasuredDraws}, kept +{run.KeptDraws}{(run.KeptNames.Length > 0 ? $" ({run.KeptNames})" : "")}; expected {wantDraws}");
        // textures: the same count as the table (the unit-0 textures in effect on the drawn geometry, Measure)
        Rule(name, e, "4d 1 texture", r1.Textures == 1, $"{r1.Textures} unit-0 textures in effect on the drawn geometry");
        // a wheel-sized part at each wheel; class ID 2 in its atlas region
        var wheelInfo = new List<string>();
        bool wheelsOk = dofs.Count == 4;
        foreach (var dof in dofs)
        {
            var origWheel = fresh.Children.Single(c => c.Name == dof.Name);
            var (olo, ohi) = Box(SoftRender.GeometryItems(origWheel, start: Mat.Identity()).Select(x => World(x.node.Geom!, x.world)));
            var items = SoftRender.GeometryItems(dof);
            var (plo, phi) = Box(items.Select(x => World(x.node.Geom!, x.world)));
            double tol = 2 * e;
            bool sized = Enumerable.Range(0, 3).All(c => Math.Abs((ohi[c] - olo[c]) - (phi[c] - plo[c])) <= tol && Math.Abs((ohi[c] + olo[c]) / 2 - (phi[c] + plo[c]) / 2) <= tol);
            var ids = new HashSet<int>();
            foreach (var (g, _, ss) in items)
            {
                var cls = (g.StateSet ?? ss)?.Textures.FirstOrDefault(t => t.Unit == 0)?.Companions?.GetValueOrDefault(IrTests.Cls)?.Baked;
                if (cls == null || !g.Geom!.TexCoords.TryGetValue(0, out var uv)) continue;
                var T = g.Geom.Triangles();
                var px = new double[4];   // not stackalloc inside the loops (CA2014)
                for (int t = 0; t < T.Length / 3; t++)
                {
                    double u = (uv.Data[2 * T[3 * t]] + uv.Data[2 * T[3 * t + 1]] + uv.Data[2 * T[3 * t + 2]]) / 3;
                    double v = (uv.Data[2 * T[3 * t] + 1] + uv.Data[2 * T[3 * t + 1] + 1] + uv.Data[2 * T[3 * t + 2] + 1]) / 3;
                    Bake.Nearest(cls, u, v, px);
                    ids.Add((int)Math.Round(px[0] * 255));
                }
            }
            bool two = ids.Contains(2);
            wheelsOk &= sized && two;
            wheelInfo.Add($"{dof.Name}: size {F(phi[0] - plo[0], 3)} x {F(phi[1] - plo[1], 3)} x {F(phi[2] - plo[2], 3)}{(sized ? "" : " (off)")}, ids {{{string.Join(",", ids.OrderBy(x => x))}}}");
        }
        Rule(name, e, "4e wheel-sized part at each wheel, class ID 2 in its atlas region", wheelsOk, string.Join("; ", wheelInfo));
    }

    void ClassMap(string name, double e, Run1 run, Original orig)
    {
        // ruling T16-b: a kept or decimated part keeps its original companion, so its IDs count as present. The kept parts
        // are named in the result line's "({names})" fragment by their OUTPUT part (the name of its largest unit); every
        // original unit is mapped to its output part by the build's own discovery (gap e), so a unit clustered into a kept
        // part (the cabin in the body cluster) is a kept-cluster member. The atlas IDs are those seen on the other units.
        var kept = run.KeptNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var atlasIds = new HashSet<int>();
        var keptUnits = new HashSet<string>();
        var asm = PartProxy.Discover(orig.Car, e);
        void Owner(PartProxy.Assembly a)
        {
            foreach (var part in a.Parts)
            {
                bool k = kept.Contains(part.Name);
                foreach (var u in part.Units)
                {
                    if (k) keptUnits.Add(u.Name);
                    int gi = orig.Geoms.FindIndex(g => ReferenceEquals(g, a.Pieces[u.Piece].Geom));
                    if (gi < 0) continue;   // not drawn (a switch child that is off)
                    foreach (int t in u.Tris)
                        if (orig.TriId[orig.TriStart[gi] + t] is int id && id >= 0 && !k) atlasIds.Add(id);
                }
            }
            foreach (var m in a.Moving) Owner(m.Inner);
        }
        Owner(asm);
        // the class maps of the result's atlases (unit 0 companions of the proxy geometries)
        var maps = run.Sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures)
            .Select(t => t.Companions?.GetValueOrDefault(IrTests.Cls)?.Baked).Where(x => x != null)
            .Distinct(ReferenceEqualityComparer.Instance).Cast<RawImage>().ToList();
        // the fixture's own source class map is still referenced by kept parts: it is not a baked atlas
        var src = orig.ClassSrc;
        maps = maps.Where(m => !(m.Width == src.Width && m.Height == src.Height && m.Pixels.SequenceEqual(src.Pixels))).ToList();
        var baked = new HashSet<(int, int)>();
        foreach (var img in maps)
        {
            int c = img.Channels;
            for (int i = 0; i < img.Width * img.Height; i++) baked.Add((img.Pixels[c * i], c > 1 ? img.Pixels[c * i + 1] : 0));
        }
        var bakedIds = baked.Select(p => p.Item1).ToHashSet();
        var allowed = new HashSet<(int, int)>(orig.SourcePixels) { (0, 0) };   // class_fill 0 (the default)
        var missing = atlasIds.Where(id => !bakedIds.Contains(id)).ToList();
        var stray = baked.Where(p => !allowed.Contains(p)).ToList();
        bool needAtlas = atlasIds.Count > 0;
        Rule(name, e, "6 atlas parts' class IDs in the baked class map, every pixel a source ID or class_fill", (!needAtlas || maps.Count > 0) && missing.Count == 0 && stray.Count == 0,
            $"{maps.Count} baked class map(s); kept/decimated ({(kept.Count == 0 ? "none" : string.Join(",", kept))}{(keptUnits.Count > kept.Count ? $": units {string.Join(",", keptUnits.OrderBy(x => x))}" : "")}) keep their companion; atlas parts' IDs {{{string.Join(",", atlasIds.OrderBy(x => x))}}}, baked {{{string.Join(",", bakedIds.OrderBy(x => x))}}}, seen on the original {{{string.Join(",", orig.SeenIds.OrderBy(x => x))}}}" +
            (missing.Count > 0 ? $", missing {{{string.Join(",", missing)}}}" : "") + (stray.Count > 0 ? $", stray {string.Join(" ", stray.Take(5))}" : ""));
    }

    // ------------------------------------------------------------------------------------------------ the original

    static Original OriginalOf(Node car)
    {
        var V = new List<double>(); var T = new List<int>(); var UV = new List<double>();
        var items = new List<Occlusion.Item>();
        var sources = new List<Proxy.Source>();
        var partNames = new List<List<string>>(); var triStart = new List<int>();
        var cls = car.StateSet!.Textures[0].Companions![IrTests.Cls].Baked!;
        var img = car.StateSet!.Textures[0].Baked;
        var geoms = new List<Node>();
        foreach (var (g, w, _, path) in SoftRender.GeometryPaths(car))
        {
            var (gv, gt) = World(g.Geom!, w);
            var guv = g.Geom!.TexCoords[0].Data;
            int b = V.Count / 3;
            triStart.Add(T.Count / 3);
            geoms.Add(g);
            V.AddRange(gv); T.AddRange(gt.Select(x => x + b)); UV.AddRange(guv);
            bool moving = path.Any(n => n.Cls == "FLT DOF" || n.Kind == Kind.Switch);
            partNames.Add(path.Skip(1).Select(n => n.Name).Append(g.Name).ToList());   // the names below the car (the car itself excluded)
            items.Add(new Occlusion.Item(gv, gt, Occludes: !moving));
            sources.Add(new Proxy.Source { V = gv, T = gt, UV = guv, Image = img });
        }
        var vis = Occlusion.Visible(items, new Occlusion.Options { Views = PartProxy.Limits.MeasureViews });
        var o = new Original { V = V.ToArray(), T = T.ToArray(), UV = UV.ToArray(), Vis = vis.SelectMany(x => x).ToArray(), Sources = sources, ClassSrc = cls,
            Car = car, Geoms = geoms, TriStart = triStart };
        o.TriId = Enumerable.Repeat(-1, o.T.Length / 3).ToArray();
        o.LowZ = Enumerable.Range(0, o.V.Length / 3).Min(i => o.V[3 * i + 2]);
        Span<double> px = stackalloc double[4];
        for (int t = 0; t < o.T.Length / 3; t++)
        {
            if (!o.Vis[t]) continue;
            double u = (o.UV[2 * o.T[3 * t]] + o.UV[2 * o.T[3 * t + 1]] + o.UV[2 * o.T[3 * t + 2]]) / 3;
            double v = (o.UV[2 * o.T[3 * t] + 1] + o.UV[2 * o.T[3 * t + 1] + 1] + o.UV[2 * o.T[3 * t + 2] + 1]) / 3;
            Bake.Nearest(cls, u, v, px);
            int id = (int)Math.Round(px[0] * 255);
            o.SeenIds.Add(id);
            o.TriId[t] = id;
            int gi = triStart.FindLastIndex(x => x <= t);
            foreach (var pn in partNames[gi]) { if (!o.PartIds.TryGetValue(pn, out var set)) o.PartIds[pn] = set = new(); set.Add(id); }
        }
        for (int i = 0; i < cls.Width * cls.Height; i++) o.SourcePixels.Add((cls.Pixels[cls.Channels * i], cls.Channels > 1 ? cls.Pixels[cls.Channels * i + 1] : 0));
        var (lo, hi) = Box(new[] { (o.V, o.T) });
        o.Ctr = Enumerable.Range(0, 3).Select(c => (lo[c] + hi[c]) / 2).ToArray();
        o.Radius = 0.5 * Math.Sqrt(Enumerable.Range(0, 3).Sum(c => (hi[c] - lo[c]) * (hi[c] - lo[c])));
        return o;
    }

    // ------------------------------------------------------------------------------------------------ silhouette IoU

    /// <summary>Mean and smallest IoU of the coverage of the original and the proxy over <see cref="Views"/> directions:
    /// orthographic, <see cref="Raster"/> px over the original's bounding sphere, every triangle filled (no culling).</summary>
    static (double mean, double min) Iou(Original orig, double[] PV, int[] PT)
    {
        double sum = 0, min = 1;
        bool cache = orig.Cover == null;
        if (cache) orig.Cover = new List<bool[]>();
        int di = 0;
        foreach (var d in Occlusion.SphereDirections(Views))
        {
            // a frame (u, w) across the view direction
            var a = Math.Abs(d[2]) < 0.9 ? new[] { 0.0, 0, 1 } : new[] { 1.0, 0, 0 };
            var u = Cross(a, d); Norm(u);
            var w = Cross(d, u);
            if (cache) orig.Cover!.Add(Cover(orig.V, orig.T, orig, u, w));
            var A = orig.Cover![di++];
            var B = Cover(PV, PT, orig, u, w);
            int inter = 0, union = 0;
            for (int i = 0; i < A.Length; i++) { if (A[i] && B[i]) inter++; if (A[i] || B[i]) union++; }
            double iou = union == 0 ? 1 : (double)inter / union;
            sum += iou; min = Math.Min(min, iou);
        }
        return (sum / Views, min);
    }

    static bool[] Cover(double[] V, int[] T, Original orig, double[] u, double[] w)
    {
        var img = new bool[Raster * Raster];
        double s = Raster / (2 * orig.Radius * 1.02);
        int n = V.Length / 3;
        var X = new double[n]; var Y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double px = V[3 * i] - orig.Ctr[0], py = V[3 * i + 1] - orig.Ctr[1], pz = V[3 * i + 2] - orig.Ctr[2];
            X[i] = (px * u[0] + py * u[1] + pz * u[2]) * s + Raster / 2.0;
            Y[i] = (px * w[0] + py * w[1] + pz * w[2]) * s + Raster / 2.0;
        }
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double area = (X[b] - X[a]) * (Y[c] - Y[a]) - (X[c] - X[a]) * (Y[b] - Y[a]);
            if (area == 0) continue;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(X[a], Math.Min(X[b], X[c])))), x1 = Math.Min(Raster - 1, (int)Math.Ceiling(Math.Max(X[a], Math.Max(X[b], X[c]))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(Y[a], Math.Min(Y[b], Y[c])))), y1 = Math.Min(Raster - 1, (int)Math.Ceiling(Math.Max(Y[a], Math.Max(Y[b], Y[c]))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double qx = x + 0.5, qy = y + 0.5;
                    double e0 = (X[b] - X[a]) * (qy - Y[a]) - (Y[b] - Y[a]) * (qx - X[a]);
                    double e1 = (X[c] - X[b]) * (qy - Y[b]) - (Y[c] - Y[b]) * (qx - X[b]);
                    double e2 = (X[a] - X[c]) * (qy - Y[c]) - (Y[a] - Y[c]) * (qx - X[c]);
                    if (area > 0 ? e0 >= 0 && e1 >= 0 && e2 >= 0 : e0 <= 0 && e1 <= 0 && e2 <= 0) img[y * Raster + x] = true;
                }
        }
        return img;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    static (double[] V, int[] T) World(GeomData g, double[] w)
    {
        var src = g.Vertices!.Data;
        var V = new double[src.Length];
        for (int i = 0; i < src.Length / 3; i++) (V[3 * i], V[3 * i + 1], V[3 * i + 2]) = Mat.Apply(w, src[3 * i], src[3 * i + 1], src[3 * i + 2]);
        return (V, g.Triangles());
    }

    static (double[] lo, double[] hi) Box(IEnumerable<(double[] V, int[] T)> meshes)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var (V, T) in meshes)
            foreach (int v in T)
                for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], V[3 * v + c]); hi[c] = Math.Max(hi[c], V[3 * v + c]); }
        return (lo, hi);
    }

    static double[] Normal(double[] V, int[] T, int t)
    {
        int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
        var n = Cross(new[] { V[3 * b] - V[3 * a], V[3 * b + 1] - V[3 * a + 1], V[3 * b + 2] - V[3 * a + 2] },
                      new[] { V[3 * c] - V[3 * a], V[3 * c + 1] - V[3 * a + 1], V[3 * c + 2] - V[3 * a + 2] });
        Norm(n);
        return n;
    }

    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    static void Norm(double[] v) { double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); if (l > 0) for (int c = 0; c < 3; c++) v[c] /= l; }

    /// <summary>A deviation in pixels at 100 m: 1080 px over a 45 degree field of view.</summary>
    static double Px(double devM) => devM * 1080 / (2 * Math.Tan(22.5 * Math.PI / 180) * 100);

    static string Line(double e, string row, Row r) =>
        $"| {Cm(e)} cm | {row} | {r.Label} | {r.Voxels} | {r.Tris} | {(r.LineDraws >= 0 ? $"{r.Draws} / {r.LineDraws}" : r.Draws.ToString(CultureInfo.InvariantCulture))} | {r.Textures} | {F(r.Dev, 4)} | {F(Px(r.Dev), 2)} | " +
        $"{F(r.IouMean, 3)} / {F(r.IouMin, 3)} | {r.Ms} | {(r.Tris == 0 ? "-" : r.Verts <= 128 ? $"yes ({r.Verts} v)" : $"no ({r.Verts} v)")} | {r.Note.Replace("|", "/")} |";

    static string F(double x, int d) => double.IsFinite(x) ? x.ToString("F" + d, CultureInfo.InvariantCulture) : x.ToString(CultureInfo.InvariantCulture);
}
