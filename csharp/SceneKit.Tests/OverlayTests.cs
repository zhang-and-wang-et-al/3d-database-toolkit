using SceneKit;
using Xunit;

/// <summary>R2-D5 UX-10: terrain under roads and decals keeps its vertices, so overlays do not float or sink.</summary>
public class OverlayTests
{
    static (double[] V, int[] T) World(Node geo)
    {
        var g = geo.Geom!;
        var va = g.Vertices!;
        var v = new double[va.Count * 3];
        for (int i = 0; i < va.Count; i++) for (int c = 0; c < 3; c++) v[3 * i + c] = va.Data[i * va.Comps + c];
        return (v, g.Triangles());
    }

    internal static Dictionary<string, object> Within(double metres, bool protect) => new()
    {
        ["simplify.target"] = "within", ["simplify.max_error"] = metres, ["simplify.protect_overlays"] = protect,
    };

    /// <summary>Original tile vertices whose xy lies under the road (y in [8, 12]).</summary>
    internal static List<(double x, double y, double z)> UnderRoad(Node tile)
    {
        var (v, _) = World(tile);
        var o = new List<(double, double, double)>();
        for (int i = 0; i < v.Length / 3; i++) if (v[3 * i + 1] >= 8 - 1e-9 && v[3 * i + 1] <= 12 + 1e-9) o.Add((v[3 * i], v[3 * i + 1], v[3 * i + 2]));
        return o;
    }

    internal static bool Present(Node tile, (double x, double y, double z) p)
    {
        var (v, _) = World(tile);
        for (int i = 0; i < v.Length / 3; i++)
            if (Math.Abs(v[3 * i] - p.x) <= 1e-6 && Math.Abs(v[3 * i + 1] - p.y) <= 1e-6 && Math.Abs(v[3 * i + 2] - p.z) <= 1e-6) return true;
        return false;
    }

    [Fact]
    public void RoadIsAnOverlayTreeIsNot()
    {
        var (_, tile, road, tree) = Meshes.TileWithRoad();
        var (tv, tt) = World(tile);
        var (rv, rt) = World(road);
        var (bv, bt) = World(tree);
        Assert.True(Overlays.IsOverlay(tv, tt, rv, rt));
        Assert.False(Overlays.IsOverlay(tv, tt, bv, bt));
    }

    [Fact]
    public void TerrainUnderTheRoadKeepsItsVertices()
    {
        var (sc, tile, _, _) = Meshes.TileWithRoad();
        var under = UnderRoad(tile);
        long t0 = tile.Geom!.Counts().tris;
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Within(0.5, true), targets: new List<Node> { tile });
        Assert.Null(res[0].Error);
        Assert.True(tile.Geom!.Counts().tris < t0, res[0].Message);
        Assert.Contains("overlay contact kept on 1 geometry", res[0].Message);
        foreach (var p in under) Assert.True(Present(tile, p), $"vertex {p} under the road is gone");

