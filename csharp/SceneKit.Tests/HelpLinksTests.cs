using SceneKit;
using Xunit;

/// <summary>HelpLinks: README anchors for F1 / the ? button.</summary>
public class HelpLinksTests
{
    [Fact]
    public void AnchorFollowsGitHub()
    {
        Assert.Equal("scene-inspector-openscenegraph--openflight", HelpLinks.Anchor("Scene inspector (OpenSceneGraph / OpenFlight)"));
        Assert.Equal("simplify-strategies", HelpLinks.Anchor("Simplify strategies"));
    }

    [Fact]
    public void EveryAnchorIsAReadmeHeading()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "README.md"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var anchors = File.ReadAllLines(Path.Combine(dir!, "README.md"))
            .Where(l => l.StartsWith('#')).Select(l => HelpLinks.Anchor(l.TrimStart('#').Trim())).ToHashSet();
        foreach (var (id, a) in HelpLinks.Anchors) Assert.True(anchors.Contains(a), $"{id} -> {a}");
        Assert.Contains("scene-inspector-openscenegraph--openflight", anchors);
    }

    [Fact]
    public void UnknownPanelGoesToTheInspectorSection()
    {
        Assert.Equal(HelpLinks.Online + "#scene-inspector-openscenegraph--openflight", HelpLinks.Url("nonsense"));
        Assert.Equal(HelpLinks.Online + "#scene-tree", HelpLinks.Url("tree"));
    }
}
