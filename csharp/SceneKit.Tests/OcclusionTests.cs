using SceneKit;
using Xunit;

public class OcclusionTests
{
    /// <summary>Closed box (12 triangles, outward facing) from lo to hi, vertices shared per corner.</summary>
    internal static (double[] V, int[] T) Box(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var V = new double[]
        {
            x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0,
            x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1,
        };
        var T = new[]
        {
            0, 2, 1, 0, 3, 2,  4, 5, 6, 4, 6, 7,  0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,  2, 3, 7, 2, 7, 6,  3, 0, 4, 3, 4, 7,
        };
        return (V, T);
    }

    /// <summary>UV sphere with about 2 * rings * segs triangles.</summary>
    internal static (double[] V, int[] T) Sphere(int rings, int segs, double r = 1)
    {
        var v = new List<double>();
        var t = new List<int>();
        for (int i = 0; i <= rings; i++)
        {
            double th = Math.PI * i / rings;
            for (int j = 0; j < segs; j++)
            {
                double ph = 2 * Math.PI * j / segs;
                v.AddRange(new[] { r * Math.Sin(th) * Math.Cos(ph), r * Math.Sin(th) * Math.Sin(ph), r * Math.Cos(th) });
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int a = i * segs + j, b = i * segs + (j + 1) % segs, c = a + segs, d = b + segs;
                if (i > 0) t.AddRange(new[] { a, c, b });
                if (i < rings - 1) t.AddRange(new[] { b, c, d });
            }
        return (v.ToArray(), t.ToArray());
    }

    [Fact]
    public void SphereDirectionsAreUnitAndSpread()
    {
        var d = Occlusion.SphereDirections(162);
        Assert.Equal(162, d.Length);
        foreach (var v in d) Assert.Equal(1.0, Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]), 9);
        // every direction of a coarse probe set has a sphere direction within 20 degrees
        foreach (var p in Occlusion.SphereDirections(30))
            Assert.Contains(d, v => v[0] * p[0] + v[1] * p[1] + v[2] * p[2] > Math.Cos(20 * Math.PI / 180));
    }

    [Fact]
    public void BoxInsideBoxIsHidden()
    {
        var outer = Box(-2, -2, -2, 2, 2, 2);
        var inner = Box(-1, -1, -1, 1, 1, 1);
        var vis = Occlusion.Visible(new[] { new Occlusion.Item(outer.V, outer.T), new Occlusion.Item(inner.V, inner.T) });
        Assert.All(vis[0], Assert.True);
        Assert.All(vis[1], Assert.False);
    }

    [Fact]
    public void TransparentShellDoesNotOcclude()
    {
        var outer = Box(-2, -2, -2, 2, 2, 2);
        var inner = Box(-1, -1, -1, 1, 1, 1);
        var vis = Occlusion.Visible(new[] { new Occlusion.Item(outer.V, outer.T, Occludes: false), new Occlusion.Item(inner.V, inner.T) });
        Assert.All(vis[0], Assert.True);
        Assert.All(vis[1], Assert.True);
    }

    [Fact]
    public void ObjectBehindSlitStaysVisible()
    {
        // a closed shell made of two boxes with a 0.4-wide slit between them along x = 0; a small box sits behind the slit
        var left = Box(-3, -2, -2, -0.2, 2, 2);
        var right = Box(0.2, -2, -2, 3, 2, 2);
        var back = Box(-3, 2, -2, 3, 2.5, 2);       // closes the slit from behind
        var inside = Box(-0.15, 0.5, -0.15, 0.15, 0.8, 0.15);
        var vis = Occlusion.Visible(new[]
        {
            new Occlusion.Item(left.V, left.T), new Occlusion.Item(right.V, right.T), new Occlusion.Item(back.V, back.T),
            new Occlusion.Item(inside.V, inside.T),
        });
        Assert.Contains(true, vis[3]);   // its front face is seen through the slit
    }

    [Fact]
    public void DenseSphereKeepsEveryTriangle()
    {
        var s = Sphere(200, 200);   // about 80,000 triangles
        var vis = Occlusion.Visible(new[] { new Occlusion.Item(s.V, s.T) });
        Assert.All(vis[0], Assert.True);
    }

    [Fact]
    public void AdjacencyKeepsNeighboursOfVisibleTriangles()
    {
        // outer box plus one triangle inside that shares a corner position (not the index) with the outer box
        var outer = Box(-2, -2, -2, 2, 2, 2);
        var V = outer.V.Concat(new double[] { -2, -2, -2, -1, -1.5, -1.5, -1.5, -1, -1.5 }).ToArray();
        var T = outer.T.Concat(new[] { 8, 9, 10 }).ToArray();
        var none = Occlusion.Visible(new[] { new Occlusion.Item(V, T) }, new Occlusion.Options { Adjacency = 0 });
        var one = Occlusion.Visible(new[] { new Occlusion.Item(V, T) }, new Occlusion.Options { Adjacency = 1 });
        Assert.False(none[0][12]);
        Assert.True(one[0][12]);
    }

    [Fact]
    public void AutoResolutionGrowsWithDensity()
    {
        var coarse = Sphere(10, 10);
        var fine = Sphere(400, 400);
        int rc = Occlusion.AutoResolution(new[] { new Occlusion.Item(coarse.V, coarse.T) }, 1.0);
        int rf = Occlusion.AutoResolution(new[] { new Occlusion.Item(fine.V, fine.T) }, 10.0);   // a fine part of a 10x larger object
        Assert.Equal(512, rc);
        Assert.True(rf > rc);
        Assert.True(rf <= 4096);
    }

    [Fact]
    public void SmallDenseObjectInALargeFrameKeepsEveryTriangle()
    {
        // a dense sphere and a tiny cube 100 units away: the shared view frame is ~100x the sphere, so its triangles are
        // far smaller than a pixel, and nothing covers them
        var s = Sphere(120, 120);
        var cube = Box(100, -0.05, -0.05, 100.1, 0.05, 0.05);
        var vis = Occlusion.Visible(new[] { new Occlusion.Item(s.V, s.T), new Occlusion.Item(cube.V, cube.T) });
        Assert.Equal(0, vis[0].Count(v => !v));
    }

    /// <summary>Open tube along +x from 0 to len: outer wall (normals out) and bore (normals in), segs around, rings along.</summary>
    internal static (double[] V, int[] T, int boreStart) Tube(double len, double outer, double bore, int segs = 24, int rings = 40)
    {
        var v = new List<double>();
        var t = new List<int>();
        int boreStart = 0;
        foreach (var (r, inward) in new[] { (outer, false), (bore, true) })
        {
            int b0 = v.Count / 3;
            if (inward) boreStart = t.Count / 3;
            for (int i = 0; i <= rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    double a = 2 * Math.PI * j / segs;
                    v.AddRange(new[] { len * i / rings, r * Math.Cos(a), r * Math.Sin(a) });
                }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int p = b0 + i * segs + j, q = b0 + i * segs + (j + 1) % segs, pn = p + segs, qn = q + segs;
                    if (!inward) t.AddRange(new[] { p, q, qn, p, qn, pn });
                    else t.AddRange(new[] { p, qn, q, p, pn, qn });
                }
        }
        return (v.ToArray(), t.ToArray(), boreStart);
    }

    [Fact]
    public void GunBarrelKeepsItsBore()
    {
        // review #5: a barrel 5 long with a 0.06 bore along x: the bore is seen when looking into the muzzle
        var (V, T, boreStart) = Tube(5, 0.15, 0.03);
        var vis = Occlusion.Visible(new[] { new Occlusion.Item(V, T) });
        Assert.True(vis[0].Length > boreStart);
        Assert.Equal(0, vis[0].Skip(boreStart).Count(v => !v));
    }

    // ------------------------------------------------------------------------------------------------ ground rule

    [Fact]
    public void AboveHorizonDropsViewsFromBelow()
    {
        // a box standing on z = 0, open at the bottom, and a separate quad 5 cm above the ground closing it from inside:
        // only a camera below the horizon looks into the box and sees the quad
        var box = Box(-1, -1, 0, 1, 1, 1);
        var open = box.T.Skip(6).ToArray();   // the first two triangles are the bottom (z = 0)
        var quadV = new double[] { -0.9, -0.9, 0.05, 0.9, -0.9, 0.05, 0.9, 0.9, 0.05, -0.9, 0.9, 0.05 };
        var quadT = new[] { 0, 2, 1, 0, 3, 2 };   // facing down
        var items = new[] { new Occlusion.Item(box.V, open), new Occlusion.Item(quadV, quadT) };
        var all = Occlusion.Visible(items);
        var above = Occlusion.Visible(items, new Occlusion.Options { AboveHorizonOnly = true });
        Assert.All(all[1], Assert.True);
        Assert.All(above[1], Assert.False);
        Assert.All(above[0], Assert.True);   // the walls and the top are still seen
        Assert.All(Occlusion.Directions(new Occlusion.Options { AboveHorizonOnly = true }), d => Assert.True(d[2] > 0));
    }

    [Fact]
    public void FullKeepsTheDirectionListOfToday()
    {
        var today = Occlusion.SphereDirections(162).Concat(Occlusion.AxisDirections()).ToArray();
        var full = Occlusion.Directions(new Occlusion.Options());
        Assert.Equal(today.Length, full.Length);
        for (int i = 0; i < today.Length; i++) Assert.Equal(today[i], full[i]);
        // above: today's list without the directions from below and on the horizon, in the same order
        Assert.Equal(today.Where(d => d[2] > 0).ToArray(), Occlusion.Directions(new Occlusion.Options { AboveHorizonOnly = true }));
        Assert.True(today.Count(d => d[2] < 0) > 100);
        // the fixtures: false (explicit) gives what the default gives
        var outer = Box(-2, -2, -2, 2, 2, 2);
        var inner = Box(-1, -1, -1, 1, 1, 1);
        var (tv, tt, _) = Tube(5, 0.15, 0.03);
        var s = Sphere(40, 40);
        foreach (var items in new[]
                 {
                     new[] { new Occlusion.Item(outer.V, outer.T), new Occlusion.Item(inner.V, inner.T) },
                     new[] { new Occlusion.Item(outer.V, outer.T, Occludes: false), new Occlusion.Item(inner.V, inner.T) },
                     new[] { new Occlusion.Item(tv, tt) },
                     new[] { new Occlusion.Item(s.V, s.T) },
                 })
        {
            var a = Occlusion.Visible(items);
            var b = Occlusion.Visible(items, new Occlusion.Options { AboveHorizonOnly = false });
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
        }
    }
}
