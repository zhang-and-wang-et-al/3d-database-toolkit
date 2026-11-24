using System.Text.RegularExpressions;
using SceneKit;
using Xunit;

/// <summary>R3-C9: every card bake measures its leaf area at 1/2, 1/4 and 1/8 size; replace_cards changes get a cards
/// verdict (OK / Check with reason) instead of the exemption line; the pass result carries cards.* record keys.</summary>
public class CardVerdictTests : IDisposable
{
    readonly List<string> dirs = new();

    string Fresh()
    {
        string d = Path.Combine(Path.GetTempPath(), $"skcv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        dirs.Add(d);
        return d;
    }

    public void Dispose() { foreach (var d in dirs) { try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }

    static PassResult Cards(Scene sc, Node tree, Dictionary<string, object> prm)
    {
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, prm, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        Assert.NotEmpty(results[0].Changes);
        return results[0];
    }

    [Fact]
    public void DenseCardsAreOk()
    {
        var (sc, tree) = CardFixtures.TexturedTree(Fresh(), leaves: 600);
        var r = Cards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 256 });
        Assert.All(r.Changes, c =>
        {
            Assert.NotNull(c.Quality);
            Assert.True(c.Quality!.Verdict == "ok", string.Join("; ", c.Quality.Warnings) + " | " + r.Message);
            Assert.StartsWith("leaf area at distance", c.Quality.Headline);
        });
    }

    [Fact]
    public void SparseCardsAreCheckWithTheFix()
    {
        // 30 leaves at texture 128 (cells about 42 px): the box-filtered mips keep about 40 % at 1/8 size; at the default
        // 512 px the leaves are large enough to keep 97 %
        var (sc, tree) = CardFixtures.TexturedTree(Fresh(), leaves: 30);
        var r = Cards(sc, tree, new() { ["replace_cards.layout"] = "crossed4", ["replace_cards.texture"] = 128 });
        Assert.All(r.Changes, c =>
        {
            Assert.True(c.Quality!.Verdict == "check", r.Message);
            Assert.Contains(c.Quality.Warnings, w => w.Contains("renderer mips will thin the leaves; use .dds with keep_coverage"));
        });
    }

