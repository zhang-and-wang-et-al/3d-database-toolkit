using System.Buffers.Binary;
using SceneKit;
using Xunit;

/// <summary>R3-C8: coverage-preserving alpha mips (<see cref="AlphaMips"/>), the DDS mip chain, the .osgt
/// useHardwareMipMapGeneration flag for textures with their own mips, and downscale_textures.keep_coverage.</summary>
public class AlphaMipsTests : IDisposable
{
    readonly List<string> dirs = new();

    string Fresh()
    {
        string d = Path.Combine(Path.GetTempPath(), $"skam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        dirs.Add(d);
        return d;
    }

    public void Dispose() { foreach (var d in dirs) { try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }

    static RawImage Rgba(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> f)
    {
        var img = new RawImage(w, h, 4);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = f(x, y);
                int i = 4 * (y * w + x);
                img.Pixels[i] = r; img.Pixels[i + 1] = g; img.Pixels[i + 2] = b; img.Pixels[i + 3] = a;
            }
        return img;
    }

    /// <summary>Passing share inside a rectangle of <paramref name="img"/> (RGBA).</summary>
    static double CoverageIn(RawImage img, int x0, int y0, int x1, int y1)
    {
        int pass = 0, n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                n++;
                if (img.Pixels[4 * (y * img.Width + x) + 3] / 255.0 >= AlphaMips.AlphaRef) pass++;
            }
        return n == 0 ? 0 : (double)pass / n;
    }

    static RawImage Checkerboard(int size) =>
        Rgba(size, size, (x, y) => ((x + y) % 2 == 0 ? ((byte)40, (byte)160, (byte)50, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)));

    [Fact]
    public void CheckerboardKeepsItsPassingShare()
    {
        var img = Checkerboard(64);
        Assert.Equal(0.5, AlphaMips.Coverage(img), 6);
        var levels = AlphaMips.Build(img);
        Assert.Equal(6, levels.Count);   // 32, 16, 8, 4, 2, 1
        Assert.Equal((1, 1), (levels[^1].Width, levels[^1].Height));
        // a single texel is either in or out: every level with more than one texel keeps half of it
        foreach (var l in levels.Where(l => l.Width * l.Height > 1))
            Assert.InRange(AlphaMips.Coverage(l), 0.45, 0.55);
        // the box-filtered chain loses it (every texel 128 or 127: all in or all out)
        var box = AlphaMips.Box(img);
        Assert.True(box.Take(5).All(l => AlphaMips.Coverage(l) is 0 or 1));
    }

    [Fact]
    public void OpaqueAndEmptyAreUnchanged()
    {
        foreach (byte a in new byte[] { 255, 0 })
        {
            var img = Rgba(16, 16, (x, y) => ((byte)(x * 10), (byte)(y * 10), (byte)50, a));
            var levels = AlphaMips.Build(img);
            Assert.Equal(4, levels.Count);
            foreach (var l in levels)
                for (int i = 3; i < l.Pixels.Length; i += 4) Assert.Equal(a, l.Pixels[i]);
        }
    }

    [Fact]
    public void ClassMipsContainOnlyLevel0Ids()
    {
        var rnd = new Random(3);
        var ids = new byte[] { 0, 3, 7 };
        var img = Rgba(32, 16, (x, y) => (ids[rnd.Next(3)], (byte)0, (byte)0, (byte)255));
        var levels = AlphaMips.Nearest(img);
        Assert.Equal(5, levels.Count);   // 16x8, 8x4, 4x2, 2x1, 1x1
        Assert.Equal((1, 1), (levels[^1].Width, levels[^1].Height));
        foreach (var l in levels)
            for (int i = 0; i < l.Pixels.Length; i += 4) Assert.Contains(l.Pixels[i], ids);
        // the top-left texel of each 2 x 2 block
        Assert.Equal(img.Pixels[4 * (2 * 32 + 2)], levels[0].Pixels[4 * (1 * 16 + 1)]);
    }

