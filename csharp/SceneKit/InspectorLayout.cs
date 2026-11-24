using System.Text.Json;

namespace SceneKit;

/// <summary>Where one panel lives: popped out (with its window bounds) or docked.</summary>
public sealed record PanelPlace(string Id, bool Out, double Left, double Top, double Width, double Height);

/// <summary>The remembered window layout: main window bounds, tree / tabs splitter, selection view flags, pop-outs.</summary>
public sealed record LayoutState(int Version, double Left, double Top, double Width, double Height, bool Maximized,
    double TreeShare, bool SelectionViewOpen, bool SelectionViewScene, IReadOnlyList<PanelPlace> Panels);

/// <summary>Reads / writes <c>%LOCALAPPDATA%\SceneInspector\layout.json</c>; anything unusable reads as "no layout".</summary>
public static class LayoutStore
{
    /// <summary>Index = the window's PanelId.</summary>
    public static readonly string[] PanelIds = { "inspect", "problems", "textures", "optimize", "review", "tree" };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SceneInspector", "layout.json");

    static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static LayoutState? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<LayoutState>(File.ReadAllText(path), Opts);
            if (s == null || s.Version != 1) return null;
            var panels = (s.Panels ?? Array.Empty<PanelPlace>())
                .Where(p => p != null && p.Id != null && Array.IndexOf(PanelIds, p.Id) >= 0).ToList();
            return s with { Panels = panels };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static void Save(string path, LayoutState s)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Pop-outs that would not be reachable on the current screens dock back; an unreachable main window
    /// gets size 0 ("default size").</summary>
    public static LayoutState Fit(LayoutState s, (double X, double Y, double W, double H) screen, double minVisible = 64)
    {
        bool Visible(double l, double t, double w, double h) =>
            Math.Min(l + w, screen.X + screen.W) - Math.Max(l, screen.X) >= minVisible &&
            Math.Min(t + h, screen.Y + screen.H) - Math.Max(t, screen.Y) >= minVisible;
        var panels = s.Panels.Select(p => p.Out && !Visible(p.Left, p.Top, p.Width, p.Height) ? p with { Out = false } : p).ToList();
        s = s with { Panels = panels };
        if (!Visible(s.Left, s.Top, s.Width, s.Height)) s = s with { Width = 0, Height = 0 };
        return s;
    }
}
