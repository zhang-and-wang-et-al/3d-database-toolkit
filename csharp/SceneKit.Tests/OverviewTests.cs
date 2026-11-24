using SceneKit;
using Xunit;

/// <summary>C: overviews for huge scenes - treemap layout of the scene by cost, findings grouped and filtered.</summary>
public class OverviewTests
{
    /// <summary>root > {big (600 tris) > {b1 400, b2 200}, small (300), tiny (100), empty (0)}.</summary>
    static (Report rep, Node root, Node big, Node b1, Node small) Make()
    {
        var b1 = new Node(Kind.Geode, name: "b1");
        var b2 = new Node(Kind.Geode, name: "b2");
        var big = new Node(Kind.Group, name: "big") { Children = { b1, b2 } };
        var small = new Node(Kind.Geode, name: "small");
        var tiny = new Node(Kind.Geode, name: "tiny");
        var empty = new Node(Kind.Group, name: "empty");
        var root = new Node(Kind.Group, name: "root") { Children = { small, big, tiny, empty } };
        var rep = new Report(new Scene("x.osgt", "osgt") { Root = root });
        rep.Stats[b1] = new NodeStats { Tris = 400, Draws = 1, TexBytes = 10 };
        rep.Stats[b2] = new NodeStats { Tris = 200, Draws = 5, TexBytes = 10 };
        rep.Stats[big] = new NodeStats { Tris = 600, Draws = 6, TexBytes = 20 };
        rep.Stats[small] = new NodeStats { Tris = 300, Draws = 1, TexBytes = 500 };
        rep.Stats[tiny] = new NodeStats { Tris = 100, Draws = 3, TexBytes = 0 };
        rep.Stats[root] = new NodeStats { Tris = 1000, Draws = 10, TexBytes = 520 };
        rep.Parents[b1] = new() { big }; rep.Parents[b2] = new() { big };
        foreach (var n in new[] { big, small, tiny, empty }) rep.Parents[n] = new() { root };
        return (rep, root, big, b1, small);
    }

    [Fact]
    public void BelowCountsAFindingOncePerAncestor()
    {
        var (rep, root, big, b1, _) = Make();
        var b2 = big.Children[1];
        var b3 = new Node(Kind.Geode, name: "b3");
        big.Children.Add(b3); rep.Parents[b3] = new() { big };
        rep.Findings.Add(new Finding("x", "high", "t", "m", new[] { b1, b2, b3 }));
        var idx = new InspectorModel.FindingIndex(rep);
        Assert.Equal(1, idx.Below[big]);
        Assert.Equal(1, idx.Below[root]);
    }

    static double Area(InspectorModel.MapRect r) => r.W * r.H;

    [Fact]
    public void AreasFollowTheChosenCost()
    {
        var (rep, root, big, b1, small) = Make();
        var rects = InspectorModel.Treemap(rep, root, "tris", 400, 300);
        var top = rects.Where(r => r.Depth == 1).ToList();
        Assert.Equal(3, top.Count);                                    // the empty group gets no rectangle
        double total = 400 * 300;
        Assert.InRange(Area(top.Single(r => r.Node == big)) / total, 0.59, 0.61);
        Assert.InRange(Area(top.Single(r => r.Node == small)) / total, 0.29, 0.31);
        // a different cost gives a different map
        var byTex = InspectorModel.Treemap(rep, root, "tex", 400, 300).Where(r => r.Depth == 1).ToList();
        Assert.True(Area(byTex.Single(r => r.Node == small)) > Area(byTex.Single(r => r.Node == big)));
    }

