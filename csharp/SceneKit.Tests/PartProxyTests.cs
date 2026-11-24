using SceneKit;
using Xunit;

/// <summary>Per-part proxies, step 1: part discovery (moving parts, connected components, clustering); step 2: classification;
/// step 3: keep mode (per-part voxel hulls, the triangle budget, one atlas, the report).</summary>
public class PartProxyTests
{
    internal static Node Geometry(string name, (double[] V, int[] T, double[] UV) m)
    {
        var g = new GeomData { Vertices = new VArray(m.V, 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: m.T.Length, indices: m.T, indexType: "DrawElementsUInt"));
        return new Node(Kind.Geometry, "osg::Geometry", name) { Geom = g };
    }

    static int Units(PartProxy.Assembly a) => a.Parts.Sum(p => p.Units.Count);

    [Fact]
    public void SplitCarFindsFourDofsAndTwoSwitchChildren()
    {
        var (_, car) = Meshes.SynthCar(true);
        var asm = PartProxy.Discover(car, 0.04);
        var dofs = asm.Moving.Where(m => m.Node.Cls == "FLT DOF").ToList();
        Assert.Equal(new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" }, dofs.Select(m => m.Node.Name).OrderBy(x => x));
        var lights = asm.Moving.Where(m => m.Node.Name == "lights").OrderBy(m => m.SwitchChild).ToList();
        Assert.Equal(new[] { 0, 1 }, lights.Select(m => m.SwitchChild));
        Assert.Equal(6, asm.Moving.Count);
        // the switched-off child is gathered too
        Assert.Contains(lights[1].Inner.Pieces, p => p.Geom.Name == "lights_off");
        Assert.NotEmpty(lights[1].Inner.Parts);
        // a wheel's pieces are in the DOF's own frame (around its origin); its Frame puts it back on the car
        foreach (var m in dofs)
        {
            var p = Assert.Single(m.Inner.Pieces);
            for (int i = 0; i < p.V.Length / 3; i++) Assert.True(Math.Abs(p.V[3 * i]) <= 0.331 && Math.Abs(p.V[3 * i + 2]) <= 0.331);
            Assert.Equal(m.Node.Matrix!, m.Inner.Frame);
            Assert.Equal(Mat.Identity(), m.StaticAbove);
            Assert.Empty(m.Inner.Moving);
        }
        Assert.DoesNotContain(asm.Pieces, p => p.Geom.Name.StartsWith("wheel") || p.Geom.Name.StartsWith("lights"));
        Assert.Equal(19, asm.AllPieces().Count());   // 13 static, 4 wheels, 2 light states
    }

    [Fact]
    public void BodyPanelsJoinOneCluster()
    {
        var (_, car) = Meshes.SynthCar(true);
        var asm = PartProxy.Discover(car, 0.04);
        var body = Assert.Single(asm.Parts, p => p.Units.Any(u => u.Name == "body_1"));
        var names = body.Units.Select(u => u.Name).ToHashSet();
        foreach (var n in new[] { "body_1", "body_2", "body_3", "body_4", "body_5", "body_6", "body_7", "body_8", "cabin" }) Assert.Contains(n, names);
        Assert.Equal("body_1", body.Name);   // the largest unit names the part
        Assert.All(asm.Parts, p => Assert.Equal(PartClass.Bulk, p.Class));
        Assert.Equal(body.T.Length / 3, body.PieceOfTri.Length);
        Assert.Equal(4.5, body.A, 6);
        Assert.Equal(0, asm.TinyDropped);
    }

    [Fact]
    public void MovingRuleIsTheRemoveOccludedRule()
    {
        Assert.True(Optimizer.Moving(new Node(Kind.Switch, "osg::Switch", "s")));
        Assert.True(Optimizer.Moving(new Node(Kind.Transform, "FLT DOF", "d")));
        var animated = new Node(Kind.Group, "FLT Group", "a");
        animated.Notes.Add("animated group");
        Assert.True(Optimizer.Moving(animated));
        Assert.True(Optimizer.Moving(new Node(Kind.Group, "osg::Sequence", "q")));
        var callback = new Node(Kind.Transform, "osg::MatrixTransform", "c")
        {
            Src = new Obj("osg::MatrixTransform", null, new List<Stmt> { new(new[] { "UpdateCallback", "TRUE" }) }, ObjStyle.Osgt),
        };
        Assert.True(Optimizer.Moving(callback));
        Assert.False(Optimizer.Moving(new Node(Kind.Group, "osg::Group", "g")));
        Assert.False(Optimizer.Moving(new Node(Kind.Group, "FLT Group", "f")));
    }

    [Fact]
    public void OnlyTheNearestLodChildIsUsed()
    {
        var near = new Node(Kind.Geode, "osg::Geode", "near") { Children = { Geometry("near_geo", LodToolsTests.Box(0, 0, 0, 1, 1, 1, 2)) } };
        var far = new Node(Kind.Geode, "osg::Geode", "far") { Children = { Geometry("far_geo", LodToolsTests.Box(0, 0, 0, 1, 1, 1, 1)) } };
        var lod = new Node(Kind.Lod, "osg::LOD", "lod") { Ranges = new() { (100, 1000), (0, 100) }, Children = { far, near } };
        var target = new Node(Kind.Group, "osg::Group", "t") { Children = { lod } };
        var asm = PartProxy.Discover(target, 0.04);
        Assert.Equal("near_geo", Assert.Single(asm.Pieces).Geom.Name);
        Assert.Equal(1, asm.LodAlternativesDropped);
    }

    [Fact]
    public void WeldedSingleMeshIsOneUnitAndOneCluster()
    {
        var (_, car) = Meshes.SynthCar(false);
        var asm = PartProxy.Discover(car, 0.04);
        Assert.Equal(1, Units(asm));
        Assert.Single(asm.Parts);
        Assert.Empty(asm.Moving);
    }

    [Fact]
    public void TwoLargeOverlappingComponentsAreOneCluster()
    {
        var a = LodToolsTests.Box(0, 0, 0, 2, 2, 2, 4);
        var b = LodToolsTests.Box(1.5, 0, 0.1, 3.5, 2, 2.1, 4);   // 0.5 m into the first, no position in common with it
        var geo = Geometry("boxes", (a.V.Concat(b.V).ToArray(), a.T.Concat(b.T.Select(t => t + a.V.Length / 3)).ToArray(), a.UV.Concat(b.UV).ToArray()));
        var target = new Node(Kind.Geode, "osg::Geode", "boxes") { Children = { geo } };
        var asm = PartProxy.Discover(target, 0.04);
        Assert.Equal(2, Units(asm));
        Assert.Single(asm.Parts);
    }

    [Fact]
    public void ApartComponentsStayApart()
    {
        // negative of the above: 10 cm apart with a 4 cm gap are two parts; a small unit never joins
        var a = LodToolsTests.Box(0, 0, 0, 2, 2, 2, 4);
        var b = LodToolsTests.Box(2.1, 0, 0, 4.1, 2, 2, 4);
        var c = LodToolsTests.Box(2, 0.9, 0.9, 2.05, 1.1, 1.1, 1);   // touches the first box
        var all = new[] { a, b, c };
        var V = all.SelectMany(m => m.V).ToArray();
        var T = new List<int>();
        int bas = 0;
        foreach (var m in all) { T.AddRange(m.T.Select(t => t + bas)); bas += m.V.Length / 3; }
        var target = new Node(Kind.Geode, "osg::Geode", "boxes") { Children = { Geometry("boxes", (V, T.ToArray(), Array.Empty<double>())) } };
        var asm = PartProxy.Discover(target, 0.04);
        Assert.Equal(3, Units(asm));
        Assert.Equal(3, asm.Parts.Count);   // the small rule: the touching small box stays its own part
        var pieces = asm.Pieces;
        var alone = PartProxy.FromPieces(pieces, target, Mat.Identity(), 0.04, u => u.Tris.Length == 12);
        Assert.Equal(3, alone.Parts.Count);
    }

    [Fact]
    public void MergedCarSplitsItsSeparateWheelComponents()
    {
        var (_, car) = Meshes.SynthCar(false, weldWheels: false);
        var asm = PartProxy.Discover(car, 0.04);
        Assert.Equal(5, Units(asm));
        Assert.Empty(asm.Moving);
    }

    // ------------------------------------------------------------------------------------------------ step 2: classification

    static (Node car, Node extra) CarWith(params (string name, (double[] V, int[] T, double[] UV) m)[] extras)
    {
        var (_, car) = Meshes.SynthCar(true);
        Node last = car;
        foreach (var (name, m) in extras)
        {
            last = new Node(Kind.Geode, "osg::Geode", name) { Children = { Geometry(name, m) } };
            car.Children.Add(last);
        }
        return (car, last);
    }

    static double CarDiag(Node car) => PartProxy.Discover(car, 0.04).Diag;

    static PartProxy.Part Named(PartProxy.Assembly a, string unit) => Assert.Single(a.Parts, p => p.Units.Any(u => u.Name == unit));

    [Fact]
    public void SplitCarHasOneBodyClusterTwoMirrorsAndAnAntenna()
    {
        var (_, car) = Meshes.SynthCar(true);
        var asm = PartProxy.Discover(car, 0.04);
        PartProxy.Classify(asm, 0.04, 0.02, asm.Diag);
        var body = Assert.Single(asm.Parts, p => p.Class == PartClass.Bulk);
        var names = body.Units.Select(u => u.Name).ToHashSet();
        foreach (var n in new[] { "body_1", "body_2", "body_3", "body_4", "body_5", "body_6", "body_7", "body_8", "cabin" }) Assert.Contains(n, names);
        foreach (var n in new[] { "mirror_l", "mirror_r", "antenna" })
        {
            var p = Named(asm, n);
            Assert.NotSame(body, p);
            Assert.Equal(PartClass.Small, p.Class);
            Assert.Single(p.Units);
        }
    }

    [Fact]
    public void WheelAndMirrorAreSmallSignIsThinBodyIsBulk()
    {
        var sign = Box2(10, 0, 0, 12, 1, 0.002);
        var (car, _) = CarWith(("sign", sign));
        var asm = PartProxy.Discover(car, 0.04);
        double carD = CarDiag(Meshes.SynthCar(true).car);
        PartProxy.Classify(asm, 0.04, 0.02, carD);
        Assert.Equal(PartClass.Bulk, Named(asm, "body_1").Class);
        Assert.Equal(PartClass.Small, Named(asm, "mirror_l").Class);
        Assert.Equal(PartClass.Thin, Named(asm, "sign").Class);
        foreach (var w in asm.Moving.Where(m => m.Node.Cls == "FLT DOF"))
            Assert.All(w.Inner.Parts, p => Assert.Equal(PartClass.Small, p.Class));
    }

    static (double[] V, int[] T, double[] UV) Box2(double x0, double y0, double z0, double x1, double y1, double z1) => LodToolsTests.Box(x0, y0, z0, x1, y1, z1, 1);

    [Fact]
    public void TouchingBoltIsTinyAndDropped()
    {
        var (_, plain) = Meshes.SynthCar(true);
        var (car, _) = CarWith(("bolt", Box2(0, 0.8, 0.99, 0.01, 0.81, 1.0)));   // 1 cm cube on the roof panel, beside the cabin
        var asm = PartProxy.Discover(car, 0.04);
        PartProxy.Classify(asm, 0.04, 0.02, CarDiag(plain));
        Assert.Equal(1, asm.TinyDropped);
        Assert.DoesNotContain(asm.Parts, p => p.Units.Any(u => u.Name == "bolt"));
    }

    [Fact]
    public void FloatingBoltIsNotDropped()
    {
        var (_, plain) = Meshes.SynthCar(true);
        var (car, _) = CarWith(("bolt", Box2(0, 0.8, 1.49, 0.01, 0.81, 1.5)));
        var asm = PartProxy.Discover(car, 0.04);
        PartProxy.Classify(asm, 0.04, 0.02, CarDiag(plain));
        Assert.Equal(0, asm.TinyDropped);
        Assert.Equal(PartClass.Tiny, Named(asm, "bolt").Class);
    }

    [Fact]
    public void TwoMetreCubeIsNeverSmallOrThin()
    {
        var cube = Box2(0, 0, 0, 2, 2, 2);
        var alone = new Node(Kind.Geode, "osg::Geode", "cube") { Children = { Geometry("cube", cube) } };
        var a = PartProxy.Discover(alone, 0.04);
        PartProxy.Classify(a, 0.04, 0.02, a.Diag);
        Assert.Equal(PartClass.Bulk, Assert.Single(a.Parts).Class);
        var (_, plain) = Meshes.SynthCar(true);
        var (car, _) = CarWith(("cube", Box2(10, 0, 0, 12, 2, 2)));
        var b = PartProxy.Discover(car, 0.04);
        PartProxy.Classify(b, 0.04, 0.02, CarDiag(plain));
        Assert.Equal(PartClass.Bulk, Named(b, "cube").Class);
    }

    [Fact]
    public void MirrorOnADoorStaysOwnPart()
    {
        var door = Box2(0, 0, 0, 2, 0.05, 1.2);
        var mirror = Box2(1.8, -0.1, 1.0, 1.95, 0.0, 1.1);   // its back touches the door
        var g1 = new Node(Kind.Geode, "osg::Geode", "door") { Children = { Geometry("door", door) } };
        var g2 = new Node(Kind.Geode, "osg::Geode", "mirror") { Children = { Geometry("mirror", mirror) } };
        var target = new Node(Kind.Group, "osg::Group", "t") { Children = { g1, g2 } };
        var asm = PartProxy.Discover(target, 0.04);
        Assert.Equal(2, asm.Parts.Count);   // Discover applies the small rule: the mirror is its own part
        PartProxy.Classify(asm, 0.04, 0.02, 5.0);
        Assert.Equal(2, asm.Parts.Count);
        Assert.NotSame(Named(asm, "door"), Named(asm, "mirror"));
        Assert.Equal(PartClass.Small, Named(asm, "mirror").Class);
    }

    [Fact]
    public void ExtentsFollowThePrincipalAxes()
    {
        var b = Box2(-1, -0.25, -0.05, 1, 0.25, 0.05);
        var r = Mat.Mul(Rot(2, 30), Rot(0, 10));
        var V = new double[b.V.Length];
        for (int i = 0; i < b.V.Length / 3; i++) (V[3 * i], V[3 * i + 1], V[3 * i + 2]) = Mat.Apply(r, b.V[3 * i], b.V[3 * i + 1], b.V[3 * i + 2]);
        var (a, bb, c) = PartProxy.Extents(V, b.T);
        Assert.True(Math.Abs(a - 2) < 0.02);
        Assert.True(Math.Abs(bb - 0.5) < 0.005);
        Assert.True(Math.Abs(c - 0.1) < 0.001);
    }

    static double[] Rot(int axis, double deg)
    {
        double t = deg * Math.PI / 180, cs = Math.Cos(t), sn = Math.Sin(t);
        var m = Mat.Identity();
        int i = (axis + 1) % 3, j = (axis + 2) % 3;
        m[4 * i + i] = cs; m[4 * i + j] = -sn; m[4 * j + i] = sn; m[4 * j + j] = cs;
        return m;
    }

    // ------------------------------------------------------------------------------------------------ step 3: keep mode

    static readonly ProxyParams Keep = new() { Parts = ProxyParts.PerPart, Ratio = 0.05, Voxels = 48 };

    /// <summary>One source per piece, in AllPieces order (positions as gathered, UV unit 0, the unit-0 image).</summary>
    static List<Proxy.Source> SourcesOf(PartProxy.Assembly asm) => asm.AllPieces().Select(p => new Proxy.Source
    {
        V = p.V, T = p.T,
        UV = p.G.TexCoords.TryGetValue(0, out var uv) && uv.Comps == 2 && uv.Data.Length == p.V.Length / 3 * 2 ? uv.Data : null,
        Image = p.State?.Textures.FirstOrDefault(t => t.Unit == 0)?.Baked,
    }).ToList();

    static (PartProxy.ObjectResult r, List<Proxy.Source> src) KeepCar(bool split)
    {
        var (_, car) = Meshes.SynthCar(split);
        var asm = PartProxy.Discover(car, 2 * 4.5 / 48);   // the keep-mode gap: 2 cells of the object's grid (4.5 m / 48)
        var src = SourcesOf(asm);
        var r = PartProxy.BuildParts(asm, src, Keep, new PartProxy.BuildOptions { TextureSize = 128 });
        Assert.NotNull(r);
        return (r!, src);
    }

    /// <summary>The split car in keep mode, built once (about 20 s: 13 voxel hulls); the tests only read it, apart from
    /// BakeAtlas filling its meshes.</summary>
    static readonly Lazy<(PartProxy.ObjectResult r, List<Proxy.Source> src)> SplitKeep = new(() => KeepCar(true));

    /// <summary>Every part result: the static ones, then those of each moving part, depth first.</summary>
    static IEnumerable<PartProxy.PartResult> AllParts(PartProxy.ObjectResult r) =>
        r.Static.Concat(r.Moving.Where(m => m.Inner != null).SelectMany(m => AllParts(m.Inner!)));

    /// <summary>The parts that are drawn: not below a switch child that is off (what the report counts, ruling T16-d).</summary>
    static IEnumerable<PartProxy.PartResult> DrawnParts(PartProxy.ObjectResult r) =>
        r.Static.Concat(r.Moving.Where(m => m.Inner != null && PartProxy.Drawn(m.M)).SelectMany(m => DrawnParts(m.Inner!)));

    static PartProxy.PartResult Built(PartProxy.ObjectResult r, string unit) => Assert.Single(r.Static, p => p.Part.Units.Any(u => u.Name == unit));

    [Fact]
    public void SplitCarGetsOneProxyPerPartInKeepMode()
    {
        var (r, _) = SplitKeep.Value;
        var body = Built(r, "body_1");
        Assert.Equal(PartMethod.Voxel, body.Method);
        foreach (var n in new[] { "mirror_l", "mirror_r", "antenna" }) Assert.NotSame(body, Built(r, n));
        Assert.Equal(6, r.Moving.Count);
        foreach (var (m, inner) in r.Moving)
        {
            Assert.NotNull(inner);
            Assert.Same(m.Inner, inner!.Asm);
            Assert.NotEmpty(inner.Static);
        }
        Assert.All(AllParts(r), p => { Assert.NotEmpty(p.T); Assert.Equal(0, p.T.Length % 3); Assert.True(p.V.Length >= 9); });
        Assert.DoesNotContain(r.Notes, n => n.Contains("single hull"));
    }

    [Fact]
    public void BudgetFollowsSurfaceAreaWithMinimums()
    {
        var (r, src) = SplitKeep.Value;
        var parts = AllParts(r).ToList();
        long before = src.Sum(s => (long)s.T.Length / 3);
        // the report counts drawn geometry (the light state that is off neither before nor after); the budget is of every piece
        Assert.Equal(r.Asm.AllPieces().Where(PartProxy.DrawnPiece).Sum(p => (long)p.T.Length / 3), r.Report.TrisBefore);
        Assert.True(r.Report.TrisBefore < before);
        Assert.Equal(DrawnParts(r).Sum(p => (long)p.T.Length / 3), r.Report.TrisAfter);
        // the ratio budgets the voxel hulls (bulk parts); small parts are convex hulls (Task 7), outside the budget
        var hulls = parts.Where(p => p.Method == PartMethod.Voxel).ToList();
        Assert.NotEmpty(hulls);
        long hullTris = hulls.Sum(p => (long)p.T.Length / 3);
        Assert.True(hullTris <= Keep.Ratio * before + 12 * hulls.Count, $"{hullTris} of {before} for {hulls.Count} voxel hulls");
        var body = Built(r, "body_1");
        Assert.All(parts.Where(p => !ReferenceEquals(p, body)), p => Assert.True(p.T.Length < body.T.Length));
        Assert.All(parts, p => Assert.True(p.T.Length / 3 >= 8, $"{p.Part.Name}: {p.T.Length / 3} triangles"));
    }

    [Fact]
    public void OneAtlasForTheWholeObject()
    {
        var (r, src) = SplitKeep.Value;
        var (tex, _) = PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, new PartProxy.BuildOptions { TextureSize = 128 });
        Assert.NotNull(tex);
        Assert.Equal(128, tex!.Width);
        Assert.Equal(7, r.Meshes.Count);   // the static parts, 4 wheels, 2 light states
        Assert.Null(r.Meshes[0].Owner);
        Assert.Equal(r.Moving.Select(m => m.M), r.Meshes.Skip(1).Select(x => x.Owner));
        // every mesh is made (the light state that is off too); the report counts the drawn ones
        Assert.Equal(r.Report.TrisAfter, r.Meshes.Where(x => x.Owner == null || PartProxy.Drawn(x.Owner)).Sum(x => (long)x.T.Length / 3));
        foreach (var mesh in r.Meshes)
        {
            int nv = mesh.V.Length / 3;
            Assert.Equal(2 * nv, mesh.UV.Length);
            Assert.Equal(3 * nv, mesh.N.Length);
            Assert.All(mesh.UV, u => Assert.InRange(u, 0.0, 1.0));
            Assert.All(mesh.T, i => Assert.InRange(i, 0, nv - 1));
            Assert.Equal(mesh.T.Length / 3, mesh.PartOfTri.Length);
            var owner = mesh.Owner == null ? r : r.Moving.Single(m => m.M == mesh.Owner).Inner!;
            Assert.All(mesh.PartOfTri, k => Assert.InRange(k, 0, owner.Static.Count - 1));
            Assert.Equal(owner.Static.Sum(p => p.T.Length / 3), mesh.T.Length / 3);
        }
        // a wheel's mesh is in the DOF's own frame, around its origin (radius 0.33, within a cell)
        foreach (var mesh in r.Meshes.Where(x => x.Owner?.Node.Cls == "FLT DOF"))
            for (int i = 0; i < mesh.V.Length / 3; i++)
                Assert.True(Math.Abs(mesh.V[3 * i]) <= 0.33 + 0.1 && Math.Abs(mesh.V[3 * i + 2]) <= 0.33 + 0.1);
    }

