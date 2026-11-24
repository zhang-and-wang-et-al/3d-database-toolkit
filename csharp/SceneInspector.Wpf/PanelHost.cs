using System.Windows;
using System.Windows.Controls;

namespace SceneInspector;

/// <summary>The inspector's panels; the first five in tab order (Ctrl+1..5), then the scene tree.</summary>
public enum PanelId { Inspect, Problems, Textures, Optimize, Review, Tree }

/// <summary>Where each panel lives: in its tab (the tree: in the left card) or popped out into its own
/// <see cref="PanelWindow"/>. Popping out re-parents the same control instance, so every reference the main window
/// holds keeps working; closing the pop-out window docks the panel back.</summary>
sealed class PanelHost
{
    readonly MainWindow w;
    readonly List<PanelWindow> outs = new();   // in pop-out order
    GridLength treeWidth, splitterWidth;
    double treeMinWidth;
    bool closingAll;
    readonly Dictionary<Window, WindowState> lastState = new();   // state before a window was minimised

    /// <summary>A panel became visible: its tab was selected, its pop-out window opened or was restored.</summary>
    public event Action<PanelId>? Shown;
    /// <summary>After every pop out / dock.</summary>
    public event Action? Changed;

    public PanelHost(MainWindow w)
    {
        this.w = w;
        w.Tabs.SelectionChanged += OnTabChanged;
        w.StateChanged += (_, _) => Remember(w);
    }

    void Remember(Window win)
    {
        if (win.WindowState != WindowState.Minimized) lastState[win] = win.WindowState;
    }

    void Restore(Window win)
    {
        if (win.WindowState == WindowState.Minimized)
            win.WindowState = lastState.TryGetValue(win, out var st) ? st : WindowState.Normal;
        if (!win.IsActive) win.Activate();
    }

    public IReadOnlyList<(PanelId Id, Window Win)> OutWindows => outs.Select(x => (x.Id, (Window)x)).ToList();

    public bool IsOut(PanelId p) => Out(p) != null;

    /// <summary>The tree column's width as it was before the tree popped out (valid while the tree is out).</summary>
    public GridLength SavedTreeWidth => treeWidth;

    /// <summary>Visible to the user: popped out and not minimised, or its tab selected (the docked tree always).</summary>
    public bool IsShown(PanelId p) => Out(p) is { } win
        ? win.WindowState != WindowState.Minimized
        : p == PanelId.Tree || ReferenceEquals(w.Tabs.SelectedItem, Tab(p));

    /// <summary>Selects the panel's tab, or restores and brings its pop-out window to the front.</summary>
    public void Show(PanelId p)
    {
        if (Out(p) is { } win)
        {
            Restore(win);
            return;
        }
        if (Tab(p) is { } tab) w.Tabs.SelectedItem = tab;
        Restore(w);   // requested from a pop-out: the main window may be behind or minimised
    }

