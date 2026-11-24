using SceneKit;
using Xunit;

public class RemoveOccludedTests
{
    static Node Geo(string name, (double[] V, int[] T) m)
    {
        var g = new GeomData { Vertices = new VArray(m.V, 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", indices: m.T));
        return new Node(Kind.Geometry, "osg::Geometry", name) { Geom = g };
    }

    static Node Group(string name, params Node[] kids)
    {
        var n = new Node(Kind.Group, "osg::Group", name);
        n.Children.AddRange(kids);
        return n;
    }

    internal static (Scene sc, Node car, Node body, Node engine) Car()
    {
        var body = Geo("body", OcclusionTests.Box(-2, -1, 0, 2, 1, 1.5));
        var engine = Geo("engine", OcclusionTests.Box(-1.5, -0.5, 0.2, -0.5, 0.5, 1));
        var car = Group("car", body, engine);
        var root = Group("root", car);
        return (new Scene("x.osgt", "osgt") { Root = root }, car, body, engine);
    }

    [Fact]
    public void EngineInsideClosedBodyIsRemoved()
    {
        var (sc, car, body, engine) = Car();
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Null(results[0].Error);
        Assert.DoesNotContain(engine, car.Children);          // empty geometry removed
        Assert.Equal(12, body.Geom!.Counts().tris);           // the body is untouched
        Assert.Contains("12", results[0].Message);
    }

    [Fact]
    public void NothingSelectedChangesNothing()
    {
        var (sc, car, _, engine) = Car();
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" });
        Assert.Contains(engine, car.Children);
        Assert.Contains("select", results[0].Message);
    }

    [Fact]
    public void GlassBodyKeepsTheInterior()
    {
        var (sc, car, body, engine) = Car();
        body.StateSet = new StateSet { Transparent = true };
        Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Contains(engine, car.Children);
        Optimizer.Run(sc, new[] { "remove_occluded" }, new Dictionary<string, object> { ["remove_occluded.see_through"] = false }, new List<Node> { car });
        Assert.DoesNotContain(engine, car.Children);
    }

    [Fact]
    public void PartlyHiddenGeometryLosesOnlyItsHiddenTriangles()
    {
        // one geometry holding both boxes: only the inner box's 12 triangles go, the vertex count shrinks
        var b = OcclusionTests.Box(-2, -1, 0, 2, 1, 1.5);
        var e = OcclusionTests.Box(-1.5, -0.5, 0.2, -0.5, 0.5, 1);
        var both = Geo("both", (b.V.Concat(e.V).ToArray(), b.T.Concat(e.T.Select(i => i + 8)).ToArray()));
        var car = Group("car", both);
        var sc = new Scene("x.osgt", "osgt") { Root = Group("root", car) };
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Equal(12, both.Geom!.Counts().tris);
        Assert.Equal(8, both.Geom.NumVertices);
        Assert.Single(results[0].Changes);                    // shows in the Changes list
    }

    [Fact]
    public void SharedGeometryOutsideSelectionIsSkipped()
    {
        var (sc, car, _, engine) = Car();
        sc.Root!.Children.Add(Group("elsewhere", engine));   // the engine is also used outside the car
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Contains(engine, car.Children);
        Assert.Equal(12, engine.Geom!.Counts().tris);
        Assert.Contains("shared", results[0].Message);
    }

    [Fact]
    public void FarLodLevelsAreLeftAlone()
    {
        // near level: body + engine; far level: a box inside the body (not drawn up close) -> stays
        var (sc, car, body, engine) = Car();
        var far = Geo("far", OcclusionTests.Box(-1, -0.4, 0.3, 1, 0.4, 1.2));
        var near = new Node(Kind.Lod, "FLT LOD", "near") { Ranges = new() { (0, 100) }, Center = new[] { 0.0, 0, 0 } };
        var farLod = new Node(Kind.Lod, "FLT LOD", "far") { Ranges = new() { (100, 1000) }, Center = new[] { 0.0, 0, 0 } };
        car.Children.Clear();
        near.Children.Add(Group("nearparts", body, engine));
        farLod.Children.Add(far);
        car.Children.Add(near);
        car.Children.Add(farLod);
        Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Equal(12, far.Geom!.Counts().tris);
        Assert.Contains(far, farLod.Children);
        Assert.DoesNotContain(engine, ((Node)near.Children[0]).Children);
    }

    [Fact]
    public void AlphaTextureCountsAsSeeThrough()
    {
        var img = new RawImage(2, 2, 4);
        for (int i = 0; i < 4; i++) img.Pixels[4 * i + 3] = (byte)(i == 0 ? 0 : 255);   // one transparent texel
        var cut = new StateSet { Textures = { new Texture { Baked = img, Exists = true, Channels = 4 } } };
        var opaqueImg = new RawImage(2, 2, 4);
        for (int i = 0; i < 4; i++) opaqueImg.Pixels[4 * i + 3] = 255;
        var opaque = new StateSet { Textures = { new Texture { Baked = opaqueImg, Exists = true, Channels = 4 } } };
        Assert.True(Optimizer.SeeThrough(cut));
        Assert.False(Optimizer.SeeThrough(opaque));     // an RGBA file without transparent texels still occludes
        Assert.False(Optimizer.SeeThrough(null));
    }

    // ---------------------------------------------------------------- final review fixes

    [Fact]
    public void GeometrySharedAboveItsParentIsSkipped()
    {
        // review #1: a seat geode (geometry inside it) used by the selected car and by an unselected convertible
        var (sc, car, _, engine) = Car();
        car.Children.Remove(engine);
        var seat = Group("seatgeode", engine);
        car.Children.Add(seat);
        var convertible = Group("convertible", seat);
        sc.Root!.Children.Add(convertible);
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Contains(engine, seat.Children);
        Assert.Contains(seat, convertible.Children);
        Assert.Equal(12, engine.Geom!.Counts().tris);
        Assert.Contains("shared", results[0].Message);
    }

    [Fact]
    public void SelectingAGeometryDirectlyWorks()
    {
        // review #2: the selected node is the geometry itself
        var b = OcclusionTests.Box(-2, -1, 0, 2, 1, 1.5);
        var e = OcclusionTests.Box(-1.5, -0.5, 0.2, -0.5, 0.5, 1);
        var both = Geo("both", (b.V.Concat(e.V).ToArray(), b.T.Concat(e.T.Select(i => i + 8)).ToArray()));
        var sc = new Scene("x.osgt", "osgt") { Root = Group("root", Group("car", both)) };
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { both });
        Assert.Equal(12, both.Geom!.Counts().tris);
        Assert.DoesNotContain("shared", results[0].Message);
    }

    [Fact]
    public void TransparencyInheritedFromAParentCounts()
    {
        // review #3a: Transparent on the body's parent, a texture-only state set on the body itself
        var (sc, car, body, engine) = Car();
        car.Children.Remove(body);
        var bodyGeode = new Node(Kind.Geode, "osg::Geode", "bodygeode") { StateSet = new StateSet { Transparent = true } };
        bodyGeode.Children.Add(body);
        body.StateSet = new StateSet { Textures = { new Texture { File = "paint.rgb", Exists = false, Channels = 3 } } };
        car.Children.Insert(0, bodyGeode);
        Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Contains(engine, car.Children);
    }

    [Fact]
    public void TintedGlassTextureIsSeeThrough()
    {
        // review #3b: alpha 160 everywhere is tinted glass, not opaque
        var img = new RawImage(2, 2, 4);
        for (int i = 0; i < 4; i++) img.Pixels[4 * i + 3] = 160;
        Assert.True(Optimizer.SeeThrough(new StateSet { Textures = { new Texture { Baked = img, Exists = true, Channels = 4 } } }));
    }

    [Fact]
    public void UnreadableTextureWithAlphaChannelIsSeeThrough()
    {
        // review #3c: a texture we cannot decode (e.g. DDS) whose header says it has alpha
        var p = Path.Combine(Path.GetTempPath(), $"sk_{Guid.NewGuid():N}.dds");
        File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        try
        {
            var t = new Texture { File = "grille.dds", Path = p, Exists = true, Channels = 4 };
            Assert.True(Optimizer.SeeThrough(new StateSet { Textures = { t } }));
            var opaque = new Texture { File = "paint.dds", Path = p, Exists = true, Channels = 3 };
            Assert.False(Optimizer.SeeThrough(new StateSet { Textures = { opaque } }));
        }
        finally { File.Delete(p); }
    }

    [Theory]
    [InlineData("FLT DOF")]
    [InlineData("switch")]
    [InlineData("animated")]
    public void MovingPartsNeitherHideNorGetEdited(string kind)
    {
        // review #4: a hatch (DOF), a damage-state switch or an animated group: what it covers stays, and it is not edited
        var (sc, car, body, engine) = Car();
        car.Children.Remove(body);
        Node moving = kind switch
        {
            "FLT DOF" => new Node(Kind.Transform, "FLT DOF", "hatch"),
            "switch" => new Node(Kind.Switch, "osg::Switch", "damage") { SwitchValues = new() { true } },
            _ => new Node(Kind.Group, "FLT Group", "anim") { Notes = { "animated group" } },
        };
        moving.Children.Add(body);
        car.Children.Insert(0, moving);
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Contains(engine, car.Children);
        Assert.Equal(12, body.Geom!.Counts().tris);
        Assert.Contains("moving", results[0].Message);
    }

    [Fact]
    public void ChangesOfThisPassAreNotFlaggedAsDamaged()
    {
        // review #6: the per-geometry damage check cannot judge removed hidden parts
        var b = OcclusionTests.Box(-2, -1, 0, 2, 1, 1.5);
        var e = OcclusionTests.Box(-1.5, -0.5, 0.2, -0.5, 0.5, 1);
        var both = Geo("both", (b.V.Concat(e.V).ToArray(), b.T.Concat(e.T.Select(i => i + 8)).ToArray()));
        var car = Group("car", both);
        var sc = new Scene("x.osgt", "osgt") { Root = Group("root", car) };
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        var c = Assert.Single(results[0].Changes);
        Assert.DoesNotContain("remove_occluded", InspectorModel.ShapePasses);
        Assert.Contains("3D preview", InspectorModel.ChangeCheckText(c));
    }

    /// <summary>The car of <see cref="Car"/> with a body whose bottom (z = 0) is a grid of 16 x 8 quads of 25 cm facing
    /// down (256 triangles; the walls and the roof are 10 more).</summary>
    static (Scene sc, Node car, Node body) CarWithGridBottom()
    {
        var box = OcclusionTests.Box(-2, -1, 0, 2, 1, 1.5);
        var V = box.V.ToList();
        var T = box.T.Skip(6).ToList();   // the box without its two bottom triangles
        int b = V.Count / 3;
        for (int j = 0; j <= 8; j++)
            for (int i = 0; i <= 16; i++) V.AddRange(new[] { -2 + 0.25 * i, -1 + 0.25 * j, 0.0 });
        for (int j = 0; j < 8; j++)
            for (int i = 0; i < 16; i++)
            {
                int a = b + j * 17 + i, r = a + 1, u = a + 17, d = u + 1;
                T.AddRange(new[] { a, u, r, r, u, d });   // facing down
            }
        var body = Geo("body", (V.ToArray(), T.ToArray()));
        var car = Group("car", body, Geo("engine", OcclusionTests.Box(-1.5, -0.5, 0.2, -0.5, 0.5, 1)));
        return (new Scene("x.osgt", "osgt") { Root = Group("root", car) }, car, body);
    }

    /// <summary>Triangles of a geometry with every corner at z = 0 (the bottom); <paramref name="inner"/>: only those at
    /// least 50 cm (two quads) inside the rim (12 x 4 quads, 96 triangles).</summary>
    static int BottomTris(Node geo, bool inner = false)
    {
        var g = geo.Geom!;
        var t = g.Triangles();
        var p = g.Vertices!.Data;
        int n = 0;
        for (int i = 0; i < t.Length; i += 3)
            if (Enumerable.Range(0, 3).All(k => p[3 * t[i + k] + 2] == 0 && (!inner || Math.Abs(p[3 * t[i + k]]) <= 1.5 && Math.Abs(p[3 * t[i + k] + 1]) <= 0.5))) n++;
        return n;
    }

    [Fact]
    public void RemoveOccludedGroundAboveRemovesTheUnderside()
    {
        var (sc, car, body) = CarWithGridBottom();
        var (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, targets: new List<Node> { car });
        Assert.Equal(266, body.Geom!.Counts().tris);   // all around: the bottom is seen from below
        Assert.DoesNotContain("horizon", results[0].Message);

        (sc, car, body) = CarWithGridBottom();
        (results, _, _) = Optimizer.Run(sc, new[] { "remove_occluded" }, new Dictionary<string, object> { ["remove_occluded.ground"] = "above" },
            new List<Node> { car });
        Assert.Null(results[0].Error);
        Assert.Equal(10, body.Geom!.Counts().tris - BottomTris(body));   // walls and roof untouched
        // the bottom faces down, so with views from above the horizon it counts as unseen whatever the raster shows (the
        // walls' lower edges no longer make it seen through the coplanar depth tolerance, and adjacency does not grow into it)
        string left = $"{BottomTris(body, inner: true)} of 96 inner, {BottomTris(body)} of 256 bottom triangles left";
        Assert.True(BottomTris(body, inner: true) <= 16, left);
        Assert.True(BottomTris(body) < 256 / 4, left);   // measured: 0 of 96 inner, 0 of 256 (before the rule: 6 and 166)
        Assert.Single(car.Children);   // the engine went as before
        Assert.EndsWith("; views from above the horizon only", results[0].Message);
    }
}