    [Fact]
    public void ChildrenStayInsideTheirParentAndDoNotOverlap()
    {
        var (rep, root, big, _, _) = Make();
        var rects = InspectorModel.Treemap(rep, root, "tris", 400, 300);
        var bigRect = rects.Single(r => r.Node == big);
        var kids = rects.Where(r => r.Depth == 2).ToList();
        Assert.Equal(2, kids.Count);
        foreach (var k in kids)
        {
            Assert.True(k.X >= bigRect.X - 1e-9 && k.Y >= bigRect.Y - 1e-9);
            Assert.True(k.X + k.W <= bigRect.X + bigRect.W + 1e-9 && k.Y + k.H <= bigRect.Y + bigRect.H + 1e-9);
        }
        var top = rects.Where(r => r.Depth == 1).ToList();
        for (int i = 0; i < top.Count; i++)
            for (int j = i + 1; j < top.Count; j++)
            {
                var a = top[i]; var b = top[j];
                double ox = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X), oy = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
                Assert.False(ox > 1e-9 && oy > 1e-9, $"{a.Node.Name} overlaps {b.Node.Name}");
            }
    }

    [Fact]
    public void ParentsKeepAStripForTheirName()
    {
        var (rep, root, big, _, _) = Make();
        var rects = InspectorModel.Treemap(rep, root, "tris", 400, 300, header: 14);
        var bigRect = rects.Single(r => r.Node == big);
        Assert.All(rects.Where(r => r.Depth == 2), k => Assert.True(k.Y >= bigRect.Y + 14));
    }

    [Fact]
    public void RectanglesAreNotTooThin()
    {
        // squarified layout: no sliver for a cost share of 10 %
        var (rep, root, _, _, _) = Make();
        foreach (var r in InspectorModel.Treemap(rep, root, "tris", 400, 300).Where(r => r.Depth == 1))
            Assert.True(Math.Max(r.W / r.H, r.H / r.W) < 4, $"{r.Node.Name}: {r.W:F0} x {r.H:F0}");
    }

    [Fact]
    public void TinyRectanglesAndDepthAreLimited()
    {
        var (rep, root, _, b1, _) = Make();
        Assert.DoesNotContain(InspectorModel.Treemap(rep, root, "tris", 400, 300, maxDepth: 1), r => r.Node == b1);
        Assert.DoesNotContain(InspectorModel.Treemap(rep, root, "tris", 40, 30, minSide: 20), r => r.Node == b1);
        Assert.True(InspectorModel.Treemap(rep, root, "tris", 400, 300, maxRects: 2).Count <= 2);
    }

    [Fact]
    public void HitTestFindsTheDeepestRectangle()
    {
        var (rep, root, big, b1, _) = Make();
        var rects = InspectorModel.Treemap(rep, root, "tris", 400, 300);
        var r1 = rects.Single(r => r.Node == b1);
        Assert.Same(b1, InspectorModel.HitTest(rects, r1.X + r1.W / 2, r1.Y + r1.H / 2)!.Node);
        Assert.Null(InspectorModel.HitTest(rects, -5, -5));
    }

    static Finding F(string code, string sev, long tris = 0, long bytes = 0) => new(code, sev, code + " title", "msg", savingTris: tris, savingBytes: bytes);

    [Fact]
    public void FindingsAreGroupedByTypeWorstFirst()
    {
        var list = new List<Finding>
        {
            F("vertex_cache", "low"), F("heavy_geometry", "medium", tris: 500), F("vertex_cache", "low"),
            F("missing_texture", "high"), F("heavy_geometry", "high", tris: 1000), F("vertex_cache", "low"),
        };
        var groups = InspectorModel.GroupFindings(list);
        Assert.Equal(new[] { "heavy_geometry", "missing_texture", "vertex_cache" }, groups.Select(g => g.Code).ToArray());
        var heavy = groups[0];
        Assert.Equal(2, heavy.Count);
        Assert.Equal("high", heavy.Worst);
        Assert.Equal(1500, heavy.Tris);
        Assert.Equal("Heavy geometry", heavy.Label);
        Assert.Equal(3, groups[2].Count);
    }

    [Theory]
    [InlineData(null, "info", 6)]
    [InlineData("vertex_cache", "info", 3)]
    [InlineData(null, "medium", 3)]
    [InlineData("heavy_geometry", "high", 1)]
    public void FindingsFilter(string? code, string minSeverity, int expected)
    {
        var list = new List<Finding>
        {
            F("vertex_cache", "low"), F("heavy_geometry", "medium"), F("vertex_cache", "low"),
            F("missing_texture", "high"), F("heavy_geometry", "high"), F("vertex_cache", "low"),
        };
        Assert.Equal(expected, InspectorModel.FilterFindings(list, code, minSeverity).Count());
    }

    static Node TexHolder(string name, Texture t) => new(Kind.Geode, "osg::Geode", name) { StateSet = new StateSet { Textures = { t } } };

    [Fact]
    public void TextureTableGroupsTheSameFile()
    {
        var master = new Node(Kind.Group, name: "master");
        var ext = new Node(Kind.External, name: "ext");   // stand-ins of the two loaded files live in Externals
        master.Children.Add(ext);
        var a = TexHolder("a", new Texture { File = "w.png", Path = @"C:\data\w.png", Exists = true, Width = 64, Height = 64 });
        var b = TexHolder("b", new Texture { File = "w.png", Path = @"C:\DATA\W.PNG", Exists = true, Width = 64, Height = 64, Unit = 1 });
        ext.Externals = new List<Node> { a, b };
        var sc = new Scene("master.flt", "flt") { Root = master };
        sc.Externals["a.flt"] = new Scene("a.flt", "flt") { Root = a };
        sc.Externals["b.flt"] = new Scene("b.flt", "flt") { Root = b };
        var rep = Analyzer.Analyze(sc);
        var row = Assert.Single(InspectorModel.TextureTable(rep));
        Assert.Equal(2, row.Texs.Count);
        Assert.Equal(2, row.Users);
        Assert.Equal(2, row.Files);
        Assert.Equal("0,1", row.Unit);
    }

    [Fact]
    public void TextureTableBuildsTheOwnerIndexOnce()
    {
        var a = TexHolder("a", new Texture { File = "x.png", Path = "/data/x.png", Width = 8, Height = 8 });
        var rep = Analyzer.Analyze(new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, name: "r") { Children = { a } } });
        Selection.IndexBuilds = 0;
        InspectorModel.TextureTable(rep);
        InspectorModel.TextureTable(rep, "x");
        Assert.Equal(1, Selection.IndexBuilds);
    }

    [Fact]
    public void UsersCountsDistinctNodes()
    {
        var t1 = new Texture { File = "x.png", Path = "/data/x.png", Width = 8, Height = 8 };
        var t2 = new Texture { File = "x.png", Path = "/data/x.png", Width = 8, Height = 8, Unit = 1 };
        var n = new Node(Kind.Geode, "osg::Geode", "n") { StateSet = new StateSet { Textures = { t1, t2 } } };
        var rep = Analyzer.Analyze(new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, name: "r") { Children = { n } } });
        var row = Assert.Single(InspectorModel.TextureTable(rep));
        Assert.Equal(1, row.Users);
        Assert.Single(row.UserNodes);
    }

    [Fact]
    public void TexturesWithoutPathStaySeparate()
    {
        var a = TexHolder("a", new Texture { File = "x.png", Width = 8, Height = 8 });
        var b = TexHolder("b", new Texture { File = "x.png", Width = 8, Height = 8 });
        var rep = Analyzer.Analyze(new Scene("x.osgt", "osgt") { Root = new Node(Kind.Group, name: "r") { Children = { a, b } } });
        var rows = InspectorModel.TextureTable(rep);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Single(r.Texs));
    }
}
