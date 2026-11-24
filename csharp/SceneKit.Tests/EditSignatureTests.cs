using SceneKit;
using Xunit;

/// <summary>History.EditSignature: the global edit counter (Node.Edits) plus the edited textures. Runs alone: other tests'
/// edits raise the global counter too.</summary>
[Collection("serial")]
public class EditSignatureTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"sksig_{Guid.NewGuid():N}");

    public EditSignatureTests() => Directory.CreateDirectory(dir);
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
    public void EditSignatureRisesWithEveryEdit()
    {
        var sc = TiledScene();
        var s0 = History.EditSignature(sc);
        var geo = sc.IterNodes(externals: true).First(n => n.Kind == Kind.Geometry);
        geo.Mark("x");
        var s1 = History.EditSignature(sc);
        Assert.Equal(s0 + 1, s1);
        var tex = new Texture { File = "t.png" };
        geo.StateSet = new StateSet { Textures = { tex } };
        Assert.Equal(s1, History.EditSignature(sc));
        tex.Resize = (1, 1);
        Assert.Equal(s1 + 1, History.EditSignature(sc));
    }

    [Fact]
    public void EditSignatureRisesWhenTheKeyIsAlreadyDirty()
    {
        var sc = TiledScene();
        var geo = sc.IterNodes(externals: true).First(n => n.Kind == Kind.Geometry);
        geo.Mark("verts");
        var s1 = History.EditSignature(sc);
        geo.Mark("verts");   // a second pass edits the same node again: the dirty set does not grow
        Assert.True(History.EditSignature(sc) > s1);
    }

    [Fact]
    public void EditSignatureRisesWithASecondTextureEdit()
    {
        var sc = TiledScene();
        var geo = sc.IterNodes(externals: true).First(n => n.Kind == Kind.Geometry);
        var tex = new Texture { File = "t.png" };
        geo.StateSet = new StateSet { Textures = { tex } };
        Assert.True(TextureEdits.SetSampling(sc, tex, "CLAMP_TO_EDGE", null, null, null, null).Ok);
        var s1 = History.EditSignature(sc);
        Assert.True(TextureEdits.SetSampling(sc, tex, "REPEAT", null, null, null, null).Ok);   // SamplingEdited was already set
        Assert.True(History.EditSignature(sc) > s1);
    }
}
