using SceneKit;
using Xunit;

/// <summary>D4: LOD switch distances from each level's measured error, so a switch moves the picture by at most the
/// allowed number of pixels: d = e * H / (2 * tan(fov / 2) * p).</summary>
public class LodDistanceTests
{
    static (Scene sc, Node geo) SphereScene()
    {
        var (V, T, _) = RebakeTests.Sphere(60, 30, 2.0);
        var g = new GeomData { Vertices = new VArray(V, 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Length, indices: T, indexType: "DrawElementsUInt"));
        var geo = new Node(Kind.Geometry, "osg::Geometry", "ball") { Geom = g };
        var geode = new Node(Kind.Geode, "osg::Geode", "ballgeode") { Children = { geo } };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { geode } };
        return (new Scene("x.osgt", "osgt") { Root = root }, geo);
    }

    static Node Lod(Scene sc, Dictionary<string, object> prms)
    {
        prms.TryAdd("generate_lod.min_tris", 100);
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, prms);
        Assert.Null(results[0].Error);
        return Assert.Single(sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
    }

    static GeomData Level(Node lod, int i) => lod.Children[i].Kind == Kind.Geode ? lod.Children[i].Children[0].Geom! : lod.Children[i].Geom!;

    [Fact]
    public void SwitchDistanceFollowsTheMeasuredError()
    {
        var (sc, geo) = SphereScene();
        var orig = geo.Geom!;
        var lod = Lod(sc, new() { ["generate_lod.screen_height"] = 1080, ["generate_lod.fov"] = 45.0, ["generate_lod.pixel_error"] = 1.0 });
        Assert.True(lod.Children.Count >= 2);
        double k = 1080 / (2 * Math.Tan(22.5 * Math.PI / 180));
        for (int i = 1; i < lod.Children.Count; i++)
        {
            double e = Quality.Compare(orig, Level(lod, i)).MaxDev;
            double d = lod.Ranges![i].min;
            Assert.InRange(d, e * k * 0.97, e * k * 1.03);
            Assert.Equal(lod.Ranges[i - 1].max, d);              // no gap, no overlap
            Assert.True(d > lod.Ranges[i - 1].min);              // farther levels switch in farther away
        }
        Assert.Equal(0.0, lod.Ranges![0].min);
        Assert.True(lod.Ranges[^1].max >= 1e29);
    }

    [Fact]
    public void LargerAllowedErrorHalvesTheDistances()
    {
        var (sc1, _) = SphereScene();
        var (sc2, _) = SphereScene();
        var a = Lod(sc1, new() { ["generate_lod.pixel_error"] = 1.0 });
        var b = Lod(sc2, new() { ["generate_lod.pixel_error"] = 2.0 });
        Assert.Equal(a.Ranges!.Count, b.Ranges!.Count);
        for (int i = 1; i < a.Ranges.Count; i++) Assert.InRange(b.Ranges[i].min / a.Ranges[i].min, 0.49, 0.51);
    }

    [Fact]
    public void FixedSwitchingKeepsRadiusMultiples()
    {
        var (sc, _) = SphereScene();
        var lod = Lod(sc, new() { ["generate_lod.switching"] = "fixed", ["generate_lod.ratios"] = "0.5, 0.2", ["generate_lod.distances"] = "8, 25" });
        double radius = Math.Sqrt(3 * 4.0 * 4.0) / 2;   // half the bounding box diagonal of the radius-2 sphere
        Assert.InRange(lod.Ranges![1].min, radius * 8 * 0.98, radius * 8 * 1.02);
        Assert.InRange(lod.Ranges[2].min, radius * 25 * 0.98, radius * 25 * 1.02);
    }

    [Fact]
    public void PassOffersErrorBasedSwitching()
    {
        var keys = Optimizer.Passes["generate_lod"].Params.Select(p => p.Key).ToList();
        Assert.Contains("switching", keys);
        Assert.Contains("pixel_error", keys);
        Assert.Equal("error", Optimizer.Passes["generate_lod"].Params.First(p => p.Key == "switching").Default);
    }
}