    [Fact]
    public void PerCellKeepsDenseAndSparseCells()
    {
        var rnd = new Random(11);
        var img = Rgba(64, 32, (x, y) =>
        {
            bool covered = x < 32 ? rnd.NextDouble() < 0.8 : rnd.NextDouble() < 0.1;
            return covered ? ((byte)40, (byte)160, (byte)50, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);
        });
        var cells = new List<Cards.CellRect> { new(0, 0, 32, 32), new(32, 0, 32, 32) };
        var levels = AlphaMips.Build(img, AlphaMips.AlphaRef, cells);
        foreach (var c in cells)
        {
            double c0 = CoverageIn(img, c.X, c.Y, c.X + c.W, c.Y + c.H);
            Assert.True(c0 > 0.05);
            for (int k = 1; (c.W >> k) >= 4 && (c.H >> k) >= 4; k++)
            {
                var l = levels[k - 1];
                double ck = CoverageIn(l, c.X >> k, c.Y >> k, (c.X + c.W) >> k, (c.Y + c.H) >> k);
                // within 10 %, or one texel's share where the level has too few texels to say 10 % (4 x 4: 1/16)
                int texels = (c.W >> k) * (c.H >> k);
                Assert.True(Math.Abs(ck - c0) <= Math.Max(0.1 * c0, 1.0 / texels), $"cell {c}, level {k}: {ck:F3} vs {c0:F3}");
            }
        }
    }

    [Fact]
    public void DdsWithoutMipsIsUnchanged()
    {
        var img = Rgba(3, 2, (x, y) => ((byte)(10 + x), (byte)(20 + y), (byte)30, (byte)(100 + x + y)));
        var a = ImageCodecs.WriteDds(img);
        Assert.Equal(a, ImageCodecs.WriteDds(img, null));
        Assert.Equal(128 + 3 * 2 * 4, a.Length);
        uint U(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
        Assert.Equal(0x100Fu, U(a, 8));
        Assert.Equal(12u, U(a, 20));
        Assert.Equal(0u, U(a, 28));
        Assert.Equal(0x1000u, U(a, 108));
        // the pre-change bytes: header fields and B, G, R, A texels
        var expected = new byte[128 + 24];
        "DDS "u8.CopyTo(expected);
        void P(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(at), v);
        P(4, 124); P(8, 0x100F); P(12, 2); P(16, 3); P(20, 12); P(76, 32); P(80, 0x41); P(88, 32);
        P(92, 0x00FF0000); P(96, 0x0000FF00); P(100, 0x000000FF); P(104, 0xFF000000); P(108, 0x1000);
        for (int p = 0; p < 6; p++)
        {
            int s = p * 4, t = 128 + p * 4;
            expected[t] = img.Pixels[s + 2]; expected[t + 1] = img.Pixels[s + 1]; expected[t + 2] = img.Pixels[s]; expected[t + 3] = img.Pixels[s + 3];
        }
        Assert.Equal(expected, a);
    }

    [Fact]
    public void DdsMipChainHeader()
    {
        var img = Rgba(8, 4, (x, y) => ((byte)(x * 30), (byte)(y * 60), (byte)90, (byte)((x + y) % 2 == 0 ? 255 : 0)));
        var mips = AlphaMips.Build(img);
        Assert.Equal(3, mips.Count);
        var d = ImageCodecs.WriteDds(img, mips);
        uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        Assert.Equal(0x20000u, U(d, 8) & 0x20000u);
        Assert.Equal(0x100Fu | 0x20000u, U(d, 8));
        Assert.Equal(32u, U(d, 20));
        Assert.Equal(4u, U(d, 28));
        Assert.Equal(0x401008u, U(d, 108));
        Assert.Equal(128 + 4 * (32 + 8 + 2 + 1), d.Length);
        // level 1 follows level 0: its first texel (B, G, R, A)
        Assert.Equal(mips[0].Pixels[2], d[128 + 128]);
        Assert.Equal(mips[0].Pixels[3], d[128 + 128 + 3]);

        var rgb = ImageCodecs.WithChannels(img, 3);
        var d3 = ImageCodecs.WriteDds(rgb, AlphaMips.Box(rgb));
        Assert.Equal(128 + 3 * (32 + 8 + 2 + 1), d3.Length);
        Assert.Equal(24u, U(d3, 20));
        Assert.Equal(4u, U(d3, 28));
    }

    /// <summary>The textured tree with its leaf texture as .dds (the atlas keeps the sources' format), cards made with
    /// <paramref name="prm"/> and saved to .osgt: the written .dds and the .osgt text.</summary>
    (byte[] dds, string osgt) DdsLeafCards(Dictionary<string, object> prm, string? alphaMips)
    {
        string dir = Fresh(), outDir = Fresh();
        var (sc, tree) = CardFixtures.TexturedTree(dir);
        File.WriteAllBytes(Path.Combine(dir, "leaf.dds"), ImageCodecs.WriteDds(CardFixtures.LeafImage()));
        var leaf = tree.Children.Single(c => c.Name == "crown").StateSet!.Textures.Single(t => t.Unit == 0);
        leaf.File = "leaf.dds"; leaf.Path = Path.Combine(dir, "leaf.dds"); leaf.Fmt = "dds";
        Companions.Detect(sc);
        prm["replace_cards.texture"] = 128;
        if (alphaMips != null) prm["replace_cards.alpha_mips"] = alphaMips;
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, prm, targets: new List<Node> { tree });
        Assert.Null(results[0].Error);
        string osgt = Path.Combine(outDir, "out.osgt");
        SceneIO.Save(sc, osgt);
        var atlas = Assert.Single(Directory.GetFiles(Path.Combine(outDir, "out_textures"), "*.dds"));
        return (File.ReadAllBytes(atlas), File.ReadAllText(osgt));
    }

