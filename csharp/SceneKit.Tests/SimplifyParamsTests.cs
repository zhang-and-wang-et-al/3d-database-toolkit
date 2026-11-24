using SceneKit;
using Xunit;

/// <summary>R2-D3 / D4: one parameter record for Try and Apply, strategies named by outcome, the error target in world units.</summary>
public class SimplifyParamsTests
{
    static GeomData Bumpy() => Meshes.Grid(40, 10, (x, y) => 0.3 * Math.Sin(x) * Math.Cos(y));
    /// <summary>A gentle grid: Within 5 cm can be met on it (on the bumpy grid Decimate's border fan and saddle sag
    /// exceed any limit this tight, see Decimate.Options.MaxError).</summary>
    static GeomData Gentle() => Meshes.Grid(40, 10, (x, y) => 0.05 * Math.Sin(x) * Math.Cos(y));

    /// <summary>(scene, geode, geometry) of one part under a matrix.</summary>
    static (Scene sc, Node geode, Node geo) Placed(GeomData g, double[] m)
    {
        var (sc, root) = Meshes.SceneOf(("part", g, m));
        var geode = root.Children[0].Children[0];
        return (sc, geode, geode.Children[0]);
    }

    [Fact]
    public void AppliedLineReadsBeforeAfterAndVerdict()
    {
        Assert.Equal("Applied: 1,800 → 899 triangles, OK", InspectorModel.AppliedLine(1800, 899, "ok"));
        Assert.EndsWith(", LIKELY DAMAGED", InspectorModel.AppliedLine(1800, 899, "damaged"));
        Assert.EndsWith(", CHECK", InspectorModel.AppliedLine(1800, 899, "check"));
        Assert.EndsWith(", NOTHING SIMPLIFIED", InspectorModel.AppliedLine(1800, 1800, "skipped"));
        Assert.Equal("Applied: 1,800 → 899 triangles, OK", InspectorModel.AppliedLine(1800, 899, "ok", 899));
    }

    [Fact]
    public void AppliedLineSaysWhenItDiffersFromThePreview() =>
        Assert.Equal("Applied: 1,800 → 905 triangles (the preview had 899), see Review", InspectorModel.AppliedLine(1800, 905, "ok", 899));

    [Fact]
    public void StrategiesMatchTheTable()
    {
        var all = Strategies.All;
        Assert.Equal(5, all.Count);
        Assert.Equal(new[] { SimplifyStrategy.KeepTexturesExact, SimplifyStrategy.LetEdgesMerge, SimplifyStrategy.NewTexture, SimplifyStrategy.ShapeOnly, SimplifyStrategy.FastRough },
            all.Select(s => s.Id));
        Assert.Equal(new[] { "Keep textures exact", "Let edges merge", "New texture", "Shape only", "Fast, rough" }, all.Select(s => s.Label));
        Assert.Equal(new[] { "qem/keep", "qem/permissive", "qem/rebake", "qem/ignore", "fast/keep" }, all.Select(s => $"{s.Method}/{s.Seams}"));
        Assert.Equal("Hard edges may merge (the normals are rebuilt); texture seams still only slide. Textures may shift slightly.",
            Strategies.Of(SimplifyStrategy.LetEdgesMerge).Help);
        Assert.Equal(SimplifyStrategy.NewTexture, Strategies.From("qem", "rebake"));
    }

    [Fact]
    public void ParsesDistancesAndPercent()
    {
        void Ok(string text, double v, bool rel)
        {
            Assert.True(ErrorTarget.TryParse(text, out var t), text);
            Assert.Equal(v, t.Value, 9);
            Assert.Equal(rel, t.Relative);
        }
        Ok("2 cm", 0.02, false);
        Ok("0,5 %", 0.5, true);
        Ok("2,5cm", 0.025, false);
        Ok("5mm", 0.005, false);
        Ok("0.3", 0.3, false);
        foreach (var bad in new[] { "", "abc", "0", "-1 cm" }) Assert.False(ErrorTarget.TryParse(bad, out _), bad);
    }

