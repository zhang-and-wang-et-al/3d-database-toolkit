using SceneKit;
using Xunit;

public class RenderTests
{
    [Fact]
    public void CameraBasisMatchesPython()
    {
        var c = new SoftRender.Camera(new double[3], 1.0);
        var (eye, fwd, right, up) = c.Basis();
        double[][] py =
        {
            new[] { 2.0985404873151476, -1.469413868456344, 1.1946078960625095 },
            new[] { -0.7424038765061041, 0.5198367907256845, -0.42261826174069944 },
            new[] { 0.573576436351046, 0.8191520442889918, -0.0 },
            new[] { -0.34618861305875415, 0.242403876506104, 0.9063077870366499 },
        };
        var cs = new[] { eye, fwd, right, up };
        for (int i = 0; i < 4; i++) for (int k = 0; k < 3; k++) Assert.Equal(py[i][k], cs[i][k], 12);
        Assert.Equal(2.826683094909585, c.Dist, 12);
    }

    [Fact]
    public void LargeTriangleSeenAtAnAngleHidesWhatIsBehindIt()
    {
        // a 2-triangle ground plane 20 x 20 and a small red box above it; from below the plane must cover the box.
        // Depth interpolated linearly in screen space (not perspective correct) makes the plane's middle too deep.
        var plane = new SoftRender.Mesh(new double[] { -10, -10, 0, 10, -10, 0, 10, 10, 0, -10, 10, 0 }, new[] { 0, 1, 2, 0, 2, 3 },
            colors: new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 });
        var bv = new List<double>();
        foreach (var (x, y, z) in new[] { (-1.0, -1.0, 0.5), (1.0, -1.0, 0.5), (1.0, 1.0, 0.5), (-1.0, 1.0, 0.5) }) bv.AddRange(new[] { x, y, z });
        var box = new SoftRender.Mesh(bv.ToArray(), new[] { 0, 1, 2, 0, 2, 3 }, colors: new double[] { 1, 0, 0, 1, 0, 0 });
        var cam = new SoftRender.Camera(new double[] { 0, 0, 0.5 }, 3.0, yaw: -135, pitch: -35);
        var (img, _) = SoftRender.Render(new[] { plane, box }, cam, 200, 200, shade: false, backfaces: false);
        int red = 0;
        for (int p = 0; p < img.Length; p += 4) if (img[p + 2] > 200 && img[p + 1] < 60) red++;
        Assert.Equal(0, red);
    }
}