    [Fact]
    public void ReportHasTheSpecKeys()
    {
        var (r, _) = SplitKeep.Value;
        using var doc = System.Text.Json.JsonDocument.Parse(r.Report.ToJson());
        var root = doc.RootElement;
        Assert.Equal(new[] { "object", "parts", "moving_parts", "tiny_dropped", "per_part", "tris_before", "tris_after", "draw_calls_before",
            "draw_calls_after", "target_m", "reached_m", "within", "ground", "normals", "build_ms" }.ToHashSet(), root.EnumerateObject().Select(x => x.Name).ToHashSet());
        var per = root.GetProperty("per_part").EnumerateArray().ToList();
        Assert.Equal(AllParts(r).Count(), per.Count);
        Assert.Equal(per.Count, root.GetProperty("parts").GetInt32());
        Assert.Equal(6, root.GetProperty("moving_parts").GetInt32());
        Assert.Equal(18, root.GetProperty("draw_calls_before").GetInt32());   // drawn only: the light state that is off is not
        Assert.Equal(6, root.GetProperty("draw_calls_after").GetInt32());
        Assert.Equal("car", root.GetProperty("object").GetString());
        Assert.False(root.GetProperty("within").GetBoolean());
        Assert.Equal("full", root.GetProperty("ground").GetString());
        Assert.Equal("hull", root.GetProperty("normals").GetString());
        foreach (var e in per)
        {
            Assert.Equal(new[] { "name", "class", "method", "tris", "dev_m" }.ToHashSet(), e.EnumerateObject().Select(x => x.Name).ToHashSet());
            Assert.Contains(e.GetProperty("method").GetString(), new[] { "voxel", "convex", "decimated", "kept" });
            Assert.Contains(e.GetProperty("class").GetString(), new[] { "tiny", "small", "thin", "bulk" });
        }
        Assert.Contains(per, e => e.GetProperty("name").GetString() == "body_1" && e.GetProperty("class").GetString() == "bulk");
    }

    [Fact]
    public void MergedInputFallsBackToTheSingleHull()
    {
        var (r, src) = KeepCar(false);
        Assert.Contains("1 part found, using the single hull", r.Notes);
        var only = Assert.Single(r.Static);
        Assert.Empty(r.Moving);
        long tris = src.Sum(s => (long)s.T.Length / 3);
        var today = Proxy.Build(src, new Proxy.Options { TargetTris = (int)Math.Max(12, tris * Keep.Ratio), Resolution = 48, TextureSize = 128, Bake = false });
        Assert.NotNull(today);
        Assert.Equal(today!.T.Length / 3, only.T.Length / 3);
        Assert.Equal(PartMethod.Voxel, only.Method);
    }

    [Fact]
    public void BakeAtlasNeedsOneSourceListPerResult()
    {
        var (r, src) = SplitKeep.Value;
        Assert.Throws<ArgumentException>(() => PartProxy.BakeAtlas(new[] { r }, Array.Empty<IReadOnlyList<Proxy.Source>>(), new PartProxy.BuildOptions { TextureSize = 128 }));
    }

    [Fact]
    public void BuildingTwiceDoesNotClassifyTwice()
    {
        var (r, src) = SplitKeep.Value;
        Assert.True(r.Asm.Classified);
        var again = PartProxy.BuildParts(r.Asm, src, Keep, new PartProxy.BuildOptions { TextureSize = 128 });
        Assert.NotNull(again);
        Assert.Equal(r.Report.Parts, again!.Report.Parts);
        Assert.Equal(r.Report.TinyDropped, again.Report.TinyDropped);
    }

    // ------------------------------------------------------------------------------------------------ step 8: re-assembly