    [Fact]
    public void ClassFillMakesCheck()
    {
        var (sc, tree) = CardFixtures.TexturedTree(Fresh(), leaves: 600, classMaps: false);   // only the crown has a class map
        var r = Cards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 256 });
        Assert.All(r.Changes, c =>
        {
            Assert.Equal("check", c.Quality!.Verdict);
            Assert.Contains(c.Quality.Warnings, w => w.Contains("filled with class 0"));
        });
    }

    [Fact]
    public void OldTextStaysAsDetail()
    {
        var (sc, tree) = CardFixtures.TexturedTree(Fresh(), leaves: 30);
        var r = Cards(sc, tree, new() { ["replace_cards.layout"] = "crossed4" });
        Assert.All(r.Changes, c =>
        {
            Assert.NotNull(c.Quality);
            var text = InspectorModel.ChangeCheckText(c);
            Assert.Contains("compare before / after in the textured 3D preview", text);
            Assert.StartsWith("leaf area at distance", text);
        });
    }

    /// <summary>Controller ruling P8: the replace_proxy change text is pinned to what the code produced before R3-C9.</summary>
    [Fact]
    public void ProxyKeepsItsExemptionLine()
    {
        var (sc, tree) = ReplaceTests.Tree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" },
            new() { ["replace_proxy.texture"] = 32, ["replace_proxy.voxels"] = 32, ["replace_proxy.ratio"] = 0.5 }, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        var c = Assert.Single(results[0].Changes);
        Assert.Null(c.Quality);
        Assert.Equal(ProxyText, InspectorModel.ChangeCheckText(c));
        Assert.Empty(results[0].Record);
    }

    const string ProxyText = "replaced (textured proxy): compare before / after in the textured 3D preview";

    [Fact]
    public void NeverOkWithoutMeasurement()
    {
        var q = Optimizer.CardVerdict(new CardBake { CoverageBox = new[] { double.NaN, double.NaN, double.NaN } });
        Assert.Equal("check", q.Verdict);
        Assert.Contains("leaf area not measured", q.Warnings);
        var q2 = Optimizer.CardVerdict(new CardBake { CoverageBox = Array.Empty<double>() });
        Assert.Equal("check", q2.Verdict);
        Assert.Contains("leaf area not measured", q2.Warnings);
    }

    [Fact]
    public void ACheckNeverShowsEighty()
    {
        var q = Optimizer.CardVerdict(new CardBake { CoverageBox = new[] { 0.9, 0.85, 0.7996 }, Ext = ".png" });
        Assert.Equal("check", q.Verdict);
        Assert.Equal("leaf area at distance 79.9 %", q.Headline);
        Assert.Contains("79.9 %", Assert.Single(q.Warnings));
        Assert.DoesNotContain("80 %", q.Headline);
        Assert.Equal("79.9", Optimizer.Pct(0.7996));
        Assert.Equal("80.0", Optimizer.Pct(0.80));
        Assert.Equal("80.4", Optimizer.Pct(0.8049));
        Assert.Equal("78", Optimizer.Pct(0.78));
        Assert.Equal("85", Optimizer.Pct(0.85));
        Assert.Equal("?", Optimizer.Pct(double.NaN));
    }

    [Fact]
    public void RecordKeysNameTheLayout()
    {
        var (sc, tree) = CardFixtures.TexturedTree(Fresh());
        var r = Cards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 128 });
        Assert.Contains("cards.layout=ground", r.Record);
        Assert.Contains("cards.triangles=16", r.Record);
        Assert.Contains("cards.pictures=8", r.Record);
        Assert.Contains("cards.mips=renderer", r.Record);
        Assert.Single(r.Record, k => Regex.IsMatch(k, @"^cards\.coverage=box \d\.\d\d/\d\.\d\d/\d\.\d\d kept \d\.\d\d/\d\.\d\d/\d\.\d\d$"));
        Assert.Contains(r.Record, k => k.StartsWith("cards.cell_px=", StringComparison.Ordinal));
        Assert.Matches(@"leaf area at 1/2, 1/4, 1/8 size: box \d+/\d+/\d+ %, kept \d+/\d+/\d+ %", r.Message);
    }

    /// <summary>Task 7 review: a crowned billboard is one bake with two card geometries (the turning card and the fixed
    /// crown); the change list shows both, and both carry the bake's verdict.</summary>
    [Fact]
    public void CrownedBillboardGeometriesBothCarryTheVerdict()
    {
        var (sc, tree) = CardFixtures.TexturedTree(Fresh(), leaves: 600);
        var r = Cards(sc, tree, new() { ["replace_cards.mode"] = "billboard", ["replace_cards.horizontal"] = 1, ["replace_cards.texture"] = 256 });
        Assert.Equal(2, r.Changes.Count);
        Assert.All(r.Changes, c => Assert.NotNull(c.Quality));
        Assert.Same(r.Changes[0].Quality, r.Changes[1].Quality);
    }

    [Fact]
    public void NonDdsNoteSaysCoverageNotWritten()
    {
        var (sc, tree) = CardFixtures.TexturedTree(Fresh());
        var r = Cards(sc, tree, new() { ["replace_cards.layout"] = "ground", ["replace_cards.texture"] = 128, ["replace_cards.alpha_mips"] = "coverage" });
        Assert.Contains("coverage mips not written: .png holds one level", r.Message);
        Assert.Matches(@"the renderer's mips keep \d+ % of the leaf area at 1/8 size", r.Message);
        Assert.Contains("cards.mips=coverage not written: .png", r.Record);
    }
}