    [Fact]
    public void DistanceTextPicksTheUnit()
    {
        Assert.Equal("2 cm", ErrorTarget.Distance(0.02));
        Assert.Equal("5 mm", ErrorTarget.Distance(0.005));
        Assert.Equal("1.5 m", ErrorTarget.Distance(1.5));
        Assert.Equal("1.2 km", ErrorTarget.Distance(1200));
        Assert.Equal("1 m", ErrorTarget.Distance(0.9999));
        Assert.Equal("1 km", ErrorTarget.Distance(999.6));
    }

    [Fact]
    public void DescribeNamesTargetAndStrategy()
    {
        Assert.Equal("within 2 cm, keep textures exact", new SimplifyParams { Error = new(0.02, false) }.Describe());
        Assert.Equal("within 0.5 % of each part's size (up to 1.2 cm), keep textures exact", new SimplifyParams().Describe(2.4));
        Assert.Equal("keep 30 % of the triangles, let edges merge",
            new SimplifyParams { Within = false, Ratio = 0.3, Strategy = SimplifyStrategy.LetEdgesMerge }.Describe());
    }

    [Fact]
    public void RoundTripsThroughPassParams()
    {
        var variants = new[]
        {
            new SimplifyParams(),
            new SimplifyParams { Error = new(0.02, false), Strategy = SimplifyStrategy.LetEdgesMerge, Borders = "lock", Parts = "keep", Crease = 30, RebakeSize = 1024 },
            new SimplifyParams { Within = false, Ratio = 0.3, Strategy = SimplifyStrategy.NewTexture },
        };
        foreach (var p in variants) Assert.Equal(p, SimplifyParams.FromPassParams(p.ToPassParams()));
    }

    [Fact]
    public void EngineRoundTripsThroughPassParams()
    {
        foreach (var s in new[] { SimplifyStrategy.KeepTexturesExact, SimplifyStrategy.LetEdgesMerge, SimplifyStrategy.ShapeOnly, SimplifyStrategy.FastRough })
        {
            var p = new SimplifyParams { Strategy = s, Engine = SimplifyEngine.Meshopt, Prune = true };
            if (s == SimplifyStrategy.FastRough) p = p.Normalized();
            Assert.Equal(p, SimplifyParams.FromPassParams(p.ToPassParams()));
        }
        // D8: New texture always runs the standard simplifier (method qem), so its engine reads back as Standard
        var nt = new SimplifyParams { Strategy = SimplifyStrategy.NewTexture, Engine = SimplifyEngine.Meshopt, Prune = true };
        Assert.Equal(nt with { Engine = SimplifyEngine.Standard }, SimplifyParams.FromPassParams(nt.ToPassParams()));
    }

    [Fact]
    public void MethodFollowsEngineAndStrategy()
    {
        string M(SimplifyStrategy s, SimplifyEngine e) => new SimplifyParams { Strategy = s, Engine = e }.Method;
        Assert.Equal("sloppy", M(SimplifyStrategy.FastRough, SimplifyEngine.Meshopt));
        Assert.Equal("meshopt", M(SimplifyStrategy.LetEdgesMerge, SimplifyEngine.Meshopt));
        Assert.Equal("meshopt", M(SimplifyStrategy.KeepTexturesExact, SimplifyEngine.Meshopt));
        Assert.Equal("meshopt", M(SimplifyStrategy.ShapeOnly, SimplifyEngine.Meshopt));
        Assert.Equal("qem", M(SimplifyStrategy.NewTexture, SimplifyEngine.Meshopt));
        foreach (var info in Strategies.All) Assert.Equal(info.Method, M(info.Id, SimplifyEngine.Standard));
        Assert.Equal("sloppy", new SimplifyParams { Strategy = SimplifyStrategy.FastRough, Engine = SimplifyEngine.Meshopt }.ToPassParams()["simplify.method"]);
        Assert.Equal("Standard", SimplifyEngines.Label(SimplifyEngine.Standard));
        Assert.Equal("meshoptimizer", SimplifyEngines.Label(SimplifyEngine.Meshopt));
        Assert.Equal("qem / R-Simp", SimplifyEngines.Tech(SimplifyEngine.Standard));
        Assert.Equal("meshopt / sloppy", SimplifyEngines.Tech(SimplifyEngine.Meshopt));
        Assert.True(SimplifyEngines.Available(SimplifyEngine.Standard, out string r0));
        Assert.Equal("", r0);
        Assert.Equal(MeshoptNative.Available, SimplifyEngines.Available(SimplifyEngine.Meshopt, out string r1));
        Assert.Equal(MeshoptNative.Unavailable, r1);
    }

