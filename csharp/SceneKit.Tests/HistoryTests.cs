using SceneKit;
using Xunit;

/// <summary>History entries that say what a step was (scope, parameters, files, duration, absolute triangles) and the
/// Undo / Redo texts built from them.</summary>
public class HistoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skhist_{Guid.NewGuid():N}");

    public HistoryTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    string Put(string rel, byte[] bytes)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    Scene TiledScene()
    {
        Put("tile_a.flt", FltWriteTests.Grid(6, "tile_a"));
        Put("tile_b.flt", FltWriteTests.Grid(6, "tile_b"));
        return SceneIO.Load(Put("master.flt", FltWriteTests.Grid(2, "master", false, "tile_a.flt", "tile_b.flt")));
    }

    [Fact]
    public void EntriesCarryScopeParamsFilesAndDuration()
    {
        var sc = TiledScene();
        var h = new UndoHistory();
        h.Push(new Snapshot(sc, "simplify") { Scope = "3 nodes", Params = "simplify.ratio=0.25", Files = 2, Seconds = 1.5 });
        Optimizer.Run(sc, new[] { "simplify" }, new Dictionary<string, object> { ["simplify.ratio"] = 0.25, ["simplify.min_tris"] = 10 });
        var e = h.Entries().Single();
        Assert.Equal(("3 nodes", "simplify.ratio=0.25", 2), (e.Scope, e.Params, e.Files));
        Assert.Equal(1.5, e.Seconds);
        Assert.True(e.TrisAfter < e.TrisBefore);
        Assert.StartsWith("Undo simplify on 3 nodes: triangles ", h.UndoText);
        Assert.Contains("simplify.ratio=0.25", h.UndoText);
        Assert.Contains("2 file(s), 1.5 s", h.UndoText);
        Assert.Null(h.RedoText);

        h.Undo();
        Assert.Null(h.UndoText);
        Assert.StartsWith("Redo simplify on 3 nodes: triangles ", h.RedoText);
        var e2 = h.Entries().Single();
        Assert.True(e2.Undone);
        Assert.Equal((e.Scope, e.Params, e.Files, e.TrisBefore, e.TrisAfter), (e2.Scope, e2.Params, e2.Files, e2.TrisBefore, e2.TrisAfter));
        h.Redo();
        Assert.Equal("Undo " + History.Describe(e), h.UndoText);
    }

    [Fact]
    public void DescribeFormatsOptionalParts()
    {
        var t = DateTime.Now;
        Assert.Equal("delete: triangles 1,000 → 500 (-50%), 1.0 s",
            History.Describe(new UndoHistory.Entry("delete", t, -500, 0, false, "", "", 0, 1.0, 1000, 500)));
        Assert.Equal("x on a: triangles 0 → 5 (±0 %), 2 file(s), 0.5 s; p.k=1",
            History.Describe(new UndoHistory.Entry("x", t, 5, 0, false, "a", "p.k=1", 2, 0.5, 0, 5)));
    }

    [Fact]
    public void NonDefaultParamsListsOnlyChangedKeys()
    {
        var p = new Dictionary<string, object>
        {
            ["simplify.ratio"] = 0.25, ["simplify.method"] = Optimizer.Passes["simplify"].Params.First(x => x.Key == "method").Default,
            ["include_externals"] = true,
        };
        Assert.Equal(new[] { "simplify.ratio=0.25" }, Optimizer.NonDefaultParams(new[] { "simplify" }, p));
        Assert.Empty(Optimizer.NonDefaultParams(new[] { "simplify" }, null));
    }

    [Fact]
    public void FilesOfCountsMasterAndExternals()
    {
        var sc = TiledScene();
        var own = sc.IterNodes().First(n => n.Kind == Kind.Geometry);
        var a = sc.Externals.Single(kv => kv.Key.Contains("tile_a")).Value!;
        var inA = a.IterNodes().First(n => n.Kind == Kind.Geometry);
        Assert.Equal(2, History.FilesOf(sc, new[] { own, inA }));
        Assert.Equal(1, History.FilesOf(sc, new[] { inA }));
        Assert.Equal(0, History.FilesOf(sc, new[] { new Node(Kind.Group, "osg::Group", "x") }));
    }

    [Fact] public void NothingChangedWhenTotalsEditsAndChangesAreEqual()
    {
        var r = new PassResult("share_duplicates", "no duplicate geometry found");
        var totals = new Dictionary<string, object?> { ["triangles"] = 10L, ["vertices"] = 30L, ["draw_calls"] = 1L, ["nodes"] = 3L, ["geometry_bytes"] = 100L, ["texture_bytes"] = 0L, ["unique_geometries"] = 1L };
        Assert.Equal("no duplicate geometry found", InspectorModel.NothingChanged(new() { r }, totals, totals, 5, 5));
        Assert.Null(InspectorModel.NothingChanged(new() { r }, totals, totals, 5, 6));
        var after = new Dictionary<string, object?>(totals) { ["nodes"] = 2L };
        Assert.Null(InspectorModel.NothingChanged(new() { r }, totals, after, 5, 5));
        r.RemovedNodes = 1;
        Assert.Null(InspectorModel.NothingChanged(new() { r }, totals, totals, 5, 5));
    }

    [Fact] public void NothingChangedFallsBackToGenericReasonAndMixedBoxing()
    {
        var before = new Dictionary<string, object?> { ["triangles"] = 10, ["nodes"] = 3L };
        var after = new Dictionary<string, object?> { ["triangles"] = 10L };
        var failed = new PassResult("weld", "", "boom");
        Assert.Equal("the passes found nothing to do", InspectorModel.NothingChanged(new() { failed, new PassResult("clean", "") }, before, after, 1, 1));
    }

    [Fact] public void LogShowsNonDefaultParametersPerPass()
    {
        var totals = new Dictionary<string, object?> { ["triangles"] = 10L, ["visible_triangles"] = 10L, ["vertices"] = 30L, ["draw_calls"] = 1L, ["nodes"] = 3L, ["geometry_bytes"] = 100L, ["texture_bytes"] = 0L };
        var results = new List<PassResult> { new("simplify", "simplified"), new("weld", "welded") };
        var before = totals; var after = totals;
        var lines = InspectorModel.LogResults(results, before, after, new[] { "simplify.ratio=0.25" });
        Assert.Contains(lines, l => l.Trim() == "parameters: simplify.ratio=0.25");
        Assert.Single(lines, l => l.Trim().StartsWith("parameters:"));
        Assert.Equal(lines.FindIndex(l => l.StartsWith("Simplify") || l.Contains("simplified")) + 1, lines.FindIndex(l => l.Trim().StartsWith("parameters:")));
    }
}