    /// <summary>replace_proxy per part in keep mode on one target (texture 64, voxels 48, no share); the pass message.</summary>
    static string RunPerPart(Scene sc, Node target, Dictionary<string, object>? extra = null)
    {
        var prms = new Dictionary<string, object>
        {
            ["replace_proxy.parts"] = "per_part", ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 48, ["replace_proxy.share"] = false,
        };
        foreach (var (k, v) in extra ?? new()) prms[k] = v;
        var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, prms, targets: new List<Node> { target });
        Assert.Null(results[0].Error);
        return results[0].Message;
    }

    /// <summary>The nodes from the root down to <paramref name="n"/> (excluded), along the first path found.</summary>
    static List<Node> Ancestors(Scene sc, Node n)
    {
        List<Node>? Find(Node at, List<Node> above)
        {
            if (ReferenceEquals(at, n)) return above;
            foreach (var c in at.Children)
                if (Find(c, new List<Node>(above) { at }) is { } l) return l;
            return null;
        }
        return Find(sc.Root!, new List<Node>()) ?? throw new InvalidOperationException($"{n.Name} is not in the scene");
    }

    /// <summary>World bounding box of everything drawn below a node (its own matrix included).</summary>
    static (double[] lo, double[] hi) WorldBox(Scene sc, Node n)
    {
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var (g, w, _) in SoftRender.GeometryItems(n, start: SoftRender.PathMatrix(Ancestors(sc, n))))
        {
            var V = g.Geom!.Vertices!.Data;
            foreach (int i in g.Geom.Triangles())
            {
                var (x, y, z) = Mat.Apply(w, V[3 * i], V[3 * i + 1], V[3 * i + 2]);
                var p = new[] { x, y, z };
                for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], p[c]); hi[c] = Math.Max(hi[c], p[c]); }
            }
        }
        return (lo, hi);
    }

    static void WithinCells((double[] lo, double[] hi) a, (double[] lo, double[] hi) b, double tol, string what)
    {
        for (int c = 0; c < 3; c++)
        {
            Assert.True(Math.Abs(a.lo[c] - b.lo[c]) <= tol, $"{what}: min[{c}] {a.lo[c]:F4} vs {b.lo[c]:F4} (tolerance {tol:F4})");
            Assert.True(Math.Abs(a.hi[c] - b.hi[c]) <= tol, $"{what}: max[{c}] {a.hi[c]:F4} vs {b.hi[c]:F4} (tolerance {tol:F4})");
        }
    }

    /// <summary>The geode a per-part run put below a moving node in place of its drawables.</summary>
    static Node ProxyGeode(Node moving) => Assert.Single(moving.Children, c => c.Kind == Kind.Geode && c.Name.EndsWith(" proxy"));

    const double CarCell = 4.5 / 48;   // the split car's extent over the voxels

    [Fact]
    public void DofAndSwitchNamesMatricesAndValuesArePreserved()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var dofs = car.Children.Where(c => c.Cls == "FLT DOF").ToList();
        var mats = dofs.Select(d => (double[])d.Matrix!.Clone()).ToList();
        var lights = car.Children.Single(c => c.Name == "lights");
        RunPerPart(sc, car);
        var holder = Assert.Single(sc.Root!.Children);
        Assert.Equal("car proxy", holder.Name);
        var now = sc.IterNodes().Where(n => n.Cls == "FLT DOF").ToList();
        Assert.Equal(4, now.Count);
        for (int i = 0; i < dofs.Count; i++)
        {
            Assert.Same(dofs[i], Assert.Single(now, n => n.Name == dofs[i].Name));
            Assert.Equal(mats[i], dofs[i].Matrix);
            Assert.Contains(dofs[i], holder.Children);   // the identity above it: no place transform
            var geo = Assert.Single(ProxyGeode(dofs[i]).Children);
            Assert.Equal(Kind.Geometry, geo.Kind);
            Assert.InRange(geo.Geom!.Triangles().Length / 3, 12, 255);   // a proxy, lighter than the 256-triangle wheel
        }
        Assert.Contains(lights, holder.Children);
        Assert.Equal("FLT Switch", lights.Cls);
        Assert.Equal(new[] { true, false }, lights.SwitchValues);
        Assert.Equal(2, lights.Children.Count);
        Assert.All(lights.Children, c => Assert.NotEmpty(SoftRender.GeometryItems(c)));
        // the static parts: one geometry in one geode
        var statics = Assert.Single(holder.Children, c => c.Kind == Kind.Geode);
        Assert.Equal("car proxy", Assert.Single(statics.Children).Name);
    }

    [Fact]
    public void MovingPartsStayWithinOneCellOfTheOriginals()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var wheels = car.Children.Where(c => c.Cls == "FLT DOF").ToList();
        var before = wheels.Select(w => WorldBox(sc, w)).ToList();
        RunPerPart(sc, car);
        for (int i = 0; i < wheels.Count; i++) WithinCells(before[i], WorldBox(sc, ProxyGeode(wheels[i])), CarCell, wheels[i].Name);
    }

    [Fact]
    public void DrawCallsAreOnePlusOnePerMovingPart()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var msg = RunPerPart(sc, car);
        // drawn geometry on both sides (ruling T16-d): 13 static + 4 wheels + the light that is on -> 1 + 4 + the light that
        // is on; the light that is off still gets its proxy (7 geometries below), it is just not drawn
        Assert.Contains("draw calls 18 -> 6", msg);
        Assert.Contains("(4 wheels and 1 switch kept separate)", msg);
        Assert.StartsWith("1 object(s) replaced with a per-part proxy: ", msg);
        var textures = sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Where(t => t.Baked != null)
            .Distinct(ReferenceEqualityComparer.Instance).ToList();
        Assert.Single(textures);
        var geos = sc.IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Equal(7, geos.Count);
        Assert.All(geos, g => Assert.Same(geos[0].StateSet, g.StateSet));   // one state set, one atlas
    }

    [Fact]
    public void SelectedDofIsTheTargetNotAMovingPart()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var fl = car.Children.Single(n => n.Name == "wheel_fl");
        var mat = (double[])fl.Matrix!.Clone();
        var (blo, bhi) = WorldBox(sc, fl);
        RunPerPart(sc, fl);
        Assert.Contains(fl, car.Children);
        Assert.Equal("FLT DOF", fl.Cls);
        Assert.Equal(mat, fl.Matrix);
        var geode = Assert.Single(fl.Children);
        Assert.Equal(Kind.Geode, geode.Kind);
        var geo = Assert.Single(geode.Children);
        Assert.True(geo.Geom!.Triangles().Length / 3 < 256);
        // in the DOF's own frame: the proxy sits where the wheel was (a wrong frame would move it by metres)
        var (lo, hi) = WorldBox(sc, geode);
        double cell = 0.66 / 48;
        for (int c = 0; c < 3; c++) Assert.InRange((lo[c] + hi[c]) / 2 - (blo[c] + bhi[c]) / 2, -2 * cell, 2 * cell);
    }

    [Fact]
    public void SharedDofNodeIsClonedNotMutated()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var fl = car.Children.Single(n => n.Name == "wheel_fl");
        var car2 = new Node(Kind.Group, "osg::Group", "car_2") { Children = { fl } };
        sc.Root!.Children.Add(car2);
        var kids = fl.Children.ToArray();
        var geode = kids[0];
        var geoms = geode.Children.Select(c => c.Geom).ToArray();
        var mat = (double[])fl.Matrix!.Clone();
        RunPerPart(sc, car);
        Assert.Same(fl, Assert.Single(car2.Children));
        Assert.Equal(kids, fl.Children);
        Assert.Equal(geoms, geode.Children.Select(c => c.Geom));
        Assert.Equal(mat, fl.Matrix);
        var holder = sc.Root.Children[0];
        Assert.Equal("car proxy", holder.Name);
        var clone = Assert.Single(holder.Children, n => n.Name == "wheel_fl");
        Assert.NotSame(fl, clone);
        Assert.Equal("FLT DOF", clone.Cls);
        Assert.Equal(mat, clone.Matrix);
        Assert.Single(ProxyGeode(clone).Children);
    }

    [Fact]
    public void MirroredDofWheelStaysInPlaceAndFacesOut()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var rl = car.Children.Single(n => n.Name == "wheel_rl");
        rl.Matrix![5] = -1;   // scale (1, -1, 1): a mirrored left wheel
        var before = WorldBox(sc, rl);
        RunPerPart(sc, car);
        var geode = ProxyGeode(rl);
        WithinCells(before, WorldBox(sc, geode), CarCell, "wheel_rl");
        var g = Assert.Single(geode.Children).Geom!;
        var V = g.Vertices!.Data; var N = g.Normals!.Data; var T = g.Triangles();
        var m = rl.Matrix;
        var inv = Mat.Invert(m)!;
        int m3 = T.Length / 3, outLocal = 0, outWorld = 0;
        for (int t = 0; t < m3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double[] P(int i) => new[] { V[3 * i], V[3 * i + 1], V[3 * i + 2] };
            var (pa, pb, pc) = (P(a), P(b), P(c));
            var ctr = Enumerable.Range(0, 3).Select(k => (pa[k] + pb[k] + pc[k]) / 3).ToArray();   // the wheel centre is the DOF's origin
            double ux = pb[0] - pa[0], uy = pb[1] - pa[1], uz = pb[2] - pa[2], vx = pc[0] - pa[0], vy = pc[1] - pa[1], vz = pc[2] - pa[2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            if (nx * ctr[0] + ny * ctr[1] + nz * ctr[2] > 0) outLocal++;   // the winding faces out in the node's frame, as the original
            // the stored normals, taken into the world by the inverse transpose of the DOF matrix, point away from the centre there
            var avg = Enumerable.Range(0, 3).Select(k => N[3 * a + k] + N[3 * b + k] + N[3 * c + k]).ToArray();
            var wn = Enumerable.Range(0, 3).Select(r => inv[r] * avg[0] + inv[4 + r] * avg[1] + inv[8 + r] * avg[2]).ToArray();
            var (wx, wy, wz) = Mat.Apply(m, ctr[0], ctr[1], ctr[2]);
            if (wn[0] * (wx - m[3]) + wn[1] * (wy - m[7]) + wn[2] * (wz - m[11]) > 0) outWorld++;
        }
        Assert.True(outLocal > 0.95 * m3, $"{outLocal} of {m3} triangles wound outward");
        Assert.True(outWorld > 0.95 * m3, $"{outWorld} of {m3} normals outward in the world");
    }

    [Fact]
    public void UndoBringsTheSplitCarBack()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var nodes = sc.IterNodes().ToList();
        var kids = nodes.Select(n => n.Children.ToArray()).ToList();
        var mats = nodes.Select(n => n.Matrix?.ToArray()).ToList();
        var values = nodes.Select(n => n.SwitchValues?.ToArray()).ToList();
        var snap = new Snapshot(sc, "before");
        RunPerPart(sc, car);
        Assert.NotSame(car, sc.Root!.Children[0]);
        Assert.NotEqual(kids[nodes.IndexOf(car.Children.Single(c => c.Name == "wheel_fl"))], car.Children.Single(c => c.Name == "wheel_fl").Children);
        snap.Restore();
        var back = sc.IterNodes().ToList();
        Assert.Equal(nodes.Count, back.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            Assert.Same(nodes[i], back[i]);
            Assert.Equal(kids[i].Length, back[i].Children.Count);
            for (int k = 0; k < kids[i].Length; k++) Assert.Same(kids[i][k], back[i].Children[k]);
            Assert.Equal(mats[i], back[i].Matrix);
            Assert.Equal(values[i], back[i].SwitchValues?.ToArray());
        }
    }

    [Fact]
    public void EmptyMovingNodeIsKeptAsItIs()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var m = Mat.Identity(); m[3] = 1;
        var emptyKid = new Node(Kind.Group, "FLT Group", "empty_kid");
        var dof = new Node(Kind.Transform, "FLT DOF", "empty_dof") { Matrix = (double[])m.Clone(), Children = { emptyKid } };
        car.Children.Add(dof);
        var lights = car.Children.Single(c => c.Name == "lights");
        var none = new Node(Kind.Group, "FLT Group", "lights_none");
        lights.Children.Add(none);
        lights.SwitchValues!.Add(false);
        RunPerPart(sc, car);
        Assert.Contains(dof, sc.IterNodes());
        Assert.Same(emptyKid, Assert.Single(dof.Children));
        Assert.Empty(emptyKid.Children);
        Assert.Equal(m, dof.Matrix);
        Assert.Contains(lights, sc.IterNodes());
        Assert.Equal(3, lights.Children.Count);
        Assert.Same(none, lights.Children[2]);
        Assert.Equal(new[] { true, false, false }, lights.SwitchValues);
    }

    static double[] Move(double x, double y, double z) { var m = Mat.Identity(); m[3] = x; m[7] = y; m[11] = z; return m; }

    static Node BoxGeode(string name, double x1, double y1, double z1) =>
        new(Kind.Geode, "osg::Geode", name) { Children = { Geometry(name, LodToolsTests.Box(0, 0, 0, x1, y1, z1, 8)) } };

    [Fact]
    public void NestedMovingNodesBelowAStaticTransformStayInPlace()
    {
        // the target has a matrix of its own; a static transform between it and a DOF; a DOF inside that DOF
        var d1Box = BoxGeode("d1_box", 0.6, 0.3, 0.3);
        var d2Box = BoxGeode("d2_box", 0.4, 0.2, 0.2);
        var d2 = new Node(Kind.Transform, "FLT DOF", "d2") { Matrix = Mat.Mul(Move(0.6, 0.1, 0.3), Rot(1, 20)), Children = { d2Box } };
        var d1 = new Node(Kind.Transform, "FLT DOF", "d1") { Matrix = Mat.Mul(Move(0.3, 0, 0.2), Rot(0, 15)), Children = { d1Box, d2 } };
        var arm = new Node(Kind.Transform, "osg::MatrixTransform", "arm") { Matrix = Mat.Mul(Move(1, 0.2, 0.5), Rot(2, 90)), Children = { d1 } };
        var baseBox = BoxGeode("base", 2, 1, 0.5);
        var obj = new Node(Kind.Transform, "osg::MatrixTransform", "obj") { Matrix = Mat.Mul(Move(5, 2, 0), Rot(2, 30)), Children = { baseBox, arm } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } } };
        var whole = WorldBox(sc, obj);
        double cell = Enumerable.Range(0, 3).Max(c => whole.hi[c] - whole.lo[c]) / 48;
        var b0 = WorldBox(sc, baseBox); var b1 = WorldBox(sc, d1Box); var b2 = WorldBox(sc, d2Box);
        var msg = RunPerPart(sc, obj, new() { ["replace_proxy.ratio"] = 0.3 });
        Assert.Contains("replaced with a per-part proxy", msg);
        var holder = Assert.Single(sc.Root!.Children);
        Assert.Equal("obj proxy", holder.Name);
        var place = Assert.Single(holder.Children, c => c.Name == "d1 place");   // the target's matrix and the static one above the DOF
        Assert.Same(d1, Assert.Single(place.Children));
        Assert.Contains(d2, d1.Children);
        WithinCells(b0, WorldBox(sc, Assert.Single(holder.Children, c => c.Kind == Kind.Geode)), cell, "static part");
        WithinCells(b1, WorldBox(sc, ProxyGeode(d1)), cell, "d1");
        WithinCells(b2, WorldBox(sc, ProxyGeode(d2)), cell, "d2");
    }

    [Fact]
    public void KeptPartIsItsOwnGeometryWithItsOriginalState()
    {
        var (_, car) = Meshes.SynthCar(true);
        var asm = PartProxy.Discover(car, 2 * CarCell);
        var mirror = Named(asm, "mirror_l");
        var pr = new PartProxy.PartResult { Part = mirror, Method = PartMethod.Kept, V = mirror.V, T = mirror.T };
        var state = new StateSet { MaterialName = "original" };
        var made = PartProxy.KeptGeometries(asm, pr, _ => state, null);
        var (geo, from) = Assert.Single(made);
        Assert.Equal("mirror_l", from.Geom.Name);
        Assert.Same(state, geo.StateSet);
        var g = geo.Geom!;
        Assert.Equal(mirror.T.Length / 3, g.Triangles().Length / 3);
        int nv = g.Vertices!.Count;
        Assert.Equal(2 * nv, g.TexCoords[0].Data.Length);
        Assert.Equal(3 * nv, g.Normals!.Data.Length);
        // the same triangles at the same places
        var want = Enumerable.Range(0, mirror.T.Length).Select(i => (mirror.V[3 * mirror.T[i]], mirror.V[3 * mirror.T[i] + 1], mirror.V[3 * mirror.T[i] + 2])).ToList();
        var T = g.Triangles(); var V = g.Vertices.Data;
        Assert.Equal(want, Enumerable.Range(0, T.Length).Select(i => (V[3 * T[i]], V[3 * T[i] + 1], V[3 * T[i] + 2])).ToList());
    }
    [Fact]
    public void KeptPartWithTwoPiecesIsOneDrawCallPerGeometryInTheAssembly()
    {
        // a part of two touching boxes with different state sets, kept as its original: built by hand, no Task 7 needed
        var a = Geometry("box_a", Box2(0, 0, 0, 1, 1, 1));
        var b = Geometry("box_b", Box2(1, 0, 0, 2, 1, 1));
        var sa = new StateSet { MaterialName = "a" }; var sb = new StateSet { MaterialName = "b" };
        a.StateSet = sa; b.StateSet = sb;
        var obj = new Node(Kind.Group, "osg::Group", "obj") { Children = { new Node(Kind.Geode, "osg::Geode", "ga") { Children = { a } }, new Node(Kind.Geode, "osg::Geode", "gb") { Children = { b } } } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } } };
        var asm = PartProxy.Discover(obj, 0.5);
        var part = Assert.Single(asm.Parts);
        Assert.Equal(2, asm.Pieces.Count);
        var r = new PartProxy.ObjectResult { Asm = asm, Cell = 0.04 };
        r.Static.Add(new PartProxy.PartResult { Part = part, Method = PartMethod.Kept, V = part.V, T = part.T });
        var ctx = new OptContext(sc);
        var made = PartProxy.Assemble(ctx, obj, r, new Dictionary<PartProxy.ProxyMesh, Node>(ReferenceEqualityComparer.Instance),
            p => ReferenceEquals(p.Geom, a) ? sa : sb, "obj");
        Assert.Equal("obj proxy", made.Holder.Name);
        Assert.Equal(2, made.Holder.Children.Count);
        Assert.All(made.Holder.Children, g => { Assert.Equal(Kind.Geode, g.Kind); Assert.Equal(part.Name + " kept", g.Name); Assert.Single(g.Children); });
        Assert.Equivalent(new[] { "a", "b" }, made.Holder.Children.Select(g => g.Children[0].StateSet!.MaterialName).ToArray());
        Assert.Equal(2, made.DrawCalls);
        Assert.Equal(2, made.KeptDraws);
        Assert.Contains($"1 parts kept original or decimated, +2 draw calls ({part.Name})", PartProxy.Detail(r, made));
    }

    [Fact]
    public void SwitchAsTargetKeepsOnChildrenInTheProxyAndOffChildrenOut()
    {
        var on1 = BoxGeode("on1", 1, 1, 1);
        var on2 = new Node(Kind.Transform, "osg::MatrixTransform", "on2_at") { Matrix = Move(2, 0, 0), Children = { BoxGeode("on2", 1, 1, 1) } };
        var off = new Node(Kind.Transform, "osg::MatrixTransform", "off_at") { Matrix = Move(10, 0, 0), Children = { BoxGeode("off", 1, 1, 1) } };
        var sw = new Node(Kind.Switch, "FLT Switch", "sw") { SwitchValues = new() { true, true, false }, Children = { on1, on2, off } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { sw } } };
        var offBefore = WorldBox(sc, off);
        RunPerPart(sc, sw);
        Assert.Contains(sw, sc.IterNodes());
        Assert.Same(off, sw.Children[^1]);
        Assert.False(sw.SwitchValues![^1]);
        Assert.Equal(sw.Children.Count, sw.SwitchValues.Count);
        Assert.Equal(offBefore.lo, WorldBox(sc, off).lo);
        // everything switched on is covered by the proxy below the switch, nothing of it is drawn from the switched-off child
        var onChildren = sw.Children.Where((_, i) => sw.SwitchValues[i]).ToList();
        Assert.NotEmpty(onChildren);
        Assert.DoesNotContain(on1, sw.Children);
        Assert.DoesNotContain(on2, sw.Children);
        var lo = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var c in onChildren)
        {
            var (l, h) = WorldBox(sc, c);
            for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], l[k]); hi[k] = Math.Max(hi[k], h[k]); }
        }
        double cell = 3.0 / 48;
        Assert.True(lo[0] <= 0 + cell && hi[0] >= 3 - cell, $"on children cover x 0..3, got {lo[0]:F3}..{hi[0]:F3}");
        Assert.True(hi[0] < 5, "nothing of the switched-off child (x 10..11) is in the proxy");
    }
    [Fact]
    public void KeptPartKeepsOriginalNormalsAndColoursInTheOwnersFrame()
    {
        var box = Geometry("box", Box2(0, 0, 0, 1, 1, 1));
        int nv = box.Geom!.Vertices!.Count;
        var n = new double[3 * nv]; var c = new double[4 * nv];
        for (int i = 0; i < nv; i++) { n[3 * i] = 1; c[4 * i] = 0.25; c[4 * i + 3] = 1; }   // all normals +x in the geometry's frame
        box.Geom.Normals = new VArray(n, 3, "Vec3Array");
        box.Geom.Colors = new VArray(c, 4, "Vec4Array");
        var obj = new Node(Kind.Transform, "osg::MatrixTransform", "obj") { Matrix = Rot(2, 90), Children = { new Node(Kind.Geode, "osg::Geode", "g") { Children = { box } } } };
        var asm = PartProxy.Discover(obj, 0.1);
        var part = Assert.Single(asm.Parts);
        var made = PartProxy.KeptGeometries(asm, new PartProxy.PartResult { Part = part, Method = PartMethod.Kept, V = part.V, T = part.T }, _ => null, null);
        var g = Assert.Single(made).Geo.Geom!;
        Assert.Equal(0.25, g.Colors!.Data[0]);
        Assert.Equal(4, g.Colors.Comps);
        for (int i = 0; i < g.Normals!.Count; i++)
        {   // +x turned 90 degrees about z is +y
            Assert.Equal(0, g.Normals.Data[3 * i], 6);
            Assert.Equal(1, g.Normals.Data[3 * i + 1], 6);
        }
    }

    // ------------------------------------------------------------------------------------------------ step 3: small, thin and tiny parts

    /// <summary>The part of a single-geometry object (its one unit).</summary>
    static (PartProxy.Assembly asm, PartProxy.Part part) OnePart(string name, (double[] V, int[] T, double[] UV) m)
    {
        var target = new Node(Kind.Geode, "osg::Geode", name) { Children = { Geometry(name, m) } };
        var asm = PartProxy.Discover(target, 0.001);
        return (asm, Assert.Single(asm.Parts));
    }

    /// <summary>An L-shaped bracket 0.4 x 0.4 x 0.05 m: two subdivided boxes that share the corner positions where they meet.</summary>
    static (double[] V, int[] T, double[] UV) LBracket()
    {
        var a = LodToolsTests.Box(0, 0, 0, 0.4, 0.05, 0.05, 8);
        var b = LodToolsTests.Box(0, 0.05, 0, 0.05, 0.4, 0.05, 8);
        return (a.V.Concat(b.V).ToArray(), a.T.Concat(b.T.Select(t => t + a.V.Length / 3)).ToArray(), a.UV.Concat(b.UV).ToArray());
    }

    /// <summary>A curved spoiler: a one-sided strip 1.2 m long and 0.3 m wide, arched 10 cm along its length and 3 cm across
    /// (so no row of it is straight), 48 x 12 quads.</summary>
    static (double[] V, int[] T, double[] UV) Spoiler()
    {
        const int nx = 48, ny = 12;
        var V = new List<double>(); var T = new List<int>(); var UV = new List<double>();
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                double x = 1.2 * i / nx, y = 0.3 * j / ny;
                V.AddRange(new[] { x, y, 0.1 * Math.Sin(Math.PI * x / 1.2) + 0.03 * Math.Sin(Math.PI * y / 0.3) });
                UV.Add((double)i / nx); UV.Add((double)j / ny);
            }
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                T.AddRange(new[] { a, b, c, b, d, c });
            }
        return (V.ToArray(), T.ToArray(), UV.ToArray());
    }

    [Fact]
    public void WheelAndMirrorBecomeConvexHulls()
    {
        var (r, _) = SplitKeep.Value;
        var parts = new List<PartProxy.PartResult> { Built(r, "mirror_l"), Built(r, "mirror_r") };
        foreach (var (m, inner) in r.Moving.Where(x => x.M.Node.Cls == "FLT DOF")) parts.Add(Assert.Single(inner!.Static));
        Assert.Equal(6, parts.Count);
        foreach (var p in parts)
        {
            Assert.Equal(PartMethod.Convex, p.Method);
            int n = p.Part.V.Length / 3;
            Assert.True(p.T.Length / 3 <= 2 * n - 4, $"{p.Part.Name}: {p.T.Length / 3} triangles for {n} points");
            Assert.False(double.IsNaN(p.DevM));
            Assert.True(PartProxy.InAtlas(p));
        }
    }

    [Fact]
    public void LShapedPartFailsTheHullCheckAndIsDecimated()
    {
        var (_, part) = OnePart("bracket", LBracket());
        var hull = ConvexHull.Build(part.V)!.Value;
        Assert.True(PartProxy.OneWayDistance(hull.V, hull.T, new TriIndex(part.V, part.T), 20000) > 0.1);   // the hull spans the inner corner
        var pr = PartProxy.BuildSmall(part, 0.02, 0.01);
        Assert.Equal(PartMethod.Decimated, pr.Method);
        Assert.True(pr.T.Length < part.T.Length);
        Assert.True(pr.DevM <= 0.02);
        Assert.False(PartProxy.InAtlas(pr));
    }

    [Fact]
    public void DecimatedPartIsDrawnWithTheOriginalStateAndUvs()
    {
        // the L bracket as two pieces (one per arm) with their own state sets and distinct texture coordinates
        var m = LBracket();
        int na = LodToolsTests.Box(0, 0, 0, 0.4, 0.05, 0.05, 8).V.Length / 3;
        var uvA = m.UV.Take(2 * na).ToArray();
        var uvB = m.UV.Skip(2 * na).Select(x => x + 10).ToArray();   // u and v of the second arm lie in 10 .. 11
        var a = Geometry("arm_a", (m.V.Take(3 * na).ToArray(), m.T.Where(t => t < na).ToArray(), uvA));
        var b = Geometry("arm_b", (m.V.Skip(3 * na).ToArray(), m.T.Where(t => t >= na).Select(t => t - na).ToArray(), uvB));
        a.Geom!.TexCoords[0] = new VArray(uvA, 2, "Vec2Array");
        b.Geom!.TexCoords[0] = new VArray(uvB, 2, "Vec2Array");
        var sa = new StateSet { MaterialName = "paint a" }; var sb = new StateSet { MaterialName = "paint b" };
        var obj = new Node(Kind.Group, "osg::Group", "obj") { Children = { new Node(Kind.Geode, "osg::Geode", "ga") { Children = { a } }, new Node(Kind.Geode, "osg::Geode", "gb") { Children = { b } } } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } } };
        var asm = PartProxy.Discover(obj, 0.001);
        var part = Assert.Single(asm.Parts);
        Assert.Equal(2, asm.Pieces.Count);
        var pr = PartProxy.BuildSmall(part, 0.02, 0.01);
        Assert.Equal(PartMethod.Decimated, pr.Method);
        var r = new PartProxy.ObjectResult { Asm = asm, Cell = 0.01 };
        r.Static.Add(pr);
        var made = PartProxy.Assemble(new OptContext(sc), obj, r, new Dictionary<PartProxy.ProxyMesh, Node>(ReferenceEqualityComparer.Instance),
            p => ReferenceEquals(p.Geom, a) ? sa : sb, "obj");
        var geos = made.Holder.Children.Select(h => Assert.Single(h.Children)).ToList();
        Assert.Equal(2, geos.Count);
        Assert.All(geos, g => Assert.Equal(part.Name + " decimated", g.Name));
        Assert.Equivalent(new[] { sa, sb }, geos.Select(g => g.StateSet).ToArray());
        Assert.Equal(pr.T.Length / 3, geos.Sum(g => g.Geom!.Triangles().Length / 3));
        foreach (var g in geos)
        {
            bool isA = ReferenceEquals(g.StateSet, sa);
            var pieceV = (isA ? a : b).Geom!.Vertices!.Data; var pieceUV = isA ? uvA : uvB;
            int nv = g.Geom!.Vertices!.Count;
            Assert.Equal(2 * nv, g.Geom.TexCoords[0].Data.Length);
            for (int v = 0; v < nv; v++)
            {
                // the result vertex at this position, its source part vertex (From), and the piece vertex at that place
                var pos = g.Geom.Vertices.Data.Skip(3 * v).Take(3).ToArray();
                var res = Enumerable.Range(0, pr.V.Length / 3).Where(k => Enumerable.Range(0, 3).All(c => pr.V[3 * k + c] == pos[c])).ToList();
                Assert.NotEmpty(res);
                Assert.Contains(res, k => Enumerable.Range(0, pieceV.Length / 3).Any(i =>
                    Enumerable.Range(0, 3).All(c => pieceV[3 * i + c] == part.V[3 * pr.From![k] + c])
                    && pieceUV[2 * i] == g.Geom.TexCoords[0].Data[2 * v] && pieceUV[2 * i + 1] == g.Geom.TexCoords[0].Data[2 * v + 1]));
                // a's coordinates lie in 0 .. 1, b's in 10 .. 11
                Assert.Equal(isA, g.Geom.TexCoords[0].Data[2 * v] <= 1);
            }
        }
        Assert.Equal(2, made.KeptDraws);
        Assert.Contains($"1 parts kept original or decimated, +2 draw calls ({part.Name})", PartProxy.Detail(r, made));
    }

    /// <summary>An L bracket of two boxes with arms of <paramref name="arm"/> m and 0.1 m thick, at x = <paramref name="x"/>, boxes subdivided <paramref name="res"/> times.</summary>
    static (double[] V, int[] T, double[] UV) Bracket(double x, double arm, int res = 1)
    {
        var a = LodToolsTests.Box(x, 0, 0, x + arm, 0.1, 0.1, res);
        var b = LodToolsTests.Box(x, 0.1, 0, x + 0.1, arm, 0.1, res);
        return (a.V.Concat(b.V).ToArray(), a.T.Concat(b.T.Select(t => t + a.V.Length / 3)).ToArray(), a.UV.Concat(b.UV).ToArray());
    }

    [Fact]
    public void ObjectWhoseEveryPartIsKeptIsLeftUnchangedWithANote()
    {
        // two brackets 8 m apart, 24 triangles each: concave, so neither hull nor decimation stays within 2 cells (about 0.16 m)
        var obj = new Node(Kind.Group, "osg::Group", "obj")
        {
            Children = { new Node(Kind.Geode, "osg::Geode", "b1") { Children = { Geometry("b1", Bracket(0, 1.5)) } },
                         new Node(Kind.Geode, "osg::Geode", "b2") { Children = { Geometry("b2", Bracket(8, 1.5)) } } },
        };
        var asm = PartProxy.Discover(obj, 0.3);
        Assert.Equal(2, asm.Parts.Count);
        var o = new PartProxy.BuildOptions { TextureSize = 64 };
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), Keep, o);
        Assert.Equal(2, asm.Parts.Count(p => p.Class == PartClass.Small));
        Assert.Null(r);
        Assert.Contains("no part could be replaced; left unchanged", o.Notes);
    }

    [Fact]
    public void MovingPartWithOnlyADecimatedPartIsReplacedNotKept()
    {
        // a bulk base and a DOF carrying one L bracket, which is small and fails the hull check, so it is decimated
        var bracket = new Node(Kind.Geode, "osg::Geode", "bracket") { Children = { Geometry("bracket", Bracket(0, 1.5, 8)) } };
        var dof = new Node(Kind.Transform, "FLT DOF", "dof") { Matrix = Move(1, 1, 1), Children = { bracket } };
        var baseBox = BoxGeode("base", 8, 4, 2);
        var obj = new Node(Kind.Group, "osg::Group", "obj") { Children = { baseBox, dof } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } } };
        var asm = PartProxy.Discover(obj, 0.3);
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), Keep, new PartProxy.BuildOptions { TextureSize = 64 });
        Assert.NotNull(r);
        var (_, inner) = Assert.Single(r!.Moving);
        var pr = Assert.Single(inner!.Static);
        Assert.Equal(PartMethod.Decimated, pr.Method);
        var msg = RunPerPart(sc, obj);
        Assert.Contains("replaced with a per-part proxy", msg);
        // the DOF was processed, not kept as it is: the original geode is gone, a decimated geometry is drawn under it
        Assert.DoesNotContain(bracket, dof.Children);
        var drawn = dof.Children.SelectMany(c => c.Children).Concat(dof.Children).Where(n => n.Kind == Kind.Geometry).ToList();
        Assert.Contains(drawn, g => g.Name == "bracket decimated");
    }

    [Fact]
    public void CurvedSpoilerIsReducedWithinE()
    {
        var (_, part) = OnePart("spoiler", Spoiler());
        var pr = PartProxy.BuildSmall(part, 0.02, 0.01);
        Assert.Equal(PartMethod.Decimated, pr.Method);
        Assert.True(pr.T.Length < part.T.Length, $"{pr.T.Length / 3} of {part.T.Length / 3} triangles");
        Assert.True(PartProxy.OneWayDistance(pr.V, pr.T, new TriIndex(part.V, part.T), 20000) <= 0.02);
        Assert.True(PartProxy.OneWayDistance(part.V, part.T, new TriIndex(pr.V, pr.T), 20000) <= 0.02);
        Assert.True(pr.DevM <= 0.02);
    }

    [Fact]
    public void NearZeroEKeepsTheOriginalWithANamedMessage()
    {
        var m = Spoiler();
        var geo = Geometry("spoiler", m);
        geo.Geom!.TexCoords[0] = new VArray(m.UV, 2, "Vec2Array");
        var state = new StateSet { MaterialName = "spoiler paint" };
        geo.StateSet = state;
        var obj = new Node(Kind.Group, "osg::Group", "obj") { Children = { new Node(Kind.Geode, "osg::Geode", "spoiler") { Children = { geo } } } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } } };
        var asm = PartProxy.Discover(obj, 0.001);
        var part = Assert.Single(asm.Parts);
        var pr = PartProxy.BuildSmall(part, 1e-7, 0.01);
        Assert.Equal(PartMethod.Kept, pr.Method);
        Assert.Equal(part.T, pr.T);
        var r = new PartProxy.ObjectResult { Asm = asm, Cell = 0.01 };
        r.Static.Add(pr);
        var made = PartProxy.Assemble(new OptContext(sc), obj, r, new Dictionary<PartProxy.ProxyMesh, Node>(ReferenceEqualityComparer.Instance), _ => state, "obj");
        Assert.Same(state, Assert.Single(Assert.Single(made.Holder.Children).Children).StateSet);
        Assert.Contains("1 parts kept original or decimated, +1 draw calls (spoiler)", PartProxy.Detail(r, made));
    }

    [Fact]
    public void FloatingAntennaTipGetsAConvexHull()
    {
        // a 3 cm tip 0.3 m above the cabin roof (z 1.39), 0.4 m from the antenna: tiny, touching nothing
        var (car, _) = CarWith(("tip", Box2(-0.25, -0.015, 1.69, -0.22, 0.015, 1.72)));
        var asm = PartProxy.Discover(car, 2 * CarCell);
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), Keep, new PartProxy.BuildOptions { TextureSize = 128 });
        Assert.NotNull(r);
        Assert.Equal(0, r!.Report.TinyDropped);
        var tip = Built(r, "tip");
        Assert.Equal(PartClass.Tiny, tip.Part.Class);
        Assert.Equal(PartMethod.Convex, tip.Method);
        Assert.Equal(12, tip.T.Length / 3);
        Assert.True(double.IsNaN(tip.DevM));   // an unchecked hull reports no distance
        Assert.Null(Assert.Single(r.Report.PerPart, e => e.Name == tip.Part.Name).DevM);
    }

    [Fact]
    public void FarFromOriginMirrorStaysAMirror()
    {
        var (_, car) = Meshes.SynthCar(true);
        var far = new Node(Kind.Transform, "osg::MatrixTransform", "far") { Matrix = Move(600000, 5300000, 0), Children = { car } };
        var asm = PartProxy.Discover(far, 2 * CarCell);
        // the object diagonal D grows over the moving pieces placed (in their own frames, near the origin, D would span
        // the 5 300 km and every unit would be small): the body stays one part, the wheels stay moving parts
        var body = Assert.Single(asm.Parts, p => p.Units.Any(u => u.Name == "body_1"));
        var names = body.Units.Select(u => u.Name).ToHashSet();
        foreach (var n in new[] { "body_1", "body_2", "body_3", "body_4", "body_5", "body_6", "body_7", "body_8", "cabin" }) Assert.Contains(n, names);
        Assert.Equal(new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" }, asm.Moving.Where(m => m.Node.Cls == "FLT DOF").Select(m => m.Node.Name).OrderBy(x => x));
        Assert.All(asm.Moving.Where(m => m.Node.Cls == "FLT DOF"), m => Assert.Single(m.Inner.Parts));
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), Keep, new PartProxy.BuildOptions { TextureSize = 128 });
        Assert.NotNull(r);
        Assert.Equal(PartMethod.Voxel, Built(r!, "body_1").Method);
        foreach (var name in new[] { "mirror_l", "mirror_r" })
        {
            var pr = Built(r!, name);
            Assert.Equal(PartMethod.Convex, pr.Method);
            var lo0 = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var hi0 = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            var lo1 = (double[])lo0.Clone(); var hi1 = (double[])hi0.Clone();
            foreach (int v in pr.Part.T) for (int c = 0; c < 3; c++) { lo0[c] = Math.Min(lo0[c], pr.Part.V[3 * v + c]); hi0[c] = Math.Max(hi0[c], pr.Part.V[3 * v + c]); }
            foreach (int v in pr.T) for (int c = 0; c < 3; c++) { lo1[c] = Math.Min(lo1[c], pr.V[3 * v + c]); hi1[c] = Math.Max(hi1[c], pr.V[3 * v + c]); }
            Assert.True(lo0[0] > 599990 && lo0[1] > 5299990, "the parts are far from the origin");
            WithinCells((lo0, hi0), (lo1, hi1), 0.01, name);
        }
    }

    // ------------------------------------------------------------------------------------------------ ground rule and hidden triangles

    /// <summary>The split car in keep mode with a ground setting (no plane given: the car's lowest point, the parent frame
    /// being the world).</summary>
    static PartProxy.ObjectResult GroundCar(ProxyGround ground)
    {
        var (_, car) = Meshes.SynthCar(true);
        var asm = PartProxy.Discover(car, 2 * CarCell);
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), Keep with { Ground = ground }, new PartProxy.BuildOptions { TextureSize = 128, Ground = ground });
        Assert.NotNull(r);
        return r!;
    }

    /// <summary>Every proxy triangle (voxel, convex and decimated parts; moving ones too unless <paramref name="moving"/> is
    /// false) in the target's parent frame: its corners and unit normal.</summary>
    static IEnumerable<(double[][] P, double[] N)> ProxyTriangles(PartProxy.ObjectResult r, bool moving = true, double[]? toParent = null)
    {
        foreach (var pr in r.Static.Where(p => p.Method != PartMethod.Kept))
            for (int t = 0; t < pr.T.Length / 3; t++)
            {
                var P = new double[3][];
                for (int k = 0; k < 3; k++)
                {
                    int v = pr.T[3 * t + k];
                    var (x, y, z) = toParent == null ? (pr.V[3 * v], pr.V[3 * v + 1], pr.V[3 * v + 2]) : Mat.Apply(toParent, pr.V[3 * v], pr.V[3 * v + 1], pr.V[3 * v + 2]);
                    P[k] = new[] { x, y, z };
                }
                double ux = P[1][0] - P[0][0], uy = P[1][1] - P[0][1], uz = P[1][2] - P[0][2];
                double vx = P[2][0] - P[0][0], vy = P[2][1] - P[0][1], vz = P[2][2] - P[0][2];
                var n = new[] { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
                double l = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (l > 0) for (int c = 0; c < 3; c++) n[c] /= l;
                yield return (P, n);
            }
        if (!moving) yield break;
        foreach (var (_, inner) in r.Moving)
            if (inner != null)
                foreach (var x in ProxyTriangles(inner, true, inner.Asm.Frame)) yield return x;
    }

    [Fact]
    public void CutLeavesNoDownFacingTriangleAtThePlane()
    {
        var (full, _) = SplitKeep.Value;
        var cut = GroundCar(ProxyGround.Cut);
        var g = PartProxy.GroundOf(cut.Asm, Mat.Identity());
        Assert.Equal(0, g.Point[2], 9);   // the wheels stand on z = 0
        Assert.Equal(new[] { 0.0, 0, 1 }, g.Normal);
        double tol = Math.Max(0, cut.Cell);   // keep mode: e = 0
        bool AtThePlaneFacingDown((double[][] P, double[] N) t) =>
            t.N[0] * g.Normal[0] + t.N[1] * g.Normal[1] + t.N[2] * g.Normal[2] < -0.5 && t.P.All(p => Math.Abs(p[2] - g.Point[2]) <= tol);
        Assert.Contains(ProxyTriangles(full), AtThePlaneFacingDown);   // the bottoms of the wheels, with ground full
        Assert.DoesNotContain(ProxyTriangles(cut), AtThePlaneFacingDown);
        Assert.True(cut.Report.TrisAfter < full.Report.TrisAfter, $"{cut.Report.TrisAfter} with the cut, {full.Report.TrisAfter} without");
        Assert.Equal("cut", cut.Report.Ground);
        Assert.EndsWith("; underside cut", PartProxy.Detail(cut, new PartProxy.Assembled()));
    }

    [Fact]
    public void AboveRemovesUndersideTrianglesOfTheProxy()
    {
        var (full, _) = SplitKeep.Value;
        var above = GroundCar(ProxyGround.Above);
        static long StaticTris(PartProxy.ObjectResult r) => r.Static.Sum(p => (long)p.T.Length / 3);
        Assert.True(StaticTris(above) < StaticTris(full), $"{StaticTris(above)} with above, {StaticTris(full)} with full");
        // the body's floor (z 0.69, facing down) is only seen from below
        static int Down(PartProxy.ObjectResult r) => ProxyTriangles(r, moving: false).Count(t => t.N[2] < -0.5);
        // a down-facing face counts as unseen above the horizon: none is left (measured 0 of 329; static 1 150 -> 811)
        Assert.True(Down(full) > 0);
        Assert.Equal(0, Down(above));
        // moving parts hide nothing and lose nothing: the wheels' (and lights') proxies are those of ground full
        Assert.Equal(full.Moving.Count, above.Moving.Count);
        for (int i = 0; i < full.Moving.Count; i++)
        {
            var (a, b) = (full.Moving[i].Inner!, above.Moving[i].Inner!);
            Assert.Equal(a.Static.Count, b.Static.Count);
            for (int k = 0; k < a.Static.Count; k++)
            {
                Assert.Equal(a.Static[k].T, b.Static[k].T);
                Assert.Equal(a.Static[k].V, b.Static[k].V);
            }
        }
        Assert.Equal("above", above.Report.Ground);
        Assert.EndsWith("; underside not seen from below", PartProxy.Detail(above, new PartProxy.Assembled()));
        Assert.DoesNotContain("underside", PartProxy.Detail(full, new PartProxy.Assembled()));
    }

    [Fact]
    public void AircraftLikeShapeAboveItsPlaneIsNotCut()
    {
        // a fuselage-like ellipsoid, 6 x 2 x 2 m, whose lowest point is 3 m above the ground plane z = 0
        var s = OcclusionTests.Sphere(24, 48);
        var V = new double[s.V.Length];
        for (int i = 0; i < V.Length / 3; i++) (V[3 * i], V[3 * i + 1], V[3 * i + 2]) = (3 * s.V[3 * i], s.V[3 * i + 1], s.V[3 * i + 2] + 4);
        var mesh = (V, s.T, new double[V.Length / 3 * 2]);
        var plane = new GroundPlane(new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 });
        PartProxy.ObjectResult Build(ProxyGround ground)
        {
            var (asm, _) = OnePart("fuselage", mesh);
            var r = PartProxy.BuildParts(asm, SourcesOf(asm), Keep with { Ground = ground }, new PartProxy.BuildOptions { TextureSize = 64, Ground = ground, Plane = plane });
            Assert.NotNull(r);
            return r!;
        }
        var (full, cut) = (Build(ProxyGround.Full), Build(ProxyGround.Cut));
        double tol = Math.Max(0, cut.Cell);
        Assert.Equal(s.T, PartProxy.Cut(V, s.T, plane, tol));   // the original: nothing within tol of the plane
        var f = Assert.Single(full.Static);
        Assert.Equal(f.T, PartProxy.Cut(f.V, f.T, plane, tol));   // nor the proxy
        Assert.Contains(ProxyTriangles(full), t => t.N[2] < -0.5 && t.P.All(p => p[2] < 3 + full.Cell));   // its belly is there
        // the cut build is the full build except for the hidden-triangle run, which with ground cut sees from above the
        // horizon only (a down-facing face counts as unseen): the same triangles as full minus exactly the down-facing ones
        var c = Assert.Single(cut.Static);
        Assert.DoesNotContain(cut.Notes, n => n.Contains("kept uncut"));
        static IEnumerable<string> Keys(PartProxy.PartResult pr, bool upOnly) => Enumerable.Range(0, pr.T.Length / 3)
            .Where(t => !upOnly || !DownFacing(pr.V, pr.T, t))
            .Select(t => string.Join(";", Enumerable.Range(0, 3).Select(k => { int v = pr.T[3 * t + k]; return $"{pr.V[3 * v]:R},{pr.V[3 * v + 1]:R},{pr.V[3 * v + 2]:R}"; })))
            .OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(Keys(f, upOnly: true), Keys(c, upOnly: false));
    }

    /// <summary>Whether triangle <paramref name="t"/> faces down (unit normal z &lt; -0.5).</summary>
    static bool DownFacing(double[] V, int[] T, int t)
    {
        int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
        double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
        double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return l > 0 && nz / l < -0.5;
    }

    [Fact]
    public void GroundPlaneKeepsParentMetresUnderARotatedScaledFrame()
    {
        // a frame below the plane's: rotated 90 degrees about x, scaled 2, 3, 4 along its own axes, moved by (1, 2, 3)
        double[] F = { 2, 0, 0, 1, 0, 0, -4, 2, 0, 3, 0, 3, 0, 0, 0, 1 };
        var g = new GroundPlane(new[] { 0.5, -1, 0.25 }, new[] { 0.0, 0, 1 });
        var gl = PartProxy.Into(g, F, out double scale);
        Assert.Equal(gl.Point, PartProxy.Into(g, F).Point);
        var (px, py, pz) = Mat.Apply(F, gl.Point[0], gl.Point[1], gl.Point[2]);
        Assert.Equal(0.25, pz, 9);   // the point is on the plane
        Assert.Equal(1, Math.Sqrt(gl.Normal.Sum(x => x * x)), 9);
        Assert.Equal(3, scale, 9);   // world z grows by 3 per unit of local y
        foreach (var q in new[] { new[] { 0.0, 0, 0 }, new[] { 1.0, 0.7, -2 }, new[] { -3.0, -0.4, 5 } })
        {
            double parent = Mat.Apply(F, q[0], q[1], q[2]).z - 0.25;
            double local = (q[0] - gl.Point[0]) * gl.Normal[0] + (q[1] - gl.Point[1]) * gl.Normal[1] + (q[2] - gl.Point[2]) * gl.Normal[2];
            Assert.Equal(parent, local * scale, 9);
        }
        // GroundOf through a rotated and scaled parent: the lowest world z, and world height = local distance x |row 2|
        var box = OcclusionTests.Box(-1, -2, -3, 1, 2, 3);
        var (asm, part) = OnePart("box", (box.V, box.T, new double[box.V.Length / 3 * 2]));
        var w = PartProxy.GroundOf(asm, F);
        double lowest = Enumerable.Range(0, part.V.Length / 3).Min(v => Mat.Apply(F, part.V[3 * v], part.V[3 * v + 1], part.V[3 * v + 2]).z);
        Assert.Equal(3 - 3 * 2, lowest, 9);
        Assert.Equal(lowest, Mat.Apply(F, w.Point[0], w.Point[1], w.Point[2]).z, 9);
        Assert.Equal(new[] { 0.0, 1, 0 }, w.Normal);
        double h = (0.5 - w.Point[0]) * w.Normal[0] + (1.0 - w.Point[1]) * w.Normal[1] + (0.0 - w.Point[2]) * w.Normal[2];
        Assert.Equal(Mat.Apply(F, 0.5, 1, 0).z - lowest, h * 3, 9);
    }

    [Fact]
    public void CutThatWouldEmptyKeepsTheTrianglesUncut()
    {
        // a lone down-facing quad on the plane: the cut would remove all of it, so it stays (Proxy.Hull and the convex and
        // decimated parts note "cut would remove the whole hull of <part>; kept uncut")
        double[] V = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        int[] T = { 0, 2, 1, 1, 2, 3 };
        var g = new GroundPlane(new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 1 });
        Assert.Empty(PartProxy.Cut(V, T, g, 0.01));
        Assert.Same(T, PartProxy.CutOrKeep(V, T, g, 0.01, out bool empty));
        Assert.True(empty);
        // with an up-facing triangle beside it only the down-facing pair goes
        int[] T2 = { 0, 2, 1, 1, 2, 3, 0, 1, 2 };
        Assert.Equal(new[] { 0, 1, 2 }, PartProxy.CutOrKeep(V, T2, g, 0.01, out empty));
        Assert.False(empty);
    }

    [Fact]
    public void WholeSaysTheGroundAppliesPerPart()
    {
        var (sc, car) = Meshes.SynthCar(true);
        string msg = RunPerPart(sc, car, new() { ["replace_proxy.parts"] = "whole", ["replace_proxy.ground"] = "cut" });
        Assert.Contains("; ground applies with parts = per_part", msg);
        (sc, car) = Meshes.SynthCar(true);
        Assert.DoesNotContain("ground applies", RunPerPart(sc, car, new() { ["replace_proxy.parts"] = "whole" }));
    }

    // ------------------------------------------------------------------------------------------------ step 4: within

    static ProxyParams WithinE(double e, int voxels) => new() { Parts = ProxyParts.PerPart, Within = true, MaxError = e, Voxels = voxels };

    /// <summary>A sphere of radius 1 m (80 x 40) as a one-part object.</summary>
    static (PartProxy.Assembly asm, PartProxy.Part part) Ball()
    {
        var (V, T) = OptimizeTests.Sphere(80, 40);
        return OnePart("ball", (V, T, new double[V.Length / 3 * 2]));
    }

    static readonly Dictionary<double, PartProxy.ObjectResult> BallRuns = new();

    /// <summary>The sphere within e, built once per e: at 2 cm (the opt-in bench row) 128 voxels, else 64 (the upper
    /// limit; the first hull asks for cells of e / 2 and the retries may refine up to it). It was 512 voxels; once the hull
    /// cells follow the error (ruling F-3) that made the 2 cm row take minutes (ruling F-5).</summary>
    static PartProxy.ObjectResult BallWithin(double e)
    {
        lock (BallRuns)
        {
            if (BallRuns.TryGetValue(e, out var done)) return done;
            var (asm, _) = Ball();
            var r = PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(e, e < 0.03 ? 128 : 64), new PartProxy.BuildOptions { TextureSize = 64 });
            Assert.NotNull(r);
            return BallRuns[e] = r!;
        }
    }

    /// <summary>The split car within 4 cm (voxels 64, gap e), built once, and how often the original's visibility was
    /// computed for it. Voxels 64 since the final fix wave (ruling F-5: 128 took over a minute once the hull cells follow
    /// the error); the hull cells are then coarser than e, so the body is decimated.</summary>
    static readonly Lazy<(PartProxy.ObjectResult r, int visibleCalls)> SplitWithin = new(() =>
    {
        var (_, car) = Meshes.SynthCar(true);
        var asm = PartProxy.Discover(car, 0.04);
        int calls = PartProxy.VisibleOriginalCalls;
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(0.04, 64), new PartProxy.BuildOptions { TextureSize = 64 });
        Assert.NotNull(r);
        return (r!, PartProxy.VisibleOriginalCalls - calls);
    });

    static double[] Placed(double[] V, double[]? m)
    {
        if (m == null) return V;
        var o = new double[V.Length];
        for (int i = 0; i < V.Length / 3; i++) (o[3 * i], o[3 * i + 1], o[3 * i + 2]) = Mat.Apply(m, V[3 * i], V[3 * i + 1], V[3 * i + 2]);
        return o;
    }

    /// <summary>Every piece of the original in the target's parent frame, merged in AllPieces order, with its visibility.</summary>
    static (double[] V, int[] T, bool[] Vis) OriginalOf(PartProxy.Assembly asm, PartProxy.Visibility vis)
    {
        var V = new List<double>(); var T = new List<int>(); var M = new List<bool>();
        int k = 0;
        void Add(PartProxy.Assembly a, double[]? frame)
        {
            foreach (var pc in a.Pieces)
            {
                int b = V.Count / 3;
                V.AddRange(Placed(pc.V, frame)); T.AddRange(pc.T.Select(x => x + b)); M.AddRange(vis.OfPiece[k++]);
            }
            foreach (var m in a.Moving) Add(m.Inner, m.Inner.Frame);
        }
        Add(asm, null);
        Assert.Equal(vis.OfPiece.Length, k);
        return (V.ToArray(), T.ToArray(), M.ToArray());
    }

    /// <summary>Every part's proxy in the target's parent frame, merged, without those <paramref name="skip"/> picks.</summary>
    static (double[] V, int[] T) ProxyOf(PartProxy.ObjectResult r, Func<PartProxy.PartResult, bool> skip)
    {
        var V = new List<double>(); var T = new List<int>();
        void Add(PartProxy.ObjectResult o, double[]? frame)
        {
            foreach (var pr in o.Static.Where(x => !skip(x)))
            {
                int b = V.Count / 3;
                V.AddRange(Placed(pr.V, frame)); T.AddRange(pr.T.Select(x => x + b));
            }
            foreach (var (_, inner) in o.Moving) if (inner != null) Add(inner, inner.Asm.Frame);
        }
        Add(r, null);
        return (V.ToArray(), T.ToArray());
    }

    /// <summary>The 2 cm row of <see cref="SphereProxyReachesTheTarget"/>, opt-in (SCENEKIT_BENCH=1): it takes the longest
    /// of the ball runs (ruling F-5).</summary>
    [BenchFact]
    public void SphereProxyReachesTheTargetAtTwoCentimetres() => SphereProxyReachesTheTarget(0.02);

    [Theory]
    [InlineData(0.04)]
    [InlineData(0.08)]
    public void SphereProxyReachesTheTarget(double e)
    {
        var r = BallWithin(e);
        Assert.Equal(e, r.Report.TargetM);
        Assert.NotNull(r.Report.ReachedM);
        Assert.True(r.Report.ReachedM <= e, $"reached {r.Report.ReachedM} for {e}");
        Assert.True(r.Report.Within);
        var only = Assert.Single(r.Static);
        Assert.Equal(PartMethod.Voxel, only.Method);
        Assert.Equal(only.DevM, r.Report.ReachedM);
    }

    [Fact]
    public void TrianglesFallAsTheTargetGrows()
    {
        // 4 and 8 cm here; the 2 cm run is opt-in (TrianglesFallFromTwoCentimetres, ruling F-5)
        long t4 = BallWithin(0.04).Report.TrisAfter, t8 = BallWithin(0.08).Report.TrisAfter;
        Assert.True(t4 > t8, $"{t4} / {t8} triangles");
    }

    [BenchFact]
    public void TrianglesFallFromTwoCentimetres()
    {
        long t2 = BallWithin(0.02).Report.TrisAfter, t4 = BallWithin(0.04).Report.TrisAfter;
        Assert.True(t2 > t4, $"{t2} / {t4} triangles");
    }

    [Fact]
    public void PixelsAtADistanceConvertAsInGenerateLod()
    {
        var (asm, _) = Ball();
        var p = WithinE(0.04, 64) with { MaxErrorPx = 1, AtDistance = 100 };
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), p, new PartProxy.BuildOptions { TextureSize = 64 });
        Assert.NotNull(r);
        Assert.Equal(0.0767, r!.Report.TargetM!.Value, 1e-4);
        Assert.True(r.Report.Within);
    }

    [Fact]
    public void UnreachableTargetKeepsPartsAndCountsThem()
    {
        // 5 cm, below one cell of the split car at 64 voxels (4.52 m / 64 = 7.1 cm): the voxel hulls cannot stay within it
        var (sc, car) = Meshes.SynthCar(true);
        int hulls = PartProxy.WithinHulls, decimations = PartProxy.WithinDecimations;
        string msg = RunPerPart(sc, car, new() { ["replace_proxy.target"] = "within", ["replace_proxy.max_error"] = 0.05, ["replace_proxy.voxels"] = 64 });
        hulls = PartProxy.WithinHulls - hulls; decimations = PartProxy.WithinDecimations - decimations;
        Assert.Contains("1 object(s) replaced with a per-part proxy", msg);
        Assert.Contains("parts kept original or decimated, +", msg);
        var asm = PartProxy.Discover(Meshes.SynthCar(true).Item2, 0.05);
        var (ext, _) = PartProxy.ObjectExtent(asm);
        Assert.Contains($"hull cells {ErrorTarget.Distance(ext / 64)}, coarser than the 5 cm asked", msg);
        Assert.Contains("within 5 cm", msg);
        Assert.Contains(", max over parts)", msg);
        // one hull or one decimation per attempt: at most one hull per part when the cell is the limit (a convex hull is
        // made once), at most MaxDecimateAttempts decimations per part
        static int Parts(PartProxy.Assembly a) => a.Parts.Count + a.Moving.Sum(m => Parts(m.Inner));
        int parts = Parts(asm);
        Assert.InRange(decimations, 1, PartProxy.Limits.MaxDecimateAttempts * parts);
        Assert.InRange(hulls, 1, PartProxy.Limits.MaxDecimateAttempts * parts);
    }

    [Fact]
    public void CoarseCellMakesOneHullAttempt()
    {
        // a 2 m sphere at 64 voxels: cells of 3.1 cm, coarser than the 1 mm asked; the cell is the limit, so one hull,
        // then the decimated original
        var (asm, _) = Ball();
        int hulls = PartProxy.WithinHulls;
        var o = new PartProxy.BuildOptions { TextureSize = 64 };
        PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(0.001, 64), o);
        Assert.Equal(1, PartProxy.WithinHulls - hulls);
    }

    [Fact]
    public void WithinWithoutAPositiveErrorSaysSo()
    {
        foreach (double e in new[] { 0.0, -1.0, double.NaN })
        {
            var (asm, _) = Ball();
            var o = new PartProxy.BuildOptions { TextureSize = 64 };
            var r = PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(e, 32), o);
            Assert.NotNull(r);
            Assert.Contains("within needs max_error > 0", o.Notes);
            Assert.Equal(0, r!.Report.TargetM);
            Assert.False(r.Report.Within);
            // the same error as the build option: the ground cut reaches max(0, cell) as in keep mode (not NaN, which cuts nothing)
            Assert.Equal(CutBall(0).Report.TrisAfter, CutBall(e).Report.TrisAfter);
        }
        static PartProxy.ObjectResult CutBall(double e) { var (a, _) = Ball(); return PartProxy.BuildParts(a, SourcesOf(a), WithinE(e, 32), new PartProxy.BuildOptions { TextureSize = 64, ErrorMetres = e, Ground = ProxyGround.Cut })!; }
    }

    [Fact]
    public void ReachedFragmentSaysWhenTheCutWentFarther()
    {
        Assert.Equal("within 4 cm (reached 3 cm, max over parts)", PartProxy.WithinReachedFragment(0.04, 0.03));
        Assert.Equal("within 4 cm asked, reached 5 cm after the cut (max over parts)", PartProxy.WithinReachedFragment(0.04, 0.05));
    }

    [Fact]
    public void EveryPartFailingLeavesTheObjectUnchanged()
    {
        // a sphere without degenerate pole triangles (those collapse with no error at all, which is within any e)
        var (V, T) = OcclusionTests.Sphere(40, 80);
        var (asm, _) = OnePart("ball", (V, T, new double[V.Length / 3 * 2]));
        var o = new PartProxy.BuildOptions { TextureSize = 64 };
        Assert.Null(PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(1e-6, 32), o));
        Assert.Contains(o.Notes, n => n.StartsWith("Within 0.001 mm: could not stay within it (best ") && n.EndsWith("); left unchanged"));
        // the pass leaves the scene as it was
        var geo = Geometry("ball", (V, T, Array.Empty<double>()));
        var geode = new Node(Kind.Geode, "osg::Geode", "ball") { Children = { geo } };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { geode } } };
        var g0 = geo.Geom;
        string msg = RunPerPart(sc, geode, new() { ["replace_proxy.target"] = "within", ["replace_proxy.max_error"] = 1e-6 });
        Assert.Contains("0 object(s) replaced", msg);
        Assert.Contains("could not stay within it (best ", msg);
        Assert.Same(geode, Assert.Single(sc.Root!.Children));
        Assert.Same(geo, Assert.Single(geode.Children));
        Assert.Same(g0, geo.Geom);
        Assert.Equal(T, geo.Geom!.Triangles());
    }

    [Fact]
    public void RemovedInteriorIsNotError()
    {
        // the engine sits inside the closed body: the hull leaves it out, and that is no error
        var (_, car, _, _) = RemoveOccludedTests.Car();
        var asm = PartProxy.Discover(car, 0.05);
        Assert.Equal(2, asm.Pieces.Count);
        var vis = PartProxy.VisibleOriginal(asm, ProxyGround.Full);
        int engine = asm.Pieces.FindIndex(p => p.Geom.Name == "engine"), body = asm.Pieces.FindIndex(p => p.Geom.Name == "body");
        Assert.DoesNotContain(true, vis.OfPiece[engine]);   // the engine is never seen
        Assert.DoesNotContain(false, vis.OfPiece[body]);
        var (OV, OT, M) = OriginalOf(asm, vis);
        var h = Proxy.Hull(SourcesOf(asm), new Proxy.Options { Resolution = 64, TargetTris = 500 });
        Assert.NotNull(h);
        double dev = PartProxy.TwoWayVisible(h!.V, h.T, OV, OT, M);
        Assert.True(dev < h.Cell, $"{dev} for a cell of {h.Cell}");
        // counted as seen, the engine would be far from the hull
        Assert.True(PartProxy.TwoWayVisible(h.V, h.T, OV, OT, M.Select(_ => true).ToArray()) > 0.3);
    }

    [Fact]
    public void DeletedMirrorIsError()
    {
        var (r, _) = SplitWithin.Value;
        var vis = PartProxy.VisibleOriginal(r.Asm, ProxyGround.Full);
        var (OV, OT, M) = OriginalOf(r.Asm, vis);
        static bool Mirror(PartProxy.PartResult pr) => pr.Part.Units.Any(u => u.Name.StartsWith("mirror"));
        Assert.Equal(2, r.Static.Count(Mirror));
        var (PV, PT) = ProxyOf(r, _ => false);
        var (NV, NT) = ProxyOf(r, Mirror);
        double with = PartProxy.TwoWayVisible(PV, PT, OV, OT, M), without = PartProxy.TwoWayVisible(NV, NT, OV, OT, M);
        Assert.True(without >= 0.05, $"without the mirrors {without}");
        Assert.True(with < without, $"with the mirrors {with}, without {without}");
    }

    [Fact]
    public void VisibilityIsComputedOncePerObject()
    {
        var (r, calls) = SplitWithin.Value;
        Assert.Equal(1, calls);
        Assert.True(r.Report.Within, $"reached {r.Report.ReachedM}");
        Assert.Equal(0.04, r.Report.TargetM);
        Assert.All(AllParts(r), p => Assert.True(p.DevM <= 0.04, $"{p.Part.Name}: {p.DevM}"));
        Assert.Equal(AllParts(r).Max(p => p.DevM), r.Report.ReachedM);
    }

    [Fact]
    public void HullCellsCoarserThanAskedAreNamed()
    {
        var (_, car) = Meshes.SynthCar(true);
        var asm = PartProxy.Discover(car, 0.01);
        var o = new PartProxy.BuildOptions { TextureSize = 64 };
        // voxels 64 since the final fix wave (ruling F-5: a full split-car within run; 128 took about twice as long)
        var r = PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(0.01, 64), o);
        var (ext, _) = PartProxy.ObjectExtent(asm);   // 4.52 m (the mirrors stick out): cells of 7.06 cm at 64
        // some parts are replaced, so the object has a result and the note is on it
        Assert.NotNull(r);
        Assert.Contains($"hull cells {ErrorTarget.Distance(ext / 64)}, coarser than the 1 cm asked", r!.Notes);
    }

    // ------------------------------------------------------------------------------------------------ step 6: normals

    /// <summary>The angle (degrees) between the vector at a[i] and (x, y, z).</summary>
    static double Deg(double[] a, int i, double x, double y, double z)
    {
        double la = Math.Sqrt(a[i] * a[i] + a[i + 1] * a[i + 1] + a[i + 2] * a[i + 2]), lb = Math.Sqrt(x * x + y * y + z * z);
        return Math.Acos(Math.Clamp((a[i] * x + a[i + 1] * y + a[i + 2] * z) / (la * lb), -1, 1)) * 180 / Math.PI;
    }

    /// <summary>A closed cylinder of radius 1 around z (z -1 .. 1) with <paramref name="n"/> sides: welded (the rim vertices
    /// shared by the side and the caps), or faceted (every side quad with vertices of its own).</summary>
    static (double[] V, int[] T) Cylinder(int n, bool faceted)
    {
        var V = new List<double>(); var T = new List<int>();
        int Add(double x, double y, double z) { V.AddRange(new[] { x, y, z }); return V.Count / 3 - 1; }
        double X(int i) => Math.Cos(2 * Math.PI * i / n);
        double Y(int i) => Math.Sin(2 * Math.PI * i / n);
        int[] lo = new int[n], hi = new int[n];
        for (int i = 0; i < n; i++) { lo[i] = Add(X(i), Y(i), -1); hi[i] = Add(X(i), Y(i), 1); }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (faceted)
            {
                int a = Add(X(i), Y(i), -1), b = Add(X(j), Y(j), -1), c = Add(X(j), Y(j), 1), d = Add(X(i), Y(i), 1);
                T.AddRange(new[] { a, b, c, a, c, d });
            }
            else T.AddRange(new[] { lo[i], lo[j], hi[j], lo[i], hi[j], hi[i] });
        }
        int cb = Add(0, 0, -1), ct = Add(0, 0, 1);
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; T.AddRange(new[] { cb, lo[j], lo[i], ct, hi[i], hi[j] }); }
        return (V.ToArray(), T.ToArray());
    }

    /// <summary>The z of a triangle's unit normal.</summary>
    static double TriNormalZ(double[] V, int[] T, int t)
    {
        int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
        double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
        double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
        double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
        return z / Math.Sqrt(x * x + y * y + z * z);
    }

    [Fact]
    public void TwelveSidedCylinderGetsTheAnalyticNormals()
    {
        // the original: a smooth (welded) 64-segment cylinder without normals; the proxy: a faceted 12-sided prism
        var (OV, OT) = Cylinder(64, false);
        var (V, T) = Cylinder(12, true);
        var n = Proxy.TransferNormals(V, T, OV, OT, null, 0.05, 45);
        var hull = Proxy.CornerNormals(V, T, 0.7);
        Assert.Equal(3 * T.Length, n.Length);
        int sides = 0;
        for (int t = 0; t < T.Length / 3; t++)
            for (int s = 0; s < 3; s++)
            {
                int v = T[3 * t + s], c = 9 * t + 3 * s;
                double fz = TriNormalZ(V, T, t);
                if (Math.Abs(fz) > 0.5)
                {
                    // a cap stays flat, as in the original (the crease keeps the side out)
                    Assert.True(Deg(n, c, 0, 0, Math.Sign(fz)) < 1, $"cap corner {t}/{s}: {Deg(n, c, 0, 0, Math.Sign(fz)):F1} degrees off");
                    continue;
                }
                sides++;
                double x = V[3 * v], y = V[3 * v + 1];
                Assert.True(Deg(n, c, x, y, 0) < 5, $"side corner {t}/{s}: {Deg(n, c, x, y, 0):F1} degrees off radial");
                Assert.True(Deg(hull, c, x, y, 0) > 10, $"hull corner {t}/{s}: {Deg(hull, c, x, y, 0):F1} degrees off radial");
            }
        Assert.Equal(12 * 6, sides);
    }

    [Fact]
    public void CubeKeepsItsHardEdges()
    {
        // the original: a welded cube (8 vertices, no normals); the proxy: the same cube subdivided
        double[] OV = { -1, -1, -1, 1, -1, -1, 1, 1, -1, -1, 1, -1, -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1 };
        int[] OT = { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
        var (V, T, _) = LodToolsTests.Box(-1, -1, -1, 1, 1, 1, 3);
        var n = Proxy.TransferNormals(V, T, OV, OT, null, 0.3, 45);
        for (int t = 0; t < T.Length / 3; t++)
        {
            int a = T[3 * t], b = T[3 * t + 1], c = T[3 * t + 2];
            double ux = V[3 * b] - V[3 * a], uy = V[3 * b + 1] - V[3 * a + 1], uz = V[3 * b + 2] - V[3 * a + 2];
            double vx = V[3 * c] - V[3 * a], vy = V[3 * c + 1] - V[3 * a + 1], vz = V[3 * c + 2] - V[3 * a + 2];
            double fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx;
            for (int s = 0; s < 3; s++)
                Assert.True(Deg(n, 9 * t + 3 * s, fx, fy, fz) < 1, $"corner {t}/{s}: {Deg(n, 9 * t + 3 * s, fx, fy, fz):F1} degrees off its face");
        }
    }

    [Fact]
    public void AuthoredNormalsWin()
    {
        // the original: a flat 2 x 2 m quad whose authored normals lean 20 degrees towards +x; the proxy: the quad subdivided
        double[] OV = { -1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0 };
        int[] OT = { 0, 1, 2, 0, 2, 3 };
        double sx = Math.Sin(20 * Math.PI / 180), cz = Math.Cos(20 * Math.PI / 180);
        double[] ON = { sx, 0, cz, sx, 0, cz, sx, 0, cz, sx, 0, cz };
        var Vl = new List<double>(); var Tl = new List<int>();
        for (int j = 0; j <= 4; j++) for (int i = 0; i <= 4; i++) Vl.AddRange(new[] { -1 + i * 0.5, -1 + j * 0.5, 0.0 });
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++) { int a = j * 5 + i; Tl.AddRange(new[] { a, a + 1, a + 6, a, a + 6, a + 5 }); }
        double[] V = Vl.ToArray(); int[] T = Tl.ToArray();
        var n = Proxy.TransferNormals(V, T, OV, OT, ON, 0.1, 45);
        for (int c = 0; c < n.Length / 3; c++) Assert.True(Deg(n, 3 * c, sx, 0, cz) < 2, $"corner {c}: {Deg(n, 3 * c, sx, 0, cz):F1} degrees off");
        // without them the flat quad shades flat
        var flat = Proxy.TransferNormals(V, T, OV, OT, null, 0.1, 45);
        for (int c = 0; c < flat.Length / 3; c++) Assert.True(Deg(flat, 3 * c, 0, 0, 1) < 1e-3, $"corner {c}");
    }

    [Fact]
    public void OriginalNormalsReachTheAtlasMeshes()
    {
        // the sphere in keep mode: with normals = original the atlas mesh shades closer to the sphere than with the hull's
        double Mean(PartProxy.BuildOptions o)
        {
            var (asm, _) = Ball();
            var src = SourcesOf(asm);
            var r = PartProxy.BuildParts(asm, src, new ProxyParams { Parts = ProxyParts.PerPart, Voxels = 16, Ratio = 0.02 }, o)!;
            PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, o);
            var m = Assert.Single(r.Meshes);
            return Enumerable.Range(0, m.V.Length / 3).Average(v => Deg(m.N, 3 * v, m.V[3 * v], m.V[3 * v + 1], m.V[3 * v + 2]));
        }
        double hull = Mean(new PartProxy.BuildOptions { TextureSize = 64 });
        double original = Mean(new PartProxy.BuildOptions { TextureSize = 64, Normals = ProxyNormals.Original });
        Assert.True(original < hull * 0.5, $"original {original:F2} degrees, hull {hull:F2} degrees off radial on average");
    }

    [Fact]
    public void ProxyBuildTakesNormalsFromTheOriginalWhenAsked()
    {
        // Proxy.Build (parts = whole): hull by default, the original's with Normals = original
        var (V, T) = OptimizeTests.Sphere(80, 40);
        double Mean(ProxyNormals normals)
        {
            var r = Proxy.Build(new[] { new Proxy.Source { V = V, T = T } }, new Proxy.Options { TargetTris = 128, Resolution = 16, Bake = false, Normals = normals })!;
            return Enumerable.Range(0, r.V.Length / 3).Average(v => Deg(r.N, 3 * v, r.V[3 * v], r.V[3 * v + 1], r.V[3 * v + 2]));
        }
        double hull = Mean(ProxyNormals.Hull), original = Mean(ProxyNormals.Original);
        Assert.True(original < hull * 0.5, $"original {original:F2} degrees, hull {hull:F2} degrees off radial on average");
    }

    [Fact]
    public void WholeModePassTakesNormalsFromTheOriginalWhenAsked()
    {
        // replace_proxy with parts = whole: the shading follows the sphere with normals = original, the hull's facets by default
        double Mean(string normals)
        {
            var (V, T) = OptimizeTests.Sphere(80, 40);
            var obj = new Node(Kind.Geode, "osg::Geode", "ball") { Children = { Geometry("ball", (V, T, new double[V.Length / 3 * 2])) } };
            var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } } };
            var prms = new Dictionary<string, object>
            {
                ["replace_proxy.parts"] = "whole", ["replace_proxy.texture"] = 64, ["replace_proxy.voxels"] = 16, ["replace_proxy.ratio"] = 0.02,
                ["replace_proxy.share"] = false, ["replace_proxy.normals"] = normals,
            };
            var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, prms, targets: new List<Node> { obj });
            Assert.Null(results[0].Error);
            GeomData? g = null;
            void Find(Node n) { if (n.Kind == Kind.Geometry && n.Name.EndsWith(" proxy")) g = n.Geom; foreach (var c in n.Children) Find(c); }
            Find(sc.Root!);
            Assert.NotNull(g);
            var v = g!.Vertices!.Data;
            return Enumerable.Range(0, v.Length / 3).Average(i => Deg(g.Normals!.Data, 3 * i, v[3 * i], v[3 * i + 1], v[3 * i + 2]));
        }
        double hull = Mean("hull"), original = Mean("original");
        Assert.True(original < hull * 0.5, $"original {original:F2} degrees, hull {hull:F2} degrees off radial on average");
    }

    [Fact]
    public void DecimatedPartsGetTransferredCornerNormals()
    {
        // an L bracket whose right-angle edges the original shades smoothly (crease 120): the hull's normals are the flat
        // axis-aligned ones, the transferred ones are rounded at the edges, and KeptGeometries draws exactly those
        var (asm, part) = OnePart("bracket", LBracket());
        var pr = PartProxy.BuildSmall(part, 0.02, 0.01);
        Assert.Equal(PartMethod.Decimated, pr.Method);
        var r = new PartProxy.ObjectResult { Asm = asm, Cell = 0.01 };
        r.Static.Add(pr);
        var src = SourcesOf(asm);
        PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, new PartProxy.BuildOptions { TextureSize = 64 });
        Assert.Null(pr.Corner);   // hull: rebuilt from the triangles when drawn
        var hullG = Assert.Single(PartProxy.KeptGeometries(asm, pr, _ => null, null)).Geo.Geom!;
        PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, new PartProxy.BuildOptions { TextureSize = 64, Normals = ProxyNormals.Original, Crease = 120 });
        Assert.Equal(3 * pr.T.Length, pr.Corner!.Length);
        var g = Assert.Single(PartProxy.KeptGeometries(asm, pr, _ => null, null)).Geo.Geom!;
        // the drawn vertex normals are Corner averaged per vertex (the geometry's triangles keep the order of pr.T)
        var T = g.Triangles();
        Assert.Equal(pr.T.Length, T.Length);
        var want = new double[g.Normals!.Data.Length];
        for (int c = 0; c < T.Length; c++) for (int k = 0; k < 3; k++) want[3 * T[c] + k] += pr.Corner[3 * c + k];
        Proxy.Normalize(want);
        for (int i = 0; i < want.Length / 3; i++)
            Assert.True(Deg(g.Normals.Data, 3 * i, want[3 * i], want[3 * i + 1], want[3 * i + 2]) < 1, $"vertex {i}");
        // and they are not the hull's: some vertex normal differs by more than 5 degrees
        Assert.Equal(hullG.Normals!.Count, g.Normals.Count);
        Assert.True(Enumerable.Range(0, g.Normals.Count).Max(i => Deg(g.Normals.Data, 3 * i, hullG.Normals.Data[3 * i], hullG.Normals.Data[3 * i + 1], hullG.Normals.Data[3 * i + 2])) > 5);
    }

    // ------------------------------------------------------------------------------------------------ step 7: the atlas part filter

    static RawImage Solid(int r, int g, int b)
    {
        var im = new RawImage(2, 2, 3);
        for (int i = 0; i < 4; i++) (im.Pixels[3 * i], im.Pixels[3 * i + 1], im.Pixels[3 * i + 2]) = ((byte)r, (byte)g, (byte)b);
        return im;
    }

    /// <summary>One box of <see cref="BoxParts"/>: its corners, its colour and its class ID (null: no class map).</summary>
    sealed record BoxPart(string Name, double[] Lo, double[] Hi, RawImage Colour, int? Class = null);

    /// <summary>An object of boxes (one geode each, so one part each) and its proxy built by hand: per part its box grown
    /// by <paramref name="grow"/> on every side (as a voxel hull one cell coarse is), convex, on the object's cell
    /// <paramref name="cell"/>. The sources carry the colour as the unit-0 image and the class ID as a class-map companion.</summary>
    static (PartProxy.ObjectResult r, List<Proxy.Source> src) BoxParts(double cell, double grow, params BoxPart[] boxes)
    {
        var target = new Node(Kind.Group, "osg::Group", "object");
        foreach (var b in boxes)
        {
            var m = LodToolsTests.Box(b.Lo[0], b.Lo[1], b.Lo[2], b.Hi[0], b.Hi[1], b.Hi[2], 2);
            var geo = Geometry(b.Name, m);
            geo.Geom!.TexCoords[0] = new VArray(m.UV, 2, "Vec2Array");
            target.Children.Add(new Node(Kind.Geode, "osg::Geode", b.Name) { Children = { geo } });
        }
        var asm = PartProxy.Discover(target, 0.001);
        Assert.Equal(boxes.Length, asm.Parts.Count);
        var src = SourcesOf(asm);
        var pieces = asm.AllPieces().ToList();
        for (int i = 0; i < src.Count; i++)
        {
            var b = boxes.Single(x => x.Name == pieces[i].Geom.Name);
            src[i].Image = b.Colour;
            if (b.Class is int id) src[i].Extra[new LayerKey(0, IrTests.Cls)] = (src[i].UV!, Solid(id, 0, 0), true);
        }
        var r = new PartProxy.ObjectResult { Asm = asm, Cell = cell };
        foreach (var b in boxes)
        {
            var hull = LodToolsTests.Box(b.Lo[0] - grow, b.Lo[1] - grow, b.Lo[2] - grow, b.Hi[0] + grow, b.Hi[1] + grow, b.Hi[2] + grow, 1);
            r.Static.Add(new PartProxy.PartResult { Part = Named(asm, b.Name), Method = PartMethod.Convex, V = hull.V, T = hull.T, Cell = cell });
        }
        return (r, src);
    }

    /// <summary>A red door panel (1 x 0.02 x 0.8 m, front face at y = 0) and a blue mirror (10 cm cube) 1 cm in front of it,
    /// with class IDs <paramref name="door"/> and <paramref name="mirror"/>; each proxy is its box grown by a 2 cm cell, so
    /// the mirror's proxy reaches 1 cm into the door, where the door's back face is closer than the mirror's.</summary>
    static (PartProxy.ObjectResult r, List<Proxy.Source> src) DoorAndMirror(int? door = null, int? mirror = null) => BoxParts(0.02, 0.02,
        new BoxPart("door", new[] { -0.5, -0.02, 0.0 }, new[] { 0.5, 0.0, 0.8 }, Solid(255, 0, 0), door),
        new BoxPart("mirror", new[] { -0.05, 0.01, 0.35 }, new[] { 0.05, 0.11, 0.45 }, Solid(0, 0, 255), mirror));

    /// <summary>The texels (row-major, row 0 at v = 1 as the bake writes them) whose centre lies strictly inside a triangle of
    /// the mesh in UV, per triangle; <paramref name="tri"/> picks the triangles.</summary>
    static IEnumerable<(int tri, int texel)> Raster(PartProxy.ProxyMesh mesh, int S, Func<int, bool>? tri = null)
    {
        for (int t = 0; t < mesh.T.Length / 3; t++)
        {
            if (tri != null && !tri(t)) continue;
            double[] u = new double[3], v = new double[3];
            for (int k = 0; k < 3; k++) { u[k] = mesh.UV[2 * mesh.T[3 * t + k]] * S; v[k] = (1 - mesh.UV[2 * mesh.T[3 * t + k] + 1]) * S; }
            double den = (v[1] - v[2]) * (u[0] - u[2]) + (u[2] - u[1]) * (v[0] - v[2]);
            if (Math.Abs(den) < 1e-12) continue;
            int x0 = Math.Max(0, (int)Math.Floor(u.Min())), x1 = Math.Min(S - 1, (int)Math.Floor(u.Max()));
            int y0 = Math.Max(0, (int)Math.Floor(v.Min())), y1 = Math.Min(S - 1, (int)Math.Floor(v.Max()));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double l0 = ((v[1] - v[2]) * (px - u[2]) + (u[2] - u[1]) * (py - v[2])) / den;
                    double l1 = ((v[2] - v[0]) * (px - u[2]) + (u[0] - u[2]) * (py - v[2])) / den;
                    if (l0 > 1e-9 && l1 > 1e-9 && 1 - l0 - l1 > 1e-9) yield return (t, y * S + x);
                }
        }
    }

    /// <summary>The texels of one part (number in the owner's static parts) in the atlas.</summary>
    static HashSet<int> PartTexels(PartProxy.ProxyMesh mesh, int part, int S) => Raster(mesh, S, t => mesh.PartOfTri[t] == part).Select(x => x.texel).ToHashSet();

    [Fact]
    public void MirrorTexelsTakeTheMirrorsColourNotTheDoors()
    {
        var (r, src) = DoorAndMirror();
        var (tex, _) = PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, new PartProxy.BuildOptions { TextureSize = 128 });
        var mesh = Assert.Single(r.Meshes);
        int mirror = r.Static.FindIndex(p => p.Part.Name == "mirror"), door = 1 - mirror;
        var texels = PartTexels(mesh, mirror, 128);
        Assert.True(texels.Count > 200, $"{texels.Count} mirror texels");
        bool Blue(int i) => tex!.Pixels[4 * i + 2] > 200 && tex.Pixels[4 * i] < 50;
        double blue = texels.Count(Blue) / (double)texels.Count;
        Assert.True(blue > 0.95, $"{blue:P1} of the mirror's texels are blue");
        // and the door stays red
        var doorTexels = PartTexels(mesh, door, 128);
        Assert.True(doorTexels.Count(i => tex!.Pixels[4 * i] > 200 && tex.Pixels[4 * i + 2] < 50) > 0.95 * doorTexels.Count);
    }

    [Fact]
    public void SixPartsOneTextureNoOverlap()
    {
        var boxes = Enumerable.Range(0, 6).Select(i => new BoxPart($"box{i}", new[] { 0.5 * i, 0.0, 0.0 }, new[] { 0.5 * i + 0.1 + 0.05 * i, 0.3, 0.2 },
            Solid(40 * i, 255 - 40 * i, 128))).ToArray();
        var (r, src) = BoxParts(0.02, 0.01, boxes);
        var (tex, extra) = PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, new PartProxy.BuildOptions { TextureSize = 128 });
        Assert.NotNull(tex);
        Assert.Empty(extra);
        var mesh = Assert.Single(r.Meshes);
        Assert.Equal(Enumerable.Range(0, 6), mesh.PartOfTri.Distinct().OrderBy(k => k));
        Assert.All(mesh.UV, u => Assert.InRange(u, 0.0, 1.0));
        var owner = new Dictionary<int, int>();
        foreach (var (t, texel) in Raster(mesh, 128))
            Assert.True(owner.TryAdd(texel, t), $"texel {texel} under triangles {(owner.TryGetValue(texel, out var o) ? o : -1)} and {t}");
        Assert.True(owner.Count > 1000);
    }

    [Fact]
    public void ClassMapNeverBlendsAcrossParts()
    {
        var (r, src) = DoorAndMirror(door: 1, mirror: 4);
        var (_, extra) = PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, new PartProxy.BuildOptions { TextureSize = 128 });
        var cls = extra[new LayerKey(0, IrTests.Cls)];
        for (int i = 0; i < 128 * 128; i++)
        {
            Assert.Contains(cls.Pixels[4 * i], new byte[] { 0, 1, 4 });   // the sources' IDs or class_fill 0
            Assert.Equal(0, cls.Pixels[4 * i + 1]);
            Assert.Equal(0, cls.Pixels[4 * i + 2]);
        }
        var mesh = Assert.Single(r.Meshes);
        int mirror = r.Static.FindIndex(p => p.Part.Name == "mirror");
        Assert.All(PartTexels(mesh, mirror, 128), i => Assert.Equal(4, cls.Pixels[4 * i]));
        Assert.All(PartTexels(mesh, 1 - mirror, 128), i => Assert.Equal(1, cls.Pixels[4 * i]));
    }

    [Fact]
    public void PartWithoutClassMapGetsClassFill()
    {
        var (r, src) = DoorAndMirror(door: 1, mirror: null);
        var (_, extra) = PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src },
            new PartProxy.BuildOptions { TextureSize = 128, ClassFill = ExtraFill.ClassFill(7) });
        var cls = extra[new LayerKey(0, IrTests.Cls)];
        var mesh = Assert.Single(r.Meshes);
        int mirror = r.Static.FindIndex(p => p.Part.Name == "mirror");
        var texels = PartTexels(mesh, mirror, 128);
        Assert.NotEmpty(texels);
        Assert.All(texels, i => Assert.Equal(7, cls.Pixels[4 * i]));
        Assert.All(PartTexels(mesh, 1 - mirror, 128), i => Assert.Equal(1, cls.Pixels[4 * i]));
    }

    [Fact]
    public void WheelRegionsCarryClassTwo()
    {
        var (r, plain) = SplitKeep.Value;
        // the same sources with the car's class-map companion (body 1, wheels 2, glass 3, mirrors 4)
        var pieces = r.Asm.AllPieces().ToList();
        var src = plain.Select((s, i) =>
        {
            var t0 = pieces[i].State?.Textures.FirstOrDefault(t => t.Unit == 0);
            var c = s.With(s.V, s.T);
            if (s.UV != null && t0?.Companions?.GetValueOrDefault(IrTests.Cls)?.Baked is { } img) c.Extra = new() { [new LayerKey(0, IrTests.Cls)] = (s.UV, img, true) };
            return c;
        }).ToList();
        Assert.All(src, s => Assert.Single(s.Extra));
        var (_, extra) = PartProxy.BakeAtlas(new[] { r }, new[] { (IReadOnlyList<Proxy.Source>)src }, new PartProxy.BuildOptions { TextureSize = 128 });
        var cls = extra[new LayerKey(0, IrTests.Cls)];
        var wheels = r.Meshes.Where(m => m.Owner?.Node.Cls == "FLT DOF").ToList();
        Assert.Equal(4, wheels.Count);
        foreach (var w in wheels)
        {
            var texels = Raster(w, 128).Select(x => x.texel).ToHashSet();
            Assert.True(texels.Count > 20, $"{w.Name}: {texels.Count} texels");
            Assert.All(texels, i => Assert.Equal(2, cls.Pixels[4 * i]));
        }
    }

    [Fact]
    public void UnfilteredLookupIsTodays()
    {
        // two overlapping subdivided boxes; random points and normals (some without a normal)
        var a = LodToolsTests.Box(0, 0, 0, 1, 0.6, 0.4, 4);
        var b = LodToolsTests.Box(0.5, 0.3, 0.2, 1.4, 0.9, 0.7, 3);
        var V = a.V.Concat(b.V).ToArray();
        var T = a.T.Concat(b.T.Select(i => i + a.V.Length / 3)).ToArray();
        var index = new TriIndex(V, T);
        int m = T.Length / 3;
        var allOne = new int[m];
        var rnd = new Random(11);
        for (int q = 0; q < 2000; q++)
        {
            double px = rnd.NextDouble() * 1.8 - 0.2, py = rnd.NextDouble() * 1.3 - 0.2, pz = rnd.NextDouble() * 1.1 - 0.2;
            double nx = 0, ny = 0, nz = 0;
            if (q % 3 != 0) { nx = rnd.NextDouble() * 2 - 1; ny = rnd.NextDouble() * 2 - 1; nz = rnd.NextDouble() * 2 - 1; }
            double maxDist = 0.05 + rnd.NextDouble() * 0.4;
            var today = index.Closest(px, py, pz, maxDist, nx, ny, nz, 0.35);
            Assert.Equal(today, index.Closest(px, py, pz, maxDist, nx, ny, nz, 0.35, null, -1));
            Assert.Equal(today, index.Closest(px, py, pz, maxDist, nx, ny, nz, 0.35, allOne, 0));   // a filter every triangle passes
            // brute force: the nearest facing triangle within maxDist, else the nearest one
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            double? best = null, any = null;
            for (int t = 0; t < m; t++)
            {
                double d2 = Geo.ClosestOnTri(px, py, pz, V, T[3 * t], T[3 * t + 1], T[3 * t + 2]).d2;
                if (d2 > maxDist * maxDist) continue;
                if (any == null || d2 < any) any = d2;
                if (nl > 0 && index.Normals[3 * t] * nx + index.Normals[3 * t + 1] * ny + index.Normals[3 * t + 2] * nz < 0.35) continue;
                if (best == null || d2 < best) best = d2;
            }
            var want = best ?? any;
            Assert.Equal(want.HasValue, today.HasValue);
            if (want != null) Assert.Equal(want.Value, today!.Value.D2, 12);
        }
        // the filter itself: with box b's triangles as another part, a point on b finds a triangle of a (or nothing)
        var partOf = Enumerable.Range(0, m).Select(t => t < a.T.Length / 3 ? 0 : 1).ToArray();
        var onB = index.Closest(1.4, 0.6, 0.45, 0.2);
        Assert.Equal(1, partOf[onB!.Value.Tri]);
        var onA = index.Closest(1.4, 0.6, 0.45, 0.5, 0, 0, 0, 0.0, partOf, 0);
        Assert.Equal(0, partOf[onA!.Value.Tri]);
        Assert.Null(index.Closest(1.4, 0.6, 0.45, 0.2, 0, 0, 0, 0.0, partOf, 0));
    }

    // ------------------------------------------------------------------------------------------------ the estimate (R3-C13)

    internal static Dictionary<Node, List<Node>> ParentMap(Scene sc)
    {
        var parents = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
        foreach (var n in sc.IterNodes())
            foreach (var c in n.Children)
            {
                if (!parents.TryGetValue(c, out var l)) parents[c] = l = new List<Node>();
                if (!l.Contains(n)) l.Add(n);
            }
        return parents;
    }

    internal static readonly ProxyParams PerPart = new() { Parts = ProxyParts.PerPart };

    [Fact]
    public void EstimateNamesThePartsAndComparesWithTheSingleHull()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var est = PartProxy.Estimate(car, PerPart, ParentMap(sc));
        Assert.StartsWith("9 parts:", est.Text);
        foreach (var part in new[] { "body", "2 mirrors", "antenna", "4 wheels", "lights" }) Assert.Contains(part, est.Text);
        Assert.Contains("About", est.Text);
        Assert.Contains("draw calls", est.Text);
        Assert.Contains("today's single hull: about", est.Text);
        Assert.Equal(9, est.Parts);
        Assert.True(est.Tris > 0 && est.SingleHullTris > 0);
        Assert.EndsWith("More triangles than the single hull.", est.Text);   // P15's lower branch
        Assert.Equal(1 + 6, est.DrawCalls);   // the static proxy, 4 wheels and 2 light states (as the build: "draw calls 18 -> 7")
        // whole: only the single hull
        var whole = PartProxy.Estimate(car, new ProxyParams(), ParentMap(sc));
        Assert.StartsWith("Today's single hull: about", whole.Text);
        Assert.DoesNotContain("parts:", whole.Text);
    }

    [Fact]
    public void EstimateWarnsAboveThreeTimes()
    {
        var root = new Node(Kind.Group, "osg::Group", "t");
        void Add(string name, (double[] V, int[] T, double[] UV) m) =>
            root.Children.Add(new Node(Kind.Geode, "osg::Geode", name) { Children = { Geometry(name, m) } });
        Add("box", Box2(0, 0, 0, 2, 2, 2));
        var rnd = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            // 1 cm bolts floating 0.5 to 1 m above the box
            double x = -1 + 4 * rnd.NextDouble(), y = -1 + 4 * rnd.NextDouble(), z = 2.5 + 0.5 * rnd.NextDouble();
            Add($"bolt_{i}", Box2(x, y, z, x + 0.01, y + 0.01, z + 0.01));
        }
        var sc = new Scene("x.osgt", "osgt") { Root = root };
        var est = PartProxy.Estimate(root, PerPart, ParentMap(sc));
        Assert.EndsWith("More than 3x the single hull's triangles.", est.Text);
        Assert.DoesNotContain("More triangles than the single hull.", est.Text);
        Assert.True(est.Tris > PartProxy.Limits.EstimateWarnFactor * est.SingleHullTris);
    }

    /// <summary>The split car as an OpenFlight file (header, texture palette, vertex palette, then the tree: groups, a DOF with
    /// its matrix record per wheel, the lights switch with its mask, objects with one face per triangle) plus car.png and
    /// car_Classes.png beside it. Vertices are shared per geometry; every face uses texture 0.</summary>
    static string WriteSplitCarFlt(string dir)
    {
        var (carScene, car) = Meshes.SynthCar(true);
        var tex = car.StateSet!.Textures[0];
        File.WriteAllBytes(Path.Combine(dir, "car.png"), ImageCodecs.WritePng(tex.Baked!));
        File.WriteAllBytes(Path.Combine(dir, "car_Classes.png"), ImageCodecs.WritePng(tex.Companions![IrTests.Cls].Baked!));
        var ms = new MemoryStream();
        void Rec(int op, byte[] body)
        {
            var h = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)op);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)(4 + body.Length));
            ms.Write(h); ms.Write(body);
        }
        byte[] Named(string s, int size) { var b = new byte[size]; System.Text.Encoding.ASCII.GetBytes(s).CopyTo(b, 0); return b; }
        void I32(byte[] b, int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(at), v);
        var hdr = new byte[320];
        Named("db", 8).CopyTo(hdr, 0);
        I32(hdr, 8, 1600);
        Rec(1, hdr);
        var tp = Named("car.png", 212);
        I32(tp, 200, 0);
        Rec(64, tp);
        var geoms = carScene.IterNodes().Where(n => n.Kind == Kind.Geometry).ToList();
        int nv = geoms.Sum(g => g.Geom!.NumVertices);
        var vp = new byte[4];
        I32(vp, 0, 8 + nv * 44);
        Rec(67, vp);
        long palStart = ms.Length - 8;
        var offs = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        foreach (var g in geoms)
        {
            offs[g] = (int)(ms.Length - palStart);
            var V = g.Geom!.Vertices!.Data;
            var UV = g.Geom.TexCoords[0].Data;
            for (int i = 0; i < g.Geom.NumVertices; i++)
            {
                var v = new byte[40];
                for (int c = 0; c < 3; c++) System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(v.AsSpan(4 + 8 * c), V[3 * i + c]);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(v.AsSpan(28), (float)UV[2 * i]);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(v.AsSpan(32), (float)UV[2 * i + 1]);
                I32(v, 36, -1);
                Rec(71, v);
            }
        }
        void Push() => Rec(10, Array.Empty<byte>());
        void Pop() => Rec(11, Array.Empty<byte>());
        void Emit(Node n)
        {
            if (n.Kind == Kind.Geode)
            {
                Rec(4, Named(n.Name, 28));
                Push();
                var g = n.Children.Single(c => c.Kind == Kind.Geometry);
                var T = g.Geom!.Triangles();
                for (int t = 0; t < T.Length; t += 3)
                {
                    var f = Named("f", 76);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(24), 0);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(f.AsSpan(26), -1);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(40), 0x40000000);   // no colour
                    Rec(5, f);
                    Push();
                    var vl = new byte[12];
                    for (int k = 0; k < 3; k++) I32(vl, 4 * k, offs[g] + 44 * T[t + k]);
                    Rec(72, vl);
                    Pop();
                }
                Pop();
                return;
            }
            if (n.Kind == Kind.Switch)
            {
                var sw = new byte[28];
                Named(n.Name, 8).CopyTo(sw, 0);
                I32(sw, 12, 0); I32(sw, 16, 1); I32(sw, 20, 1);
                uint mask = 0;
                for (int i = 0; i < n.SwitchValues!.Count; i++) if (n.SwitchValues[i]) mask |= 1u << i;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sw.AsSpan(24), mask);
                Rec(96, sw);
            }
            else if (n.Cls == "FLT DOF") Rec(14, Named(n.Name, 76));
            else Rec(2, Named(n.Name, 44));
            if (n.Matrix != null)
            {
                var m = new byte[64];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(m.AsSpan(4 * (i * 4 + j)), (float)n.Matrix[j * 4 + i]);
                Rec(49, m);
            }
            Push();
            foreach (var c in n.Children) Emit(c);
            Pop();
        }
        Push();
        Emit(car);
        Pop();
        var path = Path.Combine(dir, "car.flt");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Theory]
    [InlineData(".flt")]
    [InlineData(".osgt")]
    public void SplitCarEndToEnd(string ext)
    {
        string root = Path.Combine(Path.GetTempPath(), "splitcar_" + Guid.NewGuid().ToString("N"));
        string inDir = Path.Combine(root, "in"), outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);
        try
        {
            Scene sc;
            if (ext == ".flt") sc = SceneIO.Load(WriteSplitCarFlt(inDir));
            else
            {
                // .osgt: save the car and run the pass on the RELOADED scene (the FLT DOF class is lost on the way: wheels reload as plain transforms)
                var op = Path.Combine(inDir, "car.osgt");
                SceneIO.Save(Meshes.SynthCar(true).sc, op);
                sc = SceneIO.Load(op);
                var reWheels = sc.IterNodes().Where(n => n.Kind == Kind.Transform).OrderBy(n => n.Name).ToList();
                Assert.Equal(new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" }, reWheels.Select(w => w.Name));
                Assert.All(reWheels, w => Assert.Equal("osg::MatrixTransform", w.Cls));
                var reAt = new Dictionary<string, (double x, double y)> { ["wheel_fl"] = (1.5, 0.75), ["wheel_fr"] = (1.5, -0.75), ["wheel_rl"] = (-1.5, 0.75), ["wheel_rr"] = (-1.5, -0.75) };
                foreach (var w in reWheels)
                {
                    var e = Mat.Identity();
                    e[3] = reAt[w.Name].x; e[7] = reAt[w.Name].y; e[11] = 0.33;
                    for (int i = 0; i < 16; i++) Assert.Equal(e[i], w.Matrix![i], 9);
                }
                var reSw = Assert.Single(sc.IterNodes(), n => n.Kind == Kind.Switch);
                Assert.Equal(2, reSw.Children.Count);
                Assert.Equal(new[] { true, false }, reSw.SwitchValues);
            }
            var car = sc.IterNodes().First(n => n.Name == "car" && n.Kind == Kind.Group);
            var prms = new Dictionary<string, object>
            {
                ["replace_proxy.parts"] = "per_part", ["replace_proxy.target"] = "within", ["replace_proxy.max_error"] = 0.08,
                ["replace_proxy.ground"] = "cut", ["replace_proxy.normals"] = "original", ["replace_proxy.texture"] = 256, ["replace_proxy.voxels"] = 64,
            };
            var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, prms, targets: new List<Node> { car });
            Assert.Null(results[0].Error);
            string tail = "; within 8 cm (reached 2 cm, max over parts); 1 parts kept original or decimated, +8 draw calls (body_1); underside cut; "
                + "1 objects -> 1 atlases; Classes companions ({stem}_Classes.png) baked by nearest lookup (class IDs, never blended) into a companion atlas with the same layout";
            // frozen from a green run (no timings in it); re-recorded in the final fix wave, where triangles and draw calls count
            // drawn geometry only (ruling T16-d: the light state that is off is neither before nor after). .flt: the 4 DOF wheels
            // stay separate (draw calls 18 -> 14: the atlas geometry, 4 wheels, the light that is on and +8); the body cluster's
            // hull misses the bound, so its 8 pieces stay decimated with their own texture (+8). .osgt (reloaded): the wheels are
            // plain transforms there, not moving parts, so only the switch stays separate (18 -> 10).
            Assert.Equal(ext == ".flt"
                ? "1 object(s) replaced with a per-part proxy: 21,324 -> 1,408 triangles, draw calls 18 -> 14 (4 wheels and 1 switch kept separate)" + tail
                : "1 object(s) replaced with a per-part proxy: 21,324 -> 1,320 triangles, draw calls 18 -> 10 (1 switch kept separate)" + tail,
                results[0].Message);
            var outp = Path.Combine(outDir, "car" + ext);
            SceneIO.Save(sc, outp);
            var back = SceneIO.Load(outp);
            // the analyzer counts every geometry, the light state that is off too: one more than the line
            Assert.Equal(ext == ".flt" ? 15L : 11L, Convert.ToInt64(Analyzer.Analyze(back).Summary()["draw_calls"]));

            // the four wheels (.flt: DOF records, moving parts; .osgt reloaded: plain matrix transforms, folded into the atlas by the pass)
            var wheels = back.IterNodes().Where(n => n.Kind == Kind.Transform).OrderBy(n => n.Name).ToList();
            if (ext == ".osgt")
                Assert.Empty(wheels);
            else
            {
                Assert.Equal(new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" }, wheels.Select(w => w.Name));
                Assert.All(wheels, w => Assert.Equal("FLT DOF", w.Cls));
                var at = new Dictionary<string, (double x, double y)> { ["wheel_fl"] = (1.5, 0.75), ["wheel_fr"] = (1.5, -0.75), ["wheel_rl"] = (-1.5, 0.75), ["wheel_rr"] = (-1.5, -0.75) };
                foreach (var w in wheels)
                {
                    var expect = Mat.Identity();
                    expect[3] = at[w.Name].x; expect[7] = at[w.Name].y; expect[11] = 0.33;
                    Assert.Equal(16, w.Matrix!.Length);
                    for (int i = 0; i < 16; i++) Assert.Equal(expect[i], w.Matrix[i], 6);
                    Assert.Single(w.Children);
                }
                Assert.Equal(4, FltFile.ReadRecords(File.ReadAllBytes(outp)).Count(r => r.op == FltFile.DOF));
            }

            // the lights switch: two children, on then off
            var sw = Assert.Single(back.IterNodes(), n => n.Kind == Kind.Switch);
            Assert.Equal("lights", sw.Name);
            Assert.Equal(2, sw.Children.Count);
            Assert.Equal(new[] { true, false }, sw.SwitchValues);

            // the class atlas next to the colour atlas: source IDs (1 to 4) or class_fill (0) only, nothing blended
            var clsPath = Assert.Single(Directory.GetFiles(outDir, "car_proxy_Classes.png", SearchOption.AllDirectories));
            var cls = ImageCodecs.Read(clsPath)!;
            var colour = ImageCodecs.Read(Directory.GetFiles(outDir, "car_proxy.*", SearchOption.AllDirectories).Single(f => !f.Contains("_Classes")))!;
            Assert.Equal((colour.Width, colour.Height), (cls.Width, cls.Height));
            var pairs = IrTests.ClassPairs(cls);
            Assert.True(pairs.IsSubsetOf(new[] { (0, 0), (1, 0), (2, 0), (3, 0), (4, 0) }), "unexpected (R, G): " + string.Join(" ", pairs));
            Assert.True(pairs.Count(p => p.r > 0) >= 2, string.Join(" ", pairs));   // body and at least one more class survived
        }
        finally { Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------------------------------------------ final fix wave

    [Theory]
    [InlineData("osg::Sequence", false)]
    [InlineData("FLT Group", true)]
    public void AnimatedGroupIsKeptAsItIs(string cls, bool fltAnimated)
    {
        // ruling F-1: a sequence (or an FLT animated group) below the car is the frames of an animation: neither merged
        // into one proxy nor split; it stays as it is, with a note
        var (sc, car) = Meshes.SynthCar(true);
        var beacon = new Node(Kind.Group, cls, "beacon");
        if (fltAnimated) beacon.Notes.Add("animated group");
        foreach (var (name, z) in new[] { ("beacon_a", 1.39), ("beacon_b", 1.41) })
            beacon.Children.Add(new Node(Kind.Geode, "osg::Geode", name) { Children = { Geometry(name, Box2(-0.1, -0.1, z, 0.1, 0.1, z + 0.1)) } });
        car.Children.Add(beacon);
        Assert.True(Optimizer.Moving(beacon));
        Assert.True(PartProxy.AnimatedGroup(beacon));
        var kids = beacon.Children.ToArray();
        var geoms = kids.Select(k => k.Children.Single().Geom).ToArray();
        var msg = RunPerPart(sc, car);
        Assert.Contains("beacon kept as it is (animated group)", msg);
        Assert.Contains("(4 wheels and 1 switch kept separate)", msg);   // not counted as a part kept separate
        Assert.Contains("draw calls 20 -> 8", msg);   // 18 + the 2 frames drawn; 1 + 4 + the light that is on + the 2 frames as they were
        Assert.Contains(beacon, sc.IterNodes());
        Assert.Equal(kids, beacon.Children);
        Assert.Equal(geoms, beacon.Children.Select(k => k.Children.Single().Geom));
    }

    [Fact]
    public void WholeWithinReachesTheTargetOrLeavesTheObject()
    {
        // ruling F-2: target = within with parts = whole goes through the single-hull tighten loop
        static (Scene sc, Node obj) BallScene()
        {
            var (V, T) = OptimizeTests.Sphere(80, 40);
            var obj = new Node(Kind.Geode, "osg::Geode", "ball") { Children = { Geometry("ball", (V, T, new double[V.Length / 3 * 2])) } };
            return (new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { obj } } }, obj);
        }
        static string Run(Scene sc, Node obj, double e, int voxels)
        {
            var (results, _, _) = Optimizer.Run(sc, new[] { "replace_proxy" }, new Dictionary<string, object>
            {
                ["replace_proxy.parts"] = "whole", ["replace_proxy.target"] = "within", ["replace_proxy.max_error"] = e,
                ["replace_proxy.voxels"] = voxels, ["replace_proxy.texture"] = 64, ["replace_proxy.share"] = false,
            }, targets: new List<Node> { obj });
            Assert.Null(results[0].Error);
            return results[0].Message;
        }
        var (sc1, ball1) = BallScene();
        var ok = Run(sc1, ball1, 0.08, 64);
        Assert.StartsWith("1 object(s) replaced with a textured proxy", ok);
        var m = System.Text.RegularExpressions.Regex.Match(ok, @"; within 8 cm \(reached ([\d.]+) (mm|cm)\)");
        Assert.True(m.Success, ok);
        Assert.True(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * (m.Groups[2].Value == "mm" ? 0.001 : 0.01) <= 0.08, ok);
        Assert.DoesNotContain(ball1, sc1.Root!.Children);

        // 1 mm at 16 voxels (cells of 12.5 cm): no attempt stays within it, the ball is left as it is
        var (sc2, ball2) = BallScene();
        var geom = ball2.Children[0].Geom;
        var bad = Run(sc2, ball2, 0.001, 16);
        Assert.Contains("Within 1 mm: could not stay within it (best ", bad);
        Assert.EndsWith("; left unchanged", bad);
        Assert.StartsWith("0 object(s)", bad);
        Assert.Same(ball2, Assert.Single(sc2.Root!.Children));
        Assert.Same(geom, ball2.Children[0].Geom);
    }

    /// <summary>A comb: a base plate 0.13 x 0.6 x 0.05 m with four closed fins 1 cm thick and 20 cm high, 3 cm apart. At
    /// cells of 2 cm the voxel hull fills the gaps, so the gap floors (seen from above) lie 20 cm below it; finer cells
    /// open them.</summary>
    static (double[] V, int[] T, double[] UV) Comb()
    {
        const double gap = 0.03, fin = 0.01, height = 0.2;
        const int fins = 4;
        var V = new List<double>(); var T = new List<int>(); var UV = new List<double>();
        void Add((double[] V, int[] T, double[] UV) b) { int o = V.Count / 3; V.AddRange(b.V); T.AddRange(b.T.Select(t => t + o)); UV.AddRange(b.UV); }
        Add(LodToolsTests.Box(0, 0, -0.05, fins * fin + (fins - 1) * gap, 0.6, 0, 4));
        for (int i = 0; i < fins; i++) Add(LodToolsTests.Box(i * (fin + gap), 0, 0, i * (fin + gap) + fin, 0.6, height, 4));
        return (V.ToArray(), T.ToArray(), UV.ToArray());
    }

    [Fact]
    public void WithinHullAttemptsRefine()
    {
        // ruling F-3: the first voxel hull asks for cells of e / 2; an attempt that misses e tightens the error, and the
        // next hull's cell follows it (the resolution grows) up to the voxels, the upper limit
        var (asm, part) = OnePart("comb", Comb());
        var cells = new List<double>();
        void Made(PartProxy.Part p, Proxy.HullResult h) { if (ReferenceEquals(p, part)) lock (cells) cells.Add(h.Cell); }
        int hulls = PartProxy.WithinHulls;
        PartProxy.WithinHullMade += Made;
        try { PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(0.04, 48), new PartProxy.BuildOptions { TextureSize = 64 }); }
        finally { PartProxy.WithinHullMade -= Made; }
        Assert.True(cells.Count >= 2, $"{cells.Count} hull attempts");
        Assert.True(PartProxy.WithinHulls - hulls >= cells.Count);
        var (ext, _) = PartProxy.ObjectExtent(asm);
        Assert.Equal(PartClass.Bulk, part.Class);
        Assert.Equal(ext / Math.Ceiling(ext / 0.02), cells[0], 9);   // the first: cells of e / 2
        for (int i = 1; i < cells.Count; i++) Assert.True(cells[i] <= cells[i - 1], string.Join(", ", cells));
        Assert.True(cells[^1] < cells[0], "the resolution grows between attempts: " + string.Join(", ", cells));
        Assert.True(cells.Min() >= ext / 48 * (1 - 1e-9), "the voxels are the upper limit: " + string.Join(", ", cells));
    }

    [Fact]
    public void WithinTimeBudgetStopsFurtherHulls()
    {
        // ruling F-3: after the object's budget no further voxel hull attempt runs; the part takes the decimated original
        var (asm, part) = Ball();
        int made = 0;
        void Made(PartProxy.Part p, Proxy.HullResult h) { if (ReferenceEquals(p, part)) Interlocked.Increment(ref made); }
        var o = new PartProxy.BuildOptions { TextureSize = 64, WithinBudgetSeconds = 0 };
        PartProxy.WithinHullMade += Made;
        PartProxy.ObjectResult? r;
        try { r = PartProxy.BuildParts(asm, SourcesOf(asm), WithinE(0.08, 32), o); }
        finally { PartProxy.WithinHullMade -= Made; }
        Assert.Equal(0, made);
        var notes = (r?.Notes ?? new List<string>()).Concat(o.Notes).ToList();
        Assert.Contains(notes, n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^time budget reached after \d+ s$"));
        Assert.NotEqual(PartMethod.Voxel, r?.Static.Single().Method ?? PartMethod.Kept);
        Assert.Equal(120, PartProxy.Limits.WithinBudgetSeconds);
        Assert.Equal(PartProxy.Limits.WithinBudgetSeconds, new PartProxy.BuildOptions().WithinBudgetSeconds);
    }

    [Fact]
    public void SaveSynthCarForTheInspector()
    {
        // R3-C13: SCENEKIT_SAVE_SYNTH_CAR=1 saves the split car as .osgt for a live check in the inspector; a no-op otherwise
        if (Environment.GetEnvironmentVariable("SCENEKIT_SAVE_SYNTH_CAR") != "1") return;
        string dir = Environment.GetEnvironmentVariable("SCENEKIT_SYNTH_CAR_DIR") is { Length: > 0 } d ? d : Path.GetTempPath();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "synth_car.osgt");
        var (sc, _) = Meshes.SynthCar(true);
        SceneIO.Save(sc, path);
        Assert.True(File.Exists(path));
        // R3-C15: the car as an OpenFlight file too (4 DOF records and the lights switch survive in .flt); car.flt beside it
        Assert.True(File.Exists(WriteSplitCarFlt(dir)));
    }
}