    [Fact]
    public void OldRecordsStayStandard()
    {
        var p = SimplifyParams.FromPassParams(new Dictionary<string, object> { ["simplify.method"] = "qem", ["simplify.seams"] = "permissive" });
        Assert.Equal(SimplifyEngine.Standard, p.Engine);
        Assert.False(p.Prune);
        Assert.Equal(SimplifyStrategy.LetEdgesMerge, p.Strategy);
        var fast = SimplifyParams.FromPassParams(new Dictionary<string, object> { ["simplify.method"] = "sloppy" });
        Assert.Equal((SimplifyStrategy.FastRough, SimplifyEngine.Meshopt), (fast.Strategy, fast.Engine));
        Assert.Contains("prune", SimplifyParams.PassKeys);
        Assert.False(new SimplifyParams().Prune);
        Assert.Equal(SimplifyEngine.Standard, new SimplifyParams().Engine);
    }

    [Fact]
    public void DescribeNamesTheEngine()
    {
        Assert.Equal("within 2 cm, let edges merge, meshoptimizer",
            new SimplifyParams { Error = new(0.02, false), Strategy = SimplifyStrategy.LetEdgesMerge, Engine = SimplifyEngine.Meshopt }.Describe());
        Assert.Equal("within 2 cm, let edges merge",
            new SimplifyParams { Error = new(0.02, false), Strategy = SimplifyStrategy.LetEdgesMerge }.Describe());
        Assert.Equal("keep 30 % of the triangles, new texture",
            new SimplifyParams { Within = false, Ratio = 0.3, Strategy = SimplifyStrategy.NewTexture, Engine = SimplifyEngine.Meshopt }.Describe());
    }

    [Fact]
    public void FastRoughSwitchesToKeepPercent()
    {
        Assert.False(new SimplifyParams { Strategy = SimplifyStrategy.FastRough }.Normalized().Within);
    }

