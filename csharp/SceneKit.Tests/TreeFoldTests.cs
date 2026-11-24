using SceneKit;
using Xunit;

/// <summary>InspectorModel.FoldChain: single-child chains in the tree fold into one row.</summary>
public class TreeFoldTests
{
    static Node G(string name, params Node[] kids)
    {
        var n = new Node(Kind.Group, name: name);
        n.Children.AddRange(kids);
        return n;
    }

    /// <summary>Parent map like Report.Parents, built from the given roots.</summary>
    static Dictionary<Node, List<Node>> Parents(params Node[] roots)
    {
        var map = new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>(roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n)) continue;
            foreach (var c in n.AllChildren())
            {
                if (!map.TryGetValue(c, out var pl)) map[c] = pl = new List<Node>();
                pl.Add(n);
                stack.Push(c);
            }
        }
        return map;
    }

    [Fact]
    public void SingleChildGroupsFoldToTheDeepest()
    {
        var c = G("c", G("d"), G("e"));
        var b = G("b", c);
        var a = G("a", b);
        Assert.Equal(new[] { a, b, c }, InspectorModel.FoldChain(a, Parents(a)));
    }

    [Fact]
    public void FoldStopsAtGeometryAndKeepsLeaves()
    {
        var b = G("b", G("c"));
        b.Geom = new GeomData();
        var a = G("a", b);
        Assert.Equal(new[] { a }, InspectorModel.FoldChain(a, Parents(a)));

        var b2 = G("b", G("leaf"));
        var a2 = G("a", b2);
        Assert.Equal(new[] { a2, b2 }, InspectorModel.FoldChain(a2, Parents(a2)));   // b stays the row, the leaf its child
    }

    [Fact]
    public void SharedRootStaysItsOwnRow()
    {
        var b = G("b", G("c", G("d"), G("e")));
        var a = G("a", b);
        var other = G("other", b);
        Assert.Equal(new[] { a }, InspectorModel.FoldChain(a, Parents(a, other)));
    }

    [Fact]
    public void NothingToFoldReturnsTheStart()
    {
        var a = G("a", G("b"), G("c"));
        Assert.Equal(new[] { a }, InspectorModel.FoldChain(a, Parents(a)));
        var leaf = G("leaf");
        Assert.Equal(new[] { leaf }, InspectorModel.FoldChain(leaf, Parents(leaf)));
    }

    [Fact]
    public void FoldedRowCarriesTheFindingsOfItsFoldedNodes()
    {
        var c = G("c");
        var b = G("b", c, G("d"));
        var a = G("a", b);
        var rep = new Report(new Scene("x.osgt", "osgt") { Root = a });
        rep.Parents[b] = new() { a }; rep.Parents[c] = new() { b };
        rep.Findings.Add(new Finding("similar", "medium", "9 similar objects", "m", new[] { a }));
        rep.Findings.Add(new Finding("big", "low", "big thing", "m", new[] { c }));
        var idx = new InspectorModel.FindingIndex(rep);
        var chain = InspectorModel.FoldChain(a, rep.Parents);
        Assert.Equal(new[] { a, b }, chain);

        var row = InspectorModel.RowFor(rep, idx, chain);
        Assert.Equal("medium", row.Severity);                       // a's finding, not only the low one below b
        Assert.Contains("9 similar objects", row.Notes);
        var deepest = InspectorModel.RowFor(rep, idx, b);
        Assert.Equal("low", deepest.Severity);
        Assert.DoesNotContain("9 similar objects", deepest.Notes);
        Assert.Contains("1 finding(s) inside", deepest.Notes);
    }

    [Fact]
    public void FoldedRowNotesTheWorstFindingFirst()
    {
        var c = G("c");
        var b = G("b", c, G("d"));
        var a = G("a", b);
        var rep = new Report(new Scene("x.osgt", "osgt") { Root = a });
        rep.Parents[b] = new() { a }; rep.Parents[c] = new() { b };
        rep.Findings.Add(new Finding("small", "low", "small thing", "m", new[] { a }));
        rep.Findings.Add(new Finding("bad", "high", "bad thing", "m", new[] { b }));
        var row = InspectorModel.RowFor(rep, new InspectorModel.FindingIndex(rep), InspectorModel.FoldChain(a, rep.Parents));
        Assert.Equal("high", row.Severity);
        Assert.StartsWith($"{InspectorModel.SevMark["high"]} bad thing (+1)", row.Notes);
    }

    [Theory]
    [InlineData("lod")]
    [InlineData("paged")]
    [InlineData("switch")]
    [InlineData("hidden")]
    public void FoldStopsBeforeLodSwitchAndHiddenNodes(string what)
    {
        var b = G("b", G("c"), G("d"));
        var mid = what switch
        {
            "lod" => new Node(Kind.Lod, name: "lod"),
            "paged" => new Node(Kind.PagedLod, name: "plod"),
            "switch" => new Node(Kind.Switch, name: "sw"),
            _ => G("hidden"),
        };
        if (what == "hidden") mid.NodeMask = 0;
        mid.Children.Add(b);
        var a = G("a", mid);
        var parents = Parents(a);
        Assert.Equal(new[] { a }, InspectorModel.FoldChain(a, parents));     // the LOD (ranges) / switch / hidden node keeps its row
        Assert.Equal(new[] { mid }, InspectorModel.FoldChain(mid, parents)); // and is never a folded prefix itself
        Assert.Equal(new[] { b }, InspectorModel.FoldChain(b, parents));
    }

    [Fact]
    public void ExternalReferencesStillFold()
    {
        var b = G("b", G("c"), G("d"));
        var ext = new Node(Kind.External, name: "tile_0_0.flt") { Children = { b } };
        var a = G("a", ext);
        Assert.Equal(new[] { a, ext, b }, InspectorModel.FoldChain(a, Parents(a)));
    }
}
