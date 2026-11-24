using System.Buffers.Binary;
using SceneKit;
using Xunit;

/// <summary>TX6: texture wrap / filter modes read, edited (undoable), written for generated and loaded state sets.</summary>
public class TextureSamplingTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"sksamp_{Guid.NewGuid():N}");

    public TextureSamplingTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    static GeomData Quad()
    {
        var g = new GeomData { Vertices = new VArray(new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(new double[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: new[] { 0, 1, 2, 0, 2, 3 }, indexType: "DrawElementsUShort"));
        return g;
    }

    string Png(string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, ImageCodecs.WritePng(new RawImage(16, 16, 3)));
        return p;
    }

    /// <summary>A generated scene with one texture, saved as <paramref name="ext"/>; the text can be edited before loading.</summary>
    string Saved(string ext, Texture t, Func<string, string>? edit = null)
    {
        Png("a.png");
        var geo = new Node(Kind.Geometry, "osg::Geometry", "g") { Geom = Quad() };
        var holder = new Node(Kind.Geode, "osg::Geode", "h") { StateSet = new StateSet { Textures = { t } }, Children = { geo } };
        var p = Path.Combine(dir, "scene" + ext);
        Scene sc;
        if (ext == ".osg")
        {
            // the legacy writer is used for scenes read from .osg: start from one
            var src = Path.Combine(dir, "empty.osg");
            File.WriteAllText(src, "Group {\n  name \"root\"\n  num_children 0\n}\n");
            sc = SceneIO.Load(src);
            sc.Root!.Children.Add(holder);
        }
        else sc = new Scene(p, "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { holder } } };
        SceneIO.Save(sc, p);
        if (edit != null) File.WriteAllText(p, edit(File.ReadAllText(p)));
        return p;
    }

    static Texture Only(Scene sc) => sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Distinct().Single();

    static Texture A() => new() { File = "a.png", Exists = true, Width = 16, Height = 16, Fmt = "png" };

    [Fact]
    public void GeneratedStateSetsWriteTheChosenModes()
    {
        var t = A();
        t.WrapS = "CLAMP_TO_EDGE"; t.WrapT = "MIRROR"; t.MinFilter = "NEAREST"; t.MagFilter = "NEAREST"; t.MaxAnisotropy = 8;
        var osgt = File.ReadAllText(Saved(".osgt", t));
        Assert.Contains("WRAP_S CLAMP_TO_EDGE", osgt);
        Assert.Contains("WRAP_T MIRROR", osgt);
        Assert.Contains("MIN_FILTER NEAREST ", osgt);
        Assert.Contains("MAG_FILTER NEAREST", osgt);
        Assert.Contains("MaxAnisotropy 8", osgt);
        var osg = File.ReadAllText(Saved(".osg", t));
        Assert.Contains("wrap_s CLAMP_TO_EDGE", osg);
        Assert.Contains("wrap_t MIRROR", osg);
        Assert.Contains("min_filter NEAREST\n", osg.Replace("\r", ""));
        Assert.Contains("maxAnisotropy 8", osg);
        // defaults when nothing is set
        var plain = File.ReadAllText(Saved(".osgt", A()));
        Assert.Contains("WRAP_S REPEAT", plain);
        Assert.Contains("MIN_FILTER LINEAR_MIPMAP_LINEAR", plain);
    }

    [Theory]
    [InlineData(".osgt")]
    [InlineData(".osg")]
    public void ModesAreReadFromTheFile(string ext)
    {
        var t = A();
        t.WrapS = "CLAMP"; t.WrapT = "CLAMP_TO_BORDER"; t.MinFilter = "LINEAR"; t.MagFilter = "NEAREST"; t.MaxAnisotropy = 4;
        var tex = Only(SceneIO.Load(Saved(ext, t)));
        Assert.Equal("CLAMP", tex.WrapS);
        Assert.Equal("CLAMP_TO_BORDER", tex.WrapT);
        Assert.Equal("LINEAR", tex.MinFilter);
        Assert.Equal("NEAREST", tex.MagFilter);
        Assert.Equal(4, tex.MaxAnisotropy);
    }

    [Fact]
    public void OsgtSynonymIsNormalised()
    {
        var p = Saved(".osgt", A(), s => s.Replace("WRAP_S REPEAT", "WRAP_S MIRRORED_REPEAT"));
        Assert.Equal("MIRROR", Only(SceneIO.Load(p)).WrapS);
    }

    [Theory]
    [InlineData(".osgt", "WRAP_S", "MIN_FILTER", "MaxAnisotropy")]
    [InlineData(".osg", "wrap_s", "min_filter", "maxAnisotropy")]
    public void EditSaveReloadAndUndo(string ext, string wrapKey, string minKey, string anisoKey)
    {
        // the file has no MaxAnisotropy: the edit inserts it, undo + save takes it out again
        var p = Saved(ext, A(), s => string.Join("\n", s.Replace("\r", "").Split('\n').Where(l => !l.TrimStart().StartsWith(anisoKey))));
        var sc = SceneIO.Load(p);
        var tex = Only(sc);
        Assert.Equal("REPEAT", tex.WrapS);
        Assert.Null(tex.MaxAnisotropy);
        var h = new UndoHistory();
        var r = TextureEdits.SetSampling(sc, tex, "CLAMP_TO_EDGE", "CLAMP_TO_EDGE", "LINEAR", "NEAREST", 16, h);
        Assert.True(r.Ok, r.Message);
        Assert.True(SceneIO.SceneModified(sc));
        var outp = Path.Combine(dir, "edited" + ext);
        SceneIO.Save(sc, outp);
        var text = File.ReadAllText(outp);
        Assert.Contains($"{wrapKey} CLAMP_TO_EDGE", text);
        Assert.Contains($"{minKey} LINEAR", text);
        var back = Only(SceneIO.Load(outp));
        Assert.Equal("CLAMP_TO_EDGE", back.WrapS);
        Assert.Equal("CLAMP_TO_EDGE", back.WrapT);
        Assert.Equal("LINEAR", back.MinFilter);
        Assert.Equal("NEAREST", back.MagFilter);
        Assert.Equal(16, back.MaxAnisotropy);
        Assert.Equal("a.png", back.File);
        Assert.True(back.Exists);

        h.Undo();
        Assert.Equal("REPEAT", tex.WrapS);
        Assert.Equal("LINEAR_MIPMAP_LINEAR", tex.MinFilter);
        Assert.Null(tex.MaxAnisotropy);
        Assert.False(SceneIO.SceneModified(sc));
        var undone = Path.Combine(dir, "undone" + ext);
        SceneIO.Save(sc, undone);
        Assert.DoesNotContain(anisoKey, File.ReadAllText(undone));
        var u = Only(SceneIO.Load(undone));
        Assert.Equal("REPEAT", u.WrapS);
        Assert.Equal("LINEAR_MIPMAP_LINEAR", u.MinFilter);
    }

    [Fact]
    public void InsertedModesKeepTheSerializerOrder()
    {
        // an osgt file without any sampling statements: inserted ones come before the Image
        var p = Saved(".osgt", A(), s => string.Join("\n", s.Replace("\r", "").Split('\n')
            .Where(l => !(l.TrimStart().StartsWith("WRAP_") || l.TrimStart().StartsWith("MIN_FILTER") || l.TrimStart().StartsWith("MAG_FILTER")))));
        var sc = SceneIO.Load(p);
        var tex = Only(sc);
        Assert.Null(tex.WrapS);
        Assert.True(TextureEdits.SetSampling(sc, tex, "CLAMP_TO_EDGE", null, null, "NEAREST", null).Ok);
        var outp = Path.Combine(dir, "ins.osgt");
        SceneIO.Save(sc, outp);
        var text = File.ReadAllText(outp);
        int ws = text.IndexOf("WRAP_S CLAMP_TO_EDGE", StringComparison.Ordinal), mag = text.IndexOf("MAG_FILTER NEAREST", StringComparison.Ordinal);
        int img = text.IndexOf("Image TRUE", StringComparison.Ordinal);
        Assert.True(ws > 0 && mag > ws && img > mag, text);
        var back = Only(SceneIO.Load(outp));
        Assert.Equal("CLAMP_TO_EDGE", back.WrapS);
        Assert.Null(back.WrapT);
        Assert.Equal("NEAREST", back.MagFilter);
    }

    [Fact]
    public void InvalidValuesAreRefused()
    {
        var t = A();
        var sc = new Scene(Path.Combine(dir, "x.osgt"), "osgt") { Root = new Node(Kind.Geode, "osg::Geode", "h") { StateSet = new StateSet { Textures = { t } } } };
        Assert.False(TextureEdits.SetSampling(sc, t, "WOBBLE", null, null, null, null).Ok);
        Assert.False(TextureEdits.SetSampling(sc, t, null, null, null, "LINEAR_MIPMAP_LINEAR", null).Ok);   // not a magnification filter
        Assert.False(TextureEdits.SetSampling(sc, t, null, null, null, null, 0.5).Ok);
        Assert.Null(t.WrapS);
    }

    [Fact]
    public void OpenFlightAttrFileIsRead()
    {
        Png("a.png");
        var attr = new byte[216];
        BinaryPrimitives.WriteInt32BigEndian(attr.AsSpan(0), 16);
        BinaryPrimitives.WriteInt32BigEndian(attr.AsSpan(4), 16);
        BinaryPrimitives.WriteInt32BigEndian(attr.AsSpan(28), 0);   // min: point
        BinaryPrimitives.WriteInt32BigEndian(attr.AsSpan(32), 1);   // mag: bilinear
        BinaryPrimitives.WriteInt32BigEndian(attr.AsSpan(36), 1);   // wrap: clamp
        BinaryPrimitives.WriteInt32BigEndian(attr.AsSpan(40), 2);   // u: none (use wrap)
        BinaryPrimitives.WriteInt32BigEndian(attr.AsSpan(44), 3);   // v: mirrored repeat
        File.WriteAllBytes(Path.Combine(dir, "a.png.attr"), attr);
        var t = Images.FillTexture(new Texture { File = "a.png" }, dir);
        FltAttr.Read(t);
        Assert.Equal("CLAMP_TO_EDGE", t.WrapS);
        Assert.Equal("MIRROR", t.WrapT);
        Assert.Equal("NEAREST", t.MinFilter);
        Assert.Equal("LINEAR", t.MagFilter);
    }
}
