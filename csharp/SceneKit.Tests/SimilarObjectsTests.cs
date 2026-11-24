using SceneKit;
using Xunit;

/// <summary>Similar objects (S1): copies of the same object under different nodes, found at the highest level where
/// they still match, classified as instanced / identical / rigid / similar, with their texture variants.</summary>
public class SimilarObjectsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"sksim_{Guid.NewGuid():N}");

    public SimilarObjectsTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>Rotation about Z (degrees) and a translation.</summary>
    internal static double[] Rz(double deg, double tx, double ty, double tz = 0)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return new[] { c, -s, 0, tx, s, c, 0, ty, 0, 0, 1, tz, 0, 0, 0, 1 };
    }

    static double[] Apply(double[] m, double[] V)
    {
        var o = new double[V.Length];
        for (int i = 0; i < V.Length / 3; i++)
        {
            var (x, y, z) = Mat.Apply(m, V[3 * i], V[3 * i + 1], V[3 * i + 2]);
            o[3 * i] = x; o[3 * i + 1] = y; o[3 * i + 2] = z;
        }
        return o;
    }

    /// <summary>A solid colour RGBA PNG in the test folder, as a texture; <paramref name="cutOut"/>: the left half is
    /// transparent (alpha 0), else every texel is opaque.</summary>
    internal static Texture Png(string folder, string name, byte r, byte g, byte b, bool cutOut = false)
    {
        var img = new RawImage(8, 8, 4);
        for (int i = 0; i < 64; i++) { img.Pixels[4 * i] = r; img.Pixels[4 * i + 1] = g; img.Pixels[4 * i + 2] = b; img.Pixels[4 * i + 3] = (byte)(cutOut && i % 8 < 4 ? 0 : 255); }
        var p = Path.Combine(folder, name);
        File.WriteAllBytes(p, ImageCodecs.WritePng(img));
        return new Texture { File = name, Path = p, Exists = true, Width = 8, Height = 8, Channels = 4, Fmt = "png" };
    }

    /// <summary>A lumpy crown (not rotationally symmetric, so a wrong rotation shows) with UVs, around (0.3, 0, 4).</summary>
    static (double[] V, int[] T, double[] UV) Crown(double jitter = 0)
    {
        var (V, T, UV) = RebakeTests.Sphere(16, 8, 2.0);
        for (int i = 0; i < V.Length / 3; i++)
        {
            double x = V[3 * i], y = V[3 * i + 1], z = V[3 * i + 2];
            double f = 1 + 0.15 * Math.Sin(3 * Math.Atan2(y, x)) * Math.Cos(z) + jitter * Math.Sin(7.3 * i);
            V[3 * i] = x * f + 0.3; V[3 * i + 1] = y * f; V[3 * i + 2] = z * f + 4;
        }
        return (V, T, UV);
    }

    static Node Geo(string name, double[] V, int[] T, double[]? UV, StateSet ss)
    {
        var g = new GeomData { Vertices = new VArray(V, 3, "Vec3Array") };
        if (UV != null) g.TexCoords[0] = new VArray(UV, 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Length, indices: T, indexType: "DrawElementsUShort"));
        return new Node(Kind.Geometry, "osg::Geometry", name) { Geom = g, StateSet = ss };
    }

    /// <summary>A tree geode (trunk + textured crown), its vertices transformed by <paramref name="place"/> (pre-transformed
    /// copies) and its own state sets (the crown's uses the given texture object).</summary>
    internal static Node Tree(string name, double[] place, Texture crownTex, double jitter = 0)
    {
        var trunk = OcclusionTests.Box(-0.3, -0.3, 0, 0.3, 0.3, 3);
        var (cv, ct, cuv) = Crown(jitter);
        return new Node(Kind.Geode, "osg::Geode", name)
        {
            Children =
            {
                Geo("trunk", Apply(place, trunk.V), trunk.T, null, new StateSet { MaterialName = "bark", Diffuse = new[] { 0.5, 0.3, 0.1, 1 } }),
                Geo("crown", Apply(place, cv), ct, cuv, new StateSet { MaterialName = "leaves", Diffuse = new[] { 1.0, 1, 1, 1 }, Textures = { crownTex } }),
            },
        };
    }

    /// <summary>Six rigid copies of a tree (rotated about Z and moved, vertices pre-transformed, two of them below a
    /// rotated transform), crowns textured with A (trees 1, 2, 5) or B (3, 4, 6), plus a rock of the trunk's topology.</summary>
    internal static (Scene sc, List<Node> trees) SixTrees(string folder)
    {
        var a = Png(folder, "leaves_a.png", 30, 160, 40);
        var b = Png(folder, "leaves_b.png", 200, 120, 20);
        var trees = new List<Node>
        {
            Tree("tree_1", Rz(0, 0, 0), a), Tree("tree_2", Rz(40, 15, 3), a), Tree("tree_3", Rz(115, -8, 22), b),
            Tree("tree_4", Rz(200, 30, -12), b), Tree("tree_5", Rz(30, 5, 0), a), Tree("tree_6", Rz(-60, -5, 4), b),
        };
        var block = new Node(Kind.Transform, "osg::MatrixTransform", "block") { Matrix = Rz(90, 50, 50), Children = { trees[4], trees[5] } };
        var rock = OcclusionTests.Box(99, -1, 0, 101, 1, 1);
        var rockGeode = new Node(Kind.Geode, "osg::Geode", "rock") { Children = { Geo("rock", rock.V, rock.T, null, new StateSet { MaterialName = "stone", Diffuse = new[] { 0.5, 0.5, 0.5, 1 } }) } };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { trees[0], trees[1], trees[2], trees[3], block, rockGeode } };
        return (new Scene(Path.Combine(folder, "trees.osgt"), "osgt") { Root = root }, trees);
    }

    /// <summary>World positions of every vertex below a node, drawable after drawable (first path).</summary>
    internal static List<double[]> WorldPoints(Scene sc, Node n)
    {
        var rep = Analyzer.Analyze(sc, deepChecks: false, findings: false);
        var o = new List<double[]>();
        foreach (var (g, w, _) in SoftRender.GeometryItems(n, start: SoftRender.ParentMatrix(n, rep.Parents)))
        {
            var V = g.Geom!.Vertices!.Data;
            for (int i = 0; i < V.Length / 3; i++) { var (x, y, z) = Mat.Apply(w, V[3 * i], V[3 * i + 1], V[3 * i + 2]); o.Add(new[] { x, y, z }); }
        }
        return o;
    }

    [Theory]
    [InlineData("tree_12", "tree")]
    [InlineData("Tree_12.flt", "tree")]
    [InlineData("tree.001", "tree")]
    [InlineData("Tree #3", "tree")]
    [InlineData("tree (2)", "tree")]
    [InlineData("oak-tree", "oak-tree")]
    [InlineData("12", "")]
    public void NamesAreNormalised(string name, string expected) => Assert.Equal(expected, SimilarObjects.NormalizeName(name));

    [Fact]
    public void RigidCopiesWithTwoTextureVariantsFormOneGroup()
    {
        var (sc, trees) = SixTrees(dir);
        var groups = SimilarObjects.Find(sc);
        var g = Assert.Single(groups);                                   // the rock (trunk topology, other size) and the trunks / crowns (covered) are not reported
        Assert.Equal(Similarity.Rigid, g.Class);
        Assert.Equal(6, g.Members.Count);
        Assert.Equal(trees.ToHashSet(), g.Nodes.ToHashSet());
        Assert.Equal("tree", g.Name);
        Assert.Equal(2, g.Variants.Count);
        Assert.All(g.Variants, v => Assert.Equal(3, v.Count));
        Assert.Contains(g.Variants, v => v.Label == "leaves_a.png" && v.Members.Select(m => m.Node).ToHashSet().SetEquals(new[] { trees[0], trees[1], trees[4] }));
        Assert.Contains("2 variants", g.Differences);
        Assert.Contains("3x leaves_a.png", g.Differences);
        Assert.Contains("pre-transformed", g.Differences);
        long each = trees[0].Children.Sum(c => c.Geom!.Counts().tris);
        Assert.Equal(each, g.TrisEach);
        Assert.Equal(6 * each, g.TrisTotal);
        Assert.Equal(1, g.Shapes);
        Assert.All(g.Members, m => Assert.Equal(0, m.Shape));
        Assert.Equal("root/block/tree_5", g.Members.Single(m => m.Node == trees[4]).Path);
        // the fit maps the first member's vertices onto every copy's (vertex correspondence by index)
        var first = g.Members[0];
        var p0 = WorldPoints(sc, first.Node);
        foreach (var m in g.Members)
        {
            Assert.NotNull(m.Fit);
            var pm = WorldPoints(sc, m.Node);
            for (int i = 0; i < p0.Count; i += 7)
            {
                var (x, y, z) = Mat.Apply(m.Fit!, p0[i][0], p0[i][1], p0[i][2]);
                Assert.Equal(pm[i][0], x, 6); Assert.Equal(pm[i][1], y, 6); Assert.Equal(pm[i][2], z, 6);
            }
            // the position is the world centre of the copy
            var lo = new double[3]; var hi = new double[3];
            for (int c = 0; c < 3; c++) { lo[c] = pm.Min(p => p[c]); hi[c] = pm.Max(p => p[c]); }
            for (int c = 0; c < 3; c++) Assert.Equal((lo[c] + hi[c]) / 2, m.Position[c], 6);
        }
    }

    [Fact]
    public void CopiesOfTheSameVertexDataAreIdentical()
    {
        var (_, _) = SixTrees(dir);
        var t = Png(dir, "t.png", 10, 200, 10);
        var root = new Node(Kind.Group, "osg::Group", "root");
        for (int i = 0; i < 3; i++)
            root.Children.Add(new Node(Kind.Transform, "osg::MatrixTransform", $"place{i}") { Matrix = Rz(30 * i, 10 * i, 0), Children = { Tree("tree", Rz(0, 0, 0), t) } });
        var sc = new Scene("x.osgt", "osgt") { Root = root };
        var g = Assert.Single(SimilarObjects.Find(sc));
        Assert.Equal(Similarity.Identical, g.Class);
        Assert.All(g.Members, m => Assert.Equal(Kind.Transform, m.Node.Kind));   // the highest level that matches
        Assert.Single(g.Variants);
        Assert.DoesNotContain("pre-transformed", g.Differences);
    }

    [Fact]
    public void ACopyUnderATransformMatchesAPreTransformedOne()
    {
        var t = Png(dir, "t.png", 10, 200, 10);
        var placed = new Node(Kind.Transform, "osg::MatrixTransform", "place") { Matrix = Rz(70, 20, 5), Children = { Tree("tree_2", Rz(0, 0, 0), t) } };
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { Tree("tree_1", Rz(30, 0, 0), t), placed } };
        var g = Assert.Single(SimilarObjects.Find(new Scene("x.osgt", "osgt") { Root = root }));
        Assert.Equal(Similarity.Rigid, g.Class);
        Assert.Contains(placed, g.Nodes);
    }

    [Fact]
    public void SharedNodesAreInstanced()
    {
        var t = Png(dir, "t.png", 10, 200, 10);
        var tree = Tree("tree", Rz(0, 0, 0), t);
        var root = new Node(Kind.Group, "osg::Group", "root");
        for (int i = 0; i < 4; i++) root.Children.Add(new Node(Kind.Transform, "osg::MatrixTransform", $"place{i}") { Matrix = Rz(0, 10 * i, 0), Children = { tree } });
        var g = Assert.Single(SimilarObjects.Find(new Scene("x.osgt", "osgt") { Root = root }));
        Assert.Equal(Similarity.Instanced, g.Class);
        Assert.Equal(4 * g.TrisEach, g.TrisTotal);
        Assert.Contains("shared", g.Differences);
    }

    [Fact]
    public void ACopyWithAnotherShapeMakesTheGroupSimilar()
    {
        var t = Png(dir, "t.png", 10, 200, 10);
        var root = new Node(Kind.Group, "osg::Group", "root")
        {
            Children = { Tree("tree_1", Rz(0, 0, 0), t), Tree("tree_2", Rz(50, 10, 0), t), Tree("tree_3", Rz(0, 20, 0), t, jitter: 0.01) },
        };
        var g = Assert.Single(SimilarObjects.Find(new Scene("x.osgt", "osgt") { Root = root }));
        Assert.Equal(Similarity.Similar, g.Class);
        Assert.Equal(2, g.Shapes);
        Assert.Equal(Similarity.Similar, g.Members.Single(m => m.Node.Name == "tree_3").Match);
        Assert.Contains("2 shapes", g.Differences);
    }

    [Fact]
    public void CriteriaCanBeSwitched()
    {
        var (sc, trees) = SixTrees(dir);
        // texture on: the variants become groups of their own
        var byTexture = SimilarObjects.Find(sc, new SimilarCriteria { Texture = true });
        Assert.Equal(2, byTexture.Count);
        Assert.All(byTexture, g => Assert.Single(g.Variants));
        // name on: a renamed copy drops out (its trunk and crown still match the others' by name: not covered, reported)
        trees[3].Name = "bush";
        var byName = SimilarObjects.Find(sc, new SimilarCriteria { Name = true });
        var treeGroup = Assert.Single(byName, g => g.Name == "tree");
        Assert.Equal(5, treeGroup.Members.Count);
        Assert.DoesNotContain(byName, g => g.Nodes.Contains(trees[3]));
        Assert.Contains(byName, g => g.Name == "crown" && g.Members.Count == 6);
        Assert.Empty(SimilarObjects.Find(sc, new SimilarCriteria { Name = false, Size = false, Topology = false, Texture = false }));
    }

    [Fact]
    public void FindingPointsAtTheGroupAndReplacesDuplicateGeometry()
    {
        var (sc, trees) = SixTrees(dir);
        // two more copies with byte-identical vertex data and the same state set objects: duplicate_geometry material
        var t = trees[0].Children[1].StateSet!.Textures[0];
        var x1 = Tree("tree_7", Rz(0, 60, 0), t);
        var x2 = Tree("tree_8", Rz(0, 60, 0), t);
        for (int i = 0; i < 2; i++) x2.Children[i].StateSet = x1.Children[i].StateSet;
        sc.Root!.Children.Add(new Node(Kind.Transform, "osg::MatrixTransform", "a") { Matrix = Rz(0, 0, 40), Children = { x1 } });
        sc.Root.Children.Add(new Node(Kind.Transform, "osg::MatrixTransform", "b") { Matrix = Rz(0, 0, -40), Children = { x2 } });
        var rep = Analyzer.Analyze(sc);
        var f = Assert.Single(rep.Findings, x => x.Code == "similar_objects");
        Assert.Equal("make_instances", f.Fix);
        Assert.Equal(8, f.Nodes.Count);
        Assert.Contains("tree", f.Title);
        Assert.DoesNotContain(rep.Findings, x => x.Code == "duplicate_geometry");   // folded into the similar objects finding
        Assert.Contains("same vertex data", f.Message);
        Assert.DoesNotContain(Analyzer.Analyze(sc, deepChecks: false).Findings, x => x.Code == "similar_objects");
    }

    /// <summary>Three similar (not rigid) copies of a 5 m tree under the given name, crowns textured with <paramref name="tex"/>;
    /// <paramref name="scale"/> stretches every copy.</summary>
    Scene Copies(string name, Texture tex, double scale = 1)
    {
        var root = new Node(Kind.Group, "osg::Group", "root");
        for (int i = 0; i < 3; i++)
            root.Children.Add(new Node(Kind.Transform, "osg::MatrixTransform", $"{name}_{i}")
            {
                Matrix = Rz(0, 10 * i, 0),
                Children = { Tree($"{name}_{i}", new[] { scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, 1 }, tex, jitter: 0.01 * i) },
            });
        return new Scene("x.osgt", "osgt") { Root = root };
    }

    static Texture Opaque(Texture t) { t.Channels = 3; return t; }

    [Fact]
    public void ShortGroupWithPlantNameSuggestsCards()
    {
        var rep = Analyzer.Analyze(Copies("tree", Opaque(Png(dir, "t.png", 10, 200, 10))));
        var f = Assert.Single(rep.Findings, x => x.Code == "similar_objects");
        Assert.Equal("replace_cards", f.Fix);
    }

    [Fact]
    public void TallGroupWithAlphaIsNotFoliage()
    {
        var rep = Analyzer.Analyze(Copies("obj", Png(dir, "t.png", 10, 200, 10, cutOut: true), scale: 10));   // the crown reaches ~6 m, so 60 m
        var g = Assert.Single(SimilarObjects.Find(rep));
        Assert.False(SimilarObjects.FoliageLike(rep, g));
        var f = Assert.Single(rep.Findings, x => x.Code == "similar_objects");
        Assert.Equal("simplify", f.Fix);
        Assert.Contains("not foliage-like", f.Message);
    }

    [Fact]
    public void ShortGroupWithAlphaTextureIsFoliage()
    {
        var rep = Analyzer.Analyze(Copies("obj", Png(dir, "t.png", 10, 200, 10, cutOut: true)));
        var g = Assert.Single(SimilarObjects.Find(rep));
        Assert.True(SimilarObjects.FoliageLike(rep, g));
    }

    [Fact]
    public void ShortGroupWithOpaqueRgbaTextureIsNotFoliage()
    {
        // OpenFlight / Trian libraries keep opaque textures as RGBA: a 5 m house is no foliage
        var rep = Analyzer.Analyze(Copies("house", Png(dir, "wall.png", 200, 180, 150)));
        var g = Assert.Single(SimilarObjects.Find(rep));
        Assert.False(SimilarObjects.FoliageLike(rep, g));
    }

    [Theory]
    [InlineData("Tree_01", true)]
    [InlineData("oak_tree", true)]
    [InlineData("hedge", true)]
    [InlineData("OakTree", true)]
    [InlineData("BigBush", true)]
    [InlineData("street_lamp", false)]
    [InlineData("ambush", false)]
    public void FoliageNameStartsAtAWordOrUnderscore(string name, bool foliage) =>
        Assert.Equal(foliage, SimilarObjects.FoliageName.IsMatch(name));

    [Fact]
    public void AlphaTextureInsideAnExternalFileCountsAsFoliage()
    {
        var t = Png(dir, "t.png", 10, 200, 10, cutOut: true);
        var root = new Node(Kind.Group, "osg::Group", "root");
        for (int i = 0; i < 3; i++)
        {
            var ext = new Node(Kind.External, "osg::ProxyNode", "obj") { Matrix = Rz(0, 10 * i, 0) };
            ext.Externals = new List<Node> { Tree("obj", new[] { 1.0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, t, jitter: 0.01 * i) };
            root.Children.Add(ext);
        }
        var rep = Analyzer.Analyze(new Scene("x.osgt", "osgt") { Root = root });
        var g = new SimilarGroup { Name = "obj" };
        foreach (var n in root.Children) g.Members.Add(new SimilarMember { Node = n });
        Assert.True(SimilarObjects.FoliageLike(rep, g));
    }

    [Fact]
    public void TerrainTilesSuggestSimplifyNotCards()
    {
        var root = new Node(Kind.Group, "osg::Group", "root");
        for (int i = 0; i < 3; i++)
        {
            var slab = OcclusionTests.Box(0, 0, 0, 500 + 15 * i, 500, 2);
            root.Children.Add(new Node(Kind.Geode, "osg::Geode", $"tile_{i}") { Children = { Geo("t", slab.V, slab.T, null, new StateSet()) } });
        }
        var rep = Analyzer.Analyze(new Scene("x.osgt", "osgt") { Root = root });
        var f = Assert.Single(rep.Findings, x => x.Code == "similar_objects");
        Assert.Equal("simplify", f.Fix);
    }

    [Fact]
    public void ManyCopiesAreFoundQuickly()
    {
        var t = Png(dir, "t.png", 10, 200, 10);
        var root = new Node(Kind.Group, "osg::Group", "root");
        var rnd = new Random(3);
        for (int i = 0; i < 3000; i++)
        {
            var tree = Tree($"tree_{i}", Rz(rnd.NextDouble() * 360, rnd.NextDouble() * 1000, rnd.NextDouble() * 1000), t);
            var rock = OcclusionTests.Box(0, 0, 0, 1 + i * 0.01, 1, 1);   // many sizes: several groups of similar rocks
            root.Children.Add(tree);
            root.Children.Add(new Node(Kind.Geode, "osg::Geode", $"rock{i}") { Children = { Geo("r", rock.V, rock.T, null, new StateSet()) } });
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var groups = SimilarObjects.Find(new Scene("x.osgt", "osgt") { Root = root });
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"{sw.Elapsed.TotalSeconds:F1} s");
        var g = Assert.Single(groups, x => x.Name == "tree");
        Assert.Equal(3000, g.Members.Count);
        Assert.All(groups.Where(x => x.Name == "rock"), x => Assert.Equal(Similarity.Similar, x.Class));
        Assert.Equal(Similarity.Rigid, g.Class);
    }
}
