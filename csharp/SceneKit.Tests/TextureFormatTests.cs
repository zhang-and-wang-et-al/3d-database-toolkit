using System.Buffers.Binary;
using SceneKit;
using Xunit;

/// <summary>Saved textures keep the data format of their sources (B1): baked atlases take the format of the textures they
/// were baked from, downscaled copies keep their file type, and OpenFlight .attr files follow written images.</summary>
public class TextureFormatTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"sktexfmt_{Guid.NewGuid():N}");

    public TextureFormatTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    static RawImage Img(int ch, int size = 32)
    {
        var im = new RawImage(size, size, ch);
        for (int p = 0; p < size * size; p++)
            for (int c = 0; c < ch; c++) im.Pixels[p * ch + c] = (byte)(c == 3 ? 255 : 40 + 60 * c + (p % size < size / 2 ? 0 : 50));
        return im;
    }

    string File(string name, byte[] data)
    {
        var p = Path.Combine(dir, name);
        System.IO.File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>An .attr file: texel counts, repeat everywhere, mipmap trilinear / bilinear, and a marker byte at 100.</summary>
    static byte[] Attr(int w, int h)
    {
        var d = new byte[216];
        BinaryPrimitives.WriteInt32BigEndian(d, w);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4), h);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(28), 6);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(32), 1);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(40), 2);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(44), 2);
        d[100] = 0x5A;
        return d;
    }

    Texture Tex(string name, int unit = 0) => Images.FillTexture(new Texture { File = name, Unit = unit }, dir);

    /// <summary>The 16 x 16 OpenFlight grid with UVs on units 0 and 1 and the given textures.</summary>
    (Scene sc, Node obj) Grid(params Texture[] textures)
    {
        var src = File("grid.flt", FltWriteTests.Grid(16));
        var sc = SceneIO.Load(src);
        var geo = sc.IterNodes().First(n => n.Kind == Kind.Geometry);
        var g0 = geo.Geom!;
        var uv = Enumerable.Range(0, g0.NumVertices).SelectMany(i => new[] { g0.Vertices!.Data[3 * i] / 16, g0.Vertices.Data[3 * i + 1] / 16 }).ToArray();
        g0.TexCoords[0] = new VArray(uv, 2, "Vec2Array");
        g0.TexCoords[1] = new VArray(uv.Select(x => 1 - x).ToArray(), 2, "Vec2Array");
        geo.StateSet = new StateSet();
        geo.StateSet.Textures.AddRange(textures);
        return (sc, sc.IterNodes().First(n => n.Kind == Kind.Geode || n.Children.Contains(geo)));
    }

    /// <summary>An OpenFlight file whose texture palette names <paramref name="baseName"/> (8 characters, in place of
    /// base.rgb) on the faces' unit 0; loaded.</summary>
    (Scene sc, Texture tex) Palette(string baseName)
    {
        var b = FltTests.MultiTexBytes();
        var from = System.Text.Encoding.ASCII.GetBytes("base.rgb");
        int at = b.AsSpan().IndexOf(from);
        System.Text.Encoding.ASCII.GetBytes(baseName).CopyTo(b, at);
        var sc = SceneIO.Load(File("pal.flt", b));
        var tex = sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).First(t => t.File == baseName);
        Assert.True(tex.Exists);
        return (sc, tex);
    }

    static PassResult Run(Scene sc, Node obj, string pass)
    {
        var (results, _, _) = Optimizer.Run(sc, new[] { pass }, new Dictionary<string, object> { [pass + ".texture"] = 64 }, targets: new List<Node> { obj });
        Assert.All(results, r => Assert.Null(r.Error));
        return results.Single(r => r.Name == pass);
    }

    string Save(Scene sc, string name = "grid.out.flt")
    {
        var outp = Path.Combine(dir, name);
        SceneIO.Save(sc, outp);
        return outp;
    }

    string TexDir(string outp) => Path.Combine(dir, Path.GetFileNameWithoutExtension(outp) + "_textures");

    static List<Texture> TexturesOf(string path) => SceneIO.Load(path).IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).ToList();

    static int SgiZ(string path) => BinaryPrimitives.ReadUInt16BigEndian(System.IO.File.ReadAllBytes(path).AsSpan(10));

    [Fact]
    public void CardsFromAPngSourceInAnFltSceneAreWrittenAsPng()
    {
        File("leaf.png", ImageCodecs.WritePng(Img(4)));
        var (sc, obj) = Grid(Tex("leaf.png"));
        Run(sc, obj, "replace_cards");
        var outp = Save(sc);
        var files = Directory.GetFiles(TexDir(outp));
        var png = Assert.Single(files);
        Assert.EndsWith("_cards.png", png);
        Assert.Equal(4, ImageCodecs.Read(png)!.Channels);
        Assert.False(System.IO.File.Exists(png + ".attr"));   // the source has none: none is invented
        Assert.Contains(TexturesOf(outp), t => t.Exists && t.File.EndsWith("_cards.png"));   // the palette points at it
    }

    [Fact]
    public void CardsFromAnRgbaSourceAreRgbaWithFourChannelsAndTheAttrFollows()
    {
        File("leaf.rgba", ImageCodecs.WriteSgi(Img(4)));
        var attr = File("leaf.rgba.attr", Attr(32, 32));
        var (sc, obj) = Grid(Tex("leaf.rgba"));
        Run(sc, obj, "replace_cards");
        var outp = Save(sc);
        var rgba = Assert.Single(Directory.GetFiles(TexDir(outp), "*.rgba"));
        Assert.Equal(4, SgiZ(rgba));
        var d = System.IO.File.ReadAllBytes(rgba + ".attr");
        var src = System.IO.File.ReadAllBytes(attr);
        Assert.Equal(src.Length, d.Length);
        Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(d));                    // texel counts of the atlas
        Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(40)));          // atlas: clamp in u and v
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(44)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(28)));          // the rest is the source's
        Assert.Equal(0x5A, d[100]);
        var back = Assert.Single(TexturesOf(outp), t => t.File.EndsWith(".rgba"));
        Assert.Equal("CLAMP_TO_EDGE", back.WrapS);
    }

    [Fact]
    public void RgbSourceCardsStayRgbWithFourChannels()
    {
        File("leaf.rgb", ImageCodecs.WriteSgi(Img(3)));
        var (sc, obj) = Grid(Tex("leaf.rgb"));
        Run(sc, obj, "replace_cards");
        var rgb = Assert.Single(Directory.GetFiles(TexDir(Save(sc))));
        Assert.EndsWith("_cards.rgb", rgb);
        Assert.Equal(4, SgiZ(rgb));   // coverage needs alpha: SGI .rgb holds it
    }

    [Fact]
    public void AnExtraUnitFromAGreyscaleIntStaysGreyscaleInt()
    {
        File("base.rgb", ImageCodecs.WriteSgi(Img(3)));
        File("light.int", ImageCodecs.WriteSgi(Img(1)));
        var (sc, obj) = Grid(Tex("base.rgb"), Tex("light.int", 1));
        Run(sc, obj, "replace_proxy");
        var outp = Save(sc);
        var u1 = Assert.Single(Directory.GetFiles(TexDir(outp)), f => f.Contains("_u1"));
        Assert.EndsWith(".int", u1);
        Assert.Equal(1, SgiZ(u1));
        Assert.Equal(3, SgiZ(Assert.Single(Directory.GetFiles(TexDir(outp)), f => !f.Contains("_u1"))));   // proxy: the source's RGB
    }

    [Fact]
    public void SixteenBitSgiSourcesGiveSixteenBitAtlases()
    {
        var b = ImageCodecs.WriteSgi(Img(3), 2);
        File("base.rgb", b);
        Assert.Equal(2, ImageCodecs.SgiBpc(Path.Combine(dir, "base.rgb")));
        var (sc, obj) = Grid(Tex("base.rgb"));
        Run(sc, obj, "replace_proxy");
        var rgb = Assert.Single(Directory.GetFiles(TexDir(Save(sc))));
        Assert.Equal(2, ImageCodecs.SgiBpc(rgb));
        Assert.NotNull(ImageCodecs.Read(rgb));
    }

    [Fact]
    public void AJpegBaseOfCardsBecomesPngWithANote()
    {
        // a JPEG source (decoded already): no alpha, but the cards' coverage needs it
        var jpg = new Texture { File = "bark.jpg", Baked = Img(3), Exists = true, Channels = 3, Width = 32, Height = 32 };
        var (sc, obj) = Grid(jpg);
        var r = Run(sc, obj, "replace_cards");
        Assert.Contains("baked from .jpg textures, written as .png", r.Message);
        var outp = Save(sc);
        Assert.EndsWith("_cards.png", Assert.Single(Directory.GetFiles(TexDir(outp))));
        Assert.Contains(sc.Warnings, w => w.Contains("written as .png"));
    }

    [Fact]
    public void AJpegSourceWithoutAlphaNeedsTheAppEncoderElsePng()
    {
        var jpg = new Texture { File = "bark.jpg", Baked = Img(3), Exists = true, Channels = 3, Width = 32, Height = 32 };
        var baked = new Texture { File = "x_proxy", Baked = Img(4), BakedFrom = new List<Texture> { jpg } };
        var with = TextureFormats.Choose(baked, null, flt: true, canEncode: e => e is ".jpg" or ".png");
        Assert.Equal((".jpg", 3), (with.Ext, with.Channels));
        Assert.Null(with.Note);
        var without = TextureFormats.Choose(baked, null, flt: true, canEncode: ImageCodecs.CanWrite);
        Assert.Equal(".png", without.Ext);
        Assert.Contains("no .jpg encoder", without.Note);
    }

    [Fact]
    public void FormatOfMixedSourcesIsTheMostCommonByTexelArea()
    {
        var small = Enumerable.Range(0, 3).Select(i => new Texture { File = $"s{i}.png", Width = 64, Height = 64, Channels = 3 });
        var big = new Texture { File = "big.rgb", Width = 256, Height = 256, Channels = 3 };
        var baked = new Texture { File = "x_proxy", Baked = Img(4), BakedFrom = small.Append(big).ToList() };
        Assert.Equal(".rgb", TextureFormats.Choose(baked, null, flt: false).Ext);
        // no source: the scene's most used format, else rgb for OpenFlight / png for OSG
        var none = new Texture { File = "x_rebake", Baked = Img(4) };
        Assert.Equal(".rgb", TextureFormats.Choose(none, null, flt: true).Ext);
        Assert.Equal(".png", TextureFormats.Choose(none, null, flt: false).Ext);
        var (sc, _) = Grid(new Texture { File = "a.tga", Width = 64, Height = 64 });
        Assert.Equal(".tga", TextureFormats.Choose(none, sc, flt: true).Ext);
    }

    [Fact]
    public void GreyscaleBaseBecomesIntaForCoverageAndRgbForColour()
    {
        var bw = new Texture { File = "g.int", Width = 32, Height = 32, Channels = 1 };
        var grey = new RawImage(8, 8, 4);
        for (int i = 0; i < grey.Pixels.Length; i++) grey.Pixels[i] = 128;
        var cards = new Texture { File = "c_cards", Baked = grey, BakedCoverage = true, BakedFrom = new List<Texture> { bw } };
        Assert.Equal((".inta", 2), (TextureFormats.Choose(cards, null, true).Ext, TextureFormats.Choose(cards, null, true).Channels));
        var proxy = new Texture { File = "c_proxy", Baked = grey, BakedFrom = new List<Texture> { bw } };
        Assert.Equal((".int", 1), (TextureFormats.Choose(proxy, null, true).Ext, TextureFormats.Choose(proxy, null, true).Channels));
        var coloured = new Texture { File = "c_proxy", Baked = Img(4), BakedFrom = new List<Texture> { bw } };
        Assert.Equal((".rgb", 3), (TextureFormats.Choose(coloured, null, true).Ext, TextureFormats.Choose(coloured, null, true).Channels));
    }

    [Fact]
    public void ADdsSourceGivesAnUncompressedDdsWithAReadableHeader()
    {
        File("bark.dds", ImageCodecs.WriteDds(Img(4)));
        var tex = Tex("bark.dds");
        Assert.True(tex.Exists);
        var (sc, obj) = Grid(tex);
        var r = Run(sc, obj, "replace_cards");
        Assert.Contains("uncompressed", r.Message);
        var outp = Save(sc);
        var dds = Assert.Single(Directory.GetFiles(TexDir(outp)));
        Assert.EndsWith("_cards.dds", dds);
        var d = System.IO.File.ReadAllBytes(dds);
        Assert.Equal("DDS ", System.Text.Encoding.ASCII.GetString(d, 0, 4));
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)));
        Assert.Equal(0x41u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80)));   // RGB | ALPHAPIXELS
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(88)));
        Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(104)));
        Assert.Equal(128 + 64 * 64 * 4, d.Length);
        var info = Images.Probe(dds)!;
        Assert.Equal((64, 64, "dds"), (info.Width, info.Height, info.Fmt));
        Assert.Contains(TexturesOf(outp), t => t.Exists && t.File.EndsWith("_cards.dds"));
    }

    [Fact]
    public void ResizedTgaStaysTgaAndItsAttrFollows()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        File("bark.tga", ImageCodecs.WriteTga(Img(3)));
        var attr = File("bark.tga.attr", Attr(32, 32));
        var (sc, tex) = Palette("bark.tga");
        Assert.True(TextureEdits.SetResize(sc, tex, 16, 16).Ok);
        var outp = Save(sc, "pal.out.flt");
        var tga = Path.Combine(TexDir(outp), "bark.tga");
        var info = Images.Probe(tga)!;
        Assert.Equal((16, 16, "tga", 3), (info.Width, info.Height, info.Fmt, info.Channels));
        Assert.Single(Directory.GetFiles(TexDir(outp)), f => f.EndsWith(".tga"));
        var d = System.IO.File.ReadAllBytes(tga + ".attr");
        var src = System.IO.File.ReadAllBytes(attr);
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(d));
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4)));
        Assert.Equal(src[8..], d[8..]);   // sampling unchanged: the source's bytes
        Assert.Contains(TexturesOf(outp), t => t.Exists && t.File == "pal.out_textures/bark.tga" && t.Width == 16);
    }

    [Fact]
    public void ResizedTexturesNextToAnOsgtGetNoAttr()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        File("bark.tga", ImageCodecs.WriteTga(Img(3)));
        File("bark.tga.attr", Attr(32, 32));
        var (sc, tex) = Palette("bark.tga");
        Assert.True(TextureEdits.SetResize(sc, tex, 16, 16).Ok);
        var outp = Save(sc, "pal.out.osgt");
        Assert.True(System.IO.File.Exists(Path.Combine(TexDir(outp), "bark.tga")));
        Assert.False(System.IO.File.Exists(Path.Combine(TexDir(outp), "bark.tga.attr")));
    }

    [Fact]
    public void ADdsThatCannotBeScaledIsSkippedWithAWarningNotConverted()
    {
        ImageResizer.Current ??= new ManagedImageResizer();
        if (ImageResizer.Current is not ManagedImageResizer) return;   // an app codec might read DDS
        File("bark.dds", ImageCodecs.WriteDds(Img(4)));
        var (sc, tex) = Palette("bark.dds");
        Assert.True(TextureEdits.SetResize(sc, tex, 16, 16).Ok);
        var outp = Save(sc, "pal.out.flt");
        Assert.False(Directory.Exists(TexDir(outp)) && Directory.GetFiles(TexDir(outp)).Length > 0);
        Assert.Contains(sc.Warnings, w => w.Contains("bark.dds not downscaled"));
        Assert.Contains(TexturesOf(outp), t => t.Exists && t.File == "bark.dds");   // still the original
    }

    [Fact]
    public void DefaultOutputKeepsTheSourceFormat()
    {
        var (sc, _) = Grid();
        Assert.EndsWith("grid.optimized.flt", Optimizer.DefaultOutputPath(sc));
        var conv = new Scene(Path.Combine(dir, "x.osgt"), "osgb") { ConvertedFrom = Path.Combine(dir, "x.osgb") };
        Assert.EndsWith("x.optimized.osgb", Optimizer.DefaultOutputPath(conv));
    }
}