    static double WorldDeviation(double[] m, double worldScale)
    {
        var (sc, geode, geo) = Placed(Gentle(), m);
        var before = geo.Geom!;
        long t0 = before.Counts().tris;
        var p = new SimplifyParams { Error = new(0.05, false) };
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, p.ToPassParams(), targets: new List<Node> { geode });
        Assert.Null(res[0].Error);
        var after = geo.Geom!;
        Assert.True(after.Counts().tris < t0, $"{after.Counts().tris} of {t0}: {res[0].Message}");
        return Quality.Compare(before, after).MaxDev * worldScale;
    }

    [Fact]
    public void WithinIsMeasuredInWorldUnits()
    {
        double dev = WorldDeviation(Meshes.Scale(10, 10, 10), 10);
        Assert.True(dev <= 0.05 * 1.05, $"world deviation {dev}");
    }

    [Fact]
    public void WithinUnderMirroredScaleStaysWithinTarget()
    {
        double dev = WorldDeviation(Meshes.Scale(-2, 1, 3), 3);
        Assert.True(dev <= 0.05 * 1.05, $"world deviation {dev}");
    }

    [Fact]
    public void WithinLeavesGeometryUnchangedWhenTheLimitCannotBeMet()
    {
        var p = new SimplifyParams { Error = new(0.05, false) };
        var (sc, geode, geo) = Placed(Bumpy(), Meshes.Scale(10, 10, 10));
        long t0 = geo.Geom!.Counts().tris;
        var start = SoftRender.ParentMatrix(geode, new OptContext(sc).Parents);
        var tr = InspectorModel.TrySimplify(geode, p, new InspectorModel.TryContext(start));
        Assert.NotNull(tr);
        Assert.Equal(t0, tr!.TrisAfter);
        Assert.Contains(tr.Reasons, r => r.Contains("left unchanged"));
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, p.ToPassParams(), targets: new List<Node> { geode });
        Assert.Null(res[0].Error);
        Assert.Equal(t0, geo.Geom!.Counts().tris);
        Assert.Contains("left unchanged", res[0].Message);
    }

    [Fact]
    public void WithinWithoutAnErrorIsRefused()
    {
        var (sc, geode, _) = Placed(Bumpy(), Meshes.Scale(1, 1, 1));
        var prms = new Dictionary<string, object> { ["simplify.target"] = "within", ["simplify.max_error"] = 0.0, ["simplify.max_error_pct"] = 0.0 };
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, prms, targets: new List<Node> { geode });
        Assert.NotNull(res[0].Error);
        Assert.Contains("ArgumentException", res[0].Error);
        Assert.Contains("Within needs", res[0].Error);
    }

    [Fact]
    public void WithinHoldsAtTheLargestInstance()
    {
        // one geometry placed twice: at scale 1 (first parent) and at scale 10; the limit must hold at the scale-10 place
        var g = Gentle();
        var geo = new Node(Kind.Geometry, "osg::Geometry", "part") { Geom = g };
        var geode = new Node(Kind.Geode, "osg::Geode", "part_geode") { Children = { geo } };
        var small = new Node(Kind.Transform, "osg::MatrixTransform", "small") { Matrix = Meshes.Scale(1, 1, 1), Children = { geode } };
        var big = new Node(Kind.Transform, "osg::MatrixTransform", "big") { Matrix = Meshes.Scale(10, 10, 10), Children = { geode } };
        var target = new Node(Kind.Group, "osg::Group", "target") { Children = { small, big } };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { target } };
        var sc = new Scene("x.osgt", "osgt") { Root = root };
        long t0 = g.Counts().tris;
        var p = new SimplifyParams { Error = new(0.05, false) };
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, p.ToPassParams(), targets: new List<Node> { target });
        Assert.Null(res[0].Error);
        var after = geo.Geom!;
        if (after.Counts().tris == t0) Assert.Contains("left unchanged", res[0].Message);
        else
        {
            double dev = Quality.Compare(g, after).MaxDev * 10;
            Assert.True(dev <= 0.05 * 1.05, $"world deviation at the scale-10 place {dev}: {res[0].Message}");
        }
    }

    /// <summary>A closed 1 m box at (x, y) standing on z = 0 (12 triangles).</summary>
    static GeomData Box1(double x, double y)
    {
        var bv = new List<double>();
        for (int k = 0; k < 8; k++) { bv.Add(x + ((k & 1) == 0 ? 0 : 1)); bv.Add(y + ((k & 2) == 0 ? 0 : 1)); bv.Add((k & 4) == 0 ? 0 : 1); }
        int[] bt = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
        var g = new GeomData { Vertices = new VArray(bv.ToArray(), 3, "Vec3Array") };
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: bt.Length, indices: bt, indexType: "DrawElementsUInt"));
        return g;
    }

    [Fact]
    public void PercentOfSizeIsCappedByEachPartsOwnSize()
    {
        // a 200 m terrain grid and a 1 m prop in one group: "0.5 % of size" on the group must not allow the prop the
        // group's 1.41 m, only 0.5 % of its own diagonal (8.7 mm)
        var p = new SimplifyParams { Error = new(0.5, true), Strategy = SimplifyStrategy.ShapeOnly };
        (Scene sc, Node group, Node box, GeomData before) Make()
        {
            var (sc, root) = Meshes.SceneOf(("ground", Meshes.Grid(40, 200, (x, y) => 0.5 * Math.Sin(0.05 * x)), null), ("prop", Box1(10, 10), null));
            var box = root.Children[1].Children[0];
            return (sc, root, box, box.Geom!);
        }
        void Check(GeomData before, GeomData after, string what)
        {
            if (after.Counts().tris == before.Counts().tris) return;   // left unchanged: fine
            double own = Math.Sqrt(3);
            double dev = Quality.Compare(before, after).MaxDev;
            Assert.True(dev <= 0.005 * own * 1.05, $"{what}: prop deviation {dev} m above 0.5 % of its own size");
        }
        {
            var (sc, group, box, before) = Make();
            var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, p.ToPassParams(), targets: new List<Node> { group });
            Assert.Null(res[0].Error);
            Check(before, box.Geom!, "pass: " + res[0].Message);
        }
        {
            var (_, group, _, before) = Make();
            var tr = InspectorModel.TrySimplify(group, p);
            Assert.NotNull(tr);
            int bi = tr!.Before.FindIndex(m => m.T.Length / 3 == 12);
            Assert.True(bi >= 0);
            var am = tr.After[bi];
            var after = new GeomData { Vertices = new VArray(am.V.Select(v => (double)v).ToArray(), 3, "Vec3Array") };
            after.Prims.Add(new Primitive("TRIANGLES", "elements", count: am.T.Length, indices: am.T.ToArray(), indexType: "DrawElementsUInt"));
            Check(before, after, "try");
        }
    }

    [Fact]
    public void ApplyFromThePreviewChangesOnlyWhatTheTrySimplified()
    {
        // an LOD: Try shows (and simplifies) the finest level only; Apply from the preview must leave the coarser level
        var near = new Node(Kind.Geometry, "osg::Geometry", "near") { Geom = Gentle() };
        var far = new Node(Kind.Geometry, "osg::Geometry", "far") { Geom = Gentle() };
        var lod = new Node(Kind.Lod, "osg::LOD", "lod")
        {
            Ranges = new() { (0, 100), (100, 1000) },
            Children = { new Node(Kind.Geode, "osg::Geode", "near_geode") { Children = { near } }, new Node(Kind.Geode, "osg::Geode", "far_geode") { Children = { far } } },
        };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { lod } } };
        var farBefore = far.Geom;
        var p = new SimplifyParams { Error = new(0.5, true), Strategy = SimplifyStrategy.ShapeOnly };
        var tr = InspectorModel.TrySimplify(lod, p);
        Assert.NotNull(tr);
        Assert.True(tr!.TrisAfter < tr.TrisBefore);
        Assert.Contains(near, tr.Simplified);
        Assert.DoesNotContain(far, tr.Simplified);
        var (targets, prms) = InspectorModel.ApplyArgs(tr, p.ToPassParams());
        var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, prms, targets: targets);
        Assert.Null(res[0].Error);
        Assert.Same(farBefore, far.Geom);
        Assert.Equal(tr.TrisAfter, near.Geom!.Counts().tris);
    }

    /// <summary>A group with one local geometry and one inside an external reference (a loaded file).</summary>
    static (Scene sc, Node group, Node local, Node inExt) LocalAndExternal()
    {
        var local = new Node(Kind.Geometry, "osg::Geometry", "local") { Geom = Gentle() };
        var inExt = new Node(Kind.Geometry, "osg::Geometry", "in_ext") { Geom = Gentle() };
        var fileRoot = new Node(Kind.Group, "osg::Group", "file") { Children = { new Node(Kind.Geode, "osg::Geode", "ext_geode") { Children = { inExt } } } };
        var en = new Node(Kind.External, "external file", "ref.osgt") { External = "ref.osgt", Children = { fileRoot } };
        var group = new Node(Kind.Group, "osg::ProxyNode", "group")
        {
            Children = { new Node(Kind.Geode, "osg::Geode", "local_geode") { Children = { local } } },
            Externals = new List<Node> { en },
        };
        var sc = new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, "osg::Group", "root") { Children = { group } } };
        sc.Externals["ref.osgt"] = new Scene("ref.osgt", "osgt") { Root = fileRoot };
        return (sc, group, local, inExt);
    }

    [Fact]
    public void TryRespectsIncludeExternalsLikeApply()
    {
        var p = new SimplifyParams { Error = new(0.5, true), Strategy = SimplifyStrategy.ShapeOnly };
        {
            var (_, group, local, inExt) = LocalAndExternal();
            var off = InspectorModel.TrySimplify(group, p, new InspectorModel.TryContext(IncludeExternals: false));
            Assert.NotNull(off);
            Assert.Contains(local, off!.Simplified);
            Assert.DoesNotContain(inExt, off.Simplified);
            Assert.True(off.ExternalsSkipped);
            Assert.Single(off.Before);
        }
        {
            var (_, group, local, inExt) = LocalAndExternal();
            var on = InspectorModel.TrySimplify(group, p, new InspectorModel.TryContext(IncludeExternals: true));
            Assert.NotNull(on);
            Assert.Contains(local, on!.Simplified);
            Assert.Contains(inExt, on.Simplified);
            Assert.False(on.ExternalsSkipped);
        }
        {
            // a try with externals, then "Include externals" unticked before Apply: the external geometry stays
            var (sc, group, local, inExt) = LocalAndExternal();
            var extBefore = inExt.Geom;
            var tr = InspectorModel.TrySimplify(group, p);
            var prms = p.ToPassParams();
            prms["include_externals"] = false;
            var (targets, aprms) = InspectorModel.ApplyArgs(tr!, prms);
            Assert.DoesNotContain(inExt, targets);
            Assert.Contains(local, targets);
            var (res, _, _) = Optimizer.Run(sc, new[] { "simplify" }, aprms, targets: targets);
            Assert.Null(res[0].Error);
            Assert.Same(extBefore, inExt.Geom);
        }
    }

    [Fact]
    public void TryAndApplyGiveTheSameTriangles()
    {
        {
            var p = new SimplifyParams { Error = new(1, true), Strategy = SimplifyStrategy.KeepTexturesExact };
            var (sc, geode, geo) = Placed(Bumpy(), Meshes.Scale(2, 2, 2));
            // a 2-triangle decal far off in the same geode: too small to simplify, but part of the size "%" refers to
            var dv = new double[] { 30, 30, 0, 31, 30, 0, 31, 31, 0, 30, 31, 0 };
            var decal = new GeomData { Vertices = new VArray(dv, 3, "Vec3Array") };
            decal.Prims.Add(new Primitive("TRIANGLES", "elements", count: 6, indices: new[] { 0, 1, 2, 0, 2, 3 }, indexType: "DrawElementsUInt"));
            geode.Children.Add(new Node(Kind.Geometry, "osg::Geometry", "decal") { Geom = decal });
            var start = SoftRender.ParentMatrix(geode, new OptContext(sc).Parents);
            var tr = InspectorModel.TrySimplify(geode, p, new InspectorModel.TryContext(start));
            Assert.NotNull(tr);
            Assert.Equal(p, tr!.Params);
            Assert.True(tr.Diag > 2 * 10 * Math.Sqrt(2) * 1.5, $"diag {tr.Diag} should include the decal");
            Optimizer.Run(sc, new[] { "simplify" }, p.ToPassParams(), targets: new List<Node> { geode });
            Assert.True(tr.TrisAfter < tr.TrisBefore);
            Assert.Equal(geo.Geom!.Counts().tris, tr.TrisAfter);
        }
        {
            var p = new SimplifyParams { Within = false, Ratio = 0.3, Strategy = SimplifyStrategy.NewTexture };
            var (sc, holder, geo) = RebakeTests.TexturedSphere();
            var tr = InspectorModel.TrySimplify(holder, p);
            Assert.NotNull(tr);
            Optimizer.Run(sc, new[] { "simplify" }, p.ToPassParams(), targets: new List<Node> { holder });
            Assert.True(tr!.TrisAfter < tr.TrisBefore);
            Assert.Equal(geo.Geom!.Counts().tris, tr.TrisAfter);
        }
    }

    [Fact]
    public void UnitNoteNamesTheFileUnit()
    {
        var s = new Scene("x.flt", "flt");
        Assert.Equal("metres", InspectorModel.UnitNote(s));
        s.Info["unit_scale"] = 0.3048;
        s.Info["unit_name"] = "feet";
        Assert.Equal("metres (the file stores feet; converted on load)", InspectorModel.UnitNote(s));
    }
    static InspectorModel.TryResult DeadEnd(params string[] reasons) =>
        new() { TrisBefore = 100, TrisAfter = 100, Reasons = reasons.Length > 0 ? reasons.ToList() : new List<string> { "every collapse would exceed the max deviation" } };

    [Fact]
    public void KeepExactDeadEndSuggestsLetEdgesMerge()
    {
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.02, false), Strategy = SimplifyStrategy.KeepTexturesExact };
        var step = InspectorModel.NextStep(p, DeadEnd());
        Assert.NotNull(step);
        Assert.Equal("Try “Let edges merge”", step!.Value.Button);
        Assert.Equal(p with { Strategy = SimplifyStrategy.LetEdgesMerge }, step.Value.Next);
    }

    [Fact]
    public void WithinDeadEndSuggestsTheNextDistance()
    {
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.02, false), Strategy = SimplifyStrategy.LetEdgesMerge };
        var step = InspectorModel.NextStep(p, DeadEnd());
        Assert.Equal("Try “Within 5 cm”", step!.Value.Button);
        Assert.Equal(new ErrorTarget(0.05, false), step.Value.Next.Error);
        Assert.Equal(SimplifyStrategy.LetEdgesMerge, step.Value.Next.Strategy);
    }

    [Fact]
    public void RelativeLadderStepsUp()
    {
        var p = new SimplifyParams { Within = true, Error = new ErrorTarget(0.5, true), Strategy = SimplifyStrategy.LetEdgesMerge };
        var step = InspectorModel.NextStep(p, DeadEnd());
        Assert.Equal("Try “Within 1 % of size”", step!.Value.Button);
        Assert.Equal(new ErrorTarget(1, true), step.Value.Next.Error);
    }

    [Fact]
    public void TopOfTheLadderFallsThrough()
    {
        var rel = new SimplifyParams { Within = true, Error = new ErrorTarget(5, true), Strategy = SimplifyStrategy.LetEdgesMerge };
        Assert.Equal("Try “New texture”", InspectorModel.NextStep(rel, DeadEnd())!.Value.Button);
        var abs = new SimplifyParams { Within = true, Error = new ErrorTarget(1, false), Strategy = SimplifyStrategy.ShapeOnly };
        Assert.Null(InspectorModel.NextStep(abs, DeadEnd()));
    }

    [Fact]
    public void TooSmallHasNoSuggestion()
    {
        Assert.Null(InspectorModel.NextStep(new SimplifyParams(), DeadEnd("too small (< 4 triangles)")));
    }

    [Fact]
    public void SomethingSimplifiedHasNoSuggestion()
    {
        var r = DeadEnd();
        r.TrisAfter = 60;
        Assert.Null(InspectorModel.NextStep(new SimplifyParams(), r));
    }

    [Fact]
    public void NothingLineNamesTheReasons()
    {
        Assert.Equal("Nothing simplified: every collapse would exceed the max deviation; too small (< 4 triangles)",
            InspectorModel.NothingLine(DeadEnd("every collapse would exceed the max deviation", "too small (< 4 triangles)")));
        Assert.Equal("Nothing simplified: no collapse stayed within the target", InspectorModel.NothingLine(new InspectorModel.TryResult()));
    }
}