    [Fact]
    public void CoverageAtlasAsDdsHasMipsAndOsgtSaysFalse()
    {
        var (d, osgt) = DdsLeafCards(new() { ["replace_cards.layout"] = "ground" }, "coverage");
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(28)));   // 128 ... 1
        Assert.Equal(0x401008u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(108)));
        Assert.Equal(128 + 4 * 21845, d.Length);   // 128^2 + 64^2 + ... + 1 texels
        AssertFlags(osgt, ownMips: true);
    }

    /// <summary>Controller ruling P7: the legacy crossed path writes its own mips too when alpha_mips is set to coverage;
    /// untouched, the .dds stays one level and every texture says TRUE.</summary>
    [Fact]
    public void LegacyCrossedFollowsAlphaMips()
    {
        var (d, osgt) = DdsLeafCards(new() { ["replace_cards.mode"] = "crossed" }, "coverage");
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(28)));
        AssertFlags(osgt, ownMips: true);
        var (d1, osgt1) = DdsLeafCards(new() { ["replace_cards.mode"] = "crossed" }, null);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(d1.AsSpan(28)));
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32LittleEndian(d1.AsSpan(108)));
        AssertFlags(osgt1, ownMips: false);
    }

    /// <summary>The .dds texture says useHardwareMipMapGeneration FALSE, every other texture TRUE (osgt: TRUE is the
    /// default and not written).</summary>
    static void AssertFlags(string text, bool ownMips)
    {
        var flags = new List<(string file, string flag)>();
        foreach (var chunk in text.Split("Texture2D {").Skip(1))
        {
            var f = System.Text.RegularExpressions.Regex.Match(chunk, @"(?:file|FileName) ""([^""]+)""");
            var m = System.Text.RegularExpressions.Regex.Match(chunk, @"useHardwareMipMapGeneration (\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Assert.True(f.Success, chunk);
            flags.Add((f.Groups[1].Value, m.Success ? m.Groups[1].Value : "TRUE"));
        }
        var dds = flags.Where(x => x.file.EndsWith(".dds", StringComparison.Ordinal)).ToList();
        Assert.Single(dds);
        Assert.Equal(ownMips ? "FALSE" : "TRUE", dds[0].flag);
        var others = flags.Where(x => !x.file.EndsWith(".dds", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(others);
        Assert.All(others, x => Assert.Equal("TRUE", x.flag));
    }

    /// <summary>The textured tree with leaf.png replaced by a <paramref name="size"/> px cut-out of scattered texels (15 %
    /// covered).</summary>
    static (Scene sc, Texture leaf, string png) SparseLeafTree(string dir, int size)
    {
        var (sc, tree) = CardFixtures.TexturedTree(dir);
        var rnd = new Random(5);
        var img = Rgba(size, size, (x, y) => rnd.NextDouble() < 0.15 ? ((byte)40, (byte)160, (byte)50, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        string png = Path.Combine(dir, "leaf.png");
        File.WriteAllBytes(png, ImageCodecs.WritePng(img));
        var leaf = tree.Children.Single(c => c.Name == "crown").StateSet!.Textures.Single(t => t.Unit == 0);
        leaf.Width = leaf.Height = size;
        leaf.FileBytes = new FileInfo(png).Length;
        return (sc, leaf, png);
    }

    [Fact]
    public void DownscaleWithoutKeepCoverageIsByteIdentical()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        byte[] Run(Dictionary<string, object> prm)
        {
            string dir = Fresh(), outDir = Fresh();
            var (sc, _, _) = SparseLeafTree(dir, 32);
            var (results, _, _) = Optimizer.Run(sc, new[] { "downscale_textures" }, prm);
            Assert.Null(results[0].Error);
            SceneIO.Save(sc, Path.Combine(outDir, "small.osgt"));
            return File.ReadAllBytes(Path.Combine(outDir, "small_textures", "leaf.png"));
        }
        var byDefault = Run(new() { ["downscale_textures.max_size"] = 8 });
        var off = Run(new() { ["downscale_textures.max_size"] = 8, ["downscale_textures.keep_coverage"] = false });
        string d = Fresh();
        var (_, _, png) = SparseLeafTree(d, 32);
        string plain = Path.Combine(d, "plain.png");
        Assert.True(new ManagedImageResizer().Resize(png, plain, 8, 8));
        Assert.Equal(byDefault, off);
        Assert.Equal(File.ReadAllBytes(plain), byDefault);
    }

    [Fact]
    public void DownscaleKeepCoverageKeepsLeafArea()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        string dir = Fresh(), outDir = Fresh();
        var (sc, leaf, png) = SparseLeafTree(dir, 256);
        var classBytes = File.ReadAllBytes(Path.Combine(dir, "leaf_Classes.png"));
        var (results, _, _) = Optimizer.Run(sc, new[] { "downscale_textures" }, new() { ["downscale_textures.max_size"] = 32, ["downscale_textures.keep_coverage"] = true });
        Assert.Null(results[0].Error);
        Assert.True(leaf.KeepCoverage);
        Assert.All(leaf.Companions!.Values.Where(c => c.IsClassMap), c => Assert.False(c.KeepCoverage));
        SceneIO.Save(sc, Path.Combine(outDir, "small.osgt"));
        var src = ImageCodecs.Read(png)!;
        var outp = ImageCodecs.Read(Path.Combine(outDir, "small_textures", "leaf.png"))!;
        Assert.Equal((32, 32), (outp.Width, outp.Height));
        double c0 = AlphaMips.Coverage(src), c1 = AlphaMips.Coverage(outp);
        Assert.True(Math.Abs(c1 - c0) <= 0.1 * c0, $"{c1:F3} vs {c0:F3}");
        // a plain resize loses it
        Assert.True(AlphaMips.Coverage(ImageCodecs.Resize(src, 32, 32)) < 0.5 * c0);
        // the class map companion is copied untouched
        Assert.Equal(classBytes, File.ReadAllBytes(Path.Combine(outDir, "small_textures", "leaf_Classes.png")));
    }

    [Fact]
    public void MeasureShowsTheBoxLossAndTheKeptShare()
    {
        var rnd = new Random(9);
        var img = Rgba(64, 64, (x, y) => rnd.NextDouble() < 0.2 ? ((byte)40, (byte)160, (byte)50, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var (box, kept) = AlphaMips.Measure(img, null);
        Assert.Equal(3, box.Length);
        Assert.Equal(3, kept.Length);
        Assert.All(kept, r => Assert.InRange(r, 0.9, 1.1));
        Assert.True(box[2] < 0.5, $"box 1/8: {box[2]:F3}");
        var cells = new List<Cards.CellRect> { new(0, 0, 32, 64), new(32, 0, 32, 64) };
        var (_, keptCells) = AlphaMips.Measure(img, cells);
        Assert.All(keptCells, r => Assert.InRange(r, 0.9, 1.1));
    }
}