        var (sc2, tile2, _, _) = Meshes.TileWithRoad();
        var (res2, _, _) = Optimizer.Run(sc2, new[] { "simplify" }, Within(0.5, false), targets: new List<Node> { tile2 });
        Assert.DoesNotContain("overlay contact", res2[0].Message);
        Assert.Contains(under, p => !Present(tile2, p));
    }

    [Fact]
    public void NoOverlayNoChange()
    {
        GeomData Run(bool protect)
        {
            var (sc, root) = Meshes.SceneOf(("tile", Meshes.Grid(40, 20, Meshes.Bumps), null));
            var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Within(0.5, protect), targets: new List<Node> { root.Children[0] });
            Assert.Null(res[0].Error);
            Assert.DoesNotContain("overlay contact", res[0].Message);
            return root.Children[0].Children[0].Geom!;
        }
        var on = Run(true);
        var off = Run(false);
        Assert.Equal(off.Triangles(), on.Triangles());
        Assert.Equal(off.Vertices!.Data, on.Vertices!.Data);
        Assert.Equal(off.TexCoords[0].Data, on.TexCoords[0].Data);
    }

    [Fact]
    public void CopyOfTheTileIsNotAnOverlay()
    {
        // two tiles on the same spot (a duplicate, a test fixture): neither freezes the other
        var (sc, root) = Meshes.SceneOf(("a", Meshes.Grid(40, 20, Meshes.Bumps), null), ("b", Meshes.Grid(40, 20, Meshes.Bumps), null));
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Within(0.5, true), targets: new List<Node> { root.Children[0] });
        Assert.DoesNotContain("overlay contact", res[0].Message);
        Assert.Contains("overlay covering the tile treated as a copy, not protected", res[0].Message);
        Assert.True(root.Children[0].Children[0].Geom!.Counts().tris < 3200, res[0].Message);
    }

    [Fact]
    public void TryNotesOverlayContactKept()
    {
        var (_, tile, road, _) = Meshes.TileWithRoad();
        var r = SoftRender.GeometryItems(road).Single();
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.5, false) };
        var res = InspectorModel.TrySimplify(tile, p, new InspectorModel.TryContext(null, new[] { (r.node, r.world, r.ss) }));
        Assert.NotNull(res);
        Assert.Contains("overlay contact kept", res!.Quality.Notes);
        Assert.EndsWith("overlay contact kept", InspectorModel.VerdictLine(res.Quality));
        // unticked: no protection, no note
        var off = InspectorModel.TrySimplify(tile, p with { ProtectOverlays = false }, new InspectorModel.TryContext(null, new[] { (r.node, r.world, r.ss) }));
        Assert.DoesNotContain("overlay contact kept", off!.Quality.Notes);
    }

    [Fact]
    public void TryOnTileAndRoadParentNotesOverlayContactKept()
    {
        // the selection holds the tile and its road (a file / group node): Try finds the road inside it, like Apply
        var (sc, tile, _, _) = Meshes.TileWithRoad();
        var root = sc.Root!;
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.5, false) };
        var res = InspectorModel.TrySimplify(root, p);
        Assert.NotNull(res);
        Assert.Contains("overlay contact kept", res!.Quality.Notes);
        // Before and After hold one mesh per geometry in the same order; the tile is the one with its triangle count
        int ti = res.Before.FindIndex(m => m.T.Length / 3 == tile.Geom!.Counts().tris);
        Assert.True(ti >= 0);
        long tryTile = res.After[ti].T.Length / 3;
        Assert.True(tryTile < tile.Geom!.Counts().tris, "the tile was not simplified");

        var (sc2, tile2, _, _) = Meshes.TileWithRoad();
        var (r2, _, _) = Optimizer.Run(sc2, new[] { "simplify" }, Within(0.5, true), targets: new List<Node> { sc2.Root! });
        Assert.Null(r2[0].Error);
        Assert.Contains("overlay contact kept on 1 geometry", r2[0].Message);
        Assert.Equal(tile2.Geom!.Counts().tris, tryTile);
    }

    [Fact]
    public void TerrainUnderTheRoadKeepsItsVerticesUnderAMirroredScaledPlace()
    {
        // tile and road below a MatrixTransform that mirrors x, stretches z and moves them: the footprint is found in
        // the world and tested against the tile's local vertices
        var place = Meshes.Scale(-1, 1, 2);
        place[3] = 100; place[7] = 50;
        var (sc, tile, _, _) = Meshes.TileWithRoad(place);
        var under = UnderRoad(tile);
        long t0 = tile.Geom!.Counts().tris;
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, Within(0.5, true), targets: new List<Node> { tile });
        Assert.Null(res[0].Error);
        Assert.True(tile.Geom!.Counts().tris < t0, res[0].Message);
        Assert.Contains("overlay contact kept on 1 geometry", res[0].Message);
        foreach (var q in under) Assert.True(Present(tile, q), $"vertex {q} under the road is gone");

        var (sc2, tile2, _, _) = Meshes.TileWithRoad(place);
        var (res2, _, _) = Optimizer.Run(sc2, new[] { "simplify" }, Within(0.5, false), targets: new List<Node> { tile2 });
        Assert.Contains(under, q => !Present(tile2, q));
    }
    /// <summary>Tile and road inside an external file that the master places with a translation (the external's own
    /// matrix): the pass's places for the tile must include that edge like the overlay candidates and Try do.</summary>
    static (Scene sc, Node tile) TileWithRoadInExternal(double scale = 1)
    {
        var (sub, tile, _, _) = Meshes.TileWithRoad();
        var fileRoot = sub.Root!;
        var move = Meshes.Scale(scale, scale, scale);
        move[3] = 1000; move[7] = 500; move[11] = 20;
        var ext = new Node(Kind.External, "osg::ProxyNode", "tile_ref") { Matrix = move, External = "tile.osgt", Externals = new List<Node> { fileRoot } };
        var root = new Node(Kind.Group, "osg::Group", "master") { Children = { ext } };
        var sc = new Scene("master.osgt", "osgt") { Root = root };
        sc.Externals["tile.osgt"] = new Scene("tile.osgt", "osgt") { Root = fileRoot };
        return (sc, tile);
    }

    [Fact]
    public void TileInATranslatedExternalKeepsOverlayContactInTryAndPass()
    {
        var (sc, tile) = TileWithRoadInExternal();
        var rep = Analyzer.Analyze(sc);
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.5, false) };
        var res = InspectorModel.TrySimplify(tile, p, InspectorModel.TryContextFor(rep, tile, SoftRender.ParentMatrix(tile, rep.Parents)));
        Assert.NotNull(res);
        Assert.Contains("overlay contact kept", res!.Quality.Notes);

        var (sc2, tile2) = TileWithRoadInExternal();
        var under = UnderRoad(tile2);
        var (r2, _, _) = Optimizer.Run(sc2, new[] { "simplify" }, Within(0.5, true), targets: new List<Node> { tile2 });
        Assert.Null(r2[0].Error);
        Assert.Contains("overlay contact kept on 1 geometry", r2[0].Message);
        foreach (var q in under) Assert.True(Present(tile2, q), $"vertex {q} under the road is gone");
    }

    [Fact]
    public void TileInAScaledExternalGetsTheSameLimitInTryAndPass()
    {
        // the external scales its file by 3: Try (places from the walk) and the pass (places from the drawn matrices)
        // must turn the 1.5 m limit into the same local limit (0.5 in the file), so the tile ends with the same triangles
        var (sc, tile) = TileWithRoadInExternal(3);
        var rep = Analyzer.Analyze(sc);
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(1.5, false) };
        var res = InspectorModel.TrySimplify(tile, p, InspectorModel.TryContextFor(rep, tile, SoftRender.ParentMatrix(tile, rep.Parents)));
        Assert.NotNull(res);
        Assert.True(res!.TrisAfter < res.TrisBefore);

        var (sc2, tile2) = TileWithRoadInExternal(3);
        var (r2, _, _) = Optimizer.Run(sc2, new[] { "simplify" }, Within(1.5, true), targets: new List<Node> { tile2 });
        Assert.Null(r2[0].Error);
        Assert.Equal(res.TrisAfter, tile2.Geom!.Counts().tris);
    }
    [Fact]
    public void TryFindsTheRoadBehindManyPropsOnTheTile()
    {
        // 70 small props on the tile come before the road in traversal order: the overlay candidates have no cap
        var (sc, tile, _, _) = Meshes.TileWithRoad();
        var root = sc.Root!;
        for (int k = 0; k < 70; k++)
        {
            double x = 1 + (k % 10) * 1.8, y = k < 35 ? 2 + (k / 10) * 1.2 : 14 + ((k - 35) / 10) * 1.2, z = Meshes.Bumps(x, y), s = 0.1;
            var bv = new List<double>();
            for (int c = 0; c < 8; c++) { bv.Add(x + ((c & 1) == 0 ? -s : s)); bv.Add(y + ((c & 2) == 0 ? -s : s)); bv.Add(z + ((c & 4) == 0 ? 0 : 0.3)); }
            int[] bt = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            var g = new GeomData { Vertices = new VArray(bv.ToArray(), 3, "Vec3Array") };
            g.Prims.Add(new Primitive("TRIANGLES", "elements", count: bt.Length, indices: bt, indexType: "DrawElementsUInt"));
            root.Children.Add(new Node(Kind.Geode, "osg::Geode", $"prop{k}") { Children = { new Node(Kind.Geometry, "osg::Geometry", $"prop{k}") { Geom = g } } });
        }
        var rep = Analyzer.Analyze(sc);
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.5, false) };
        var res = InspectorModel.TrySimplify(tile, p, InspectorModel.TryContextFor(rep, tile, SoftRender.ParentMatrix(tile, rep.Parents)));
        Assert.NotNull(res);
        Assert.Contains("overlay contact kept", res!.Quality.Notes);
    }
    [Fact]
    public void OverlayTestUsesAFixedNumberOfSamples()
    {
        // a large candidate is decided from at most MaxSamples area-weighted points; their weights sum to its area
        var (v, t) = World(new Node(Kind.Geometry) { Geom = Meshes.Grid(100, 20, Meshes.Bumps) });
        var s = Overlays.Sample(v, t);
        Assert.Equal(Overlays.MaxSamples, s.W.Length);
        Assert.Equal(s.Total, s.W.Sum(), 6);
        var small = Overlays.Sample(v, t.Take(30).ToArray());
        Assert.Equal(40, small.W.Length);
    }
    [Fact]
    public void FastRoughSaysItCannotKeepTerrainUnderRoads()
    {
        var (sc, tile, _, _) = Meshes.TileWithRoad();
        var prms = new Dictionary<string, object> { ["simplify.target"] = "keep", ["simplify.ratio"] = 0.5, ["simplify.method"] = "fast", ["simplify.protect_overlays"] = true };
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, prms, targets: new List<Node> { tile });
        Assert.Null(res[0].Error);
        Assert.Contains("Fast, rough cannot keep terrain under roads", res[0].Message);
        var (_, tile2, road2, _) = Meshes.TileWithRoad();
        var r2 = SoftRender.GeometryItems(road2).Single();
        var t = InspectorModel.TrySimplify(tile2, new SimplifyParams { Within = false, Ratio = 0.5, Strategy = SimplifyStrategy.FastRough },
            new InspectorModel.TryContext(null, new[] { (r2.node, r2.world, r2.ss) }));
        Assert.Contains("Fast, rough cannot keep terrain under roads", t!.Quality.Notes);
        // no overlay, no note
        var (sc3, root3) = Meshes.SceneOf(("tile", Meshes.Grid(40, 20, Meshes.Bumps), null));
        var (res3, _, _) = Optimizer.Run(sc3, new[] { "simplify" }, prms, targets: new List<Node> { root3.Children[0] });
        Assert.DoesNotContain("cannot keep terrain", res3[0].Message);
    }
}
