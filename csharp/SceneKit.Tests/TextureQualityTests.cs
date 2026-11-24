using SceneKit;
using Xunit;

/// <summary>R2-D5 (UX-29): the damage verdict measures texture shift, UV stretch and flipped UV triangles.</summary>
public class TextureQualityTests
{
    /// <summary>Shape compare plus texture compare of two geometries, before's state set describing the texture.</summary>
    static QualityReport Check(GeomData before, GeomData after, StateSet? ss, string seams = "keep")
    {
        var q = Quality.Compare(before, after);
        Quality.CompareTextured(q, before, after, ss, seams);
        return q;
    }

    [Fact]
    public void UnchangedUvsAreOk()
    {
        var (g, ss) = Meshes.TexturedGrid(8, 256);
        var (after, _) = Meshes.TexturedGrid(8, 256);
        var q = Check(g, after, ss);
        Assert.NotNull(q.TexShiftP95);
        Assert.True(q.TexShiftP95 < 0.01, $"shift {q.TexShiftP95}");
        Assert.True(q.UvStretch < 1.01, $"stretch {q.UvStretch}");
        Assert.Equal(0, q.UvFlipped);
        Assert.Equal("ok", q.Verdict);
    }

    [Fact]
    public void StretchedUvsAreChecked()
    {
        var (g, ss) = Meshes.TexturedGrid(8, 8);
        var (after, _) = Meshes.TexturedGrid(8, 8, (u, v) => (0.5 + 1.6 * (u - 0.5), v));
        var q = Check(g, after, ss);
        Assert.True(q.UvStretch > 1.5, $"stretch {q.UvStretch}");
        Assert.True(q.TexShiftP95 < 4, $"shift {q.TexShiftP95}");
        Assert.Equal("check", q.Verdict);
    }

    [Fact]
    public void ShiftedUvsAreDamaged()
    {
        var (g, ss) = Meshes.TexturedGrid(8, 256);
        var (after, _) = Meshes.TexturedGrid(8, 256, (u, v) => (u + 5.0 / 256, v));
        var q = Check(g, after, ss);
        Assert.True(q.TexShiftP95 >= 4.5, $"shift {q.TexShiftP95}");
        Assert.Equal("damaged", q.Verdict);
    }

    [Fact]
    public void FlippedUvTriangleIsDamaged()
    {
        const int n = 8;
        var (g, ss) = Meshes.TexturedGrid(n, 256, unshared: true);
        var (after, _) = Meshes.TexturedGrid(n, 256, unshared: true);
        // triangle 0 sits in cell (0, 0): mirror its u inside the cell (u -> 1/n - u)
        var uv = after.TexCoords[0].Data;
        for (int k = 0; k < 3; k++) uv[2 * k] = (double)(float)(1.0 / n) - uv[2 * k];
        var q = Check(g, after, ss);
        Assert.True(q.UvFlipped >= 1, $"flipped {q.UvFlipped}");
        Assert.Equal("damaged", q.Verdict);
    }

    [Fact]
    public void MirroredIslandIsFine()
    {
        var (g, ss) = Meshes.TexturedGrid(8, 256, (u, v) => (1 - u, v));
        var (after, _) = Meshes.TexturedGrid(8, 256, (u, v) => (1 - u, v));
        var q = Check(g, after, ss);
        Assert.Equal(0, q.UvFlipped);
        Assert.Equal("ok", q.Verdict);
    }

    [Fact]
    public void RebakeAndIgnoreNeverSayOkOnTexture()
    {
        var (g, ss) = Meshes.TexturedGrid(8, 256);
        var (after, _) = Meshes.TexturedGrid(8, 256);
        var rb = Check(g, after, ss, "rebake");
        Assert.Equal("textures rebaked", rb.TextureNote);
        Assert.StartsWith("SHAPE OK - textures rebaked", InspectorModel.VerdictLine(rb));
        var ig = Check(g, after, ss, "ignore");
        Assert.Equal("textures ignored", ig.TextureNote);
        Assert.StartsWith("SHAPE OK - textures ignored", InspectorModel.VerdictLine(ig));
    }

    [Fact]
    public void RebakeNoteStaysOnCheckLinesAndOkLinesHaveOneColon()
    {
        var check = new QualityReport { Verdict = "check", TextureNote = "textures rebaked", Warnings = { "flipped faces" } };
        Assert.Equal("CHECK - visible changes possible (textures rebaked):  flipped faces", InspectorModel.VerdictLine(check));
        var ok = new QualityReport { Verdict = "ok", TextureNote = "textures ignored", Warnings = { "w" }, Notes = { "n" } };
        Assert.Equal("SHAPE OK - textures ignored: compare the textured view;  w;  n", InspectorModel.VerdictLine(ok));
        var okNote = new QualityReport { Verdict = "ok", TextureNote = "textures rebaked", Notes = { "n" } };
        Assert.Equal("SHAPE OK - textures rebaked: compare the textured view;  n", InspectorModel.VerdictLine(okNote));
    }

    [Fact]
    public void LargestTextureWithoutUvsFallsBackToOneWithUvs()
    {
        // a large detail texture on unit 1 without texcoords: measure the unit-0 texture that has them
        var (g, ss) = Meshes.TexturedGrid(8, 256);
        var (after, _) = Meshes.TexturedGrid(8, 256);
        ss.Textures.Add(new Texture { File = "detail.png", Width = 2048, Height = 2048, Unit = 1, Exists = true });
        var q = Check(g, after, ss);
        Assert.Null(q.TextureNote);
        Assert.NotNull(q.TexShiftP95);
    }

    [Fact]
    public void UntexturedSaysNoTexture()
    {
        var (g, _) = Meshes.TexturedGrid(8, 256);
        var (after, _) = Meshes.TexturedGrid(8, 256);
        var q = Check(g, after, null);
        Assert.Equal("no texture", q.TextureNote);
        Assert.Equal("OK - looks unchanged (no texture)", InspectorModel.VerdictLine(q));
    }

    [Fact]
    public void MissingTextureFileSkipsTexelMetrics()
    {
        var (g, ss) = Meshes.TexturedGrid(8, 0);
        var (after, _) = Meshes.TexturedGrid(8, 0, (u, v) => (u + 0.25, v));
        var shape = Quality.Compare(g, after).Verdict;
        var q = Check(g, after, ss);
        Assert.Null(q.TexShiftP95);
        Assert.Equal("texture size unknown", q.TextureNote);
        Assert.Equal(shape, q.Verdict);
    }

    [Fact]
    public void MixedTexturedAndUntexturedAggregates()
    {
        var (g, ss) = Meshes.TexturedGrid(8, 256);
        var (after, _) = Meshes.TexturedGrid(8, 256);
        var textured = Check(g, after, ss);
        var plain = Check(g, after, null);
        Assert.Equal("ok", textured.Verdict);
        Assert.Equal("no texture", plain.TextureNote);
        var a = InspectorModel.Aggregate(new[] { textured, plain });
        Assert.Null(a.TextureNote);
        Assert.NotNull(a.TexShiftP95);
    }
}
