using SceneKit;
using Xunit;

/// <summary>R3-C1: today's crossed, billboard and LOD far-card output, byte for byte (hashes in data/cards_golden.json,
/// taken before any card change). Every later card task keeps these passing; the data is never regenerated.</summary>
public class CardGoldenTests : IDisposable
{
    readonly List<string> dirs = new();

    string Fresh()
    {
        string d = Path.Combine(Path.GetTempPath(), $"skcg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        dirs.Add(d);
        return d;
    }

    public void Dispose() { foreach (var d in dirs) { try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }

    /// <summary>Crossed 4 as replace_cards builds it: planes p at 180 p / 4 and 180 p / 4 + 180.</summary>
    static readonly double[] Crossed4 = { 0, 180, 45, 225, 90, 270, 135, 315 };

    static Dictionary<string, string> Render(IList<double> azimuths)
    {
        var res = Cards.Render(CardFixtures.Items(), azimuths, 128);
        Assert.NotNull(res);
        var r = new Dictionary<string, string> { ["atlas"] = CardGolden.Hash(res!.Atlas) };
        foreach (var (k, img) in res.Extra) r["extra/" + k] = CardGolden.Hash(img);
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var c in res.Cards)
            foreach (double x in c.Dir.Concat(new[] { c.U0, c.V0, c.U1, c.V1, c.S0, c.S1, c.Z0, c.Z1 }).Concat(res.Center))
                h.AppendData(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(x)));
        r["cards"] = Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        return r;
    }

    [Fact]
    public void RenderCrossed4IsByteIdentical() => CardGolden.AssertMatches("render.crossed4", Render(Crossed4));

    [Fact]
    public void RenderBillboardIsByteIdentical() => CardGolden.AssertMatches("render.billboard", Render(new[] { 90.0 }));

    static Dictionary<string, string> ReplaceCards(string dir, string outDir, string mode, int planes)
    {
        var (sc, a, b) = CardFixtures.TwoCopies(dir);
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, new()
        {
            ["replace_cards.mode"] = mode, ["replace_cards.planes"] = planes, ["replace_cards.texture"] = 128, ["replace_cards.share"] = true,
        }, targets: new List<Node> { a, b });
        Assert.Null(results[0].Error);
        return CardGolden.Capture(sc, outDir);
    }

    [Theory]
    [InlineData("pass.crossed4", "crossed", 4)]
    [InlineData("pass.crossed3", "crossed", 3)]
    [InlineData("pass.billboard", "billboard", 4)]
    public void ReplaceCardsIsByteIdentical(string name, string mode, int planes) =>
        CardGolden.AssertMatches(name, ReplaceCards(Fresh(), Fresh(), mode, planes));

    static Dictionary<string, string> LodCards(string dir, string outDir)
    {
        var (sc, tree) = CardFixtures.TexturedTree(dir);
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new()
        {
            ["generate_lod.far_level"] = "cards", ["generate_lod.min_tris"] = 0, ["generate_lod.proxy_texture"] = 128,
        }, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        return CardGolden.Capture(sc, outDir);
    }

    [Fact]
    public void LodFarCardsIsByteIdentical() => CardGolden.AssertMatches("pass.lodcards", LodCards(Fresh(), Fresh()));

    /// <summary>An OpenFlight host (FltWriteTests.Grid) in a fresh folder with the textured tree grafted in: the tree's image files
    /// are written next to the .flt, so the textures resolve from the FLT scene's folder.</summary>
    static Dictionary<string, string> FltCards(string dir, string outDir, string mode)
    {
        string host = Path.Combine(dir, "host.flt");
        File.WriteAllBytes(host, FltWriteTests.Grid(4));
        var sc = SceneIO.Load(host);
        var tree = CardFixtures.TexturedTreeNode(dir);
        sc.Root!.Children.Add(tree);
        Companions.Detect(sc);
        var prm = new Dictionary<string, object> { ["replace_cards.mode"] = mode, ["replace_cards.texture"] = 128 };
        if (mode == "crossed") { prm["replace_cards.planes"] = 4; prm["replace_cards.share"] = true; }
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, prm, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        return CardGolden.CaptureFlt(sc, outDir);
    }

    [Fact]
    public void FltCrossed4IsByteIdentical() => CardGolden.AssertMatches("flt.crossed4", FltCards(Fresh(), Fresh(), "crossed"));

    [Fact]
    public void FltBillboardIsByteIdentical() => CardGolden.AssertMatches("flt.billboard", FltCards(Fresh(), Fresh(), "billboard"));

    /// <summary>Two runs into fresh folders give the same hashes: nothing run-dependent (dates, GUIDs, folders) is left in them.</summary>
    [Fact]
    public void CaptureIsDeterministic()
    {
        var cases = new Dictionary<string, Func<Dictionary<string, string>>>
        {
            ["render.crossed4"] = () => Render(Crossed4),
            ["render.billboard"] = () => Render(new[] { 90.0 }),
            ["pass.crossed4"] = () => ReplaceCards(Fresh(), Fresh(), "crossed", 4),
            ["pass.crossed3"] = () => ReplaceCards(Fresh(), Fresh(), "crossed", 3),
            ["pass.billboard"] = () => ReplaceCards(Fresh(), Fresh(), "billboard", 4),
            ["pass.lodcards"] = () => LodCards(Fresh(), Fresh()),
            ["flt.crossed4"] = () => FltCards(Fresh(), Fresh(), "crossed"),
            ["flt.billboard"] = () => FltCards(Fresh(), Fresh(), "billboard"),
        };
        foreach (var (name, run) in cases)
            Assert.True(run().OrderBy(x => x.Key).SequenceEqual(run().OrderBy(x => x.Key)), $"{name}: two captures differ");
    }
}
