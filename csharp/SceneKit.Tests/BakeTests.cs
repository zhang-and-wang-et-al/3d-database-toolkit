using SceneKit;
using Xunit;

public class BakeTests
{
    /// <summary>A flat square proxy, a fine grid of triangles in shuffled order, whose UV layout puts samples of several
    /// triangles far apart in the list on each texel; the original carries a class map with a different ID on almost
    /// every texel it is sampled at. The class map must not depend on which thread reaches a texel first.</summary>
    static Bake.Input ClassMapInput()
    {
        const int n = 128;
        var V = new double[(n + 1) * (n + 1) * 3];
        var UV = new double[(n + 1) * (n + 1) * 2];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                int v = j * (n + 1) + i;
                V[3 * v] = UV[2 * v] = (double)i / n;
                V[3 * v + 1] = UV[2 * v + 1] = (double)j / n;
            }
        var tris = new List<int[]>();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                tris.Add(new[] { a, b, d });
                tris.Add(new[] { a, d, c });
            }
        var rnd = new Random(7);
        var T = tris.OrderBy(_ => rnd.Next()).SelectMany(t => t).ToArray();

        var OV = new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
        var OT = new[] { 0, 1, 2, 0, 2, 3 };
        var OUV = new double[] { 0, 0, 1, 0, 1, 1, 0, 1 };
        var cls = new RawImage(256, 256, 3);
        for (int i = 0; i < 256 * 256; i++) cls.Pixels[3 * i] = (byte)(1 + rnd.Next(9));
        return new Bake.Input
        {
            V = V, T = T, UV = UV, Size = 64, Original = new TriIndex(OV, OT), OV = OV, OT = OT, TriSource = new[] { 0, 0 },
            OUV = OUV, OColor = Enumerable.Repeat(1.0, 16).ToArray(), HasUV = new[] { true }, Images = new RawImage?[] { null },
            MaxDist = 0.1,
            Layers = new() { [new LayerKey(0, "{stem}_Classes{ext}")] = new Bake.Layer { OUV = OUV, Images = new RawImage?[] { cls }, Nearest = true, Fill = ExtraFill.ClassFill(0) } },
        };
    }

    [Fact]
    public void ClassMapBakeIsOrderIndependent()
    {
        var inp = ClassMapInput();
        var first = Bake.RunAll(inp).extra.Values.Single().Pixels;
        for (int run = 0; run < 8; run++)
            Assert.Equal(first, Bake.RunAll(inp).extra.Values.Single().Pixels);
    }
}
