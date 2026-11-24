using SceneKit;
using Xunit;

public class TexturedRenderTests
{
    const int W = 64, H = 64;

    // a quad facing the camera, filling most of the frame; uv (0,0) bottom left, (1,1) top right
    static (double[] V, int[] T, double[] Uv, SoftRender.Camera cam) Quad()
    {
        var cam = new SoftRender.Camera(new double[3], 1.0);
        var (_, _, r, u) = cam.Basis();
        double s = 0.6;
        var V = new double[12];
        double[][] corners = { new[] { -1.0, -1.0 }, new[] { 1.0, -1.0 }, new[] { 1.0, 1.0 }, new[] { -1.0, 1.0 } };
        for (int i = 0; i < 4; i++)
            for (int k = 0; k < 3; k++) V[3 * i + k] = (corners[i][0] * r[k] + corners[i][1] * u[k]) * s;
        return (V, new[] { 0, 1, 2, 0, 2, 3 }, new[] { 0.0, 0, 1, 0, 1, 1, 0, 1 }, cam);
    }

    // 2x2 RGBA image, top row red | green, bottom row blue | white (rows are stored top down)
    static RawImage Img(byte alpha = 255)
    {
        var img = new RawImage(2, 2, 4);
        byte[][] px = { new byte[] { 255, 0, 0 }, new byte[] { 0, 255, 0 }, new byte[] { 0, 0, 255 }, new byte[] { 255, 255, 255 } };
        for (int i = 0; i < 4; i++) { Array.Copy(px[i], 0, img.Pixels, 4 * i, 3); img.Pixels[4 * i + 3] = alpha; }
        return img;
    }

    static (int r, int g, int b) Px(byte[] bgra, int x, int y) { int o = 4 * (y * W + x); return (bgra[o + 2], bgra[o + 1], bgra[o]); }

    static byte[] Draw(SoftRender.Mesh m, SoftRender.Camera cam, SoftRender.Paint paint) =>
        SoftRender.Render(new[] { m }, cam, W, H, shade: false, paint: paint).bgra;

    [Fact]
    public void TexturedQuadShowsTheTextureTheRightWayUp()
    {
        var (V, T, Uv, cam) = Quad();
        var tex = new Texture { Baked = Img(), Exists = true };
        var bgra = Draw(new SoftRender.Mesh(V, T, uv: Uv, tex: tex), cam, SoftRender.Paint.Textured);
        Assert.Equal((255, 0, 0), Px(bgra, 24, 24));     // top left of the screen = top left of the image
        Assert.Equal((0, 255, 0), Px(bgra, 40, 24));
        Assert.Equal((0, 0, 255), Px(bgra, 24, 40));
        Assert.Equal((255, 255, 255), Px(bgra, 40, 40));
    }

    [Fact]
    public void MaterialModeIgnoresTheTexture()
    {
        var (V, T, Uv, cam) = Quad();
        var tex = new Texture { Baked = Img(), Exists = true };
        var bgra = Draw(new SoftRender.Mesh(V, T, colors: new[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 }, uv: Uv, tex: tex), cam, SoftRender.Paint.Material);
        Assert.Equal(Px(bgra, 24, 24), Px(bgra, 40, 40));
    }

    [Fact]
    public void MissingTextureIsAMagentaChecker()
    {
        var (V, T, Uv, cam) = Quad();
        var tex = new Texture { File = "gone.rgb", Exists = false };
        var bgra = Draw(new SoftRender.Mesh(V, T, uv: Uv, tex: tex), cam, SoftRender.Paint.Textured);
        var seen = new HashSet<(int, int, int)>();
        for (int y = 16; y < 48; y += 2) for (int x = 16; x < 48; x += 2) seen.Add(Px(bgra, x, y));
        Assert.Contains((255, 0, 255), seen);
        Assert.Contains((0, 0, 0), seen);
    }

    [Fact]
    public void TextureWithoutUvsIsMagenta()
    {
        var (V, T, _, cam) = Quad();
        var tex = new Texture { Baked = Img(), Exists = true };
        var bgra = Draw(new SoftRender.Mesh(V, T, tex: tex), cam, SoftRender.Paint.Textured);
        Assert.Equal((255, 0, 255), Px(bgra, 32, 32));
    }

    [Fact]
    public void TransparentTexelsAreCutOut()
    {
        var (V, T, Uv, cam) = Quad();
        var tex = new Texture { Baked = Img(alpha: 0), Exists = true };
        var empty = SoftRender.Render(Array.Empty<SoftRender.Mesh>(), cam, W, H).bgra;
        var bgra = Draw(new SoftRender.Mesh(V, T, uv: Uv, tex: tex), cam, SoftRender.Paint.Textured);
        Assert.Equal(Px(empty, 32, 32), Px(bgra, 32, 32));   // background shows through
    }

    [Fact]
    public void UvCheckerAlternates()
    {
        var (V, T, Uv, cam) = Quad();
        var bgra = Draw(new SoftRender.Mesh(V, T, uv: Uv), cam, SoftRender.Paint.UvChecker);
        var lum = new List<int>();
        for (int x = 14; x < 50; x++) { var (r, g, b) = Px(bgra, x, 32); lum.Add(r + g + b); }
        int changes = lum.Zip(lum.Skip(1)).Count(p => Math.Abs(p.First - p.Second) > 100);
        Assert.InRange(changes, 4, 12);   // 8 cells per uv unit across the quad
    }

    [Fact]
    public void MeshFromGeomTakesUvsAndTheUnit0Texture()
    {
        var (V, T, Uv, _) = Quad();
        var g = new GeomData { Vertices = new VArray(V, 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(Uv, 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: T));
        var tex = new Texture { Baked = Img(), Exists = true, Unit = 0 };
        var ss = new StateSet { Textures = { new Texture { Unit = 1 }, tex } };
        var m = SoftRender.MeshFromGeom(g, null, ss)!;
        Assert.Equal(Uv, m.Uv);
        Assert.Same(tex, m.Tex);
        Assert.Same(tex, m.WithStyle(2).Tex);
    }
}
