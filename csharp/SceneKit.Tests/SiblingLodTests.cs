using SceneKit;
using Xunit;

/// <summary>OpenFlight writes every LOD level as its own LOD record (one child each) next to its siblings; only the
/// most detailed level of each group (same centre) is drawn when close, the others must not count as visible.</summary>
public class SiblingLodTests
{
    static Node Box(string name, int tris)
    {
        // a strip of `tris` triangles
        var v = new List<double>();
        var t = new List<int>();
        for (int i = 0; i <= tris / 2; i++) { v.AddRange(new double[] { i, 0, 0 }); v.AddRange(new double[] { i, 1, 0 }); }
        for (int i = 0; i < tris / 2; i++) { int a = 2 * i; t.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 }); }
        var g = new GeomData { Vertices = new VArray(v.ToArray(), 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: t.ToArray()));
        return new Node(Kind.Geometry, "osg::Geometry", name) { Geom = g };
    }

    static Node Lod(string name, double near, double far, double[]? center, Node child) =>
        new(Kind.Lod, "FLT LOD", name) { Ranges = new() { (near, far) }, Center = center, Children = { child } };

    static Node Group(params Node[] kids)
    {
        var g = new Node(Kind.Group, "FLT Group", "g");
        g.Children.AddRange(kids);
        return g;
    }

    [Fact]
    public void OnlyTheNearestLevelOfAGroupIsVisible()
    {
        var c = new[] { 2.0, 2.0, 2.0 };
        var root = Group(Lod("near", 0, 60, c, Box("red", 100)), Lod("mid", 60, 200, c, Box("green", 20)), Lod("far", 200, 2000, c, Box("blue", 4)));
        var rep = Analyzer.Analyze(new Scene("x.flt", "flt") { Root = root });
        Assert.Equal(124, rep.Total.Tris);
        Assert.Equal(100, rep.Total.VisTris);
        Assert.Equal(new[] { "red" }, SoftRender.GeometryItems(root).Select(x => x.node.Name));
    }

    [Fact]
    public void GroupsAreSeparatedByCentre()
    {
        // two buildings with their own near / far pair under the same parent: both near levels are visible
        double[] a = { 0, 0, 0 }, b = { 50, 0, 0 };
        var root = Group(Lod("an", 0, 100, a, Box("a_near", 40)), Lod("af", 100, 900, a, Box("a_far", 2)),
                         Lod("bn", 0, 100, b, Box("b_near", 30)), Lod("bf", 100, 900, b, Box("b_far", 2)));
        var rep = Analyzer.Analyze(new Scene("x.flt", "flt") { Root = root });
        Assert.Equal(70, rep.Total.VisTris);
        Assert.Equal(new[] { "a_near", "b_near" }, SoftRender.GeometryItems(root).Select(x => x.node.Name).OrderBy(s => s));
    }

    [Fact]
    public void PlainSiblingsAndASingleLodStayVisible()
    {
        var root = Group(Box("plain", 10), Lod("only", 50, 500, null, Box("lodded", 6)));
        var rep = Analyzer.Analyze(new Scene("x.flt", "flt") { Root = root });
        Assert.Equal(16, rep.Total.VisTris);
        Assert.Equal(2, SoftRender.GeometryItems(root).Count);
    }

    [Fact]
    public void FltReaderKeepsTheLodCentre()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sk_{Guid.NewGuid():N}");
        try
        {
            var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/make_compat_scene.py"));
            var psi = new System.Diagnostics.ProcessStartInfo("python", $"\"{script}\" \"{dir}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null) return;   // no Python: nothing to check here
                p.WaitForExit();
                if (p.ExitCode != 0) return;   // e.g. no Pillow
            }
            var sc = SceneIO.Load(Path.Combine(dir, "compat.flt"));
            var lods = sc.IterNodes(externals: true).Where(n => n.Kind == Kind.Lod).ToList();
            Assert.Equal(3, lods.Count);
            foreach (var l in lods) Assert.Equal(new[] { 2.0, 2.0, 2.0 }, l.Center);
            var rep = Analyzer.Analyze(sc);
            var red = lods.Single(l => l.Ranges![0].min == 0);
            Assert.Equal(rep.Stat(red).Tris + rep.Total.Tris - rep.Stat(lods[0]).Tris - rep.Stat(lods[1]).Tris - rep.Stat(lods[2]).Tris, rep.Total.VisTris);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    static Node LodOf(string name, double near, double far, double[]? center, params Node[] kids)
    {
        var l = new Node(Kind.Lod, "FLT LOD", name) { Ranges = new() { (near, far) }, Center = center };
        l.Children.AddRange(kids);
        return l;
    }

    [Fact]
    public void AnOpenFlightLodDrawsAllItsChildren()
    {
        // OpenFlight (e.g. Trian3D Builder): one LOD record holds many buildings / trees; its one range applies to all of them
        var root = Group(LodOf("tile", 0, 3000, new[] { 0.0, 0, 0 }, Box("house", 10), Box("tree", 4), Box("bush", 2)));
        var rep = Analyzer.Analyze(new Scene("x.flt", "flt") { Root = root });
        Assert.Equal(16, rep.Total.VisTris);
        Assert.Equal(new[] { "bush", "house", "tree" }, SoftRender.GeometryItems(root).Select(x => x.node.Name).OrderBy(s => s));
    }

    [Fact]
    public void OpenFlightLevelsWithSeveralChildrenStillHideTheFarLevel()
    {
        var c = new[] { 1.0, 1, 1 };
        var root = Group(LodOf("near", 0, 500, c, Box("house", 10), Box("tree", 4)),
                         LodOf("far", 500, 5000, c, Box("house_lo", 2), Box("tree_lo", 2)));
        var rep = Analyzer.Analyze(new Scene("x.flt", "flt") { Root = root });
        Assert.Equal(14, rep.Total.VisTris);
        Assert.Equal(new[] { "house", "tree" }, SoftRender.GeometryItems(root).Select(x => x.node.Name).OrderBy(s => s));
    }
}