    public void PopOut(PanelId p, Rect? bounds = null)
    {
        if (IsOut(p)) return;
        var panel = Panel(p);
        var r = bounds ?? DefaultBounds(p == PanelId.Tree ? w.TreeCard : w.Tabs);
        if (p == PanelId.Tree)
        {
            var cols = w.MainArea.ColumnDefinitions;
            treeWidth = cols[0].Width;
            treeMinWidth = cols[0].MinWidth;
            splitterWidth = cols[1].Width;
            w.TreeCard.Child = null;
            w.TreeCard.Visibility = Visibility.Collapsed;
            w.MainSplitter.Visibility = Visibility.Collapsed;
            w.TreeView.PopOutButton.Visibility = Visibility.Collapsed;   // the tab buttons leave with their tabs; this one would travel along
            cols[0].MinWidth = 0;
            cols[0].Width = new GridLength(0);
            cols[1].Width = new GridLength(0);
        }
        else
        {
            var tab = Tab(p)!;
            if (tab.IsSelected)
                w.Tabs.SelectedItem = w.Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t != tab && t.Visibility == Visibility.Visible);
            tab.Content = null;
            tab.Visibility = Visibility.Collapsed;
        }
        var win = new PanelWindow(p, p.ToString(), panel, w)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
        };
        win.Closed += OnWindowClosed;
        win.StateChanged += (_, _) => { Remember(win); if (win.WindowState != WindowState.Minimized) Shown?.Invoke(p); };
        outs.Add(win);
        win.Show();
        Shown?.Invoke(p);
        Changed?.Invoke();
    }

    public void Dock(PanelId p)
    {
        if (Out(p) is { } win) DockCore(win, closeWindow: true);
    }

    /// <summary>The main window closes: close the pop-outs without docking them back (the layout stays as it was).</summary>
    public void CloseAll()
    {
        closingAll = true;
        foreach (var win in outs.ToList()) win.Close();
    }

    void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!closingAll && sender is PanelWindow win && outs.Contains(win)) DockCore(win, closeWindow: false);
    }

    void DockCore(PanelWindow win, bool closeWindow)
    {
        var p = win.Id;
        outs.Remove(win);
        lastState.Remove(win);
        var panel = Panel(p);
        win.Content = null;
        if (p == PanelId.Tree)
        {
            var cols = w.MainArea.ColumnDefinitions;
            cols[0].Width = treeWidth;
            cols[0].MinWidth = treeMinWidth;
            cols[1].Width = splitterWidth;
            w.TreeCard.Child = panel;
            w.TreeCard.Visibility = Visibility.Visible;
            w.MainSplitter.Visibility = Visibility.Visible;
            w.TreeView.PopOutButton.Visibility = Visibility.Visible;
        }
        else
        {
            var tab = Tab(p)!;
            tab.Content = panel;
            tab.Visibility = Visibility.Visible;
            if (w.Tabs.SelectedItem == null) w.Tabs.SelectedItem = tab;
        }
        if (closeWindow) win.Close();   // Closed sees it docked already
        Changed?.Invoke();
    }

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, w.Tabs)) return;   // selectors inside the panels bubble up here too
        if (w.Tabs.SelectedItem is TabItem tab && IdOf(tab) is { } p) Shown?.Invoke(p);
    }

    PanelWindow? Out(PanelId p) => outs.FirstOrDefault(x => x.Id == p);

    TabItem? Tab(PanelId p) => p switch
    {
        PanelId.Inspect => w.InspectTab,
        PanelId.Problems => w.ProblemsTab,
        PanelId.Textures => w.TexturesTab,
        PanelId.Optimize => w.OptimizeTab,
        PanelId.Review => w.ReviewTab,
        _ => null,
    };

    PanelId? IdOf(TabItem tab) =>
        Enum.GetValues<PanelId>().Where(p => ReferenceEquals(Tab(p), tab)).Select(p => (PanelId?)p).FirstOrDefault();

    FrameworkElement Panel(PanelId p) => p switch
    {
        PanelId.Inspect => w.InspectView,
        PanelId.Problems => w.ProblemsView,
        PanelId.Textures => w.TexturesView,
        PanelId.Optimize => w.OptimizeView,
        PanelId.Review => w.ReviewView,
        _ => w.TreeView,
    };

    /// <summary>Where the panel sat on screen, shifted a little so the new window reads as lifted off; a fixed size
    /// beside the main window's corner when that place is not visible (minimised main window).</summary>
    Rect DefaultBounds(FrameworkElement area)
    {
        const double shift = 24, frameW = 16, frameH = 39;   // non-client frame of a normal window, roughly
        if (area.IsVisible && area.ActualWidth > 0 && PresentationSource.FromVisual(area)?.CompositionTarget is { } ct)
        {
            var tl = ct.TransformFromDevice.Transform(area.PointToScreen(new Point(0, 0)));
            return new Rect(tl.X + shift, tl.Y + shift, Math.Max(480, area.ActualWidth + frameW), Math.Max(360, area.ActualHeight + frameH));
        }
        return new Rect(w.Left + 60, w.Top + 60, 720, 640);
    }
}
