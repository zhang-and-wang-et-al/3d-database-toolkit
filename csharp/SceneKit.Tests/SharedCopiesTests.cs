using SceneKit;
using Xunit;

/// <summary>Copies of one object: cards / proxies baked once per variant and reused by every copy (S2), copies made
/// instances of one shared geometry (S3).</summary>
public class SharedCopiesTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"skshare_{Guid.NewGuid():N}");

    public SharedCopiesTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>World bounding box of everything drawn below a node (first path).</summary>
    static (double[] lo, double[] hi) WorldBox(Scene sc, Node n)
    {
        var pts = SimilarObjectsTests.WorldPoints(sc, n);
        var lo = new double[3]; var hi = new double[3];
        for (int c = 0; c < 3; c++) { lo[c] = pts.Min(p => p[c]); hi[c] = pts.Max(p => p[c]); }
        return (lo, hi);
    }

    static void SameBox((double[] lo, double[] hi) a, (double[] lo, double[] hi) b, double tol, string what)
    {
        for (int c = 0; c < 3; c++)
        {
            Assert.True(Math.Abs(a.lo[c] - b.lo[c]) <= tol, $"{what}: min[{c}] {a.lo[c]:F3} vs {b.lo[c]:F3}");
            Assert.True(Math.Abs(a.hi[c] - b.hi[c]) <= tol, $"{what}: max[{c}] {a.hi[c]:F3} vs {b.hi[c]:F3}");
        }
    }

    /// <summary>Every place a tree stood: (parent, index in the parent's children).</summary>
    static List<(Node parent, int index)> Places(Scene sc, List<Node> trees)
    {
        var o = new List<(Node, int)>();
        foreach (var t in trees)
            foreach (var p in sc.IterNodes())
            {
                int i = p.Children.IndexOf(t);
                if (i >= 0) { o.Add((p, i)); break; }
            }
        return o;
    }

    static List<RawImage> Atlases(Scene sc) =>
        sc.IterNodes().Where(n => n.StateSet != null).SelectMany(n => n.StateSet!.Textures).Where(t => t.Baked != null)
            .Select(t => t.Baked!).Distinct(ReferenceEqualityComparer.Instance).Cast<RawImage>().ToList();

    static long Summary(Scene sc, string key) => Convert.ToInt64(Analyzer.Analyze(sc, deepChecks: false, findings: false).Summary()[key]);

    static string Run(Scene sc, string pass, Dictionary<string, object>? prms = null, List<Node>? targets = null)
    {
        var (results, _, _) = Optimizer.Run(sc, new[] { pass }, prms, targets);
        Assert.Null(results[0].Error);
        return results[0].Message;
    }

    /// <summary>The six trees inside a scene read from OpenFlight (so it can be saved as .flt).</summary>
    (Scene sc, List<Node> trees) FltTrees()
    {
        var src = Path.Combine(dir, "master.flt");
        File.WriteAllBytes(src, FltWriteTests.Grid(0, "master"));
        var sc = SceneIO.Load(src);
        var (tsc, trees) = SimilarObjectsTests.SixTrees(dir);
        foreach (var c in tsc.Root!.Children) sc.Root!.Children.Add(c);
        return (sc, trees);
    }

    // ------------------------------------------------------------------------------ S2: shared cards / proxies

    [Theory]
    [InlineData("billboard")]
    [InlineData("crossed")]
    public void CardsAreBakedOncePerVariantAndPlacedOnEveryCopy(string mode)
    {
        var (sc, trees) = SimilarObjectsTests.SixTrees(dir);
        var boxes = trees.Select(t => WorldBox(sc, t)).ToList();
        var places = Places(sc, trees);
        var msg = Run(sc, "replace_cards", new() { ["replace_cards.mode"] = mode, ["replace_cards.texture"] = 64 }, trees);
        Assert.Contains("6 objects -> 2 atlases", msg);
        Assert.Equal(2, Atlases(sc).Count);
        for (int i = 0; i < trees.Count; i++)
        {
            var (p, k) = places[i];
            var now = p.Children[k];
            Assert.NotSame(trees[i], now);
            var box = WorldBox(sc, now);
            if (mode == "crossed") SameBox(boxes[i], box, 0.45, trees[i].Name);
            else
            {
                // an axial billboard turns to the viewer: its height and centre count, not its width
                Assert.InRange(box.lo[2] - boxes[i].lo[2], -0.05, 0.05);
                Assert.InRange(box.hi[2] - boxes[i].hi[2], -0.1, 0.1);
                for (int c = 0; c < 2; c++) Assert.InRange((box.lo[c] + box.hi[c]) / 2 - (boxes[i].lo[c] + boxes[i].hi[c]) / 2, -0.5, 0.5);
            }
        }
    }

    [Fact]
    public void WithoutShareEveryCopyGetsItsOwnAtlas()
    {
        var (sc, trees) = SimilarObjectsTests.SixTrees(dir);
        var msg = Run(sc, "replace_cards", new() { ["replace_cards.mode"] = "billboard", ["replace_cards.texture"] = 32, ["replace_cards.share"] = false }, trees);
        Assert.Equal(6, Atlases(sc).Count);
        Assert.DoesNotContain("atlases", msg);
    }

    [Fact]
    public void ProxiesAreBuiltOncePerVariant()
    {
        var (sc, trees) = SimilarObjectsTests.SixTrees(dir);
        var boxes = trees.Select(t => WorldBox(sc, t)).ToList();
        var places = Places(sc, trees);
        var msg = Run(sc, "replace_proxy", new() { ["replace_proxy.texture"] = 32, ["replace_proxy.voxels"] = 24, ["replace_proxy.ratio"] = 0.3 }, trees);
        Assert.Contains("6 objects -> 2 atlases", msg);
        Assert.Equal(2, Atlases(sc).Count);
        for (int i = 0; i < trees.Count; i++)
        {
            var (p, k) = places[i];
            SameBox(boxes[i], WorldBox(sc, p.Children[k]), 0.4, trees[i].Name);
        }
    }

    [Theory]
    [InlineData(".osgt", "crossed")]
    [InlineData(".osgt", "billboard")]
    [InlineData(".flt", "crossed")]
    [InlineData(".flt", "billboard")]
    public void SharedCardsSaveAndLoadBack(string ext, string mode)
    {
        var (sc, trees) = ext == ".flt" ? FltTrees() : SimilarObjectsTests.SixTrees(dir);
        Run(sc, "replace_cards", new() { ["replace_cards.mode"] = mode, ["replace_cards.texture"] = 32 }, trees);
        long tris = Summary(sc, "triangles");
        var box = WorldBox(sc, sc.Root!);
        var outp = Path.Combine(dir, "out" + ext);
        SceneIO.Save(sc, outp);
        var texDir = Path.Combine(dir, "out_textures");
        Assert.Equal(2, Directory.GetFiles(texDir).Length);                 // one atlas per variant
        var back = SceneIO.Load(outp);
        Assert.Equal(tris, Summary(back, "triangles"));
        SameBox(box, WorldBox(back, back.Root!), 1e-3, "scene");
    }

    // ------------------------------------------------------------------------------ S3: make instances

    [Fact]
    public void MakeInstancesKeepsOneGeometryPerVariant()
    {
        var (sc, trees) = SimilarObjectsTests.SixTrees(dir);
        var boxes = trees.Select(t => WorldBox(sc, t)).ToList();
        var places = Places(sc, trees);
        long tris = Summary(sc, "triangles"), bytes = Summary(sc, "geometry_bytes");
        var msg = Run(sc, "make_instances");
        Assert.Contains("4 copies", msg);
        Assert.Equal(tris, Summary(sc, "triangles"));
        Assert.True(Summary(sc, "geometry_bytes") < bytes * 0.5, $"{Summary(sc, "geometry_bytes")} of {bytes} bytes");
        Assert.Equal(5, sc.IterNodes().Count(n => n.Kind == Kind.Geometry));   // 2 variants x (trunk, crown) + the rock
        for (int i = 0; i < trees.Count; i++)
        {
            var (p, k) = places[i];
            SameBox(boxes[i], WorldBox(sc, p.Children[k]), 1e-6, trees[i].Name);
        }
        // the copies keep their look: every place still shows its own variant's texture
        var a = trees[0].Children[1].StateSet!.Textures[0];
        foreach (int i in new[] { 0, 1, 4 })
        {
            var (p, k) = places[i];
            var crown = SoftRender.GeometryItems(p.Children[k]).Single(x => x.node.Name == "crown").node;
            Assert.Same(a, crown.StateSet!.Textures[0]);
        }
    }

    [Fact]
    public void InstancesAreNotReportedOrInstancedAgain()
    {
        var (sc, _) = SimilarObjectsTests.SixTrees(dir);
        Run(sc, "make_instances");
        Assert.DoesNotContain(Analyzer.Analyze(sc).Findings, f => f.Code == "similar_objects");
        Assert.All(SimilarObjects.Find(sc), g => Assert.Equal(g.Members.Count, g.Variants.Count));   // one instanced member per variant
        Assert.StartsWith("0 copies", Run(sc, "make_instances"));
    }

    [Fact]
    public void MakeInstancesOnASelection()
    {
        var (sc, trees) = SimilarObjectsTests.SixTrees(dir);
        var msg = Run(sc, "make_instances", targets: new List<Node> { trees[0], trees[1], trees[2] });
        Assert.Contains("1 copies", msg);                                  // tree_2 -> tree_1; tree_3 has the other texture
        Assert.Equal(11, sc.IterNodes().Count(n => n.Kind == Kind.Geometry));
    }

    [Theory]
    [InlineData(".osgt")]
    [InlineData(".flt")]
    public void InstancesSaveAndLoadBack(string ext)
    {
        var (sc, _) = ext == ".flt" ? FltTrees() : SimilarObjectsTests.SixTrees(dir);
        Run(sc, "make_instances");
        long tris = Summary(sc, "triangles");
        var box = WorldBox(sc, sc.Root!);
        var outp = Path.Combine(dir, "inst" + ext);
        SceneIO.Save(sc, outp);
        var back = SceneIO.Load(outp);
        Assert.Equal(tris, Summary(back, "triangles"));
        SameBox(box, WorldBox(back, back.Root!), 1e-3, "scene");
        Assert.Equal(5, Drawn(back));                                     // shared objects stay shared (.flt: instance records)
        if (ext == ".flt")
        {
            Assert.Equal(2, Op(outp, FltFile.INST_DEF));                     // one definition per shared tree
            Assert.Equal(6, Op(outp, FltFile.INST_REF));                     // a reference per place
        }
    }

    static int Op(string path, int op) => FltFile.ReadRecords(File.ReadAllBytes(path)).Count(r => r.op == op);

    static int Drawn(Scene sc) => SoftRender.GeometryItems(sc.Root!).Select(x => x.node).Distinct(ReferenceEqualityComparer.Instance).Count();

    [Fact]
    public void FltObjectsMadeInstancesAreWrittenOnceAsInstanceDefinitions()
    {
        var (sc0, _) = FltTrees();
        var plain = Path.Combine(dir, "plain.flt");
        SceneIO.Save(sc0, plain);                                          // six tree objects of their own
        Assert.Equal(0, Op(plain, FltFile.INST_DEF));
        var sc = SceneIO.Load(plain);
        long tris = Summary(sc, "triangles");
        var box = WorldBox(sc, sc.Root!);
        Assert.Contains("4 copies", Run(sc, "make_instances"));
        var outp = Path.Combine(dir, "inst.flt");
        SceneIO.Save(sc, outp);
        Assert.Equal(2, Op(outp, FltFile.INST_DEF));
        Assert.Equal(6, Op(outp, FltFile.INST_REF));
        long before = new FileInfo(plain).Length, after = new FileInfo(outp).Length;
        Assert.True(after < before * 0.7, $"{after} of {before} bytes");
        var back = SceneIO.Load(outp);
        Assert.Equal(tris, Summary(back, "triangles"));
        SameBox(box, WorldBox(back, back.Root!), 1e-3, "scene");
        Assert.Equal(5, Drawn(back));
        // a file with instances saved again unchanged stays byte for byte
        var again = Path.Combine(dir, "again.flt");
        SceneIO.Save(back, again);
        Assert.Equal(File.ReadAllBytes(outp), File.ReadAllBytes(again));
    }

    [Fact]
    public void SharedDuplicateObjectsAreWrittenAsFltInstances()
    {
        var src = Path.Combine(dir, "master.flt");
        File.WriteAllBytes(src, FltWriteTests.Grid(0, "master"));
        var sc = SceneIO.Load(src);
        var b = OcclusionTests.Box(-1, -1, 0, 1, 1, 2);
        for (int i = 0; i < 3; i++)
        {
            var g = new GeomData { Vertices = new VArray((double[])b.V.Clone(), 3, "Vec3Array") };
            g.Prims.Add(new Primitive("TRIANGLES", "elements", count: b.T.Length, indices: (int[])b.T.Clone(), indexType: "DrawElementsUShort"));
            var geode = new Node(Kind.Geode, "osg::Geode", "box") { Children = { new Node(Kind.Geometry, "osg::Geometry", "box") { Geom = g } } };
            sc.Root!.Children.Add(new Node(Kind.Transform, "osg::MatrixTransform", $"place{i}") { Matrix = SimilarObjectsTests.Rz(30 * i, 5 * i, 0), Children = { geode } });
        }
        long tris = Summary(sc, "triangles");
        var box = WorldBox(sc, sc.Root!);
        Assert.Contains("geode copies shared", Run(sc, "share_duplicates"));
        var outp = Path.Combine(dir, "shared.flt");
        SceneIO.Save(sc, outp);
        Assert.Equal(1, Op(outp, FltFile.INST_DEF));
        Assert.Equal(3, Op(outp, FltFile.INST_REF));
        var back = SceneIO.Load(outp);
        Assert.Equal(tris, Summary(back, "triangles"));
        SameBox(box, WorldBox(back, back.Root!), 1e-3, "scene");
        Assert.Equal(1, Drawn(back));
    }

    [Fact]
    public void UndoBringsTheCopiesBack()
    {
        var (sc, trees) = SimilarObjectsTests.SixTrees(dir);
        var before = sc.IterNodes().Select(n => (n, n.Children.ToArray())).ToList();
        var box = WorldBox(sc, sc.Root!);
        int count = sc.IterNodes().Count();
        var snap = new Snapshot(sc, "make instances");
        Run(sc, "make_instances");
        snap.Restore();
        foreach (var (n, kids) in before) Assert.Equal(kids, n.Children);
        Assert.Equal(count, sc.IterNodes().Count());
        SameBox(box, WorldBox(sc, sc.Root!), 1e-9, "scene");
    }
}
