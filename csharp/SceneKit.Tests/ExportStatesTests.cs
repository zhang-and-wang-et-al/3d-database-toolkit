using SceneKit;
using Xunit;

/// <summary>ExportStates: the original plus every checkpoint as numbered, self-contained folders sharing unchanged files with base/.</summary>
public class ExportStatesTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skexp_{Guid.NewGuid():N}");
    readonly string root, master, outDir;
    readonly List<CheckpointInfo> states = new();
    readonly long tileB0, tileB2;

    public ExportStatesTests()
    {
        Directory.CreateDirectory(dir);
        Scene sc;
        (sc, root) = SaveCopyTests.BuildFltDb(Path.Combine(dir, "db"), simplifyA: false);
        master = Path.Combine(root, "master.flt");
        outDir = Path.Combine(dir, "export");
        tileB0 = Tris(Sub(sc, "tile_b.flt"));
        var store = new CheckpointStore(Path.Combine(dir, "checkpoints"));
        string key = CheckpointStore.KeyFor(master), s = store.NewSession();
        Simplify(sc, "tile_a.flt");
        states.Add(store.Write(sc, key, s, 1, "simplify tile_a", "tile_a", "simplify.ratio=0.25", null));
        Simplify(sc, "tile_b.flt");
        tileB2 = Tris(Sub(sc, "tile_b.flt"));
        states.Add(store.Write(sc, key, s, 2, "simplify tile_b", "tile_b", "simplify.ratio=0.25", 1));
        Assert.True(tileB2 < tileB0);
    }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    static void Simplify(Scene sc, string tile) =>
        Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 10 },
            targets: Sub(sc, tile).IterNodes().ToList());

    static long Tris(Scene sc) => Convert.ToInt64(Analyzer.Analyze(sc, deepChecks: false).Summary()["triangles"]);
    static Scene Sub(Scene sc, string name) => sc.Externals.Single(kv => Path.GetFileName(kv.Key) == name).Value!;

    [Fact]
    public void WritesOneFolderPerStatePlusOriginalAndReadme()
    {
        var res = ExportStates.Run(master, states, outDir);
        string[] names = { "state_00_original", "state_01_simplify_tile_a", "state_02_simplify_tile_b" };
        Assert.Equal(names.Select(n => Path.Combine(outDir, n)), res.StateFolders);
        foreach (var f in res.StateFolders) Assert.True(File.Exists(Path.Combine(f, "master.flt")), f);
        Assert.True(File.Exists(Path.Combine(outDir, "base", "master.flt")));
        Assert.Equal(Path.Combine(outDir, "README.txt"), res.Readme);
        var lines = File.ReadAllLines(res.Readme);
        Assert.Contains(lines, l => l.Contains(master));
        Assert.Equal(3, lines.Count(l => l.StartsWith("state_")));
        foreach (var n in names) Assert.Single(lines, l => l.StartsWith(n + "  —  "));
        Assert.Contains(lines, l => l.StartsWith("state_02_simplify_tile_b") && l.Contains("tile_b") && l.Contains("simplify.ratio=0.25") && l.Contains($"→ {states[1].Tris}"));
        Assert.Contains("Load the folders in order in the renderer; the first one that fails names the step that broke it.", File.ReadAllText(res.Readme));
        Assert.True(res.Linked > 0);                                      // the temp volume is NTFS: unchanged files are shared
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "tile_b.flt")), File.ReadAllBytes(Path.Combine(outDir, "base", "tile_b.flt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "tile_a.flt")), File.ReadAllBytes(Path.Combine(outDir, "base", "tile_a.flt")));   // writing state 1 left base alone
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "master.flt")), File.ReadAllBytes(Path.Combine(outDir, "base", "master.flt")));
    }

    [Fact]
    public void EachStateLoadsOnItsOwn()
    {
        var res = ExportStates.Run(master, states, outDir);
        var s2 = SceneIO.Load(Path.Combine(res.StateFolders[2], "master.flt"));
        Assert.DoesNotContain(s2.Warnings, w => w.Contains("not found"));
        Assert.Equal(tileB2, Tris(Sub(s2, "tile_b.flt")));
        Assert.Equal(states[1].Tris, Tris(s2));
        var s1 = SceneIO.Load(Path.Combine(res.StateFolders[1], "master.flt"));
        Assert.DoesNotContain(s1.Warnings, w => w.Contains("not found"));
        Assert.Equal(tileB0, Tris(Sub(s1, "tile_b.flt")));
        Assert.Equal(states[0].Tris, Tris(s1));
        var s0 = SceneIO.Load(Path.Combine(res.StateFolders[0], "master.flt"));
        Assert.Equal(Tris(SceneIO.Load(master)), Tris(s0));
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, "tile_b.flt")), File.ReadAllBytes(Path.Combine(res.StateFolders[1], "tile_b.flt")));
    }

    [Fact]
    public void FallsBackToCopiesWhenLinkingFails()
    {
        var res = ExportStates.Run(master, states, outDir, link: (_, _) => false);
        Assert.Equal(0, res.Linked);
        Assert.True(res.Copied > 0);
        foreach (var f in res.StateFolders)
        {
            foreach (var n in new[] { "master.flt", "tile_a.flt", "tile_b.flt", "notes.txt" }) Assert.True(File.Exists(Path.Combine(f, n)), Path.Combine(f, n));
            Assert.DoesNotContain(SceneIO.Load(Path.Combine(f, "master.flt")).Warnings, w => w.Contains("not found"));
        }
        Assert.Equal(tileB2, Tris(Sub(SceneIO.Load(Path.Combine(res.StateFolders[2], "master.flt")), "tile_b.flt")));
    }

    static CheckpointInfo Ck(int index, string label, int? parent) =>
        new($"c{index}", "s", index, label, new DateTime(2026, 10, 21, 12, 0, index), $"m{index}", "src", 0, 0, "", "", parent, Array.Empty<string>(), false);

    [Fact]
    public void ChainLeavesOutUndoneBranches()
    {
        // A, B, undo B, C: C was built on A
        var all = new[] { Ck(1, "A", 0), Ck(2, "B", 1), Ck(3, "C", 1) };
        Assert.Equal(new[] { "A", "C" }, ExportStates.Chain(all, 3).Select(c => c.Label));
        Assert.Equal(new[] { "A", "B" }, ExportStates.Chain(all, 2).Select(c => c.Label));
        Assert.Empty(ExportStates.Chain(all, null));
        Assert.Empty(ExportStates.Chain(all, 0));
        // no parent recorded: on top of the previous index
        Assert.Equal(new[] { "A", "B", "C" }, ExportStates.Chain(new[] { Ck(1, "A", null), Ck(2, "B", null), Ck(3, "C", null) }, 3).Select(c => c.Label));
    }

    [Fact]
    public void ExportOfAnUndoneAndReplacedStepHasOnlyTheCurrentPath()
    {
        // state 3 written on top of state 1 (state 2 undone): exported as state_01, state_02
        var store = new CheckpointStore(Path.Combine(dir, "checkpoints"));
        var sc = SceneIO.Load(master);
        Simplify(sc, "tile_a.flt");
        var c = store.Write(sc, CheckpointStore.KeyFor(master), states[0].Session, 3, "C", "tile_a", "", 1);
        var res = ExportStates.Run(master, ExportStates.Chain(new[] { states[0], states[1], c }, 3), outDir);
        Assert.Equal(new[] { "state_00_original", "state_01_simplify_tile_a", "state_02_C" }, res.StateFolders.Select(Path.GetFileName));
        Assert.Equal(tileB0, Tris(Sub(SceneIO.Load(Path.Combine(res.StateFolders[2], "master.flt")), "tile_b.flt")));
        Assert.DoesNotContain("simplify_tile_b", File.ReadAllText(res.Readme));
    }

    [Fact]
    public void SaveOverBackupIsTheOriginal()
    {
        // the database before the save over lies in the backup folder; the database itself now has another tile_b
        string backup = Path.Combine(dir, "backup");
        Directory.CreateDirectory(backup);
        foreach (var f in new[] { "master.flt", "tile_b.flt" }) File.Copy(Path.Combine(root, f), Path.Combine(backup, f));
        File.WriteAllBytes(Path.Combine(root, "tile_b.flt"), FltWriteTests.Grid(3, "tile_b"));
        var store = new CheckpointStore(Path.Combine(dir, "checkpoints"));
        var bk = store.AddBackupEntry(CheckpointStore.KeyFor(master), states[0].Session, backup, "master.flt", "before this session's save over");
        var res = ExportStates.Run(master, states, outDir, original: bk);
        Assert.Equal(File.ReadAllBytes(Path.Combine(backup, "tile_b.flt")), File.ReadAllBytes(Path.Combine(outDir, "base", "tile_b.flt")));
        Assert.Equal(tileB0, Tris(Sub(SceneIO.Load(Path.Combine(res.StateFolders[0], "master.flt")), "tile_b.flt")));
    }

    [Fact]
    public void RefusesAFolderInsideTheDatabase()
    {
        Assert.Throws<ArgumentException>(() => ExportStates.Run(master, states, Path.Combine(root, "export")));
        Assert.Throws<ArgumentException>(() => ExportStates.Run(master, states, root));
        Assert.False(Directory.Exists(Path.Combine(root, "export")));
    }
}
