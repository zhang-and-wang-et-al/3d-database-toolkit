using SceneKit;
using Xunit;

/// <summary>D3: replace a selected object by billboard / cross cards (trees, bushes) or by one textured proxy.</summary>
public class ReplaceTests
{
    static Node Geo(string name, (double[] V, int[] T) m, double[] colour)
    {
        var g = new GeomData { Vertices = new VArray(m.V, 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: m.T.Length, indices: m.T, indexType: "DrawElementsUInt"));
        return new Node(Kind.Geometry, "osg::Geometry", name) { Geom = g, StateSet = new StateSet { MaterialName = name, Diffuse = colour } };
    }

    /// <summary>A tree 6 high at (10, 20, 0): brown trunk (box 0.6 wide, 0..3) and a green crown (sphere r 2 at z 4).</summary>
    internal static (Scene sc, Node tree) Tree()
    {
        var trunk = OcclusionTests.Box(9.7, 19.7, 0, 10.3, 20.3, 3);
        var (sv, st, _) = RebakeTests.Sphere(24, 12, 2.0);
        for (int i = 0; i < sv.Length / 3; i++) { sv[3 * i] += 10; sv[3 * i + 1] += 20; sv[3 * i + 2] += 4; }
        var tree = new Node(Kind.Geode, "osg::Geode", "tree")
        {
            Children = { Geo("trunk", trunk, new[] { 0.5, 0.3, 0.1, 1 }), Geo("crown", (sv, st), new[] { 0.1, 0.7, 0.1, 1 }) },
        };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { tree } };
        return (new Scene("x.osgt", "osgt") { Root = root }, tree);
    }

    static Node Run(Scene sc, Node target, string pass, Dictionary<string, object>? prms = null)
    {
        var (results, _, _) = Optimizer.Run(sc, new[] { pass }, prms, targets: new List<Node> { target });
        Assert.Null(results[0].Error);
        return Assert.Single(sc.Root!.Children);
    }

    static (double[] lo, double[] hi) Box(Node n)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var (g, w, _) in SoftRender.GeometryItems(n))
        {
            var V = g.Geom!.Vertices!.Data;
            foreach (int i in g.Geom.Triangles())
            {
                var p = new[] { V[3 * i], V[3 * i + 1], V[3 * i + 2] };
                for (int c = 0; c < 3; c++)
                {
                    double x = w[4 * c] * p[0] + w[4 * c + 1] * p[1] + w[4 * c + 2] * p[2] + w[4 * c + 3];
                    lo[c] = Math.Min(lo[c], x); hi[c] = Math.Max(hi[c], x);
                }
            }
        }
        return (lo, hi);
    }

    [Fact]
    public void CrossedCardsReplaceTheTree()
    {
        var (sc, tree) = Tree();
        var cards = Run(sc, tree, "replace_cards", new() { ["replace_cards.mode"] = "crossed", ["replace_cards.planes"] = 4, ["replace_cards.texture"] = 256 });
        Assert.NotSame(tree, cards);
        var geo = Assert.Single(SoftRender.GeometryItems(cards)).node;
        Assert.Equal(4 * 2 * 2, geo.Geom!.Counts().tris);           // 4 planes, front and back, 2 triangles each
        var ss = geo.StateSet ?? cards.StateSet;
        Assert.NotNull(ss);
        Assert.Equal("ON", ss!.Modes["GL_ALPHA_TEST"]);
        Assert.Equal("ON", ss.Modes["GL_CULL_FACE"]);
        var atlas = Assert.Single(ss.Textures).Baked;
        Assert.NotNull(atlas);
        Assert.Equal("CLAMP_TO_EDGE", ss.Textures[0].WrapS);      // an atlas: no bleeding from the opposite edge
        Assert.Equal("CLAMP_TO_EDGE", ss.Textures[0].WrapT);
        Assert.Equal(256, atlas!.Width);
        // the cards stand where the tree stood, as wide and as high
        var (lo, hi) = Box(cards);
        Assert.InRange(lo[2], -0.01, 0.01);
        Assert.InRange(hi[2], 5.9, 6.01);
        Assert.InRange((lo[0] + hi[0]) / 2, 9.9, 10.1);
        Assert.InRange((lo[1] + hi[1]) / 2, 19.9, 20.1);
        Assert.InRange(hi[0] - lo[0], 3.8, 4.2);
    }

    [Fact]
    public void CardImagesShowTheTrunkBelowAndTheCrownAbove()
    {
        var (sc, tree) = Tree();
        var cards = Run(sc, tree, "replace_cards", new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 128 });
        var geo = Assert.Single(SoftRender.GeometryItems(cards)).node;
        var img = (geo.StateSet ?? cards.StateSet)!.Textures[0].Baked!;
        Span<double> c = stackalloc double[4];
        // card UVs: u across, v up (0 = ground, 1 = top of the tree)
        Bake.Bilinear(img, 0.5, 0.2, c);                            // trunk: z 1.2
        Assert.True(c[3] > 0.9 && c[0] > c[1] && c[0] > c[2], $"trunk {c[0]:F2} {c[1]:F2} {c[2]:F2} {c[3]:F2}");
        Bake.Bilinear(img, 0.5, 0.7, c);                            // crown: z 4.2
        Assert.True(c[3] > 0.9 && c[1] > c[0] && c[1] > c[2], $"crown {c[0]:F2} {c[1]:F2} {c[2]:F2} {c[3]:F2}");
        Bake.Bilinear(img, 0.1, 0.2, c);                            // beside the trunk: empty
        Assert.True(c[3] < 0.1, $"beside the trunk alpha {c[3]:F2}");
    }

    [Fact]
    public void BillboardModeMakesOneAxialBillboard()
    {
        var (sc, tree) = Tree();
        var bb = Run(sc, tree, "replace_cards", new() { ["replace_cards.mode"] = "billboard" });
        Assert.Equal(Kind.Geode, bb.Kind);
        Assert.Equal("osg::Billboard", bb.Cls);
        Assert.NotNull(bb.Center);
        Assert.InRange(bb.Center![0], 9.9, 10.1);
        Assert.InRange(bb.Center[1], 19.9, 20.1);
        Assert.InRange(bb.Center[2], -0.01, 0.01);
        var geo = Assert.Single(bb.Children);
        Assert.Equal(2, geo.Geom!.Counts().tris);
        var V = geo.Geom.Vertices!.Data;
        for (int i = 0; i < V.Length / 3; i++) Assert.Equal(0.0, V[3 * i + 1], 6);   // in the x-z plane, facing -y (the Billboard normal)
        var (lo, hi) = Box(bb);                                     // the preview places the card at the billboard's position
        Assert.InRange(hi[2], 5.9, 6.01);
        Assert.InRange((lo[0] + hi[0]) / 2, 9.9, 10.1);
        Assert.InRange(lo[1], 19.9, 20.1);
    }

    [Fact]
    public void PreviewCullsBackFacesWhereTheStateSetSaysSo()
    {
        // a quad facing +x, seen from -x: drawn (tinted) normally, not drawn with GL_CULL_FACE ON
        var g = new GeomData { Vertices = new VArray(new double[] { 0, -1, 0, 0, 1, 0, 0, 1, 2, 0, -1, 2 }, 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: 6, indices: new[] { 0, 1, 2, 0, 2, 3 }, indexType: "DrawElementsUInt"));
        var cam = new SoftRender.Camera(new[] { 0.0, 0, 1 }, 1.5, yaw: 180, pitch: 0);
        var plain = SoftRender.MeshFromGeom(g, null, new StateSet())!;
        var culled = SoftRender.MeshFromGeom(g, null, new StateSet { Modes = { ["GL_CULL_FACE"] = "ON" } })!;
        Assert.Equal(2, SoftRender.Render(new[] { plain }, cam, 64, 64).info.Drawn);
        Assert.Equal(0, SoftRender.Render(new[] { culled }, cam, 64, 64).info.Drawn);
        var front = new SoftRender.Camera(new[] { 0.0, 0, 1 }, 1.5, yaw: 0, pitch: 0);
        Assert.Equal(2, SoftRender.Render(new[] { culled }, front, 64, 64).info.Drawn);
    }

    [Fact]
    public void CardsCanBeTheFarthestLodLevel()
    {
        var (sc, tree) = Tree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "generate_lod" }, new Dictionary<string, object>
            { ["generate_lod.min_tris"] = 100, ["generate_lod.far_level"] = "cards", ["generate_lod.proxy_texture"] = 128 });
        Assert.Null(results[0].Error);
        var lod = Assert.Single(sc.IterNodes().Where(n => n.Kind == Kind.Lod).ToList());
        var far = lod.Children[^1];
        var geo = Assert.Single(SoftRender.GeometryItems(far)).node;
        Assert.Equal("ON", geo.StateSet!.Modes["GL_ALPHA_TEST"]);
        Assert.Equal(16, geo.Geom!.Counts().tris);
        Assert.True(lod.Ranges![^1].min > lod.Ranges[^2].min);
    }

    [Fact]
    public void ChangesListPointsToThePreview()
    {
        var (sc, tree) = Tree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards" }, targets: new List<Node> { tree });
        Assert.NotEmpty(results[0].Changes);
        Assert.All(results[0].Changes, c => Assert.Contains("3D preview", InspectorModel.ChangeCheckText(c)));
    }

    [Fact]
    public void LaterPassesLeaveTheBillboardWhereItIs()
    {
        // review #1: a moved tree becomes a billboard; flattening the transform must not move the card off its position
        var (sc, tree) = Tree();
        sc.Root!.Children.Clear();
        var m = Mat.Identity(); m[3] = 100; m[7] = 50;
        var xf = new Node(Kind.Transform, "osg::MatrixTransform", "place") { Matrix = m, Children = { tree } };
        sc.Root.Children.Add(xf);
        Optimizer.Run(sc, new[] { "replace_cards" }, new() { ["replace_cards.mode"] = "billboard" }, targets: new List<Node> { tree });
        var (lo0, hi0) = Box(sc.Root);
        Optimizer.Run(sc, new[] { "flatten_transforms", "merge", "simplify" }, new() { ["simplify.min_tris"] = 1 });
        var (lo1, hi1) = Box(sc.Root);
        for (int c = 0; c < 3; c++) { Assert.Equal(lo0[c], lo1[c], 6); Assert.Equal(hi0[c], hi1[c], 6); }
        var bb = sc.IterNodes().Single(n => n.Cls == "osg::Billboard");
        Assert.All(bb.Children, g => Assert.False(g.Geom!.Optimizable));
    }

    [Theory]
    [InlineData("replace_cards", "crossed")]
    [InlineData("replace_cards", "billboard")]
    [InlineData("replace_proxy", "")]
    public void SelectingADrawableNeverPutsAGeodeInsideAGeode(string pass, string mode)
    {
        // review #6: the user selects the crown (a drawable of the tree geode)
        var (sc, tree) = Tree();
        var crown = tree.Children[1];
        var prms = new Dictionary<string, object> { ["replace_proxy.texture"] = 32, ["replace_proxy.voxels"] = 32, ["replace_proxy.ratio"] = 0.5 };
        if (mode.Length > 0) prms["replace_cards.mode"] = mode;
        Optimizer.Run(sc, new[] { pass }, prms, targets: new List<Node> { crown });
        foreach (var n in sc.IterNodes().Where(n => n.Kind == Kind.Geode))
            Assert.All(n.Children, c => Assert.Equal(Kind.Geometry, c.Kind));
        if (mode == "billboard") Assert.Contains(crown, sc.IterNodes());   // shares its geode with the trunk: skipped
        else Assert.DoesNotContain(crown, sc.IterNodes());                // replaced by a drawable in the same geode
    }

    [Fact]
    public void LodIsNotBuiltForABillboard()
    {
        var (sc, tree) = Tree();
        Optimizer.Run(sc, new[] { "replace_cards" }, new() { ["replace_cards.mode"] = "billboard" }, targets: new List<Node> { tree });
        var bb = sc.IterNodes().Single(n => n.Cls == "osg::Billboard");
        Optimizer.Run(sc, new[] { "generate_lod" }, targets: new List<Node> { bb });
        Assert.DoesNotContain(sc.IterNodes(), n => n.Kind == Kind.Lod);
    }

    [Fact]
    public void UndoBringsTheTreeBack()
    {
        var (sc, tree) = Tree();
        var snap = new Snapshot(sc, "before");
        Run(sc, tree, "replace_cards");
        snap.Restore();
        Assert.Same(tree, Assert.Single(sc.Root!.Children));
    }

    [Fact]
    public void NothingSelectedChangesNothing()
    {
        var (sc, tree) = Tree();
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_cards", "replace_proxy" });
        Assert.All(results, r => Assert.StartsWith("nothing changed", r.Message));
        Assert.Same(tree, Assert.Single(sc.Root!.Children));
    }

    [Theory]
    [InlineData(".osgt")]
    [InlineData(".osg")]
    public void BillboardSavesAndLoadsBack(string ext)
    {
        var (sc, tree) = Tree();
        string? src = null;
        if (ext == ".osg")
        {
            // the legacy writer is used for scenes read from .osg: start from one
            src = Path.Combine(Path.GetTempPath(), $"sk_bbsrc_{Guid.NewGuid():N}.osg");
            File.WriteAllText(src, "Group {\n  name \"root\"\n  num_children 0\n}\n");
            sc = SceneIO.Load(src);
            sc.Root!.Children.Add(tree);
        }
        Run(sc, tree, "replace_cards", new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 64 });
        string outp = Path.Combine(Path.GetTempPath(), $"sk_bb_{Guid.NewGuid():N}{ext}");
        string texDir = Path.Combine(Path.GetDirectoryName(outp)!, Path.GetFileNameWithoutExtension(outp) + "_textures");
        try
        {
            SceneIO.Save(sc, outp);
            string text = File.ReadAllText(outp);
            Assert.Contains("Billboard", text);
            Assert.Contains("AXIAL_ROT", text);
            Assert.Contains("GL_ALPHA_TEST", text);
            if (ext == ".osgt")   // OpenSceneGraph reads StateSet fields in a fixed order: modes before attributes
                Assert.True(text.IndexOf("ModeList", StringComparison.Ordinal) < text.IndexOf("AttributeList", StringComparison.Ordinal));
            var back = SceneIO.Load(outp);
            var bb = Assert.Single(back.IterNodes().Where(n => n.Cls.EndsWith("Billboard")).ToList());
            Assert.Equal(2, bb.Children.Sum(c => c.Geom?.Counts().tris ?? 0));
            Assert.NotNull(bb.Center);                                  // read back from the position list
            Assert.InRange(bb.Center![0], 9.9, 10.1);
            Assert.InRange(bb.Center[1], 19.9, 20.1);
        }
        finally
        {
            if (File.Exists(outp)) File.Delete(outp);
            if (src != null) File.Delete(src);
            if (Directory.Exists(texDir)) Directory.Delete(texDir, true);
        }
    }

    [Fact]
    public void ProxyReplacesAnAssembly()
    {
        var (sc, car, _, _) = RemoveOccludedTests.Car();
        long before = Convert.ToInt64(Analyzer.Analyze(sc, deepChecks: false).Summary()["triangles"]);
        var (blo, bhi) = Box(car);
        var proxy = Run(sc, car, "replace_proxy", new() { ["replace_proxy.ratio"] = 0.5, ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 48 });
        var geo = Assert.Single(SoftRender.GeometryItems(proxy)).node;
        Assert.NotNull((geo.StateSet ?? proxy.StateSet)!.Textures[0].Baked);
        Assert.Equal("CLAMP_TO_EDGE", (geo.StateSet ?? proxy.StateSet)!.Textures[0].WrapS);
        var (lo, hi) = Box(proxy);
        for (int c = 0; c < 3; c++)
        {
            Assert.InRange(lo[c], blo[c] - 0.2, blo[c] + 0.2);
            Assert.InRange(hi[c], bhi[c] - 0.2, bhi[c] + 0.2);
        }
    }

    [Fact]
    public void ProxyKeepsAnObjectThatIsAlreadySimpler()
    {
        // a plain box (12 triangles) cannot get a lighter proxy: it stays as it is
        var box = new Node(Kind.Geode, "osg::Geode", "box") { Children = { Geo("box", OcclusionTests.Box(0, 0, 0, 2, 1, 1), new[] { 0.5, 0.5, 0.5, 1 }) } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { box } } };
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, new() { ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 32 },
            targets: new List<Node> { box });
        Assert.Null(results[0].Error);
        Assert.Contains("1 kept (already simpler than a proxy would be)", results[0].Message);
        Assert.Same(box, Assert.Single(sc.Root!.Children));
    }

    // ------------------------------------------------------------------------------ per-part proxy (car package)

    static readonly Dictionary<string, object> GoldenProxy = new() { ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 48, ["replace_proxy.share"] = false };

    [Fact]
    public void WholeGivesExactlyTodaysResult()
    {
        // negative: parts = whole is today's single hull, bit for bit; the message names the moving parts it merged
        var (today, t0) = Meshes.SynthCar(true);
        Optimizer.Run(today, new[] { "replace_proxy" }, new Dictionary<string, object>(GoldenProxy), targets: new List<Node> { t0 });
        var (sc, car) = Meshes.SynthCar(true);
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, new Dictionary<string, object>(GoldenProxy) { ["replace_proxy.parts"] = "whole" },
            targets: new List<Node> { car });
        Assert.Null(results[0].Error);
        var fp = ProxyGoldenTests.Fingerprint(sc);
        Assert.Equal(ProxyGoldenTests.Fingerprint(today), fp);
        Assert.StartsWith("1066 ", fp);
        Assert.Single(sc.IterNodes(), n => n.Geom != null);
        var msg = results[0].Message;
        Assert.Contains("; moving parts merged into the hull: ", msg);
        foreach (var n in new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr", "lights" }) Assert.Contains(n, msg);
        Assert.Contains("(parts = per_part keeps them)", msg);
    }

    [Fact]
    public void SharedCopiesGetOneBuild()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var (_, car2) = Meshes.SynthCar(true);
        car2.StateSet = car.StateSet;   // the same texture (a baked image counts by identity)
        var m = Mat.Identity(); m[3] = 10;
        var place2 = new Node(Kind.Transform, "osg::MatrixTransform", "place2") { Matrix = m, Children = { car2 } };
        sc.Root!.Children.Add(place2);
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, new Dictionary<string, object>
        {
            ["replace_proxy.parts"] = "per_part", ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 48, ["replace_proxy.share"] = true,
        }, targets: new List<Node> { car, car2 });
        Assert.Null(results[0].Error);
        var msg = results[0].Message;
        Assert.Contains("2 objects -> 1 atlases", msg);
        Assert.StartsWith("2 object(s) replaced with a per-part proxy: ", msg);
        Assert.Contains("draw calls 36 -> 12", msg);   // drawn geometry only (ruling T16-d): per car 1 + 4 wheels + the light that is on
        // the second car shows the first one's proxy (its transform already moves it there)
        var holder = sc.Root.Children[0];
        Assert.Equal("car proxy", holder.Name);
        Assert.Same(holder, Assert.Single(place2.Children));
    }
}