/// <summary>The estimate tests that read the process-wide counters (<c>Proxy.HullCalls</c>, <c>PartProxy.DiscoverCalls</c>):
/// in the "serial" collection, which runs alone, so no other class builds hulls or discovers while they count.</summary>
[Collection("serial")]
public class ProxyEstimateSerialTests
{
    [Fact]
    public void EstimateIsFast()
    {
        var (sc, car) = Meshes.SynthCar(true);
        var parents = PartProxyTests.ParentMap(sc);
        int hulls = Proxy.HullCalls;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PartProxy.Estimate(car, PartProxyTests.PerPart, parents);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(hulls, Proxy.HullCalls);
    }

    [Fact]
    public void WholeModeCountsWithoutClustering()
    {
        var (sc, car) = Meshes.SynthCar(true);
        long before = PartProxy.Discover(car, 0.2).AllPieces().Sum(pc => (long)pc.T.Length / 3);
        int discovers = PartProxy.DiscoverCalls;
        var est = PartProxy.Estimate(car, new ProxyParams(), PartProxyTests.ParentMap(sc));
        Assert.Equal(discovers, PartProxy.DiscoverCalls);   // no clustering
        Assert.Equal((long)Math.Max(12, before * new ProxyParams().Ratio), est.SingleHullTris);
        Assert.Equal(est.SingleHullTris, est.Tris);
        Assert.StartsWith("Today's single hull: about", est.Text);
        Assert.EndsWith("wheels and switch merged.", est.Text);   // the moving nodes are listed
    }

    [Fact]
    public void ASelectionAboveTheCapIsNotEstimated()
    {
        // one geometry of cap + 1 degenerate triangles over three vertices: counted, never discovered
        long tris = PartProxy.Limits.EstimateMaxTris + 1;
        var t = new int[3 * tris];
        for (long i = 0; i < tris; i++) { t[3 * i] = 0; t[3 * i + 1] = 1; t[3 * i + 2] = 2; }
        var geom = PartProxyTests.Geometry("big", (new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, t, new double[0]));
        var root = new Node(Kind.Group, "osg::Group", "t") { Children = { new Node(Kind.Geode, "osg::Geode", "g") { Children = { geom } } } };
        var sc = new Scene("x.osgt", "osgt") { Root = root };
        int discovers = PartProxy.DiscoverCalls;
        foreach (var p in new[] { PartProxyTests.PerPart, new ProxyParams() })
        {
            var est = PartProxy.Estimate(root, p, PartProxyTests.ParentMap(sc));
            Assert.Equal("Selection too large to estimate.", est.Text);
            Assert.Equal(0, est.Tris);
        }
        Assert.Equal(discovers, PartProxy.DiscoverCalls);
    }
}
