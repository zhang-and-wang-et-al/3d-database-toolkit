using SceneKit;
using Xunit;

public class ReportTextTests
{
    [Fact] public void ShortPathsAreUnchanged() => Assert.Equal("a/b/c/d", ReportText.MiddlePath("a/b/c/d", 3));

    [Fact] public void LongPathsKeepTileAndLastTwoLevels() =>
        Assert.Equal("master/tiles/tile_0_0/…/ground/terrain",
            ReportText.MiddlePath("master/tiles/tile_0_0/tile_0_0.flt/tile/tile/ground/terrain", 40));

    [Fact] public void TrianPathKeepsTheTileWhenVeryTight() =>
        Assert.Equal("…/tile_0_0/…/ground/terrain",
            ReportText.MiddlePath("master/tiles/tile_0_0/tile_0_0.flt/tile/tile/ground/terrain", 28));

    [Fact] public void AutoNamedLevelIsNotTheTile() =>
        Assert.Equal("…/tile_0_0.flt/…/b/c", ReportText.MiddlePath("db/g1/tile_0_0.flt/a/b/c", 20));

    [Fact] public void NoDistinguishingLevelFallsBackToTwoHeadLevels()
    {
        Assert.Equal("a/b/…/e/f", ReportText.MiddlePath("a/b/c/d/e/f", 9));
        Assert.Equal("a/…/e/f", ReportText.MiddlePath("a/b/c/d/e/f", 8));
    }

    [Fact] public void StillTooLongDropsToOneHeadLevel() =>
        Assert.Equal("master/…/tile/terrain",
            ReportText.MiddlePath("master/tile_with_a_very_long_name/another_long_level_name/tile/tile/terrain", 25));

    [Fact] public void NeverCutsInsideALevel()
    {
        string input = "master/tile_0_0/tile_0_0.flt/lod/tile/terrain";
        var levels = input.Split('/').ToHashSet();
        for (int max = 1; max < 50; max++)
            foreach (var part in ReportText.MiddlePath(input, max).Split('/'))
                Assert.True(part == "…" || levels.Contains(part), $"max {max}: {part}");
    }

    [Fact]
    public void ReviewNoteCarriesTheResultLine()
    {
        // R3-C13: the Review row of a per-part proxy is the per-object result line, and its verdict never says OK
        var (sc, car) = Meshes.SynthCar(true);
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, new Dictionary<string, object>
        {
            ["replace_proxy.parts"] = "per_part", ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 48, ["replace_proxy.share"] = false,
        }, targets: new List<Node> { car });
        Assert.Null(results[0].Error);
        var changes = results[0].Changes.Where(c => c.PassName == "replace_proxy").ToList();
        Assert.NotEmpty(changes);
        foreach (var c in changes)
        {
            string text = InspectorModel.ChangeCheckText(c);
            Assert.StartsWith("replaced (1 object(s) replaced with a per-part proxy:", text);
            Assert.EndsWith(": compare before / after in the textured 3D preview", text);
            Assert.DoesNotContain("OK", text);
            Assert.NotEqual("ok", InspectorModel.ChangeVerdict(c));
        }
    }
}
