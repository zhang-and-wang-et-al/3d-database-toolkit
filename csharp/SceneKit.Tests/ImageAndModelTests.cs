using SceneKit;
using Xunit;

public class ImageAndModelTests
{
    static RawImage Gradient(int w, int h, int ch)
    {
        var img = new RawImage(w, h, ch);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                    img.Pixels[(y * w + x) * ch + c] = (byte)((x * 7 + y * 3 + c * 50) & 0xFF);
        return img;
    }

    [Theory]
    [InlineData(".rgb", 3)]
    [InlineData(".rgba", 4)]
    [InlineData(".bw", 1)]
    [InlineData(".tga", 3)]
    [InlineData(".tga", 4)]
    [InlineData(".png", 3)]
    [InlineData(".png", 4)]
    [InlineData(".png", 1)]
    public void CodecsRoundTrip(string ext, int ch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"skimg_{Guid.NewGuid():N}{ext}");
        try
        {
            var img = Gradient(37, 23, ch);
            Assert.True(ImageCodecs.Write(img, path));
            var back = ImageCodecs.Read(path)!;
            Assert.Equal((37, 23, ch), (back.Width, back.Height, back.Channels));
            Assert.Equal(img.Pixels, back.Pixels);
            var probe = Images.Probe(path);
            if (probe != null) Assert.Equal((37, 23), (probe.Width, probe.Height));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResizeKeepsFlatColourAndSize()
    {
        var img = new RawImage(64, 32, 3);
        for (int i = 0; i < img.Pixels.Length; i += 3) { img.Pixels[i] = 200; img.Pixels[i + 1] = 100; img.Pixels[i + 2] = 50; }
        var small = ImageCodecs.Resize(img, 16, 8);
        Assert.Equal((16, 8), (small.Width, small.Height));
        Assert.All(Enumerable.Range(0, 16 * 8), p => Assert.Equal(new byte[] { 200, 100, 50 }, small.Pixels[(p * 3)..(p * 3 + 3)]));
    }

    [Fact]
    public void ResizeNearestKeepsExactValues()
    {
        var img = new RawImage(4, 4, 3);
        for (int i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = (byte)(i * 10 % 250);
        var small = ImageCodecs.ResizeNearest(img, 2, 2);
        Assert.Equal((2, 2, 3), (small.Width, small.Height, small.Channels));
        // texel = floor((x+0.5)*4/2) = 1 and 3
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(img.Pixels[((2 * y + 1) * 4 + 2 * x + 1) * 3 + c], small.Pixels[(y * 2 + x) * 3 + c]);
    }

    [Fact]
    public void ManagedResizerWritesSgi()
    {
        var src = Path.Combine(Path.GetTempPath(), $"skimg_{Guid.NewGuid():N}.rgb");
        var dst = src + ".small.rgb";
        try
        {
            ImageCodecs.Write(Gradient(256, 128, 3), src);
            Assert.True(new ManagedImageResizer().Resize(src, dst, 64, 32));
            var back = ImageCodecs.Read(dst)!;
            Assert.Equal((64, 32), (back.Width, back.Height));
            Assert.False(new ManagedImageResizer().Resize(src + ".missing", dst, 8, 8));
        }
        finally { File.Delete(src); File.Delete(dst); }
    }

    [Fact]
    public void BarMatchesPython()
    {
        // python: inspector_gui.bar(x)
        Assert.Equal("██▍    41.7%", InspectorModel.Bar(0.4166));
        Assert.Equal("██████ 100.0%", InspectorModel.Bar(1.0));
        Assert.Equal("        0.0%", InspectorModel.Bar(0.0));
        Assert.Equal("        0.0%", InspectorModel.Bar(-1));
    }

    [Fact]
    public void RenderDrawsSomethingAndHeatMapColours()
    {
        var V = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var mesh = new SoftRender.Mesh(V, new[] { 0, 1, 2 }, null, new[] { 0.0, 0.0, 5.0 });
        var cam = SoftRender.Camera.Fit(new[] { mesh });
        var (img, info) = SoftRender.Render(new[] { mesh }, cam, 64, 48, wire: true, heatMax: 1.0);
        Assert.Equal(64 * 48 * 4, img.Length);
        Assert.Equal(1, info.Drawn);
        var (r, g, b) = SoftRender.HeatColor(1.0, 1.0);
        Assert.Equal((1.0, 0.0, 0.0), (r, g, b));
        // some pixel differs from the background gradient
        var (empty, _) = SoftRender.Render(Array.Empty<SoftRender.Mesh>(), cam, 64, 48);
        Assert.NotEqual(empty, img);
    }
}
