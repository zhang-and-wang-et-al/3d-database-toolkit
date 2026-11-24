using SceneKit;
using Xunit;

public class SortTests
{
    // root with three children; stats chosen so every sort key gives a different order
    static (Report rep, Node root, Node a, Node b, Node c) Make()
    {
        var a = new Node(Kind.Geode, name: "beta");
        var b = new Node(Kind.Geode, name: "Alpha");
        var c = new Node(Kind.Geode, name: "gamma");
        var root = new Node(Kind.Group, name: "root") { Children = { a, b, c } };
        var rep = new Report(new Scene("x.osgt", "osgt") { Root = root });
        rep.Stats[a] = new NodeStats { Tris = 100, VisTris = 10, Verts = 300, Draws = 1, GeoBytes = 5, TexBytes = 900 };
        rep.Stats[b] = new NodeStats { Tris = 50, VisTris = 50, Verts = 100, Draws = 7, GeoBytes = 900, TexBytes = 0 };
        rep.Stats[c] = new NodeStats { Tris = 100, VisTris = 30, Verts = 200, Draws = 3, GeoBytes = 50, TexBytes = 10 };
        rep.Stats[root] = new NodeStats { Tris = 250, GeoBytes = 955, TexBytes = 910 };
        rep.Instances[c] = 4;
        return (rep, root, a, b, c);
    }

    static string Names(IEnumerable<Node> nodes) => string.Join(",", nodes.Select(n => n.Name));

    [Theory]
    [InlineData("scene", "beta,Alpha,gamma")]
    [InlineData("tris", "beta,gamma,Alpha")]        // tie (100) keeps scene order
    [InlineData("vis", "Alpha,gamma,beta")]
    [InlineData("verts", "beta,gamma,Alpha")]
    [InlineData("draws", "Alpha,gamma,beta")]
    [InlineData("geo", "Alpha,gamma,beta")]
    [InlineData("tex", "beta,gamma,Alpha")]
    [InlineData("bytes", "beta,Alpha,gamma")]       // 905, 900, 60
    [InlineData("inst", "gamma,beta,Alpha")]        // 4, 1, 1
    [InlineData("name", "Alpha,beta,gamma")]        // case-insensitive A-Z
    public void DefaultDirection(string mode, string expected)
    {
        var (rep, root, _, _, _) = Make();
        Assert.Equal(expected, Names(InspectorModel.SortedChildren(rep, root, mode)));
    }

    [Theory]
    [InlineData("tris", "Alpha,beta,gamma")]        // reversed, the tie still keeps scene order
    [InlineData("vis", "beta,gamma,Alpha")]
    [InlineData("name", "gamma,beta,Alpha")]
    [InlineData("scene", "beta,Alpha,gamma")]       // scene order has no direction
    public void Reversed(string mode, string expected)
    {
        var (rep, root, _, _, _) = Make();
        Assert.Equal(expected, Names(InspectorModel.SortedChildren(rep, root, mode, reverse: true)));
    }

    [Fact]
    public void FindingsWorstFirst()
    {
        var (rep, root, a, b, c) = Make();
        rep.Findings.Add(new Finding("x", "low", "low one", "", new[] { a }));
        rep.Findings.Add(new Finding("y", "high", "high one", "", new[] { c }));
        rep.Findings.Add(new Finding("z", "low", "another low", "", new[] { a }));
        var idx = new InspectorModel.FindingIndex(rep);
        Assert.Equal("gamma,beta,Alpha", Names(InspectorModel.SortedChildren(rep, root, "findings", idx: idx)));
        Assert.Equal("Alpha,beta,gamma", Names(InspectorModel.SortedChildren(rep, root, "findings", reverse: true, idx: idx)));
    }

    [Fact]
    public void EveryOrderModeIsSortable()
    {
        var (rep, root, _, _, _) = Make();
        var idx = new InspectorModel.FindingIndex(rep);
        foreach (var (_, mode) in InspectorModel.Orders)
            Assert.Equal(3, InspectorModel.SortedChildren(rep, root, mode, idx: idx).Count);
        Assert.True(InspectorModel.AscendingByDefault("name"));
        Assert.False(InspectorModel.AscendingByDefault("tris"));
    }

    [Fact]
    public void StickyOrderKeepsRowsWhereTheyWere()
    {
        var (rep, root, a, _, c) = Make();
        var sticky = new Dictionary<Node, int> { [c] = 0, [a] = 1 };
        Assert.Equal("gamma,beta,Alpha", Names(InspectorModel.SortedChildren(rep, root, "bytes", sticky: sticky)));   // bytes alone: beta,Alpha,gamma
        Assert.Equal("gamma,beta,Alpha", Names(InspectorModel.SortedChildren(rep, root, "scene", sticky: sticky)));
    }
}
