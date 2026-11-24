using SceneKit;
using Xunit;

/// <summary>R2-D5 UX-11: parts that lose over 90 % of their area are listed and marked red. UX-09: new gaps between
/// neighbouring parts are measured.</summary>
public class PartsQualityTests
{
    [Fact]
    public void PartThatLosesItsAreaIsAlmostGone()
    {
        var before = Meshes.Grid(10, 1);
        var after = Meshes.Grid(10, 1);
        var t = after.Triangles();
        after.Prims.Clear();
        after.Prims.Add(new Primitive("TRIANGLES", "elements", count: 3, indices: new[] { t[0], t[1], t[2] }, indexType: "DrawElementsUInt"));
        var q = Quality.Compare(before, after);
        Assert.True(q.AreaLossPct > Quality.GoneAreaLoss);
        Assert.Contains(q.Warnings, w => w.StartsWith("almost gone", StringComparison.Ordinal));
        Assert.NotEqual("ok", q.Verdict);
    }

    [Fact]
    public void FlatPanelAtFourTrianglesIsNotGone()
    {
        var (_, root) = Meshes.SceneOf(("panel", Meshes.Grid(10, 10), null));
        var r = InspectorModel.TrySimplify(root, new SimplifyParams { Within = true, Error = new ErrorTarget(0.01, false) });
        Assert.NotNull(r);
        Assert.True(r!.TrisAfter <= 4, $"tris after {r.TrisAfter}");
        Assert.True(r.Quality.AreaLossPct < 1);
        Assert.Empty(r.Gone);
        Assert.DoesNotContain(r.Quality.Warnings, w => w.Contains("almost gone", StringComparison.Ordinal));
    }

    [Fact]
    public void GoneLineListsTheParts()
    {
        Assert.Equal("3 parts almost gone: wheel_fl, wheel_fr, antenna", InspectorModel.GoneLine(new[] { "wheel_fl", "wheel_fr", "antenna" }));
        Assert.Equal("1 part almost gone: x", InspectorModel.GoneLine(new[] { "x" }));
        var seven = InspectorModel.GoneLine(new[] { "a", "b", "c", "d", "e", "f", "g" });
        Assert.Equal("7 parts almost gone: a, b, c, d, e, …", seven);
    }

    // ------------------------------------------------------------------ UX-09 cracks between parts

    static GeomData BoxGeom(double x0, double y0, double z0, double x1, double y1, double z1, int s, Func<double, double, double, (double, double, double)>? move = null)
    {
        var (V, T, _) = LodToolsTests.Box(x0, y0, z0, x1, y1, z1, s);
        if (move != null)
            for (int i = 0; i < V.Length / 3; i++) (V[3 * i], V[3 * i + 1], V[3 * i + 2]) = move(V[3 * i], V[3 * i + 1], V[3 * i + 2]);
        var g = new GeomData { Vertices = new VArray(V, 3, "Vec3dArray") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Length, indices: T, indexType: "DrawElementsUInt"));
        return g;
    }

    // window x in [1, 2], body x in [0, 1]: they share the face x = 1
    static GeomData Window(double shift = 0) => BoxGeom(1, 0, 0, 2, 1, 1, 2, (x, y, z) => (x == 1 ? x + shift : x, y, z));
    static GeomData Body() => BoxGeom(0, 0, 0, 1, 1, 1, 2);

    [Fact]
    public void SharedEdgeGapIsMeasured()
    {
        // after: the window's vertices on the shared face moved 0.03 away from the body (a 3 cm crack)
        var parts = new[] { new Part("window", Window(), Window(0.03), Mat.Identity()), new Part("body", Body(), Body(), Mat.Identity()) };
        var c = Quality.Cracks(parts);
        Assert.NotNull(c);
        Assert.Equal("window", c!.A);
        Assert.Equal("body", c.B);
        Assert.Equal(0.03, c.Gap, 1e-6);
    }

    [Fact]
    public void UnchangedPartsHaveNoGap()
    {
        var parts = new[] { new Part("window", Window(), Window(), Mat.Identity()), new Part("body", Body(), Body(), Mat.Identity()) };
        var c = Quality.Cracks(parts);
        Assert.NotNull(c);
        Assert.True(c!.Gap < 1e-9, $"gap {c.Gap}");
    }

    [Fact]
    public void CrackLineNamesGapAndParts()
    {
        Assert.Equal("gap or overlap of 3 cm between 'window' and 'body'", InspectorModel.CrackLine(new("window", "body", 0.03)));
    }

    [Fact]
    public void MatchedTileBordersStayClosed()
    {
        static double H(double x, double y) => 0.3 * Math.Sin(1.3 * x) * Math.Cos(0.9 * y) + 0.2 * Math.Sin(0.7 * x + 1.1 * y);
        var place = Mat.Identity(); place[3] = 10;
        var (sc, root) = Meshes.SceneOf(("tileA", Meshes.Grid(20, 10, H, uv: false), null), ("tileB", Meshes.Grid(20, 10, (x, y) => H(x + 10, y), uv: false), place));
        var tileA = root.Children[0];
        var b = SoftRender.GeometryItems(root.Children[1]).Single();
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(1, true), Borders = "match" };
        var r = InspectorModel.TrySimplify(tileA, p, new InspectorModel.TryContext(null, new[] { (b.node, b.world, b.ss) }));
        Assert.NotNull(r);
        Assert.True(r!.TrisAfter < r.TrisBefore, $"nothing simplified ({r.TrisAfter} of {r.TrisBefore})");
        Assert.DoesNotContain(r.Quality.Warnings, w => w.StartsWith("gap or overlap of", StringComparison.Ordinal));
        // matched borders stay closed even at Keep 10 %, where each part alone may move far beyond the OK deviation
        var matched = InspectorModel.TrySimplify(tileA, p with { Within = false, Ratio = 0.1 }, new InspectorModel.TryContext(null, new[] { (b.node, b.world, b.ss) }));
        Assert.True(matched!.TrisAfter < matched.TrisBefore, $"nothing simplified ({matched.TrisAfter} of {matched.TrisBefore})");
        Assert.DoesNotContain(matched.Quality.Warnings, w => w.StartsWith("gap or overlap of", StringComparison.Ordinal));
        // the check has teeth: borders that may slide open a gap wider than the OK deviation at Keep 10 %
        var slid = InspectorModel.TrySimplify(tileA, p with { Within = false, Ratio = 0.1, Borders = "slide" }, new InspectorModel.TryContext(null, new[] { (b.node, b.world, b.ss) }));
        Assert.Contains(slid!.Quality.Warnings, w => w.StartsWith("gap or overlap of", StringComparison.Ordinal) && w.EndsWith("between 'tileA' and 'tileB'", StringComparison.Ordinal));
        Assert.NotEqual("ok", slid.Quality.Verdict);
    }

    [Fact]
    public void TouchingFindsTheNeighbourTile()
    {
        var place = Mat.Identity(); place[3] = 10;
        var far = Mat.Identity(); far[3] = 30;
        var (sc, root) = Meshes.SceneOf(("tileA", Meshes.Grid(4, 10), null), ("tileB", Meshes.Grid(4, 10), place), ("tileC", Meshes.Grid(4, 10), far));
        var rep = Analyzer.Analyze(sc);
        var t = Selection.Touching(rep, new[] { root.Children[0] });
        var only = Assert.Single(t);
        Assert.Equal("tileB", only.Node.Name);
        Assert.Equal(10, only.World[3], 9);
    }
}
