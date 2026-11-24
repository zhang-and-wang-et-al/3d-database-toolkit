using SceneKit;
using Xunit;

/// <summary>LayoutStore: the inspector's remembered window / pop-out layout (layout.json).</summary>
public class LayoutTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"sklay_{Guid.NewGuid():N}");
    string P => Path.Combine(dir, "sub", "layout.json");

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    static LayoutState Sample(params PanelPlace[] panels) =>
        new(1, 100, 50, 1400, 900, false, 0.4, true, false, panels);

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var s = Sample(new PanelPlace("problems", true, 10, 20, 600, 500), new PanelPlace("tree", true, 700, 30, 400, 800),
                       new PanelPlace("inspect", false, 0, 0, 0, 0));
        LayoutStore.Save(P, s);
        var l = LayoutStore.Load(P);
        Assert.NotNull(l);
        Assert.Equal(s with { Panels = Array.Empty<PanelPlace>() }, l! with { Panels = Array.Empty<PanelPlace>() });
        Assert.True(s.Panels.SequenceEqual(l.Panels));
        Assert.Equal(0.4, l.TreeShare);
        Assert.False(File.Exists(P + ".tmp"));
    }

    [Fact]
    public void CorruptFileGivesNull()
    {
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "c.json");
        File.WriteAllText(f, "{ not json");
        Assert.Null(LayoutStore.Load(f));
        File.WriteAllText(f, "");
        Assert.Null(LayoutStore.Load(f));
        LayoutStore.Save(P, Sample(new PanelPlace("problems", true, 10, 20, 600, 500)));
        string full = File.ReadAllText(P);
        File.WriteAllText(f, full[..(full.Length / 2)]);
        Assert.Null(LayoutStore.Load(f));
        Assert.Null(LayoutStore.Load(Path.Combine(dir, "missing.json")));
    }

    [Fact]
    public void NewerVersionGivesNull()
    {
        LayoutStore.Save(P, Sample() with { Version = 2 });
        Assert.Null(LayoutStore.Load(P));
    }

    [Fact]
    public void UnknownPanelIdsAreIgnored()
    {
        LayoutStore.Save(P, Sample(new PanelPlace("inspect", true, 1, 2, 300, 300), new PanelPlace("minimap", true, 1, 2, 300, 300)));
        var l = LayoutStore.Load(P)!;
        Assert.Single(l.Panels);
        Assert.Equal("inspect", l.Panels[0].Id);
    }

    [Fact]
    public void PanelOnAMissingMonitorDocksBack()
    {
        var s = Sample(new PanelPlace("problems", true, 2500, 100, 600, 500), new PanelPlace("textures", true, 1800, 100, 600, 500));
        var f = LayoutStore.Fit(s, (0, 0, 1920, 1080));
        Assert.False(f.Panels[0].Out);
        Assert.True(f.Panels[1].Out);
        Assert.Equal(100, f.Left);
    }

    [Fact]
    public void MainWindowOffScreenGetsDefaultSize()
    {
        var f = LayoutStore.Fit(Sample() with { Left = -5000 }, (0, 0, 1920, 1080));
        Assert.Equal(0, f.Width);
        Assert.Equal(0, f.Height);
    }
}
