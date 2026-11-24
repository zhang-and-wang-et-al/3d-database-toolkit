using SceneKit;
using Xunit;

/// <summary>Selection.Describe and History.OwnerFiles: which file a node lives in and how widely it is shared.</summary>
public class SelectionTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"sksel_{Guid.NewGuid():N}");
    public SelectionTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>master.flt -> tile_a.flt, tile_b.flt; both tiles -> tree.flt (one shared external).</summary>
    (Scene sc, Report rep) Build()
    {
        File.WriteAllBytes(Path.Combine(dir, "master.flt"), FltWriteTests.Grid(0, "master", false, "tile_a.flt", "tile_b.flt"));
        File.WriteAllBytes(Path.Combine(dir, "tile_a.flt"), FltWriteTests.Grid(2, "tile_a", false, "tree.flt"));
        File.WriteAllBytes(Path.Combine(dir, "tile_b.flt"), FltWriteTests.Grid(2, "tile_b", false, "tree.flt"));
        File.WriteAllBytes(Path.Combine(dir, "tree.flt"), FltWriteTests.Grid(2, "tree"));
        var sc = SceneIO.Load(Path.Combine(dir, "master.flt"));
        return (sc, Analyzer.Analyze(sc));
    }

    static Scene Sub(Scene sc, string file) => sc.Externals.First(kv => Path.GetFileName(kv.Key) == file).Value!;

    [Fact]
    public void OwnerFilesMapsNodesToTheirFile()
    {
        var (sc, _) = Build();
        var owner = History.OwnerFiles(sc);
        Assert.Equal(sc.Path, owner[sc.Root!]);
        Assert.EndsWith("tile_a.flt", owner[Sub(sc, "tile_a.flt").Root!]);
        Assert.EndsWith("tree.flt", owner[Sub(sc, "tree.flt").Root!]);
    }

    [Fact]
    public void SingleNodeInMasterIsNotShared()
    {
        var (sc, rep) = Build();
        var n = sc.IterNodes().First(x => x.Kind != Kind.External && x.Name == "master");
        var info = Selection.Describe(rep, sc, new[] { n });
        Assert.False(info.Shared);
        Assert.Equal(1, info.Files);
        Assert.Equal($"'master' ({OsgReader.Short(n.Cls)}) in master.flt", info.Text);
    }

    [Fact]
    public void SharedExternalCountsEveryReferencingFile()
    {
        var (sc, rep) = Build();
        var n = Sub(sc, "tree.flt").IterNodes().First(x => x.Name == "tree");
        var info = Selection.Describe(rep, sc, new[] { n });
        Assert.Equal(2, info.Files);
        Assert.Equal(2, info.Places);
        Assert.True(info.Shared);
        Assert.EndsWith("drawn at 2 places in 2 files", info.Text.Replace("place(s)", "places").Replace("file(s)", "files"));
        Assert.Contains("in tree.flt", info.Text);
    }

    [Fact]
    public void SeveralNodesListTheFirstTwoNames()
    {
        var (sc, rep) = Build();
        var nodes = Sub(sc, "tile_a.flt").IterNodes().Where(x => x.Kind != Kind.External).Take(3).ToList();
        Assert.Equal(3, nodes.Count);
        var info = Selection.Describe(rep, sc, nodes);
        Assert.Equal(3, info.Count);
        Assert.Equal($"3 nodes: '{Nm(nodes[0])}', '{Nm(nodes[1])}', … in 1 file(s)", info.Text);
        Assert.False(info.Shared);
        Assert.Equal("3 nodes in 1 file(s)", info.Short);
        var two = Selection.Describe(rep, sc, nodes.Take(2).ToList());
        Assert.Equal($"2 nodes: '{Nm(nodes[0])}', '{Nm(nodes[1])}' in 1 file(s)", two.Text);
    }

    [Fact]
    public void UnsharedNodesInTwoTilesNameTheirOwnFiles()
    {
        File.WriteAllBytes(Path.Combine(dir, "master.flt"), FltWriteTests.Grid(0, "master", false, "tile_0_0.flt", "tile_1_1.flt"));
        File.WriteAllBytes(Path.Combine(dir, "tile_0_0.flt"), FltWriteTests.Grid(2, "terr_a"));
        File.WriteAllBytes(Path.Combine(dir, "tile_1_1.flt"), FltWriteTests.Grid(2, "terr_b"));
        var sc = SceneIO.Load(Path.Combine(dir, "master.flt"));
        var rep = Analyzer.Analyze(sc);
        var a = Sub(sc, "tile_0_0.flt").IterNodes().First(x => x.Name == "terr_a");
        var b = Sub(sc, "tile_1_1.flt").IterNodes().First(x => x.Name == "terr_b");
        var info = Selection.Describe(rep, sc, new[] { a, b });
        Assert.False(info.Shared);
        Assert.Equal(2, info.Files);
        Assert.Equal("2 nodes: 'terr_a', 'terr_b' in 2 file(s)", info.Text);
        Assert.Equal("2 nodes in 2 file(s)", info.Short);
    }

    [Fact]
    public void SeveralNodesWithASharedOneSayWhereItIsDrawn()
    {
        var (sc, rep) = Build();
        var tree = Sub(sc, "tree.flt").IterNodes().First(x => x.Name == "tree");
        var tile = Sub(sc, "tile_a.flt").IterNodes().First(x => x.Name == "tile_a");
        var info = Selection.Describe(rep, sc, new[] { tile, tree });
        Assert.True(info.Shared);
        Assert.Equal(2, info.Files);   // tile_a.flt and tree.flt
        Assert.Equal("2 nodes: 'tile_a', 'tree' in 2 file(s), drawn at 2 places", info.Text);
    }

    static string Nm(Node n) => n.Name.Length > 0 ? n.Name : OsgReader.Short(n.Cls);

    [Fact]
    public void DetailsNameTheOwnerFileAndPlaces()
    {
        var (sc, rep) = Build();
        var n = Sub(sc, "tile_a.flt").IterNodes().First(x => x.Kind != Kind.External);
        var text = InspectorModel.Details(rep, new InspectorModel.FindingIndex(rep), n);
        Assert.Matches(@"file:\s+tile_a\.flt", text);
        Assert.Contains("drawn at:  ", text);
        Assert.Contains("place(s) in 1 file(s)", text);
    }

    [Fact]
    public void DescribeReusesTheIndexForTheSameReport()
    {
        var (sc, rep) = Build();
        var n = Sub(sc, "tree.flt").IterNodes().First(x => x.Name == "tree");
        int before = Selection.IndexBuilds;
        var a = Selection.Describe(rep, sc, new[] { n });
        var b = Selection.Describe(rep, sc, new[] { n });
        Assert.Equal(a.Text, b.Text);
        Assert.Equal((a.Places, a.Files), (b.Places, b.Files));
        Assert.Equal(before + 1, Selection.IndexBuilds);
    }

    [Fact]
    public void EmptySelectionIsTheWholeScene()
    {
        var (sc, rep) = Build();
        var info = Selection.Describe(rep, sc, Array.Empty<Node>());
        Assert.Equal("the whole scene", info.Text);
        Assert.Equal(0, info.Count);
    }

    /// <summary>A 10 x 10 textured grid (200 triangles) as one geometry under a group of the given name.</summary>
    static Node TexturedGridGroup(string name, Texture tex, double x0)
    {
        var V = new List<double>(); var UV = new List<double>(); var T = new List<int>();
        for (int j = 0; j <= 10; j++)
            for (int i = 0; i <= 10; i++) { V.AddRange(new[] { x0 + i, j, 0.0 }); UV.AddRange(new[] { i / 10.0, j / 10.0 }); }
        for (int j = 0; j < 10; j++)
            for (int i = 0; i < 10; i++)
            {
                int a = j * 11 + i, b = a + 1, c = a + 12, d = a + 11;
                T.AddRange(new[] { a, b, c, a, c, d });
            }
        var g = new GeomData { Vertices = new VArray(V.ToArray(), 3, "Vec3Array") };
        g.TexCoords[0] = new VArray(UV.ToArray(), 2, "Vec2Array");
        g.Prims.Add(new Primitive("TRIANGLES", "elements", count: T.Count, indices: T.ToArray(), indexType: "DrawElementsUShort"));
        var geo = new Node(Kind.Geometry, "osg::Geometry", "") { Geom = g, StateSet = new StateSet { Textures = { tex } } };
        return new Node(Kind.Group, "osg::Group", name) { Children = { geo } };
    }

    /// <summary>tree_oak_03 and tree_oak_04, both textured with leaves.png (two Texture objects, same file).</summary>
    static (Report rep, Node a, Node b) TwoTrees()
    {
        var a = TexturedGridGroup("tree_oak_03", new Texture { File = "leaves.png", Path = @"C:\textures\leaves.png" }, 0);
        var b = TexturedGridGroup("tree_oak_04", new Texture { File = "leaves.png", Path = @"C:\Textures\LEAVES.png" }, 20);
        var root = new Node(Kind.Group, "osg::Group", "root") { Children = { a, b } };
        return (Analyzer.Analyze(new Scene("x.osgt", "osgt") { Root = root }), a, b);
    }

    [Fact]
    public void CaptionNamesTrianglesAndTextures()
    {
        var (rep, a, _) = TwoTrees();
        Assert.Equal("tree_oak_03, 200 triangles, 1 texture", Selection.Caption(rep, new[] { a }));
    }

    [Fact]
    public void CaptionOfSeveralNodes()
    {
        var (rep, a, b) = TwoTrees();
        Assert.Equal("2 nodes, 400 triangles, 1 texture", Selection.Caption(rep, new[] { a, b }));
    }

    [Fact]
    public void CaptionOfNothing()
    {
        var (rep, _, _) = TwoTrees();
        Assert.Equal("Nothing selected", Selection.Caption(rep, Array.Empty<Node>()));
    }
}
