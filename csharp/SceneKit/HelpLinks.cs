namespace SceneKit;

/// <summary>Where F1 and the ? button lead: the README on GitHub, at the section of the current panel.</summary>
public static class HelpLinks
{
    public const string Online = "https://github.com/zhang-and-wang-et-al/3d-database-toolkit/blob/main/README.md";

    const string InspectorSection = "scene-inspector-openscenegraph--openflight";

    /// <summary>Panel / topic id to README anchor.</summary>
    public static readonly IReadOnlyDictionary<string, string> Anchors = new Dictionary<string, string>
    {
        ["inspect"] = "inspect",
        ["problems"] = "problems",
        ["textures"] = "textures",
        ["optimize"] = "optimize",
        ["review"] = "review",
        ["tree"] = "scene-tree",
        ["strategies"] = "simplify-strategies",
        ["passes"] = "optimisation-passes",
        ["layout"] = "panels-and-layout",
    };

    /// <summary>GitHub's heading anchor: lower case, everything but letters, digits, spaces and '-' dropped, spaces to '-'.</summary>
    public static string Anchor(string heading)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in heading.ToLowerInvariant())
        {
            if (c == ' ') sb.Append('-');
            else if (c == '-' || char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Url(string id) =>
        Online + "#" + (Anchors.TryGetValue(id, out var a) ? a : InspectorSection);
}
