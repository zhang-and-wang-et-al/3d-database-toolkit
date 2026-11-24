using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SceneKit;

namespace SceneInspector;

/// <summary>One line of the scene tree (a flattened tree: children are inserted below an expanded row).</summary>
public sealed class TreeRow : INotifyPropertyChanged
{
    /// <summary>The row's node: the deepest node of a folded chain.</summary>
    public Node? Node { get; init; }
    /// <summary>The nodes the row shows, top first (one node when nothing is folded, empty for the "... more" row).</summary>
    public List<Node> ChainNodes { get; init; } = new();
    /// <summary>Grey prefix naming the folded nodes above <see cref="Node"/>: "tile_0_0 › tile_0_0.flt › ", empty when not folded.</summary>
    public string Chain { get; init; } = "";
    /// <summary>Full path of <see cref="Node"/> (the name cell's tooltip).</summary>
    public string? FullPath { get; init; }
    public int Depth { get; init; }
    public bool HasChildren { get; init; }
    bool expanded;
    public bool Expanded
    {
        get => expanded;
        set { expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph))); }
    }
    public Thickness Indent => new(Depth * 12, 0, 0, 0);
    /// <summary>Chevron (Segoe icon font): down when open, right when closed.</summary>
    public string Glyph => HasChildren ? (Expanded ? "" : "") : "";
    public string Text { get; init; } = "";
    public double Share { get; init; }
    public double ShareWidth => 38 * Math.Clamp(double.IsNaN(Share) ? 0 : Share, 0, 1);
    public string ShareText => Node == null ? "" : PyFormat.Fixed(Math.Clamp(double.IsNaN(Share) ? 0 : Share, 0, 1) * 100, 1) + "%";
    public string Tris { get; init; } = "";
    public string Vis { get; init; } = "";
    public string Verts { get; init; } = "";
    /// <summary>What the count column shows: triangles, visible triangles or vertices (its header's context menu).</summary>
    public string Count { get; init; } = "";
    public string? CountTip => Node == null ? null : $"{Tris} triangles, {Vis} worst case visible, {Verts} vertices";
    public string Draws { get; init; } = "";
    public string Geo { get; init; } = "";
    public string Tex { get; init; } = "";
    public string Inst { get; init; } = "";
    /// <summary>Texture unit badge from the model: "0 1", "0 1 ⚠".</summary>
    public string Units { get; init; } = "";
    public bool UnitsWarn => Units.EndsWith("⚠", StringComparison.Ordinal);
    public string UnitsText => Units.TrimEnd('⚠', ' ');
    public Visibility UnitsVisibility => Units.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnitsWarnVisibility => UnitsWarn ? Visibility.Visible : Visibility.Collapsed;
    public Brush UnitsTint => UnitsWarn ? Ui.Brush("Sev.Med.Tint") : Ui.Brush("SurfaceRaised");
    public Brush UnitsBrush => UnitsWarn ? Ui.Brush("Sev.Med") : Ui.Brush("TextMuted");
    public string? UnitsTip => Units.Length == 0 ? null : UnitsWarn
        ? $"Texture units {UnitsText}: an image is missing or a geometry has no texture coordinates for one of them (see the Inspect / Textures tab)"
        : $"Texture units {UnitsText} (own and inherited state)";
    public string Notes { get; init; } = "";
    public string? NotesTip => Notes.Length == 0 ? null : Severity != null ? $"{SevLabel}: {Notes}" : Notes;
    public string? Severity { get; init; }
    bool changed, changedBelow;
    /// <summary>A node of the row was changed by the last run (solid dot).</summary>
    public bool Changed { get => changed; set { changed = value; RaiseChanged(); } }
    /// <summary>Only nodes below were changed (hollow dot).</summary>
    public bool ChangedBelow { get => changedBelow; set { changedBelow = value; RaiseChanged(); } }
    public Visibility ChangedVisibility => changed || changedBelow ? Visibility.Visible : Visibility.Hidden;
    public Brush ChangedFill => changed ? Ui.Brush("Accent") : Brushes.Transparent;
    public Brush ChangedStroke => changed || changedBelow ? Ui.Brush("Accent") : Brushes.Transparent;
    void RaiseChanged()
    {
        foreach (var n in new[] { nameof(ChangedVisibility), nameof(ChangedFill), nameof(ChangedStroke) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public Brush SevBrush => Ui.SevBrush(Severity);
    public Brush SevTint => Ui.SevTint(Severity);
    public string SevLabel => Ui.SevLabel(Severity);
    public Visibility SevVisibility => Severity != null ? Visibility.Visible : Visibility.Collapsed;
    public Brush Foreground { get; init; } = Brushes.White;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record FindingRow(Finding F, string Title, string Severity, string Saving, string Fix)
{
    public Brush SevBrush => Ui.SevBrush(Severity);
    public Brush SevTint => Ui.SevTint(Severity);
    public string SevLabel => Ui.SevLabel(Severity);
}

/// <summary>One line of the undo history in Review: Target = number of done steps after going there (0 = the oldest
/// state kept). Disk rows (<c>Ck</c> set) are checkpoints on disk: Target -1 reloads the database at that state; a disk row that
/// is also the oldest state in memory keeps Target 0. Target -2 reloads the database as it is on disk ("Scene as opened" once
/// that state is no longer in memory).</summary>
public sealed record HistoryRow(int Target, string Label, string Time, string Tris, string Draws, bool Undone, bool Current,
    string Scope = "", string Detail = "", CheckpointInfo? Ck = null)
{
    public bool Disk => Ck != null;
    public Brush Foreground => Ui.Brush(Disk && !Current ? "TextMuted" : Undone ? "TextDisabled" : "Text");
    public Visibility CurrentVisibility => Current ? Visibility.Visible : Visibility.Hidden;
    string Hint => Current ? "The scene is in this state"
        : Target == -2 ? "Click to reload the database as it is on disk"
        : Disk && Target < 0 ? "On disk; click to reload the database at this state"
        : Undone ? "Click to redo up to this step" : "Click to undo back to this step";
    public string Tip => Detail.Length > 0 ? Detail + "\n" + Hint : Hint;
}

/// <summary>A hotspot line; D = "×16 similar" for geometry that is part of a group of copies (Similar view).</summary>
public sealed record HotRow(string Label, string A, string B, string C, List<Node> Nodes, string D = "", string? DTip = null, string? FullPath = null);

public sealed record ChangeRow(Change C, string Path, string? Verdict, bool Pending, string Pass, string Tris, string Verts, string Check, string? UnitDelta, string? FullPath = null)
{
    public Visibility UnitVisibility => UnitDelta != null ? Visibility.Visible : Visibility.Collapsed;
    public string RiskLabel => Pending ? "Checking…" : Ui.VerdictLabel(Verdict);
    public Brush RiskBrush => Pending ? Ui.Brush("TextMuted") : Ui.VerdictBrush(Verdict);
    public Brush RiskTint => Pending ? Ui.Brush("Sev.Info.Tint") : Ui.VerdictTint(Verdict);
}

public partial class MainWindow : Window
{
    public static readonly RoutedCommand UndoCommand = new(), RedoCommand = new(), OpenCommand = new(), SaveCommand = new(), SaveAsCommand = new(),
        ReloadCommand = new(), ExportCommand = new(), RunCommand = new(), FilterCommand = new(), TabCommand = new(), HelpCommand = new();
    const string SceneFilter = "Scenes|*.osgt;*.osg;*.flt;*.osgb;*.ive;*.osgx|OpenSceneGraph text|*.osgt;*.osg|OpenFlight|*.flt|All files|*.*";
    const string MapHint = "Area = cost. Colour = worst finding at or below (red high, orange medium, blue low, grey none). " +
                           "Click: select in the tree. Double click: zoom in.";
    /// <summary>"Fix all" asks first when it would touch more nodes than this.</summary>
    const int ConfirmFixAbove = 20;

    readonly string? osgconv;
    internal Scene? Scene;
    internal Report? Rep;
    InspectorModel.FindingIndex? index;
    bool modified, busy;
    readonly ObservableCollection<TreeRow> rows = new();
    readonly UndoHistory history = new();
    readonly Dictionary<string, CheckBox> passBoxes = new();
    readonly Dictionary<string, Expander> passExpanders = new();
    readonly Dictionary<string, (FrameworkElement control, PassParam p)> paramControls = new();
    /// <summary>The estimate line under the replace_proxy rows (R3-C13); empty without a selection.</summary>
    TextBlock? proxyEstimate;

    /// <summary>The one simplify record (R2-D3): Try, Apply, the Optimize tab and every simplify run read it.</summary>
    internal SimplifyParams Simplify { get; private set; } = new();
    /// <summary>The simplify record in the Optimize tab's simplify pass.</summary>
    SimplifyEditor OptEditor = null!;
    IEnumerable<SimplifyEditor> SimplifyEditors => new[] { Preview.TryEditor, OptEditor };

    /// <summary>The node whose "Applied: ..." status line is on screen (cleared when the selection moves).</summary>
    Node? appliedNode;

    /// <summary>Store a changed record (normalised: Fast, rough always keeps a share) and show it in every other editor
    /// (and in <paramref name="from"/> too when normalising changed it).</summary>
    internal void SetSimplify(SimplifyParams p, SimplifyEditor? from)
    {
        var n = p.Normalized();
        bool switched = n != p;
        Simplify = n;
        foreach (var ed in SimplifyEditors)
            if (!ReferenceEquals(ed, from) || switched) ed.Value = n;
        if (switched) Status.Text = "Fast, rough has no error limit: switched to Keep %";
        Preview.RefreshApply();
    }

    void InitSimplifyEditors()
    {
        foreach (var ed in SimplifyEditors)
        {
            ed.Value = Simplify;
            ed.Changed += (from, p) => { SetSimplify(p, from); OnProfileFieldEdited(); };
        }
        Preview.TryEditor.ShowAdvanced = false;
        OptEditor.ShowAdvanced = true;
    }
    List<Change> changes = new();
    bool optimisedOnce;
    internal Node? PendingPreview;
    DateTime skipPreviewUntil = DateTime.MinValue;
    /// <summary>Scene totals right after opening (the KPI tiles show the change since then).</summary>
    Dictionary<string, object?>? openSummary;
    ulong? openUnits;
    /// <summary>The folder of the last Save Copy (Ctrl+S then writes there again, with the same options); null = Save Over.</summary>
    string? boundFolder;
    string? boundMasterName;
    bool boundIncludeUnchanged = true;
    bool invalidParams;
    readonly List<UIElement> reviewPills = new();

    // ---- checkpoints on disk (one per step, written after it while the user reviews)
    readonly CheckpointStore store = new(CheckpointStore.DefaultDir);
    /// <summary>Key of the opened database (null: no checkpoints, see <see cref="ckError"/>); the session is created with the first step.</summary>
    string? ckKey, ckSession;
    /// <summary>Index of the last checkpoint of this session (continues across restores).</summary>
    int ckIndex;
    /// <summary>The restored checkpoint of this session the scene was reloaded at (null: the database as opened).</summary>
    int? ckParent;
    /// <summary>The step's checkpoint to write; parent: the checkpoint it was done on (0: the database as opened).</summary>
    (int index, string label, string scope, string prms, int parent)? pendingCheckpoint;
    List<CheckpointInfo> diskCheckpoints = new();
    /// <summary>Checkpoint index of each in-memory history entry (same order as <c>history.Entries()</c>).</summary>
    readonly List<int> memIndices = new();
    /// <summary>Checkpoint index of the newest step dropped by the undo cap: that checkpoint is the oldest state still in memory.</summary>
    int? droppedTop;
    /// <summary>The checkpoint the scene was reloaded at (null after a normal open).</summary>
    CheckpointInfo? restoredInfo;
    (int index, string label)? loadedState;
    /// <summary>Why the last checkpoint could not be written (or why there are none); shown in the History panel.</summary>
    string? ckError;
    bool ckErrorIsWrite;
    (int count, long bytes)? ckSize;
    bool checkpointing;

    public MainWindow(string? osgconv = null)
    {
        this.osgconv = osgconv;
        InitializeComponent();
        TreeView.Host = InspectView.Host = ProblemsView.Host = TexturesView.Host = OptimizeView.Host = ReviewView.Host = this;
        PanelHost = new PanelHost(this);
        PanelHost.Shown += OnPanelShown;
        Closed += (_, _) => PanelHost.CloseAll();
        PanelHost.Changed += SaveLayout;
        LoadLayout();
        Dialogs.DarkTitleBar(this);
        Tree.ItemsSource = rows;
        SetOrder(OrderMode, false);   // header arrow for the default order
        BuildOptimize();
        InitCompanions();
        Preview.Attach(this);
        InitSimplifyEditors();
        InitSelectionView();
        foreach (var (label, _) in InspectorModel.MapMetrics) MapMetricBox.Items.Add(label);
        MapMetricBox.SelectedIndex = 0;
        MapInfo.Text = MapHint;
        Map.SizeChanged += (_, _) => FillMap();
        Map.HoverChanged += OnMapHover;
        Map.Clicked += OnMapClick;
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); ApplyFilter(); };
        UpdateRunButton();
    }

    /// <summary>Default size: 90 % of the work area, centred; on small screens (laptops, high scaling) start maximised.</summary>
    void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Round(area.Width * 0.9));
        Height = Math.Max(MinHeight, Math.Round(area.Height * 0.9));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (area.Width < 1500 || area.Height < 840) WindowState = WindowState.Maximized;
    }

    // ---- layout memory (layout.json)

    LayoutState? pendingLayout;
    bool layoutRestoring = true;   // no saving until the start-up layout is applied
    bool selectionViewOpen = true, selectionViewScene;   // the selection view's expander and Scene around toggle

    void LoadLayout()
    {
        var l = LayoutStore.Load(LayoutStore.DefaultPath);
        if (l != null)
            l = LayoutStore.Fit(l, (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        if (l == null || l.Width <= 0 || l.Height <= 0)
            FitToScreen();
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l.Left; Top = l.Top;
            Width = Math.Max(MinWidth, l.Width); Height = Math.Max(MinHeight, l.Height);
            if (l.Maximized) WindowState = WindowState.Maximized;
        }
        if (l != null)
        {
            selectionViewOpen = l.SelectionViewOpen;
            selectionViewScene = l.SelectionViewScene;
            SetTreeShare(l.TreeShare);
        }
        pendingLayout = l;
        Loaded += (_, _) =>
        {
            if (pendingLayout != null)
                foreach (var pl in pendingLayout.Panels)
                {
                    int i = Array.IndexOf(LayoutStore.PanelIds, pl.Id);
                    if (pl.Out && i >= 0 && pl.Width > 0 && pl.Height > 0)
                        PanelHost.PopOut((PanelId)i, new Rect(pl.Left, pl.Top, pl.Width, pl.Height));
                }
            pendingLayout = null;
            layoutRestoring = false;
            SaveLayout();
        };
    }

    void SetTreeShare(double share)
    {
        if (!(share > 0.05 && share < 0.95)) return;
        var cols = MainArea.ColumnDefinitions;
        cols[0].Width = new GridLength(share * 100, GridUnitType.Star);
        cols[2].Width = new GridLength((1 - share) * 100, GridUnitType.Star);
    }

    double CurrentTreeShare()
    {
        var cols = MainArea.ColumnDefinitions;
        var w0 = PanelHost.IsOut(PanelId.Tree) ? PanelHost.SavedTreeWidth : cols[0].Width;
        var w2 = cols[2].Width;
        double a = w0.IsStar ? w0.Value : cols[0].ActualWidth, b = w2.IsStar ? w2.Value : cols[2].ActualWidth;
        return a + b > 0 ? a / (a + b) : 0.51;
    }

    void SaveLayout()
    {
        if (layoutRestoring) return;
        try
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            var panels = new List<PanelPlace>();
            for (int i = 0; i < LayoutStore.PanelIds.Length; i++)
            {
                var win = PanelHost.OutWindows.Where(o => o.Id == (PanelId)i).Select(o => o.Win).FirstOrDefault();
                if (win == null) { panels.Add(new PanelPlace(LayoutStore.PanelIds[i], false, 0, 0, 0, 0)); continue; }
                var r = win.WindowState == WindowState.Normal ? new Rect(win.Left, win.Top, win.Width, win.Height) : win.RestoreBounds;
                panels.Add(new PanelPlace(LayoutStore.PanelIds[i], true, r.Left, r.Top, r.Width, r.Height));
            }
            LayoutStore.Save(LayoutStore.DefaultPath, new LayoutState(1, b.Left, b.Top, b.Width, b.Height,
                WindowState == WindowState.Maximized, CurrentTreeShare(), selectionViewOpen, selectionViewScene, panels));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a layout that cannot be saved is not worth an error
    }

    void OnResetLayout(object sender, RoutedEventArgs e)
    {
        layoutRestoring = true;
        try
        {
            foreach (var (id, _) in PanelHost.OutWindows) PanelHost.Dock(id);
            WindowState = WindowState.Normal;
            FitToScreen();
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            SetTreeShare(0.51);
            // the selection view's defaults too: open, without the scene around
            selectionViewOpen = true;
            selectionViewScene = false;
            SelectionScene.IsChecked = false;
            Locator.SceneAround = false;
            SelectionViewExpander.IsExpanded = true;
            try { File.Delete(LayoutStore.DefaultPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally { layoutRestoring = false; }
        selectionTimer.Stop();
        selectionTimer.Start();
    }

    // ============================================================== commands / busy state

    void SetBusy(bool on)
    {
        busy = on;
        Progress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = on;
        CommandManager.InvalidateRequerySuggested();
        UpdateExportStatesButton();
    }

    void CanIdle(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !busy;
    void CanScene(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !busy && Scene != null;
    void CanUndo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !busy && Scene != null && history.CanUndo;
    void CanRedo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !busy && Scene != null && history.CanRedo;
    void CanRun(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = !busy && Scene != null && CheckedPasses().Count > 0 && !invalidParams && (ScopeSelection.IsChecked != true || SelectedNodes().Count > 0);

    void OnTabShortcut(object sender, ExecutedRoutedEventArgs e)
    {
        if (int.TryParse(e.Parameter as string, out int i) && i >= 0 && i <= (int)PanelId.Review) ShowPanel((PanelId)i);
    }

    // ---- help: F1 and the ? button open the README at the section of the current panel

    static readonly Dictionary<string, string> HelpHeadings = new()
    {
        ["inspect"] = "Inspect", ["problems"] = "Problems", ["textures"] = "Textures", ["optimize"] = "Optimize", ["review"] = "Review",
        ["tree"] = "Scene tree", ["strategies"] = "Simplify strategies", ["passes"] = "Optimisation passes", ["layout"] = "Panels and layout",
    };

    /// <summary>The panel F1 means: the active pop-out window's panel, else the tree when it has keyboard focus, else the selected tab.</summary>
    string CurrentHelpId()
    {
        var outWin = PanelHost.OutWindows.FirstOrDefault(o => o.Win.IsActive);
        if (outWin.Win != null) return outWin.Id.ToString().ToLowerInvariant();
        if (TreeView.IsKeyboardFocusWithin) return "tree";
        if (Tabs.SelectedItem is TabItem tab)
            foreach (var id in new[] { PanelId.Inspect, PanelId.Problems, PanelId.Textures, PanelId.Optimize, PanelId.Review })
                if (ReferenceEquals(tab, id switch { PanelId.Inspect => InspectTab, PanelId.Problems => ProblemsTab, PanelId.Textures => TexturesTab,
                                                     PanelId.Optimize => OptimizeTab, _ => ReviewTab }))
                    return id.ToString().ToLowerInvariant();
        return "layout";
    }

    void OnHelp(object sender, ExecutedRoutedEventArgs e) => OpenHelp(CurrentHelpId());

    /// <summary>Online: the README on GitHub at the section; offline: the bundled copy next to the exe.</summary>
    internal void OpenHelp(string id)
    {
        bool online;
        try { online = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); } catch { online = false; }
        try
        {
            if (online)
            {
                string url = SceneKit.HelpLinks.Url(id);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                Status.Text = $"Help: {url}";
                return;
            }
            string file = System.IO.Path.Combine(AppContext.BaseDirectory, "README.md");
            if (!System.IO.File.Exists(file)) { Status.Text = "Help: no network and the bundled README.md is missing"; return; }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { System.Diagnostics.Process.Start("notepad.exe", $"\"{file}\""); }
            Status.Text = $"Help: section “{(HelpHeadings.TryGetValue(id, out var h) ? h : "Scene inspector")}” in the offline README";
        }
        catch (Exception ex) { Status.Text = $"Help could not be opened: {ex.Message}"; }
    }

    // ---- panels: in their tab or popped out into their own window (PanelHost)

    internal PanelHost PanelHost { get; }

    /// <summary>Selects the panel's tab, or brings its pop-out window to the front.</summary>
    internal void ShowPanel(PanelId p) => PanelHost.Show(p);

    /// <summary>The panel is visible: its tab is selected, or its pop-out window is open and not minimised.</summary>
    internal bool PanelShown(PanelId p) => PanelHost.IsShown(p);

    /// <summary>Owner for dialogs: the active pop-out window (a confirmation opens over the panel that asked), else the main
    /// window; when that is minimised, a visible pop-out, else the main window restored first (a dialog owned by a
    /// minimised window would not be seen).</summary>
    internal Window DialogOwner
    {
        get
        {
            if (PanelHost.OutWindows.FirstOrDefault(o => o.Win.IsActive).Win is { } active) return active;
            if (WindowState != WindowState.Minimized) return this;
            if (PanelHost.OutWindows.Select(o => o.Win).FirstOrDefault(w => w.IsVisible && w.WindowState != WindowState.Minimized) is { } shown) return shown;
            SystemCommands.RestoreWindow(this);
            return this;
        }
    }

    void OnPanelShown(PanelId p)
    {
        PreviewPending();
        if (SimilarVisible && simGroups == null && Rep != null && !busy) FindSimilar();
    }

    internal void OnPopOutButton(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<PanelId>(tag, out var p)) PanelHost.PopOut(p);
    }

    void OnViewMenu(object sender, RoutedEventArgs e)
    {
        var menu = ViewMenuButton.ContextMenu;
        foreach (var mi in menu.Items.OfType<MenuItem>())
            if (mi.Tag is string tag && Enum.TryParse<PanelId>(tag, out var p)) mi.IsChecked = PanelHost.IsOut(p);
        menu.PlacementTarget = ViewMenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void OnViewOut(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string tag || !Enum.TryParse<PanelId>(tag, out var p)) return;
        if (PanelHost.IsOut(p)) PanelHost.Dock(p);
        else PanelHost.PopOut(p);
    }

    void OnFocusFilter(object sender, ExecutedRoutedEventArgs e)
    {
        ShowPanel(PanelId.Tree);
        LeftTree.IsChecked = true;
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    // ============================================================== background work

    /// <summary>Run <paramref name="fn"/> on a worker thread (status messages go to the status line), then
    /// <paramref name="done"/> on the UI thread. One task at a time: commands are disabled meanwhile.</summary>
    internal void Work<T>(string label, Func<Action<string>, T> fn, Action<T> done)
    {
        if (busy)
        {
            Status.Text = checkpointing ? "Writing checkpoint … please wait" : "Please wait until the current task has finished.";
            return;
        }
        SetBusy(true);
        Status.Text = label;
        bool finished = false;   // a progress message arriving after the result must not overwrite it
        void ReportStatus(string msg) => Dispatcher.BeginInvoke(() => { if (!finished) Status.Text = msg.Length > 160 ? msg[..160] : msg; });
        Task.Run(() => fn(ReportStatus)).ContinueWith(t =>
        {
            finished = true;
            SetBusy(false);
            if (t.Exception != null)
            {
                var exc = t.Exception.InnerException ?? t.Exception;
                Status.Text = $"Error: {exc.Message}";
                Dialogs.Error(DialogOwner, "Something went wrong", exc);
                return;
            }
            Status.Text = "Ready";
            done(t.Result);
            if (pendingCheckpoint != null && !busy) StartCheckpoint();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Write the checkpoint of the step just done. The scene is idle meanwhile (every change goes through
    /// <see cref="Work{T}"/>, which waits); a failure is shown in the History panel, not as an error dialog.</summary>
    void StartCheckpoint()
    {
        var p = pendingCheckpoint!.Value;
        pendingCheckpoint = null;
        if (Scene == null || ckKey == null) return;
        try { ckSession ??= store.NewSession(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetCheckpointError(e.Message, write: true);
            return;
        }
        var scene = Scene;
        string key = ckKey, session = ckSession;
        int? parent = p.parent;
        string status = Status.Text;
        checkpointing = true;
        Work($"Writing checkpoint {p.index} …", _ =>
        {
            try { return (info: (CheckpointInfo?)store.Write(scene, key, session, p.index, p.label, p.scope, p.prms, parent), error: (string?)null); }
            catch (Exception e) { return (info: null, error: e.Message); }
        }, r =>
        {
            checkpointing = false;
            Status.Text = status;   // keep the message of the step itself
            if (!ReferenceEquals(scene, Scene) || key != ckKey) return;   // another database was opened meanwhile
            if (r.info != null)
            {
                if (ckErrorIsWrite) ckError = null;
                diskCheckpoints.Add(r.info);
                ckSize = null;
                FillHistory();
            }
            else SetCheckpointError(r.error ?? "unknown error", write: true);
        });
    }

    void SetCheckpointError(string message, bool write)
    {
        ckError = message;
        ckErrorIsWrite = write;
        FillHistory();
    }

    /// <summary>The database's own master file, also for a scene reloaded at a checkpoint (its master lies in the checkpoint folder,
    /// possibly in another format: the checkpoint's source, see <see cref="Scene.Origin"/>).</summary>
    static string DatabaseMaster(Scene scene) => DatabaseRoot.Master(scene);

    /// <summary>The folder the History panel and the copy manifest name: the session's folder once it exists, else the key's.</summary>
    string? CheckpointFolder()
    {
        if (ckKey == null) return null;
        string keyDir = Path.Combine(store.BaseDir, ckKey);
        return ckSession != null && Directory.Exists(Path.Combine(keyDir, ckSession)) ? Path.Combine(keyDir, ckSession) : keyDir;
    }

    // ============================================================== file actions

    void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        var menu = OpenMenuButton.ContextMenu;
        menu.PlacementTarget = OpenMenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Title = "Open scene", Filter = SceneFilter };
        if (dlg.ShowDialog(DialogOwner) == true) OpenPath(dlg.FileName);
    }

    bool ConfirmDiscard() => !modified ||
        Dialogs.Confirm(DialogOwner, "Discard unsaved changes?", "The scene has changes that are not saved. They are lost if you continue.", "Discard Changes");

    void OnReload(object sender, RoutedEventArgs e)
    {
        if (Scene == null || !ConfirmDiscard()) return;
        if (restoredInfo != null) RestoreCheckpoint(restoredInfo);   // a checkpoint state: load it again with its layers
        else OpenPath(Scene.ConvertedFrom ?? Scene.Path);
    }

    /// <summary>Load a scene; <paramref name="afterLoad"/> runs once it is loaded and shown (Save Over's reload: log and status).
    /// <paramref name="layers"/> loads a checkpoint state (<see cref="LoadOptions.Layers"/>); <paramref name="source"/> is then the
    /// database's master (the checkpoint key is computed from it).</summary>
    public void OpenPath(string path, Action? afterLoad = null, IReadOnlyList<string>? layers = null, string? source = null)
    {
        var opts = new LoadOptions { FollowProxy = FollowExt.IsChecked, FollowPaged = FollowPaged.IsChecked, Osgconv = osgconv };
        opts.CompanionPatterns = companionPatterns;
        if (layers is { Count: > 0 })
        {
            opts.Layers = layers.ToList();
            opts.Origin = source;   // the database's master this state stands for (e.g. master.ive for a checkpoint's master.osgt)
        }
        Work($"Reading {Path.GetFileName(path)} ...", progress =>
        {
            opts.Progress = progress;
            var sc = SceneIO.Load(path, opts);
            progress("analysing ...");
            return (sc, Analyzer.Analyze(sc, progress: progress));
        }, res =>
        {
            (Scene, Rep) = res;
            foreach (var ed in SimplifyEditors) ed.UnitNote = InspectorModel.UnitNote(Scene);
            CompanionHintText(Rep.Textures.Count(t => t.Companions != null));
            openSummary = Rep.Summary();
            openUnits = SceneUnits();
            boundFolder = null;
            boundMasterName = null;
            SavePathText.Text = "";
            SavePathText.ToolTip = null;
            OpenCheckpoints(source ?? DatabaseMaster(Scene));
            UpdateSaveButton();
            history.Clear();
            UpdateUndo();
            SetModified(false);
            changes = new List<Change>();
            stickyOrder = null;
            ClearChangedDots();
            optimisedOnce = false;
            reviewPills.Clear();
            ReviewTitle.Text = "No optimization run yet";
            ReviewSub.Text = "Run passes in the Optimize tab (F5). Every changed geometry is listed here with a damage check.";
            OptLog.Clear();
            FillChanges();
            ClearFilter(refill: false);
            RefreshAll();
            ShowWarnings();
            afterLoad?.Invoke();
        });
    }

    /// <summary>After a load: the database's checkpoint key and the checkpoints on disk. The session goes on when the same
    /// database is opened again (a restore, the reload after Save Over); another database starts a new one (lazily).</summary>
    void OpenCheckpoints(string databaseMaster)
    {
        string? oldKey = ckKey;
        ckError = null;
        ckErrorIsWrite = false;
        try
        {
            ckKey = CheckpointStore.KeyFor(databaseMaster);
            diskCheckpoints = store.List(ckKey);
        }
        catch (Exception e)
        {
            ckKey = null;
            diskCheckpoints = new List<CheckpointInfo>();
            ckError = $"Checkpoints are not available: {e.Message}";
        }
        if (ckKey == null || ckKey != oldKey) ckSession = null;
        ckIndex = diskCheckpoints.Where(c => c.Session == ckSession && !c.IsBackup).Select(c => c.Index).DefaultIfEmpty(0).Max();
        ckParent = null;
        ckSize = null;
        pendingCheckpoint = null;
        memIndices.Clear();
        droppedTop = null;
        restoredInfo = null;
        loadedState = null;
    }

    /// <summary>Reload the database at a checkpoint (its files over the database folder); the undo list starts empty.</summary>
    void RestoreCheckpoint(CheckpointInfo info)
    {
        string? session = ckSession;
        OpenPath(info.Master, () =>
        {
            if (info.Session == session && session == ckSession) ckParent = info.IsBackup ? null : info.Index;
            restoredInfo = info;
            loadedState = (info.Index, info.Label);
            UpdateTitle();
            FillHistory();
            Status.Text = $"Reloaded at state {info.Index}: {info.Label}";
            Log($"Reloaded at state {info.Index}: {info.Label} ({info.Folder})");
        }, info.Layers, info.Source);
    }

    void ShowWarnings()
    {
        int n = Scene?.Warnings.Count ?? 0;
        WarningsButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningsButton.Content = $"{PyFormat.Int(n)} warning{(n == 1 ? "" : "s")}";
        if (n > 0) Status.Text = $"Loaded with {n} warning{(n == 1 ? "" : "s")} - see the button at the top right";
    }

    void OnShowWarnings(object sender, RoutedEventArgs e)
    {
        if (Scene == null || Scene.Warnings.Count == 0) return;
        Dialogs.List(DialogOwner, "Load warnings", $"{Scene.Warnings.Count} warning(s) while reading {Path.GetFileName(Scene.ConvertedFrom ?? Scene.Path)}:",
            Scene.Warnings);
    }

    void SetModified(bool on)
    {
        modified = on;
        UpdateTitle();
        ModifiedChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Style = (Style)FindResource(on ? "Button.Accent" : "Button.Ghost");
    }

    void UpdateTitle()
    {
        string name = Scene != null ? Path.GetFileName(Scene.ConvertedFrom ?? Scene.Path) : "";
        string bound = boundFolder != null ? $" [copy: {Path.GetFileName(boundFolder)}]" : "";
        string state = loadedState is { } ls && Scene?.Layers != null ? $" (state {ls.index}: {ls.label})" : "";
        Title = name.Length > 0 ? $"{name}{state}{(modified ? " •" : "")}{bound} - Scene Inspector" : "Scene Inspector";
    }

    /// <summary>Ctrl+S: "Save" to the bound copy folder after a Save Copy, else "Save Over Opened Database…".</summary>
    void UpdateSaveButton()
    {
        if (boundFolder != null)
        {
            SaveButton.Content = "Save";
            SaveButton.ToolTip = $"Save to the copy in {boundFolder} (Ctrl+S)";
        }
        else
        {
            SaveButton.Content = "Save Over Opened Database…";
            SaveButton.ToolTip = "Overwrite the opened files after backing them up (Ctrl+S)";
        }
        UpdateTitle();
    }

    /// <summary>Ctrl+S: write the copy folder again (after a Save Copy), otherwise Save Over Opened Database.</summary>
    void OnSave(object sender, RoutedEventArgs e)
    {
        if (Scene == null) return;
        if (boundFolder != null) SaveCopyTo(boundFolder, boundMasterName, boundIncludeUnchanged);
        else SaveOverOpened();
    }

    /// <summary>Ctrl+Shift+S: Save Copy to Folder.</summary>
    void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (Scene == null) return;
        string defName = Path.GetFileNameWithoutExtension(Scene.Path) + OsgWrite.NativeExt(Scene);
        var answer = Dialogs.AskSaveCopy(DialogOwner, boundFolder ?? SaveCopy.DefaultFolder(Scene), boundMasterName ?? defName,
            boundIncludeUnchanged, DatabaseRoot.Of(Scene));
        if (answer is not { } a) return;
        var (folder, masterName, include) = a;
        SaveCopyTo(folder, string.Equals(masterName, defName, StringComparison.Ordinal) ? null : masterName, include);
    }

    /// <summary>Write a self-contained copy of the database to <paramref name="folder"/> and bind Ctrl+S to it.</summary>
    void SaveCopyTo(string folder, string? masterName, bool includeUnchanged)
    {
        var scene = Scene!;
        int warnings0 = scene.Warnings.Count;
        var lines = history.Entries().Select(x => $"{x.Time:HH:mm:ss}  {History.Describe(x)}").ToList();
        if (CheckpointFolder() is string ckFolder) lines.Add($"checkpoints: {ckFolder}");
        Work($"Saving copy to {folder} …", progress => SaveCopy.ToFolder(scene, folder, new SaveCopyOptions
        {
            IncludeUnchanged = includeUnchanged, MasterName = masterName, Osgconv = osgconv, Progress = progress, HistoryLines = lines,
        }), res =>
        {
            boundFolder = folder;
            boundMasterName = masterName;
            boundIncludeUnchanged = includeUnchanged;
            SetModified(false);
            UpdateSaveButton();
            int changed = res.Files.Count(f => f.kind == "changed");
            Status.Text = $"Saved copy to {folder} ({res.Files.Count} files, {changed} changed)";
            SavePathText.Text = $"Copy: {folder}";
            SavePathText.ToolTip = res.Master;
            Log($"Saved copy: {res.Master} ({res.Files.Count} files, {changed} changed)");
            Log($"  manifest: {res.Manifest}");
            foreach (var w in scene.Warnings.Skip(warnings0)) Log($"  warning: {w}");
            if (scene.Warnings.Count > warnings0)
            {
                int nw = scene.Warnings.Count;
                WarningsButton.Visibility = Visibility.Visible;
                WarningsButton.Content = $"{PyFormat.Int(nw)} warning{(nw == 1 ? "" : "s")}";
            }
        });
    }

    /// <summary>Save Over Opened Database: confirm, back up every file it touches, overwrite them, reload.</summary>
    void SaveOverOpened()
    {
        var scene = Scene!;
        var plan = SaveOver.Plan(scene);
        if (plan.Unsupported.Count > 0)
        {
            Dialogs.Warn(DialogOwner, "Cannot save over the opened database", SaveOver.UnsupportedMessage(plan));
            return;
        }
        if (plan.Files.Count == 0 && plan.Folders.Count == 0 && plan.Copies.Count == 0)
        {
            Dialogs.Info(DialogOwner, "Nothing to save", "The opened database has no changes to write.");
            return;
        }
        int n = plan.Files.Count;
        string names = string.Join(", ", plan.Files.Take(5).Select(Path.GetFileName)) + (n > 5 ? ", …" : "");
        string copies = plan.Copies.Count > 0 ? $"\n{plan.Copies.Count} file(s) of the restored state (images and the like) are copied into the database folder." : "";
        if (!Dialogs.Confirm(DialogOwner, "Save over the opened database?",
                $"{n} file(s) will be overwritten: {names}{copies}\nA copy of each goes to {Path.GetFileName(plan.BackupFolder)} first. " +
                "The database is reloaded afterwards.", "Back Up and Save Over"))
            return;
        string master = DatabaseMaster(scene);   // also when a checkpoint state is loaded: the save goes to the database
        Work("Saving over …", progress =>
        {
            var r = SaveOver.Run(scene, plan, osgconv, progress);
            string? key = null;
            try { key = CheckpointStore.KeyFor(master); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return (res: r, newKey: key);
        }, done =>
        {
            var (res, newKey) = done;
            SetModified(false);
            int? keepParent = CurrentCheckpoint(history.UndoCount);   // the reload forgets the steps: later ones chain onto this state
            string? ckFailure = RekeyAfterSaveOver(newKey, res.BackupFolder, master);
            OpenPath(master, () =>   // the reload clears the log: write it afterwards
            {
                if (ckKey != null && ckSession != null) { ckParent = keepParent; FillHistory(); }
                Log($"Saved over {res.Written.Count} file(s); backup in {res.BackupFolder}");
                foreach (var f in res.Written) Log($"  {f}");
                Status.Text = $"Saved over and reloaded; backup in {res.BackupFolder}";
                if (ckFailure != null)
                {
                    Log($"  checkpoints: {ckFailure}");
                    SetCheckpointError(ckFailure, write: true);
                }
            });
        });
    }

    /// <summary>After Save Over the database has a new key: move the checkpoints there (they now sit on top of the backup) and
    /// add the entry for the database as it was before the save. Returns the failure message, if any.</summary>
    string? RekeyAfterSaveOver(string? newKey, string backupFolder, string master)
    {
        if (ckKey == null) return null;   // no checkpoints for this database (the History panel says why)
        if (newKey == null) return "the saved database could not be read";
        try
        {
            if (newKey != ckKey) store.Rekey(ckKey, newKey, backupFolder);
            else
            {
                // the master file kept its key (only other files were written): move the sessions out and back in, so that
                // their base roots still get the backup folder (Rekey must not move a key onto itself)
                string tmp = ckKey + "_rekey";
                store.Rekey(ckKey, tmp, backupFolder);
                store.Rekey(tmp, newKey, backupFolder);   // adds the backup a second time; the layer list drops duplicates
            }
            ckSession ??= store.NewSession();
            store.AddBackupEntry(newKey, ckSession, backupFolder, Path.GetFileName(master), "before this session's save over", master);
            ckKey = newKey;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ckKey = newKey;
            return e.Message;
        }
    }

    void OnExportReport(object sender, RoutedEventArgs e)
    {
        if (Rep == null || Scene == null) return;
        string b = Path.ChangeExtension(Scene.ConvertedFrom ?? Scene.Path, null);
        var dlg = new SaveFileDialog
        {
            Title = "Export report", FileName = Path.GetFileName(b) + ".report.txt", InitialDirectory = Path.GetDirectoryName(b),
            Filter = "Text|*.txt|JSON|*.json",
        };
        if (dlg.ShowDialog(DialogOwner) != true) return;
        string text = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ReportText.Json(Rep) : ReportText.Text(Rep);
        File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(false));
        Status.Text = $"Report written: {dlg.FileName}";
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        else SaveLayout();
    }

    // ============================================================== display

    void RefreshAll()
    {
        if (Rep == null) return;
        index = new InspectorModel.FindingIndex(Rep);
        InvalidateSimilar();
        UpdateKpis();
        if (filterMatch != null) ComputeFilter();
        FillTree();
        FillFindings();
        FillHotspots();
        FillTextures();
        FillMap();
        UpdateRunButton();
    }

    // ---- KPI tiles

    static long L(Dictionary<string, object?> s, string k) => Convert.ToInt64(s[k]);

    void UpdateKpis()
    {
        if (Rep == null || Scene == null) return;
        var s = Rep.Summary();
        var was = openSummary ?? s;
        KpiScene.Text = Path.GetFileName(Scene.ConvertedFrom ?? Scene.Path);
        KpiScene.ToolTip = Scene.ConvertedFrom ?? Scene.Path;
        KpiSceneSub.Text = $"{PyFormat.Int(L(s, "nodes"))} nodes · {s["format"]}";
        Kpi(KpiTris, KpiTrisSub, s, was, "triangles", false, $"{Analyzer.HumanCount(L(s, "visible_triangles"))} visible");
        Kpi(KpiDraws, KpiDrawsSub, s, was, "draw_calls", false, $"{PyFormat.Int(L(s, "unique_geometries"))} unique geometries");
        Kpi(KpiGeo, KpiGeoSub, s, was, "geometry_bytes", true, $"{Analyzer.HumanCount(L(s, "vertices"))} vertices");
        Kpi(KpiTex, KpiTexSub, s, was, "texture_bytes", true, $"{PyFormat.Int(L(s, "textures"))} textures");
        // texture units the scene draws with, and how that changed since open (a pass may drop a unit)
        ulong units = SceneUnits(), unitsWas = openUnits ?? units;
        if (units != 0 || unitsWas != 0)
        {
            KpiTexSub.Text += units == unitsWas ? $" · units {UnitRange(units)}" : $" · units {UnitRange(unitsWas)} → {UnitRange(units)}";
            if (units != unitsWas)
            {
                KpiTexSub.Foreground = Ui.Brush((units & ~unitsWas) == 0 && units != unitsWas ? "Sev.Med" : "Text");
                KpiTexSub.ToolTip = $"Texture units drawn: {UnitRange(units)} (at open: {UnitRange(unitsWas)})";
            }
        }

        var fc = (Dictionary<string, int>)s["findings"]!;
        var fw = (Dictionary<string, int>)was["findings"]!;
        KpiHigh.Text = $"{fc["high"]} High";
        KpiMed.Text = $"{fc["medium"]} Medium";
        KpiLow.Text = $"{fc["low"]} Low";
        int now = fc["high"] + fc["medium"] + fc["low"], before = fw["high"] + fw["medium"] + fw["low"];
        if (now != before)
        {
            KpiFindSub.Text = $"{(now < before ? "▼" : "▲")} {Math.Abs(now - before)} since open (was {before})";
            KpiFindSub.Foreground = Ui.Brush(now < before ? "Good" : "Sev.Med");
        }
        else
        {
            KpiFindSub.Text = fc["info"] > 0 ? $"+ {fc["info"]} info" : "click to see them";
            KpiFindSub.Foreground = Ui.Brush("TextMuted");
        }
        int urgent = now;   // same sum as the High + Medium + Low tiles
        ProblemsBadge.Visibility = urgent > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemsCount.Text = urgent.ToString(CultureInfo.InvariantCulture);
        long objects = Rep?.Findings.Where(f => f.Severity != "info").Sum(f => (long)f.Nodes.Count) ?? 0;
        ProblemsBadge.ToolTip = $"{urgent} findings (groups), {objects} objects";
        ProblemsBadge.Background = Ui.Brush(fc["high"] > 0 ? "Sev.High.Tint" : "Sev.Med.Tint");
        ProblemsCount.Foreground = Ui.Brush(fc["high"] > 0 ? "Sev.High" : "Sev.Med");
    }

    /// <summary>Texture units the geometries of the scene draw with (bit u = unit u).</summary>
    ulong SceneUnits() => Rep?.Scene.Root is Node root ? Rep.TexIndex.Subtree(root).Units : 0;

    /// <summary>"0–1", "0, 2", "none".</summary>
    static string UnitRange(ulong mask)
    {
        var u = Enumerable.Range(0, 64).Where(i => (mask >> i & 1) != 0).ToList();
        if (u.Count == 0) return "none";
        if (u.Count == 1) return u[0].ToString(CultureInfo.InvariantCulture);
        return u[^1] - u[0] == u.Count - 1 ? $"{u[0]}–{u[^1]}" : string.Join(", ", u);
    }

    /// <summary>Value with the change since open below it (green when smaller), or the hint when unchanged.</summary>
    static void Kpi(TextBlock value, TextBlock sub, Dictionary<string, object?> s, Dictionary<string, object?> was, string key, bool bytes, string hint)
    {
        long a = L(s, key), b = L(was, key);
        value.Text = bytes ? Analyzer.HumanBytes(a) : Analyzer.HumanCount(a);
        if (a != b)
        {
            double rel = b != 0 ? Math.Abs((a - b) / (double)b) : 0;
            string pct = b != 0 ? PyFormat.Percent(rel, rel < 0.01 ? 1 : 0) : "new";   // the arrow gives the direction
            sub.Text = $"{(a < b ? "▼" : "▲")} {pct} since open";
            sub.ToolTip = $"was {(bytes ? Analyzer.HumanBytes(b) : PyFormat.Int(b))}";
            sub.Foreground = Ui.Brush(a < b ? "Good" : "Sev.Med");
        }
        else
        {
            sub.Text = hint;
            sub.ToolTip = null;
            sub.Foreground = Ui.Brush("TextMuted");
        }
    }

    void OnKpiFindings(object sender, MouseButtonEventArgs e)
    {
        ShowPanel(PanelId.Problems);
        ProbFindings.IsChecked = true;
    }

    // ---- tree order

    string OrderMode = "cost";
    bool orderReverse;

    /// <summary>Header click: sort by that column; a second click on the same column flips the direction.</summary>
    internal void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: GridViewColumn col }) return;   // also skips the filler header
        if (col.Header is not GridViewColumnHeader { Tag: string mode }) return;
        SetOrder(mode, mode == OrderMode && !orderReverse);
    }

    /// <summary>Header context menu of the count column: show (and sort by) triangles, visible triangles or vertices.</summary>
    internal void OnCountColumn(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode, Header: string label }) return;
        CountHeader.Tag = mode;
        CountHeader.Content = mode == "vis" ? "Visible" : label;
        SetOrder(mode, false);
    }

    void SetOrder(string mode, bool reverse)
    {
        OrderMode = mode;
        orderReverse = reverse;
        stickyOrder = null;
        bool asc = InspectorModel.AscendingByDefault(mode) != reverse;
        foreach (var col in ((GridView)Tree.View).Columns)
        {
            if (col.Header is not GridViewColumnHeader h) continue;
            var label = ((string)h.Content).TrimEnd(' ', '▴', '▾');
            h.Content = (h.Tag as string) == mode ? label + (asc ? " ▴" : " ▾") : label;
        }
        if (Rep != null) FillTree();
    }

    // ---- tree

    /// <summary>Where the rows were before a run: the tree keeps that order while the run's numbers change, until a header is
    /// clicked or another scene is opened. Keyed by every node a row shows (a folded row stands for its whole chain).</summary>
    Dictionary<Node, int>? stickyOrder;
    /// <summary>Nodes the last run changed, and their first-parent ancestors (the hollow dot).</summary>
    readonly HashSet<Node> changedNodes = new(ReferenceEqualityComparer.Instance), changedAbove = new(ReferenceEqualityComparer.Instance);

    void ClearChangedDots()
    {
        changedNodes.Clear();
        changedAbove.Clear();
        foreach (var r in rows) { r.Changed = false; r.ChangedBelow = false; }
    }

    void MarkChangedDots(IEnumerable<Node> nodes)
    {
        changedNodes.Clear();
        changedAbove.Clear();
        foreach (var n in nodes)
        {
            changedNodes.Add(n);
            var p = Rep!.Parents.TryGetValue(n, out var ps) && ps.Count > 0 ? ps[0] : null;
            while (p != null && changedAbove.Add(p))
                p = Rep.Parents.TryGetValue(p, out ps) && ps.Count > 0 ? ps[0] : null;
        }
    }

    /// <summary>The row for the tree position of head; with Fold chains on, its single-child chain folds into it.</summary>
    TreeRow MakeRow(Node head, int depth)
    {
        var chain = FoldChains.IsChecked == true ? InspectorModel.FoldChain(head, Rep!.Parents) : new List<Node> { head };
        var n = chain[^1];
        var r = InspectorModel.RowFor(Rep!, index!, chain);
        var st = Rep!.Stat(n);
        var tot = Rep.Total;
        double share = Math.Max(st.Tris / (double)Math.Max(tot.Tris, 1), st.Bytes / (double)Math.Max(tot.Bytes, 1));
        string notes = r.Notes;
        foreach (var mark in InspectorModel.SevMark.Values)
            if (notes.StartsWith(mark + " ", StringComparison.Ordinal)) { notes = notes[(mark.Length + 1)..]; break; }
        Brush fg = r.External ? Ui.Brush("External") : r.Hidden ? Ui.Brush("TextDisabled") : Ui.Brush("Text");
        return new TreeRow
        {
            Node = n, ChainNodes = chain, FullPath = Rep.PathOf(n),
            Chain = string.Concat(chain.Take(chain.Count - 1).Select(c => (c.Name.Length > 0 ? c.Name : c.Cls) + " › ")),
            Depth = depth, HasChildren = r.HasChildren, Text = r.Text, Share = share, Tris = r.Tris, Vis = r.Vis,
            Verts = r.Verts, Count = (CountHeader.Tag as string) switch { "vis" => r.Vis, "verts" => r.Verts, _ => r.Tris }, Draws = r.Draws, Geo = r.Geo, Tex = r.Tex, Inst = r.Inst, Units = r.Units, Notes = notes, Severity = r.Severity, Foreground = fg,
            Changed = chain.Any(changedNodes.Contains), ChangedBelow = chain.Any(changedAbove.Contains),
        };
    }

    void FillTree()
    {
        var keep = Tree.SelectedItems.Cast<TreeRow>().Select(r => r.Node).OfType<Node>().ToList();
        // re-open what was open (re-sorting or an edit should not fold the tree up)
        // (all nodes of a folded row: they stay open when Fold chains is switched off)
        var open = new HashSet<Node>(rows.Where(r => r.Expanded).SelectMany(r => r.ChainNodes), ReferenceEqualityComparer.Instance);
        if (filterAncestors != null) open.UnionWith(filterAncestors);
        rows.Clear();
        if (Scene?.Root == null || Rep == null) return;
        var root = MakeRow(Scene.Root, 0);
        rows.Add(root);
        Expand(root, open);
        if (keep.Count > 0)
        {
            var alive = new HashSet<Node>(Scene.IterNodes(externals: true), ReferenceEqualityComparer.Instance);
            keep = keep.Where(n => alive.Contains(n) && !ReferenceEquals(n, Scene.Root)).ToList();
        }
        if (keep.Count > 0) Reveal(keep);   // stay on the nodes that were selected before an edit / undo
        else Tree.SelectedItem = root;
    }

    /// <summary>Show the children of row; children whose node is in reopen are expanded the same way. With a filter,
    /// nodes on the way to a match only show the children that lead to matches; a match shows all its children.</summary>
    void Expand(TreeRow row, HashSet<Node>? reopen = null)
    {
        if (!row.HasChildren || row.Expanded || row.Node == null) return;
        row.Expanded = true;
        int at = rows.IndexOf(row) + 1;
        var kids = InspectorModel.SortedChildren(Rep!, row.Node, OrderMode, orderReverse, index, stickyOrder);
        if (filterShow != null && filterMatch != null && !row.ChainNodes.Any(filterMatch.Contains)) kids = kids.Where(filterShow.Contains).ToList();
        const int limit = 2000;
        var add = kids.Take(limit).Select(c => MakeRow(c, row.Depth + 1)).ToList();
        if (kids.Count > limit) add.Add(new TreeRow { Depth = row.Depth + 1, Text = $"... {kids.Count - limit} more (sorted out)", Foreground = Ui.Brush("TextMuted") });
        foreach (var r in add) rows.Insert(at++, r);
        if (reopen == null) return;
        foreach (var r in add)
            if (r.Node != null && reopen.Remove(r.Node)) Expand(r, reopen);   // Remove: a shared node re-opens once
    }

    void Collapse(TreeRow row)
    {
        if (!row.Expanded) return;
        row.Expanded = false;
        int i = rows.IndexOf(row) + 1;
        while (i < rows.Count && rows[i].Depth > row.Depth) rows.RemoveAt(i);
    }

    void Toggle(TreeRow row)
    {
        if (row.Expanded) Collapse(row); else Expand(row);
    }

    internal void OnGlyphClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TreeRow r && r.HasChildren)
        {
            Toggle(r);
            e.Handled = true;
        }
    }

    internal void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListViewItem)?.DataContext is TreeRow r && r.HasChildren) Toggle(r);
    }

    internal void OnTreeKey(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not TreeRow r) return;
        if (e.Key == Key.Right) { Expand(r); e.Handled = true; }
        else if (e.Key == Key.Left)
        {
            if (r.Expanded) Collapse(r);
            else
            {
                int i = rows.IndexOf(r) - 1;
                while (i >= 0 && rows[i].Depth >= r.Depth) i--;
                if (i >= 0) { Tree.SelectedItem = rows[i]; Tree.ScrollIntoView(rows[i]); }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete) { OnDeleteSelected(sender, e); e.Handled = true; }
    }

    /// <summary>The child row of parent whose (folded) chain starts at n.</summary>
    TreeRow? ChildRow(TreeRow parent, Node n)
    {
        int i = rows.IndexOf(parent) + 1;
        for (; i < rows.Count && rows[i].Depth > parent.Depth; i++)
            if (rows[i].Depth == parent.Depth + 1 && rows[i].ChainNodes.Count > 0 && ReferenceEquals(rows[i].ChainNodes[0], n)) return rows[i];
        return null;
    }

    /// <summary>The row of a node, expanding the tree along its first-parent path (the deepest reachable row). A node inside
    /// a folded chain resolves to the folded row.</summary>
    TreeRow? RowFor(Node target)
    {
        if (Rep == null || rows.Count == 0) return null;
        var chain = InspectorModel.ChainTo(Rep, target);
        if (chain == null) return null;
        var row = rows[0];
        // the path nodes the row covers (a single-child chain: the path runs through all of them)
        int k = row.ChainNodes.Count;
        while (k < chain.Count)
        {
            Expand(row);
            var next = ChildRow(row, chain[k]);
            if (next == null) return row;
            row = next;
            k += row.ChainNodes.Count;
        }
        return row;
    }

    internal void Reveal(IEnumerable<Node> nodes, int limit = 50)
    {
        var list = nodes.Take(limit).ToList();
        // a node the filter hides: drop the filter rather than select something else
        if (filterShow != null && list.Any(n => !filterShow.Contains(n))) ClearFilter(refill: true);
        var found = list.Select(RowFor).OfType<TreeRow>().Distinct().ToList();
        if (found.Count == 0) return;
        Tree.SelectedItems.Clear();
        foreach (var r in found) Tree.SelectedItems.Add(r);
        Tree.ScrollIntoView(found[0]);
    }

    internal void OnExpandBiggest(object sender, RoutedEventArgs e)
    {
        if (rows.Count == 0) return;
        var row = Tree.SelectedItem as TreeRow ?? rows[0];
        for (int k = 0; k < 64; k++)
        {
            Expand(row);
            var kids = new List<TreeRow>();
            for (int i = rows.IndexOf(row) + 1; i < rows.Count && rows[i].Depth > row.Depth; i++)
                if (rows[i].Depth == row.Depth + 1 && rows[i].Node != null) kids.Add(rows[i]);
            if (kids.Count == 0) break;
            bool firstIsBiggest = OrderMode != "scene" && OrderMode != "name" && OrderMode != "findings" && !orderReverse;
            row = firstIsBiggest ? kids[0] : kids.OrderByDescending(r => Rep!.Stat(r.Node!).Tris).First();
        }
        Tree.SelectedItems.Clear();
        Tree.SelectedItem = row;
        Tree.ScrollIntoView(row);
    }

    internal void OnTreeSelect(object sender, SelectionChangedEventArgs e)
    {
        UpdateScopeFromSelection();
        proxyEstimateDue = true;   // the estimate follows with the render, after the clicks / arrow keys settle
        if (appliedNode != null && !(Tree.SelectedItem is TreeRow { Node: Node cur } && ReferenceEquals(cur, appliedNode) && Tree.SelectedItems.Count == 1))
        {
            appliedNode = null;
            Status.Text = "Ready";   // the "Applied: ..." line goes with the selection it was about
        }
        selectionTimer.Stop();   // one render per selection change, after the clicks / arrow keys settle
        selectionTimer.Start();
        if (Tree.SelectedItem is not TreeRow { Node: Node node } || Rep == null) return;
        DetailsBox.Text = InspectorModel.Details(Rep, index!, node);
        DetailsTitle.Text = ReportText.NodeLabel(node);
        FillUnitCards(node);
        if (ReferenceEquals(node, Preview.Node) || DateTime.Now < skipPreviewUntil) return;   // keeps a before / after comparison
        PendingPreview = node;
        PreviewPending();
    }

    internal void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        if (DetailsBox.Text.Length == 0) return;
        try { Clipboard.SetText(DetailsBox.Text); Status.Text = "Details copied"; }
        catch (Exception) { Status.Text = "The clipboard is busy, try again"; }
    }

    /// <summary>The ancestors (root first) of the selected tree row showing <paramref name="node"/>: for a shared node this
    /// is the place that was clicked. Null when the node is not the selected row.</summary>
    internal List<Node>? SelectedAncestors(Node node)
    {
        if (Tree.SelectedItem is not TreeRow { Node: Node sel } row || !ReferenceEquals(sel, node)) return null;
        var path = new List<Node>(row.ChainNodes.Take(row.ChainNodes.Count - 1).Reverse());   // folded nodes above it
        int depth = row.Depth;
        for (int i = rows.IndexOf(row) - 1; i >= 0 && depth > 0; i--)
            if (rows[i].Depth < depth && rows[i].Node != null)
            {
                path.AddRange(Enumerable.Reverse(rows[i].ChainNodes));
                depth = rows[i].Depth;
            }
        path.Reverse();
        return path;
    }

    // ---- selection view under the tree ("what did I click")

    readonly DispatcherTimer selectionTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    void InitSelectionView()
    {
        Locator.Attach(this);
        Locator.SceneAround = selectionViewScene;
        SelectionScene.IsChecked = selectionViewScene;
        SelectionViewExpander.IsExpanded = selectionViewOpen;
        Locator.Opened += OnLocatorOpened;
        selectionTimer.Tick += (_, _) =>
        {
            selectionTimer.Stop();
            UpdateSelectionView();
            if (proxyEstimateDue) RefreshProxyEstimate();
        };
    }

    /// <summary>Picture and caption of the selected nodes; independent of the Inspect tab (it is always on screen).</summary>
    void UpdateSelectionView()
    {
        if (!SelectionViewExpander.IsExpanded)
        {
            captionReq++;
            SelectionCaption.Text = "";   // not computed while closed: no stale name in the header
            return;
        }
        var rep = Rep;
        var nodes = rep != null ? SelectedNodes() : new List<Node>();
        Locator.ShowNodes(nodes);
        int my = ++captionReq;
        if (rep == null || nodes.Count == 0) { SelectionCaption.Text = "Nothing selected"; return; }
        // the texture count walks the whole subtree (the root after opening): on a worker, like the picture
        Task.Run(() => Selection.Caption(rep, nodes)).ContinueWith(t =>
        {
            if (my == captionReq) SelectionCaption.Text = t.Exception == null ? t.Result : "";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    int captionReq;

    internal void OnSelectionViewToggled(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != SelectionViewExpander) return;   // Expanded / Collapsed bubble up from nested expanders
        selectionViewOpen = SelectionViewExpander.IsExpanded;
        SaveLayout();
        selectionTimer.Stop();
        selectionTimer.Start();
    }

    internal void OnSelectionScene(object sender, RoutedEventArgs e)
    {
        selectionViewScene = SelectionScene.IsChecked == true;
        Locator.SceneAround = selectionViewScene;
        SaveLayout();
        selectionTimer.Stop();
        selectionTimer.Start();
    }

    void OnLocatorOpened()
    {
        if (Locator.Node is not Node first) return;
        PendingPreview = null;
        ShowPanel(PanelId.Inspect);
        Preview.ShowNode(first);
    }

    /// <summary>Render the selected node only while the 3D preview is visible (big nodes take a moment).</summary>
    internal void PreviewPending()
    {
        if (PendingPreview == null || !PanelShown(PanelId.Inspect)) return;
        var n = PendingPreview;
        PendingPreview = null;
        Preview.ShowNode(n);
    }

    void OnPreviewTry(object sender, RoutedEventArgs e)
    {
        var nodes = SelectedNodes();
        if (nodes.Count == 0) return;
        PendingPreview = null;
        ShowPanel(PanelId.Inspect);
        Preview.ShowNode(nodes[0]);
        Preview.TrySimplify();
    }

    // ---- left pane: tree or map

    internal void OnLeftMode(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || MapPanel == null) return;
        bool map = LeftMap.IsChecked == true;
        Tree.Visibility = TreeTools.Visibility = map ? Visibility.Collapsed : Visibility.Visible;
        MapPanel.Visibility = MapTools.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
        if (map) Dispatcher.BeginInvoke(FillMap, DispatcherPriority.Loaded);
    }

    // ---- tree filter

    readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    HashSet<Node>? filterMatch, filterShow, filterAncestors;
    bool suppressFilter;
    const int MaxFilterMatches = 2000;

    internal void OnFilterText(object sender, TextChangedEventArgs e)
    {
        if (suppressFilter) return;
        filterTimer.Stop();
        filterTimer.Start();
    }

    internal void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!suppressFilter && IsLoaded) ApplyFilter();
    }

    internal void OnFoldChains(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && Rep != null) FillTree();
    }

    internal void OnFilterKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClearFilter(refill: true); e.Handled = true; }
        else if (e.Key == Key.Enter) { filterTimer.Stop(); ApplyFilter(); Tree.Focus(); e.Handled = true; }
        else if (e.Key == Key.Down) { Tree.Focus(); e.Handled = true; }
    }

    void ClearFilter(bool refill)
    {
        suppressFilter = true;
        FilterBox.Text = "";
        OnlyFindings.IsChecked = false;
        suppressFilter = false;
        filterTimer.Stop();
        filterMatch = filterShow = filterAncestors = null;
        if (refill) FillTree();
    }

    void ApplyFilter()
    {
        if (Rep == null) return;
        ComputeFilter();
        rows.Clear();   // fold the tree: the filter decides what is open
        FillTree();
        if (filterMatch != null)
        {
            var first = rows.FirstOrDefault(r => r.ChainNodes.Any(filterMatch.Contains));
            if (first != null) { Tree.SelectedItem = first; Tree.ScrollIntoView(first); }
            Status.Text = filterMatch.Count == 0 ? "No node matches the filter" :
                $"{PyFormat.Int(filterMatch.Count)}{(filterMatch.Count >= MaxFilterMatches ? "+" : "")} matching node(s)";
            Ui.SetEmptyText(Tree, "No node matches the filter (Esc clears it).");
        }
    }

    /// <summary>Matches = nodes whose name or class contains the text (and that have findings, when asked for);
    /// shown = matches and the nodes on the way to them.</summary>
    void ComputeFilter()
    {
        string text = FilterBox.Text.Trim();
        bool withFindings = OnlyFindings.IsChecked == true;
        if (Rep == null || (text.Length == 0 && !withFindings) || index == null)
        {
            filterMatch = filterShow = filterAncestors = null;
            return;
        }
        var match = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var n in Rep.AllNodes())
        {
            if (text.Length > 0 && n.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0 &&
                n.Cls.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (withFindings && index.Of(n).Count == 0) continue;
            match.Add(n);
            if (match.Count >= MaxFilterMatches) break;
        }
        var anc = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var n in match)
        {
            Node? cur = Rep.Parents.TryGetValue(n, out var ps) && ps.Count > 0 ? ps[0] : null;
            while (cur != null && anc.Add(cur)) cur = Rep.Parents.TryGetValue(cur, out var pp) && pp.Count > 0 ? pp[0] : null;
        }
        filterMatch = match;
        filterAncestors = anc;
        filterShow = new HashSet<Node>(match, ReferenceEqualityComparer.Instance);
        filterShow.UnionWith(anc);
        if (Scene?.Root != null) filterShow.Add(Scene.Root);
    }

    // ---- context menu (built from the registered passes, with the current settings)

    internal void OnTreeMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = TreeMenu;
        menu.Items.Clear();
        bool any = SelectedNodes().Count > 0 && !busy;

        menu.Items.Add(MenuEntry("Preview Simplify…", "", OnPreviewTry, any, tip: "Try simplify on the first selected node in the 3D preview (nothing changes until you apply)"));
        var opt = MenuEntry("Optimize Selected", "", null, any);
        var prms = CollectParams();
        string? lastGroup = null;
        foreach (var spec in Optimizer.PipelineSort(Optimizer.Passes.Keys).Select(n => Optimizer.Passes[n]).OrderBy(s => Array.IndexOf(GroupOrder, s.Group)))
        {
            if (lastGroup != null && spec.Group != lastGroup) opt.Items.Add(new Separator());
            lastGroup = spec.Group;
            var mode = spec.Params.FirstOrDefault(p => p.Key == "mode" && p.Choices != null);
            if (mode != null)
            {
                foreach (var choice in mode.Choices!)
                    opt.Items.Add(PassEntry(spec, $"{spec.Label} ({choice})", $"{spec.Name}:{choice}", ParamSummary(spec, prms, skip: "mode")));
            }
            else opt.Items.Add(PassEntry(spec, spec.Label, spec.Name, ParamSummary(spec, prms)));
        }
        opt.Items.Add(new Separator());
        opt.Items.Add(MenuEntry("Configure Passes…", "", (_, _) =>
        {
            ScopeSelection.IsChecked = true;
            ShowPanel(PanelId.Optimize);
        }, true));
        menu.Items.Add(opt);
        menu.Items.Add(MenuEntry("Select Similar Objects", "", OnSelectSimilar, any && Rep != null,
            tip: "Find copies of this object (or the nearest object above it) elsewhere in the scene and show them in Problems › Similar"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Delete Selected", "", OnDeleteSelected, any, gesture: "Del"));
        menu.Items.Add(MenuEntry("Hide Selected (Node Mask 0)", "", OnSetMask, any, tag: "hide"));
        menu.Items.Add(MenuEntry("Unhide Selected", "", OnSetMask, any, tag: "show"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Expand Most Expensive Path", "", OnExpandBiggest, rows.Count > 0));
    }

    static readonly string[] GroupOrder = { "cleanup", "memory", "draw calls", "speed", "precision", "detail", "textures" };

    static MenuItem MenuEntry(string header, string? icon, RoutedEventHandler? click, bool enabled, string? gesture = null, string? tag = null, string? tip = null)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled, Tag = tag, InputGestureText = gesture ?? "" };
        if (icon != null) mi.Icon = new TextBlock { Text = icon };
        if (tip != null) mi.ToolTip = tip;
        if (click != null) mi.Click += click;
        return mi;
    }

    MenuItem PassEntry(PassSpec spec, string label, string tag, string summary)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = label });
        if (summary.Length > 0) head.Children.Add(new TextBlock { Text = "   " + summary, Foreground = Ui.Brush("TextMuted") });
        var mi = new MenuItem { Header = head, Tag = tag, ToolTip = spec.Help };
        if (!spec.Safe) mi.Icon = new TextBlock { Text = "", Foreground = Ui.Brush(spec.Structure ? "TextMuted" : "Sev.Med"), ToolTip = spec.Structure ? "changes structure" : "changes look" };
        mi.Click += OnRunOnSelection;
        return mi;
    }

    /// <summary>The first two settings of a pass as they are in the Optimize tab ("ratio 0.5, min tris 10000"); simplify:
    /// the shared record ("within 0.5 % of size, keep textures exact").</summary>
    string ParamSummary(PassSpec spec, Dictionary<string, object> prms, string? skip = null)
    {
        if (spec.Name == "simplify") return Simplify.Describe();   // the shared record, as the Try strip shows it
        var parts = spec.Params.Where(p => p.Key != skip).Take(2).Select(p =>
        {
            var v = prms.TryGetValue($"{spec.Name}.{p.Key}", out var x) ? x : p.Default;
            string s = v switch { bool b => b ? "on" : "off", double d => PyFormat.Repr(d), _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
            return $"{p.Key.Replace('_', ' ')} {s}";
        });
        return string.Join(", ", parts);
    }

    // ---- problems: findings / hotspots

    internal void OnProblemsMode(object sender, RoutedEventArgs e)
    {
        if (FindingsView == null || HotspotsView == null || SimilarView == null) return;
        bool hot = ProbHotspots.IsChecked == true, sim = ProbSimilar.IsChecked == true;
        FindingsView.Visibility = hot || sim ? Visibility.Collapsed : Visibility.Visible;
        HotspotsView.Visibility = hot ? Visibility.Visible : Visibility.Collapsed;
        SimilarView.Visibility = sim ? Visibility.Visible : Visibility.Collapsed;
        if (sim && simGroups == null && Rep != null && !busy && SimilarVisible) FindSimilar();   // first open after the scene (or a run) changed it
    }

    bool fillingFindingTypes;

    void FillFindings()
    {
        // the type list: all, then one entry per finding type (worst first); the chosen type stays chosen if it still exists
        string? keep = (FindingTypeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        fillingFindingTypes = true;
        FindingTypeBox.Items.Clear();
        FindingTypeBox.Items.Add(new ComboBoxItem { Content = $"All types ({PyFormat.Int(Rep!.Findings.Count)})", Tag = null });
        foreach (var g in InspectorModel.GroupFindings(Rep.Findings))
        {
            string saving = string.Join(", ", new[]
            {
                g.Tris > 0 ? Analyzer.HumanCount(g.Tris) + " tris" : "", g.Bytes > 0 ? Analyzer.HumanBytes(g.Bytes) : "",
                g.Draws > 0 ? PyFormat.Int(g.Draws) + " draws" : "",
            }.Where(x => x.Length > 0));
            FindingTypeBox.Items.Add(new ComboBoxItem
            {
                Content = $"{g.Label}  ({PyFormat.Int(g.Count)}, {g.Worst}{(saving.Length > 0 ? ", saves " + saving : "")})", Tag = g.Code,
                Foreground = Ui.SevBrush(g.Worst),
            });
        }
        FindingTypeBox.SelectedItem = FindingTypeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == keep) ?? FindingTypeBox.Items[0];
        fillingFindingTypes = false;
        ShowFindings();
    }

    void ShowFindings()
    {
        if (Rep == null) return;
        string? code = (FindingTypeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        string sev = (FindingSevBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "info";
        var shown = InspectorModel.FilterFindings(Rep.Findings, code, sev).Select(f => new FindingRow(f, f.Title, f.Severity, InspectorModel.Saving(f),
            InspectorModel.FixLabel(f.Fix != null && Optimizer.Passes.ContainsKey(f.Fix) ? f.Fix : null))).ToList();
        FindingList.ItemsSource = shown;
        FindingCount.Text = $"{PyFormat.Int(shown.Count)} of {PyFormat.Int(Rep.Findings.Count)}";
        Ui.SetEmptyText(FindingList, Rep.Findings.Count == 0 ? "No findings - the scene looks clean." : "No findings match the filter.");
        UpdateFindingActions();
    }

    internal void OnFindingFilter(object sender, SelectionChangedEventArgs e)
    {
        if (!fillingFindingTypes && IsLoaded) ShowFindings();
    }

    Finding? CurrentFinding => (FindingList.SelectedItem as FindingRow)?.F;

    internal void OnFindingSelect(object sender, SelectionChangedEventArgs e) => UpdateFindingActions();

    /// <summary>Detail text and the fix buttons of the selected finding ("Fix all 12 with 'Merge duplicate vertices'").</summary>
    void UpdateFindingActions()
    {
        var f = CurrentFinding;
        bool fixable = f?.Fix != null && Optimizer.Passes.ContainsKey(f.Fix) && Rep != null;
        FindingText.Text = f != null && Rep != null
            ? InspectorModel.FindingText(Rep, f) + (InspectorModel.CardsFindingNote(f, ActiveCardsProfile) is { } cardsNote ? "\n\n" + cardsNote : "") : "";
        FixButton.IsEnabled = fixable;
        ShowInTreeButton.IsEnabled = f is { Nodes.Count: > 0 };
        if (fixable)
        {
            int n = Rep!.Findings.Count(x => x.Fix == f!.Fix);
            string label = InspectorModel.FixLabel(f!.Fix);
            if (label.Length > 36) label = label[..35] + "…";
            FixAllButton.Content = $"Fix All {PyFormat.Int(n)} with “{label}”";
            FixAllButton.IsEnabled = true;
            FixAllButton.ToolTip = $"Run “{InspectorModel.FixLabel(f.Fix)}” on the nodes of all {n} findings it fixes";
        }
        else
        {
            FixAllButton.Content = "Fix All of This Kind";
            FixAllButton.IsEnabled = false;
            FixAllButton.ToolTip = "Select a finding with a suggested fix";
        }
    }

    internal void OnFindingKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnShowFindingNodes(sender, e);
        e.Handled = true;
    }

    internal void OnShowFindingNodes(object sender, RoutedEventArgs e)
    {
        var f = CurrentFinding;
        if (f is { Nodes.Count: > 0 })
        {
            LeftTree.IsChecked = true;
            Reveal(f.Nodes);
        }
    }

    internal void OnFixFinding(object sender, RoutedEventArgs e) => FixFinding(false);
    internal void OnFixAllSame(object sender, RoutedEventArgs e) => FixFinding(true);

    void FixFinding(bool allSame)
    {
        var f = CurrentFinding;
        if (f?.Fix == null || !Optimizer.Passes.ContainsKey(f.Fix) || Rep == null) return;
        var findings = allSame ? Rep.Findings.Where(x => x.Fix == f.Fix).ToList() : new List<Finding> { f };
        var targets = InspectorModel.FixTargets(findings);
        if (f.Fix is "replace_cards" or "replace_proxy")
        {
            // replacements always ask, with the consequence
            var nodes = targets ?? findings.SelectMany(x => x.Nodes).Distinct().ToList();
            long tris = nodes.Sum(n => Rep.Stat(n).Tris * Math.Max(1, Rep.Instances.GetValueOrDefault(n, 1)));   // every place, like Similar's
            string with = f.Fix == "replace_cards" ? "billboard or cross cards" : "a proxy mesh";
            string estimate = f.Fix == "replace_proxy" ? ProxyEstimateParagraph(nodes.FirstOrDefault(), CollectParams()) : "";
            if (!Dialogs.Confirm(DialogOwner, allSame ? "Fix all?" : "Replace?", $"Run “{InspectorModel.FixLabel(f.Fix)}” on {(allSame ? $"{findings.Count} findings" : "this finding")}?\n\n" +
                                                   $"{estimate}{ReplaceNote(targets?.Count ?? findings.Count, tris, with)}\n\nYou can check the result in Review.", "Run Fix"))
                return;
        }
        else if (allSame && (targets == null || targets.Count > ConfirmFixAbove))
        {
            string where = targets == null ? "the whole scene" : $"{PyFormat.Int(targets.Count)} nodes";
            if (!Dialogs.Confirm(DialogOwner, "Fix all?", $"Run “{InspectorModel.FixLabel(f.Fix)}” on {where} ({findings.Count} findings)?\n\n" +
                                                   "You can check the result in Review and undo it (Ctrl+Z).", "Run Fix"))
                return;
        }
        RunPasses(new List<string> { f.Fix }, targets);
    }

    void FillHotspots()
    {
        var rep = Rep!;
        HotGeo.ItemsSource = rep.Geoms.Select((g, i) => (g, i)).OrderBy(x => -x.g.Tris * x.g.Instances).ThenBy(x => x.i).Take(200)
            .Select(x =>
            {
                var (similar, tip) = SimilarNote(x.g.Node);
                string full = rep.PathOf(x.g.Node);
                return new HotRow(ReportText.MiddlePath(full, 60), Analyzer.HumanCount(x.g.Tris * x.g.Instances), x.g.Instances.ToString(),
                    Analyzer.HumanBytes(x.g.Bytes), new List<Node> { x.g.Node }, similar, tip, full);
            }).ToList();
        HotTex.ItemsSource = rep.Textures.Select((t, i) => (t, i)).OrderBy(x => -x.t.GpuBytes).ThenBy(x => x.i).Take(200)
            .Select(x =>
            {
                var t = x.t;
                string size = !(t.Exists || t.Embedded) ? "MISSING" : t.Width != 0 ? $"{t.Width}x{t.Height}" : "?";
                var users = rep.TexUsers.TryGetValue(t, out var u) ? u : new List<Node>();
                return new HotRow(t.Label, size, Analyzer.HumanBytes(t.GpuBytes), users.Count.ToString(), users);
            }).ToList();
    }

    internal void OnHotGoto(object sender, MouseButtonEventArgs e) => HotGoto(sender);

    internal void OnHotKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        HotGoto(sender);
        e.Handled = true;
    }

    void HotGoto(object sender)
    {
        if ((sender as ListView)?.SelectedItem is not HotRow { Nodes.Count: > 0 } r) return;
        LeftTree.IsChecked = true;
        Reveal(r.Nodes);
    }

    // ---- map

    Node? mapRoot;

    string MapMode => InspectorModel.MapMetrics[Math.Max(0, MapMetricBox.SelectedIndex)].mode;

    string MapValue(double v) => MapMode is "geo" or "tex" or "bytes" ? Analyzer.HumanBytes(v) : Analyzer.HumanCount((long)v);

    void FillMap()
    {
        if (Rep?.Scene.Root == null || MapPanel.Visibility != Visibility.Visible || Map.ActualWidth < 10 || Map.ActualHeight < 10) return;
        // after optimising, the zoomed-in node may be gone
        if (mapRoot != null && !ReferenceEquals(mapRoot, Rep.Scene.Root) && !Rep.Parents.ContainsKey(mapRoot)) mapRoot = null;
        var root = mapRoot ?? Rep.Scene.Root;
        var rects = InspectorModel.Treemap(Rep, root, MapMode, Map.ActualWidth, Map.ActualHeight, header: TreemapView.LabelHeight);
        Map.Show(rects, n => index?.NodeSev.GetValueOrDefault(n), MapValue);
        MapPath.Text = $"{ReportText.MiddlePath(Rep.PathOf(root), 80)}   ·   {MapValue(InspectorModel.Metric(Rep, root, MapMode))}, {PyFormat.Int(rects.Count)} boxes";
    }

    internal void OnMapMetric(object sender, SelectionChangedEventArgs e) => FillMap();

    internal void OnMapUp(object sender, RoutedEventArgs e)
    {
        if (Rep == null || mapRoot == null) return;
        mapRoot = Rep.Parents.TryGetValue(mapRoot, out var ps) && ps.Count > 0 ? ps[0] : null;
        FillMap();
    }

    internal void OnMapTop(object sender, RoutedEventArgs e)
    {
        mapRoot = null;
        FillMap();
    }

    void OnMapHover(InspectorModel.MapRect? r)
    {
        if (Rep == null) return;
        if (r == null) { MapInfo.Text = MapHint; return; }
        var st = Rep.Stat(r.Node);
        int findings = (index?.Of(r.Node).Count ?? 0) + (index?.Below.GetValueOrDefault(r.Node) ?? 0);
        MapInfo.Text = $"{Rep.PathOf(r.Node)}\n{Analyzer.HumanCount(st.Tris)} triangles ({Analyzer.HumanCount(st.VisTris)} visible), " +
                       $"{PyFormat.Int(st.Draws)} draws, geometry {Analyzer.HumanBytes(st.GeoBytes)}, textures {Analyzer.HumanBytes(st.TexBytes)}, " +
                       $"{PyFormat.Int(findings)} finding(s)";
    }

    void OnMapClick(InspectorModel.MapRect r, int clicks)
    {
        if (clicks >= 2)
        {
            // zoom into the box under the current map root that holds the clicked one
            var top = Map.Rects.Where(x => x.Depth == 1).FirstOrDefault(x => r.X >= x.X && r.Y >= x.Y && r.X < x.X + x.W && r.Y < x.Y + x.H);
            if (top != null && top.Node.AllChildren().Any()) { mapRoot = top.Node; FillMap(); }
            return;
        }
        Reveal(new[] { r.Node });
    }

    // ============================================================== editing

    internal List<Node> SelectedNodes()
    {
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        return Tree.SelectedItems.Cast<TreeRow>().Select(r => r.Node).OfType<Node>()
            .Where(n => n.Kind != Kind.External && seen.Add(n)).ToList();
    }

    /// <summary>Run optimisation passes (undo snapshot first), then re-analyse and show what changed in Review.</summary>
    /// <param name="scopeNodes">The nodes the run is described by in Review and History (default: the targets); Apply
    /// from the preview targets the previewed geometries but names the node.</param>
    /// <param name="profileTag">"Name@Version" of the active, unedited profile; only the Optimize panel's Run passes it, so fixes,
    /// Similar, Cards All and the preview Try are recorded without a profile.</param>
    internal void RunPasses(List<string> names, List<Node>? targets = null, Dictionary<string, object>? prms = null, bool fromPreview = false,
        List<Node>? scopeNodes = null, string? profileTag = null)
    {
        if (Scene == null || names.Count == 0) return;
        prms ??= CollectParams();
        prms.TryAdd("include_externals", OptExternals.IsChecked == true);
        string label = names.Count == 1 ? InspectorModel.FixLabel(names[0]) : $"{names.Count} passes";
        var described = targets == null ? null : scopeNodes ?? targets;
        string scope = described == null ? "the whole scene" : $"{PyFormat.Int(described.Count)} node{(described.Count == 1 ? "" : "s")}";
        string histScope = described == null || Rep == null ? "the whole scene" : Selection.Describe(Rep, Scene, described).Text;
        if (targets != null) scope = histScope;
        var scene = Scene;
        ClearChangedDots();
        Work("Optimizing ...", progress =>
        {
            progress("taking undo snapshot ...");
            var snap = new Snapshot(scene, label);
            long editsBefore = History.EditSignature(scene);
            var nonDefault = Optimizer.NonDefaultParams(names, prms);
            var recordPrms = new Dictionary<string, object>(prms);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Optimizer.DomJournal = snap.DomSwaps;
            (List<PassResult>, Dictionary<string, object?>, Dictionary<string, object?>) r;
            try { r = Optimizer.Run(scene, names, prms, targets, progress); }
            finally { Optimizer.DomJournal = null; }
            double seconds = sw.Elapsed.TotalSeconds;
            long editsAfter = History.EditSignature(scene);
            progress("analysing ...");
            var rep = Analyzer.Analyze(scene, progress: progress);
            return (snap, r, rep, editsBefore, editsAfter, seconds, nonDefault, recordPrms);
        }, res =>
        {
            var (snap, (results, before, after), rep, editsBefore, editsAfter, seconds, nonDefault, recordPrms) = res;
            Rep = rep;
            // a run that changed nothing records no undo step and leaves the modified state alone
            string? nothing = InspectorModel.NothingChanged(results, before, after, editsBefore, editsAfter);
            if (nothing == null)
            {
                snap.Scope = histScope;
                snap.Params = InspectorModel.RecordParams(profileTag, names, recordPrms, results);
                snap.Files = History.FilesOf(scene, results.SelectMany(r => r.Changes).Select(c => c.Node));
                if (snap.Files == 0)   // structure-only passes list no geometry: count the files that changed
                    snap.Files = new[] { scene }.Concat(scene.Externals.Values.OfType<Scene>()).Count(SceneIO.SceneModified);
                snap.Seconds = seconds;
                PushHistory(snap);
            }
            if (profileTag != null) Log("profile: " + profileTag);
            foreach (var line in InspectorModel.LogResults(results, before, after, profileTag != null ? Optimizer.AllParams(names, recordPrms) : nonDefault)) Log(line);
            if (nothing != null) Log($"      (nothing changed: {nothing})");
            if (nothing == null) SetModified(true);
            var sticky = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
            foreach (var (row, at) in rows.Select((row, at) => (row, at)))
                foreach (var cn in row.ChainNodes) sticky.TryAdd(cn, at);
            stickyOrder = sticky;
            MarkChangedDots(results.SelectMany(r => r.Changes).Select(c => c.Node));
            RefreshAll();
            SetChanges(results, prms);
            SetReviewSummary(label, names.Count, scope, results, before, after, nothing);
            if (!fromPreview) ShowPanel(PanelId.Review);   // "Apply" from the preview stays there
            var node = Preview.Node;
            if (fromPreview)
            {
                // Apply as previewed: the side-by-side and the verdict stay on screen, no tab switch, no new preview
                var counts = results.SelectMany(r => r.Changes).Select(c => c.Counts()).ToList();
                if (nothing == null && node != null && Preview.LastTry is { } lt)
                {
                    // The run's changes cover only the geometries that changed; the preview counted the whole node.
                    // Compare the two by what they removed, and show the node's totals like the preview does.
                    long removed = counts.Sum(c => c.trisBefore) - counts.Sum(c => c.trisAfter);
                    Preview.Applied();
                    Status.Text = InspectorModel.AppliedLine(lt.TrisBefore, lt.TrisBefore - removed, lt.Quality.Verdict, lt.TrisAfter);
                    appliedNode = node;
                }
                else
                {
                    Preview.RefreshApply();
                    if (nothing != null) Status.Text = $"Nothing changed: {nothing}";
                }
                return;
            }
            if (node != null && PanelShown(PanelId.Inspect))
            {
                var ch = results.SelectMany(r => r.Changes).FirstOrDefault(c => ReferenceEquals(c.Node, node) && c.Before != null);
                if (ch != null) Preview.ShowChange(ch);   // show what really happened
                else Preview.ShowNode(node);
            }
        });
    }

    /// <summary>Review header: what ran, the totals before -> after and the errors.</summary>
    void SetReviewSummary(string label, int passes, string scope, List<PassResult> results, Dictionary<string, object?> before, Dictionary<string, object?> after,
        string? nothingReason = null)
    {
        int failed = results.Count(r => r.Error != null);
        int changed = results.Sum(r => r.Changes.Count);
        // passes that only change the structure (make instances) list no geometry: show their own message instead
        bool structural = changed == 0 && failed == 0 && Convert.ToInt64(before["nodes"]) != Convert.ToInt64(after["nodes"]);
        string message = results.FirstOrDefault(r => r.Error == null && r.Message.Length > 0)?.Message ?? "";
        ReviewTitle.Text = changed > 0 ? $"{label}: {PyFormat.Int(changed)} geometr{(changed == 1 ? "y" : "ies")} changed"
            : structural && message.Length > 0 ? $"{label}: {message}" : $"{label}: no geometry changed";
        Ui.SetEmptyText(ChangeList, structural ? "No geometry was reshaped: the pass changed the scene structure (see the totals above and the run log)."
            : "Nothing changed yet.\nRun passes in the Optimize tab (F5) or fix a finding to see the changes here.");
        ReviewSub.Text = $"{passes} pass{(passes == 1 ? "" : "es")} on {scope}, {DateTime.Now:HH:mm}" +
                         (failed > 0 ? $"  ·  {failed} failed, see the run log" : "") + "  ·  Undo with Ctrl+Z";
        reviewPills.Clear();
        foreach (var (key, name, bytes) in new[] { ("triangles", "Triangles", false), ("draw_calls", "Draw calls", false),
                     ("geometry_bytes", "Geometry", true), ("texture_bytes", "Textures", true), ("nodes", "Nodes", false) })
        {
            long b = Convert.ToInt64(before[key]), a = Convert.ToInt64(after[key]);
            if (a == b) continue;
            string F(long v) => bytes ? Analyzer.HumanBytes(v) : Analyzer.HumanCount(v);
            string pct = b != 0 ? $"  {InspectorModel.PlusPercent((a - b) / (double)b)}" : "";
            reviewPills.Add(Pill($"{name}  {F(b)} → {F(a)}{pct}", a < b ? "Good" : "Sev.Med", a < b ? "Verdict.Ok.Tint" : "Sev.Med.Tint"));
        }
        // texture units summed over the geometries (QuickTotals): fewer means a pass dropped a unit somewhere
        if (results.FirstOrDefault(r => r.TotalsBefore != null)?.TotalsBefore is { } tb && results.LastOrDefault(r => r.Totals != null)?.Totals is { } ta &&
            tb.TryGetValue("texture_units", out var ub) && ta.TryGetValue("texture_units", out var ua) && ua != ub)
            reviewPills.Add(Pill($"Texture units  {PyFormat.Int(ub)} → {PyFormat.Int(ua)}", ua < ub ? "Sev.Med" : "Text", ua < ub ? "Sev.Med.Tint" : "Sev.Info.Tint"));
        if (reviewPills.Count == 0) reviewPills.Add(Pill("Totals unchanged", "TextMuted", "Sev.Info.Tint"));
        if (nothingReason != null)
        {
            ReviewTitle.Text = $"{label}: nothing changed";
            ReviewSub.Text = nothingReason;
            Ui.SetEmptyText(ChangeList, $"Nothing changed: {nothingReason}");
        }
        if (failed > 0 || changed == 0) LogExpander.IsExpanded = true;
        FillChanges();
    }

    static Border Pill(string text, string fg, string bg) => new()
    {
        Style = (Style)Application.Current.FindResource("Chip"), Background = Ui.Brush(bg), Margin = new Thickness(0, 4, 6, 0), Padding = new Thickness(8, 2, 8, 3),
        Child = new TextBlock { Text = text, Foreground = Ui.Brush(fg), FontWeight = FontWeights.SemiBold, FontSize = 12 },
    };

    // ---- undo / redo / history

    void PushHistory(Snapshot snap)
    {
        int kept = history.UndoCount;   // the undone steps are discarded by the push
        int parent = CurrentCheckpoint(kept) ?? 0;   // the state the step was done on, also after undo
        history.Push(snap);   // drops the oldest steps over the cap (the history list says so)
        // the step's checkpoint is written once the step is done (end of the current Work, see StartCheckpoint)
        pendingCheckpoint = (++ckIndex, snap.Label, snap.Scope, snap.Params, parent);
        if (memIndices.Count > kept) memIndices.RemoveRange(kept, memIndices.Count - kept);
        memIndices.Add(ckIndex);
        int n = history.UndoCount + history.RedoCount;
        while (memIndices.Count > n)
        {
            droppedTop = memIndices[0];
            memIndices.RemoveAt(0);
        }
        UpdateUndo();
    }

    static string Short(string text) => text.Length > 36 ? text[..35] + "…" : text;

    /// <summary>Checkpoint index of the state after <paramref name="undoCount"/> steps: the step's own, else the oldest state in
    /// memory (the newest dropped step or the restored checkpoint); null for the database as opened.</summary>
    int? CurrentCheckpoint(int undoCount) =>
        undoCount > 0 && undoCount <= memIndices.Count ? memIndices[undoCount - 1] : droppedTop ?? ckParent;

    void UpdateUndo()
    {
        UndoButton.Content = history.UndoLabel is string u ? Short($"Undo {u}") : "Undo";
        UndoButton.ToolTip = history.UndoText is string u2 ? u2 + " (Ctrl+Z)" : "Nothing to undo";
        RedoButton.ToolTip = history.RedoText is string r ? r + " (Ctrl+Y)" : "Nothing to redo";
        FillHistory();
        CommandManager.InvalidateRequerySuggested();
    }

    void OnUndo(object sender, RoutedEventArgs e) => StepHistory(history.UndoCount - 1);
    void OnRedo(object sender, RoutedEventArgs e) => StepHistory(history.UndoCount + 1);

    /// <summary>Undo or redo until <paramref name="target"/> steps are done, then re-analyse once.</summary>
    void StepHistory(int target)
    {
        if (busy || Scene == null || target < 0 || target > history.UndoCount + history.RedoCount || target == history.UndoCount) return;
        bool undo = target < history.UndoCount;
        var labels = new List<string>();
        while (history.UndoCount != target)
        {
            var snap = undo ? history.Undo() : history.Redo();
            if (snap == null) break;
            labels.Add(snap.Label);
        }
        if (labels.Count == 0) return;
        string verb = undo ? "Undone" : "Redone";
        string what = labels.Count == 1 ? labels[0] : $"{labels.Count} steps ({string.Join(", ", labels.Take(3))}{(labels.Count > 3 ? ", …" : "")})";
        SetModified(true);
        UpdateUndo();
        foreach (var l in labels)
            Log(undo ? $"UNDO: {l} (scene restored to the state before it)" : $"REDO: {l}");
        changes = new List<Change>();
        ClearChangedDots();
        reviewPills.Clear();
        ReviewTitle.Text = $"{verb}: {what}";
        ReviewSub.Text = undo ? "The scene is back to the state before it. Redo with Ctrl+Y, or pick a step in the history below."
                              : "The step is applied again. Undo with Ctrl+Z, or pick a step in the history below.";
        FillChanges();
        Reanalyse($"{verb}: {what}");
        if (Preview.Node != null) PendingPreview = Preview.Node;
    }

    /// <summary>History list in Review: the oldest kept state, then every step (undone ones muted, after the current one).</summary>
    void FillHistory()
    {
        var entries = history.Entries();
        // the checkpoint that is also the oldest state in memory (undo target 0): the newest dropped step, or the restored state
        CheckpointInfo? baseCk = droppedTop is int dt
            ? diskCheckpoints.FirstOrDefault(c => c.Session == ckSession && !c.IsBackup && c.Index == dt)
            : history.Dropped == 0 ? restoredInfo : null;
        // "Scene as opened" is in memory unless steps were dropped or a checkpoint state was loaded: then it reloads the database
        bool openedInMemory = history.Dropped == 0 && Scene?.Layers == null;
        var list = new List<HistoryRow>
        {
            openedInMemory
                ? new(0, "Scene as opened", "", "", "", false, history.UndoCount == 0)
                : new(-2, "Scene as opened", "", "", "", false, false, "", "The database as it is on disk"),
        };
        static string D(long d) => d == 0 ? "±0" : (d > 0 ? "+" : "−") + Analyzer.HumanCount(Math.Abs(d));
        if (!openedInMemory && baseCk == null && history.UndoCount + history.RedoCount > 0)
            list.Add(new HistoryRow(0, "Oldest state kept", "", "", "", false, history.UndoCount == 0));
        if (baseCk != null) list.Add(DiskRow(baseCk, 0, history.UndoCount == 0));
        for (int i = 0; i < entries.Count; i++)
        {
            var en = entries[i];
            list.Add(new HistoryRow(i + 1, en.Label, en.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), D(en.TrisDelta), D(en.DrawsDelta), en.Undone,
                i + 1 == history.UndoCount, en.Scope, History.Describe(en)));
        }
        // checkpoints not in memory: dropped steps, discarded branches, earlier sessions; the save-over backups first
        var inMemory = new HashSet<int>(memIndices);
        var disk = diskCheckpoints
            .Where(c => !(baseCk != null && string.Equals(c.Folder, baseCk.Folder, StringComparison.OrdinalIgnoreCase)) && !(c.Session == ckSession && !c.IsBackup && inMemory.Contains(c.Index)))
            .OrderByDescending(c => c.IsBackup).ThenBy(c => c.IsBackup ? -c.Time.Ticks : c.Time.Ticks).ToList();
        foreach (var c in disk) list.Add(DiskRow(c, -1, false));
        HistoryList.ItemsSource = list;
        int n = entries.Count;
        HistoryExpander.Header = (n == 0 ? "History" : $"History  ·  {n} step{(n == 1 ? "" : "s")}" + (history.RedoCount > 0 ? $", {history.RedoCount} undone" : ""))
                                 + (disk.Count > 0 ? $"  ·  {disk.Count} on disk" : "");
        FillCheckpointLine();
        UpdateExportStatesButton();
    }

    /// <summary>The states Export States writes: the path of this session's checkpoints from the original to the current
    /// state, oldest first (undone steps a later step replaced are left out). A step whose checkpoint is not on disk (not
    /// written) is skipped: the path starts at the newest one that is.</summary>
    List<CheckpointInfo> ExportableStates()
    {
        var mine = diskCheckpoints.Where(c => c.Session == ckSession && !c.IsBackup).ToList();
        var onDisk = mine.Select(c => c.Index).ToHashSet();
        int? current = null;
        for (int u = history.UndoCount; u >= 0 && current == null; u--)
            if (CurrentCheckpoint(u) is int k && onDisk.Contains(k)) current = k;
        return ExportStates.Chain(mine, current);
    }

    /// <summary>This session's first save-over backup: the database as it was before, the original of the exported states.</summary>
    CheckpointInfo? SaveOverOriginal() =>
        diskCheckpoints.Where(c => c.IsBackup && c.Session == ckSession).OrderBy(c => c.Time).FirstOrDefault();

    void UpdateExportStatesButton() =>
        ExportStatesButton.IsEnabled = Scene != null && !busy && ckKey != null && ExportableStates().Count > 0;

    /// <summary>Export States…: pick a folder (default "&lt;database folder&gt;_states" next to the database), then write it.</summary>
    internal void OnExportStates(object sender, RoutedEventArgs e)
    {
        if (Scene == null || busy || ckKey == null) return;
        string root = DatabaseRoot.Of(Scene);
        string? parent = Path.GetDirectoryName(root);
        string suggested = parent == null ? Path.Combine(Path.GetTempPath(), "database_states") : Path.Combine(parent, Path.GetFileName(root) + "_states");
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Folder for the exported states (suggested: {suggested})" };
        // the suggested folder is the default: made (next to the database, never in it) so the dialog opens in it, and
        // removed again when it stays empty and another folder is chosen or the dialog is cancelled
        bool made = false;
        try
        {
            if (!Directory.Exists(suggested) && Path.GetDirectoryName(suggested) is { } up && Directory.Exists(up))
            {
                Directory.CreateDirectory(suggested);
                made = true;
            }
            if (Directory.Exists(suggested)) dlg.InitialDirectory = suggested;
        }
        catch (Exception) { /* not a path or not writable: start where Windows likes */ }
        bool ok = dlg.ShowDialog(DialogOwner) == true;
        if (made && (!ok || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dlg.FolderName)), suggested, StringComparison.OrdinalIgnoreCase)))
            try { if (!Directory.EnumerateFileSystemEntries(suggested).Any()) Directory.Delete(suggested); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left as an empty folder */ }
        if (!ok) return;
        ExportStatesTo(dlg.FolderName);
    }

    /// <summary>Write the unchanged database and every checkpoint state to <paramref name="folder"/> (outside the database folder).</summary>
    internal void ExportStatesTo(string folder)
    {
        if (Scene == null || busy) return;
        var states = ExportableStates();
        if (states.Count == 0) return;
        string master = DatabaseMaster(Scene);
        var original = SaveOverOriginal();
        try
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() &&
                !Dialogs.Confirm(DialogOwner, "Folder is not empty", $"{folder} already has files; the export writes into it and replaces files of the same name.", "Export"))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable: the export reports it */ }
        Work($"Exporting state 1 of {states.Count + 1} …", progress =>
        {
            try
            {
                return ExportStates.Run(master, states, folder, original: original, progress: msg =>
                {
                    // "writing state_02_x/ …" -> "Exporting state n of m …"; base/ is not a state
                    var m = System.Text.RegularExpressions.Regex.Match(msg, @"^writing state_(\d+)_");
                    progress(m.Success ? $"Exporting state {int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1} of {states.Count + 1} …" : msg);
                });
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Could not export the states to {folder}: {ex.Message}", ex);
            }
        }, res =>
        {
            Status.Text = $"Exported {res.StateFolders.Count} states to {res.Folder}";
            Log($"Exported states: {res.Folder} ({res.StateFolders.Count} folders, {res.Linked} linked, {res.Copied} copied)");
            Dialogs.Info(DialogOwner, "States exported", $"{res.StateFolders.Count} state folders plus base/ in {res.Folder}\n" +
                $"{PyFormat.Int(res.Linked)} files linked to base/, {PyFormat.Int(res.Copied)} copied\nREADME: {res.Readme}");
        });
    }

    HistoryRow DiskRow(CheckpointInfo c, int target, bool current)
    {
        string label = c.IsBackup || c.Session == ckSession ? c.Label : $"{c.Label} ({c.Session})";
        string time = c.Time.ToString(c.Time.Date == DateTime.Today ? "HH:mm:ss" : "dd MMM HH:mm", CultureInfo.InvariantCulture);
        string detail = c.IsBackup ? $"{c.Label}: the database before the save, from {c.Folder}"
            : $"{c.Label}{(c.Scope.Length > 0 ? " — " + c.Scope : "")}; triangles {PyFormat.Int(c.Tris)}" + (c.Params.Length > 0 ? $"\nparameters: {c.Params}" : "");
        return new HistoryRow(target, label, time, "", "", false, current, c.IsBackup ? "" : c.Scope, detail, c);   // no deltas: states, not steps
    }

    /// <summary>The line above the History list: where the checkpoints are, or why one could not be written; then the cap note.</summary>
    void FillCheckpointLine()
    {
        string? line = null;
        bool warn = false;
        if (ckError != null)
        {
            line = ckErrorIsWrite ? $"Checkpoint could not be written: {ckError}" : ckError;
            warn = true;
        }
        else if (ckKey != null && CheckpointFolder() is string folder)
        {
            try { ckSize ??= store.Size(ckKey); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ckSize = (diskCheckpoints.Count, 0); }
            var (count, bytes) = ckSize.Value;
            line = $"Checkpoints: {folder} ({count}, {Analyzer.HumanBytes(bytes)})";
        }
        var cap = history.CapNote();
        string text = string.Join("\n", new[] { line, cap }.Where(s => s != null));
        HistoryCap.Text = text;
        HistoryCap.ToolTip = ckError == null && ckKey != null
            ? "A checkpoint of every step is written here (kept 14 days, 5 GB in all); click a grey row to reload the database at that state"
            : null;
        if (warn) HistoryCap.Foreground = Ui.Brush("Sev.Med");
        else HistoryCap.ClearValue(TextBlock.ForegroundProperty);
        HistoryCap.Visibility = Scene != null && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void OnHistoryClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListViewItem)?.DataContext is HistoryRow r) ActivateHistoryRow(r);
    }

    internal void OnHistoryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || HistoryList.SelectedItem is not HistoryRow r) return;
        ActivateHistoryRow(r);
        e.Handled = true;
    }

    /// <summary>A step in memory: undo / redo up to it. A checkpoint on disk (or the database as opened, once that is no longer
    /// in memory): reload the database at that state.</summary>
    void ActivateHistoryRow(HistoryRow r)
    {
        if (r.Target >= 0) { StepHistory(r.Target); return; }
        if (Scene == null) return;
        if (busy)
        {
            Status.Text = checkpointing ? "Writing checkpoint … please wait" : "Please wait until the current task has finished.";
            return;
        }
        if (r.Ck is { } ck)
        {
            if (Dialogs.Confirm(DialogOwner, "Reload at this state?", "The database is reloaded from the checkpoint; the current undo list is replaced by " +
                    "the checkpoints on disk. Unsaved changes stay on disk as checkpoints.", "Reload"))
                RestoreCheckpoint(ck);
        }
        else if (r.Target == -2)
        {
            if (Dialogs.Confirm(DialogOwner, "Reload the database?", "The database is reloaded as it is on disk; the current undo list is replaced by " +
                    "the checkpoints on disk. Unsaved changes stay on disk as checkpoints.", "Reload"))
                OpenPath(DatabaseMaster(Scene));
        }
    }

    void OnRunOnSelection(object sender, RoutedEventArgs e)
    {
        var nodes = SelectedNodes();
        if (nodes.Count == 0 || sender is not MenuItem { Tag: string tag }) return;
        // "pass:mode" runs the pass with that mode instead of the one in the settings
        int colon = tag.IndexOf(':');
        if (colon < 0) { RunPasses(new List<string> { tag }, nodes); return; }
        var prms = CollectParams();
        prms[tag[..colon] + ".mode"] = tag[(colon + 1)..];
        RunPasses(new List<string> { tag[..colon] }, nodes, prms);
    }

    void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (Scene == null || Rep == null || busy) return;
        var nodes = SelectedNodes().Where(n => !ReferenceEquals(n, Scene.Root)).ToList();
        if (nodes.Count == 0 || !Dialogs.Confirm(DialogOwner, "Delete nodes?",
                $"Delete {nodes.Count} node(s) and everything below them from the scene? Undo brings them back (Ctrl+Z).", "Delete"))
            return;
        PushHistory(new Snapshot(Scene, $"delete {nodes.Count} node(s)") { Scope = $"{nodes.Count} node(s)" });
        foreach (var n in nodes)
            foreach (var p in (Rep.Parents.TryGetValue(n, out var ps) ? ps : new List<Node>()).ToList())
                if (p.Children.Contains(n)) Optimizer.ReplaceChild(p, n, Array.Empty<Node>());
        SetModified(true);
        Reanalyse("Deleted");
    }

    void OnSetMask(object sender, RoutedEventArgs e)
    {
        var nodes = SelectedNodes();
        if (nodes.Count == 0 || Scene == null || busy) return;
        bool visible = (sender as MenuItem)?.Tag as string == "show";
        PushHistory(new Snapshot(Scene, visible ? "unhide" : "hide") { Scope = $"{nodes.Count} node(s)" });
        foreach (var n in nodes)
        {
            n.NodeMask = visible ? 0xFFFFFFFFL : 0;
            n.Mark("node_mask");
        }
        SetModified(true);
        Reanalyse("Node mask changed");
    }

    void Reanalyse(string msg)
    {
        var scene = Scene!;
        Work("Analysing ...", progress => Analyzer.Analyze(scene, progress: progress), rep =>
        {
            Rep = rep;
            UpdateTitle();
            RefreshAll();
            Status.Text = msg;
            PreviewPending();
        });
    }

    // ============================================================== optimize tab

    static readonly (string key, string label)[] PresetLabels =
    {
        ("safe", "Safe - lossless clean-up"), ("aggressive", "Aggressive - changes the look"), ("lod", "LOD - add detail levels"),
        ("none", "None"), ("custom", "Custom"),
    };
    bool applyingPreset;

    void BuildOptimize()
    {
        foreach (var (key, label) in PresetLabels)
            if (key is "none" or "custom" || Optimizer.Presets.ContainsKey(key)) PresetBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        foreach (var key in Optimizer.Presets.Keys.Where(k => PresetLabels.All(p => p.key != k)))   // presets added later
            PresetBox.Items.Insert(PresetBox.Items.Count - 2, new ComboBoxItem { Content = key, Tag = key });
        LoadProfileItems(true);   // separator, built-in profiles, user profiles (R3-C12)
        PresetBox.DropDownOpened += (_, _) => LoadProfileItems(true);   // profile files dropped in by hand show up without a restart

        var groups = new Dictionary<string, List<PassSpec>>();
        foreach (var spec in Optimizer.Passes.Values)
        {
            if (!groups.TryGetValue(spec.Group, out var l)) groups[spec.Group] = l = new List<PassSpec>();
            l.Add(spec);
        }
        foreach (var group in GroupOrder.Concat(groups.Keys.Except(GroupOrder)))
        {
            if (!groups.TryGetValue(group, out var specs)) continue;
            PassPanel.Children.Add(new TextBlock
            {
                Text = group.ToUpperInvariant(), Style = (Style)FindResource("Caption"), Margin = new Thickness(2, PassPanel.Children.Count == 0 ? 4 : 16, 0, 4),
            });
            foreach (var spec in specs) PassPanel.Children.Add(PassBlock(spec));
        }
        applyingPreset = true;
        ApplyPreset("safe");
        applyingPreset = false;
        SyncPresetBox();
    }

    /// <summary>One pass: an expander whose header is the check box (plus a "changes look" badge); inside the
    /// description and the settings. Ticking a pass by hand opens it.</summary>
    FrameworkElement PassBlock(PassSpec spec)
    {
        var cb = new CheckBox { Content = spec.Label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        passBoxes[spec.Name] = cb;
        var head = new DockPanel { LastChildFill = false };
        head.Children.Add(cb);
        if (!spec.Safe)
        {
            var badge = new Border
            {
                Style = (Style)FindResource("Chip"), Margin = new Thickness(10, 0, 0, 0),
                Background = Ui.Brush(spec.Structure ? "Hover" : "Sev.Med.Tint"),
                Child = new TextBlock
                {
                    Text = spec.Structure ? "changes structure" : "changes look",
                    Foreground = Ui.Brush(spec.Structure ? "TextMuted" : "Sev.Med"), FontSize = 11.5,
                },
                ToolTip = spec.Structure ? "Rearranges the scene graph; the picture is unchanged, check instancing in Review"
                                         : "Changes the look - check the result in Review",
            };
            head.Children.Add(badge);
        }

        var body = new StackPanel { Margin = new Thickness(26, 0, 4, 8) };
        bool simplify = spec.Name == "simplify";
        var shownParams = simplify ? spec.Params.Where(p => !SimplifyParams.PassKeys.Contains(p.Key)).ToArray() : spec.Params;
        if (simplify)
        {
            // the shared record (target, strategy, advanced) instead of the generated controls of its keys
            OptEditor = new SimplifyEditor { Margin = new Thickness(0, 0, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(OptEditor, "OptEditor");
            body.Children.Add(OptEditor);
        }
        var helpLine = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("TextMuted"), Margin = new Thickness(0, 0, 0, shownParams.Length > 0 ? 8 : 0) };
        helpLine.Inlines.Add(new Run(spec.Help + " "));
        var helpLink = new Hyperlink(new Run("Help")) { ToolTip = "Open the README section on the optimisation passes" };
        var linkStyle = new Style(typeof(Hyperlink));
        linkStyle.Setters.Add(new Setter(Hyperlink.ForegroundProperty, Ui.Brush("Accent")));
        var linkHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        linkHover.Setters.Add(new Setter(Hyperlink.ForegroundProperty, Ui.Brush("AccentHover")));
        linkStyle.Triggers.Add(linkHover);
        helpLink.Style = linkStyle;
        helpLink.Click += (_, _) => OpenHelp("passes");
        helpLine.Inlines.Add(helpLink);
        body.Children.Add(helpLine);
        if (shownParams.Length > 0)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 320 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;
            foreach (var p in shownParams)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = p.Label, Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 12, 3) };
                Grid.SetRow(label, row);
                grid.Children.Add(label);
                FrameworkElement ctl;
                if (p.Type == typeof(bool)) ctl = new CheckBox { IsChecked = (bool)p.Default, VerticalAlignment = VerticalAlignment.Center };
                else if (p.Choices != null)
                {
                    var combo = new ComboBox { Width = 140, ItemsSource = p.Choices };
                    combo.SelectedItem = Convert.ToString(p.Default, CultureInfo.InvariantCulture);
                    ctl = combo;
                }
                else
                {
                    var tb = new TextBox { Width = 140, Text = DefaultText(p.Default) };
                    tb.TextChanged += (_, _) => Validate();
                    ctl = tb;
                }
                ctl.Margin = new Thickness(0, 3, 0, 3);
                ctl.HorizontalAlignment = HorizontalAlignment.Left;
                switch (ctl)   // an edit after choosing a profile: the box shows "Custom (from ...)"
                {
                    case CheckBox c: c.Checked += (_, _) => OnProfileFieldEdited(); c.Unchecked += (_, _) => OnProfileFieldEdited(); break;
                    case ComboBox c: c.SelectionChanged += (_, _) => OnProfileFieldEdited(); break;
                    case TextBox c: c.TextChanged += (_, _) => OnProfileFieldEdited(); break;
                }
                if (spec.Name == "replace_proxy")   // every replace_proxy control refreshes the estimate line
                    switch (ctl)
                    {
                        case CheckBox c: c.Checked += (_, _) => ScheduleProxyEstimate(); c.Unchecked += (_, _) => ScheduleProxyEstimate(); break;
                        case ComboBox c: c.SelectionChanged += (_, _) => ScheduleProxyEstimate(); break;
                        case TextBox c: c.TextChanged += (_, _) => ScheduleProxyEstimate(); break;
                    }
                if (spec.Name == "replace_cards" && p.Key == "layout")   // the Layout row above replaces it (same key, same values)
                    label.Visibility = ctl.Visibility = Visibility.Collapsed;
                Grid.SetRow(ctl, row);
                Grid.SetColumn(ctl, 1);
                grid.Children.Add(ctl);
                paramControls[$"{spec.Name}.{p.Key}"] = (ctl, p);
                row++;
            }
            if (spec.Name == "replace_cards") body.Children.Add(CardLayoutRow());
            body.Children.Add(grid);
            if (spec.Name == "replace_cards") WireCardControls();
        }
        if (spec.Name == "replace_proxy")
        {
            proxyEstimate = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("TextMuted"), Margin = new Thickness(0, 6, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(proxyEstimate, "ProxyEstimate");
            body.Children.Add(proxyEstimate);
        }

        var exp = new Expander { Header = head, Content = body, Margin = new Thickness(0, 1, 0, 1), ToolTip = null };
        passExpanders[spec.Name] = exp;
        if (spec.Name == "replace_proxy")   // the estimate is only computed while the pass is open or ticked
        {
            exp.Expanded += (_, _) => ScheduleProxyEstimate();
            exp.Collapsed += (_, _) => ScheduleProxyEstimate();
            cb.Checked += (_, _) => ScheduleProxyEstimate();
            cb.Unchecked += (_, _) => ScheduleProxyEstimate();
        }
        cb.Checked += (_, _) => { if (!applyingPreset) exp.IsExpanded = true; OnPassToggled(); };
        cb.Unchecked += (_, _) => { if (!applyingPreset) exp.IsExpanded = false; OnPassToggled(); };
        return exp;
    }

    void OnPassToggled()
    {
        if (applyingPreset) return;
        OnProfileFieldEdited();
        SyncPresetBox();
        Validate();
    }

    static string DefaultText(object v) => v switch
    {
        double d => PyFormat.Repr(d),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    void ApplyPreset(string? name)
    {
        var chosen = name != null && Optimizer.Presets.TryGetValue(name, out var p) ? p.ToHashSet() : new HashSet<string>();
        foreach (var (k, cb) in passBoxes) cb.IsChecked = chosen.Contains(k);
    }

    internal void OnPreset(object sender, SelectionChangedEventArgs e)
    {
        if (applyingPreset || PresetBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is Profile profile)
        {
            applyingPreset = true;
            try
            {
                activeProfile = profile;
                fromProfile = null;
                DropProfileLabel();
                ApplyProfile(profile);
            }
            finally { applyingPreset = false; }
            ShowProfileHint();
            Validate();
            return;
        }
        if (item.Tag is not string key || key == "profile-label") return;
        if (key == "custom")
        {   // keeps the fields; after a profile the box says where they came from
            if (activeProfile != null) OnProfileFieldEdited();
            else if (fromProfile != null) ShowProfileLabel();
            return;
        }
        activeProfile = null;
        fromProfile = null;
        ShowProfileHint();
        applyingPreset = true;
        try { DropProfileLabel(); ApplyPreset(key == "none" ? null : key); }
        finally { applyingPreset = false; }
        Validate();
    }

    /// <summary>Shows the preset matching the ticked passes, or "Custom".</summary>
    void SyncPresetBox()
    {
        if (activeProfile != null) return;   // a chosen, unchanged profile stays shown
        if (fromProfile != null) { ShowProfileLabel(); return; }
        var on = passBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        string key = on.Count == 0 ? "none" : Optimizer.Presets.FirstOrDefault(kv => kv.Value.ToHashSet().SetEquals(on)).Key ?? "custom";
        applyingPreset = true;
        PresetBox.SelectedItem = PresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == key);
        applyingPreset = false;
    }

    List<string> CheckedPasses() => Optimizer.PipelineSort(Optimizer.Passes.Keys.Where(n => passBoxes.TryGetValue(n, out var cb) && cb.IsChecked == true));

    static bool TryParam(PassParam p, string raw, out object? val)
    {
        val = null;
        raw = raw.Trim();
        if (p.Type == typeof(int))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
            val = (int)d;
        }
        else if (p.Type == typeof(double))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d)) return false;
            val = d;
        }
        else val = raw;
        return true;
    }

    /// <summary>Marks unreadable numbers red; Run stays disabled while a ticked pass has one.</summary>
    void Validate()
    {
        string? first = null;
        foreach (var (key, (ctl, p)) in paramControls)
        {
            if (ctl is not TextBox tb) continue;
            bool ok = TryParam(p, tb.Text, out _);
            Ui.SetInvalid(tb, !ok);
            tb.ToolTip = ok ? null : $"Enter a {(p.Type == typeof(int) ? "whole number" : "number")}, e.g. {DefaultText(p.Default)}";
            string pass = key[..key.IndexOf('.')];
            if (!ok && first == null && passBoxes[pass].IsChecked == true)
                first = $"{Optimizer.Passes[pass].Label} › {p.Label}: “{tb.Text}” is not a {(p.Type == typeof(int) ? "whole number" : "number")}";
        }
        invalidParams = first != null;
        ValidationText.Text = first ?? "";
        ValidationText.Visibility = first != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateRunButton();
    }

    internal Dictionary<string, object> CollectParams()
    {
        var prms = new Dictionary<string, object>();
        foreach (var (key, (ctl, p)) in paramControls)
        {
            object? val = ctl switch
            {
                CheckBox cb => cb.IsChecked == true,
                ComboBox combo => combo.SelectedItem as string,
                TextBox tb => TryParam(p, tb.Text, out var v) ? v : null,
                _ => null,
            };
            if (val != null) prms[key] = val;
        }
        if (cardLayoutBox != null && paramControls.ContainsKey("replace_cards.layout"))
            prms["replace_cards.layout"] = CurrentCardLayout().MatchesNamed() ?? "custom";
        foreach (var (key, val) in Simplify.ToPassParams()) prms[key] = val;   // the shared record, last (R2-D3)
        prms["include_externals"] = IncludeExternals;   // also for Apply from the preview (ApplyArgs reads it)
        return prms;
    }

    bool settingScope;

    /// <summary>Selecting nodes below the root switches the scope to "Selection", selecting the root back to the scene.</summary>
    void UpdateScopeFromSelection()
    {
        if (Scene?.Root == null) { UpdateRunButton(); return; }
        var sel = SelectedNodes();
        if (sel.Count > 0)
        {
            settingScope = true;
            if (sel.Any(n => !ReferenceEquals(n, Scene.Root))) ScopeSelection.IsChecked = true; else ScopeScene.IsChecked = true;
            settingScope = false;
        }
        UpdateRunButton();
    }

    internal void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (!settingScope && IsInitialized) UpdateRunButton();
    }

    /// <summary>"Run 7 passes on 3 selected nodes (F5)", counts on the scope buttons.</summary>
    void UpdateRunButton()
    {
        if (RunButton == null) return;
        int n = CheckedPasses().Count, sel = SelectedNodes().Count;
        ScopeScene.Content = Rep != null ? $"Whole scene ({PyFormat.Int(Rep.NodeCount)} nodes)" : "Whole scene";
        ScopeSelection.Content = $"Selection ({PyFormat.Int(sel)})";
        bool bySel = ScopeSelection.IsChecked == true;
        SelectionInfo? info = bySel && Scene != null && Rep != null && sel > 0 ? Selection.Describe(Rep, Scene, SelectedNodes()) : null;
        string scope = info != null ? info.Short : bySel ? $"{PyFormat.Int(sel)} selected node{(sel == 1 ? "" : "s")}" : "the whole scene";
        ScopeLine.Text = info != null ? info.Text : bySel && sel == 0 ? "Nothing selected" : "Whole scene";
        ScopeLine.ToolTip = info != null ? info.Text : null;
        // '_' in Button content is an access-key marker; file names like tile_0_0.flt need it doubled
        RunButton.Content = n == 0 ? "Tick passes to run" : $"Run {n} pass{(n == 1 ? "" : "es")} on {scope.Replace("_", "__")}  (F5)";
        RunButton.ToolTip = Scene == null ? "Open a scene first" : invalidParams ? "Fix the highlighted value first" :
            ScopeSelection.IsChecked == true && sel == 0 ? "Select nodes in the tree first" : "Run the ticked passes (F5)";
        CommandManager.InvalidateRequerySuggested();
    }

    void OnRunPasses(object sender, RoutedEventArgs e)
    {
        if (Scene == null || invalidParams) return;
        if (!PanelShown(PanelId.Optimize))
        {
            ShowPanel(PanelId.Optimize);
            Status.Text = "Optimize tab: press F5 again to run";
            return;
        }
        var names = CheckedPasses();
        if (names.Count == 0) return;
        List<Node>? targets = null;
        if (ScopeSelection.IsChecked == true)
        {
            targets = SelectedNodes();
            if (targets.Count == 0) { Status.Text = "Select nodes in the tree first"; return; }
            var info = Selection.Describe(Rep!, Scene, targets);
            if (info.Shared && !Dialogs.Confirm(DialogOwner, "Run on a shared selection?",
                    $"{info.Text}.\nThe passes change every place that draws it.\n\nYou can check the result in Review and undo it (Ctrl+Z).", "Run"))
                return;
        }
        RunPasses(names, targets, profileTag: activeProfile?.Tag);
    }

    internal void Log(string text)
    {
        OptLog.AppendText(text + "\n");
        OptLog.ScrollToEnd();
    }

    // ============================================================== review

    /// <summary>The simplify seams mode of the run that made <see cref="changes"/> (texture check of its simplify changes).</summary>
    string runSeams = "keep";

    void SetChanges(List<PassResult> results, Dictionary<string, object>? prms = null)
    {
        changes = results.SelectMany(r => r.Changes).ToList();
        runSeams = prms != null && prms.TryGetValue("simplify.seams", out var sm) && sm is string str && str.Length > 0 ? str : "keep";
        optimisedOnce = true;
        FillChanges();
        var todo = changes.Where(c => InspectorModel.ShapePasses.Contains(c.PassName) && c.Before != null && c.Quality == null)
            .OrderBy(c => -c.Counts().trisBefore).Take(300).ToList();
        CheckAsync(todo, runSeams);
    }

    readonly HashSet<Change> checking = new(ReferenceEqualityComparer.Instance);

    void FillChanges()
    {
        var parents = Rep?.Parents ?? new Dictionary<Node, List<Node>>(ReferenceEqualityComparer.Instance);
        int damaged = 0, check = 0, ok = 0, unitDrops = 0;
        var list = new List<ChangeRow>();
        foreach (var c in InspectorModel.SortChanges(changes).Take(5000))
        {
            var (b, a, vb, va) = c.Counts();
            string? v = InspectorModel.ChangeVerdict(c);
            if (v == "damaged") damaged++;
            if (v == "check") check++;
            if (v == "ok") ok++;
            string fullPath = History.NodePath(c.Node, parents, 64);
            list.Add(new ChangeRow(c, ReportText.MiddlePath(fullPath, 48), v, v == null && checking.Contains(c), c.PassName,
                $"{PyFormat.Int(b)} → {PyFormat.Int(a)}", $"{PyFormat.Int(vb)} → {PyFormat.Int(va)}", CheckWithoutUnits(c), c.UnitDelta, fullPath));
            if (c.UnitDelta != null) unitDrops++;
        }
        ChangeList.ItemsSource = list;
        ReviewBadge.Visibility = changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewCount.Text = PyFormat.Int(changes.Count);
        ReviewBadge.Background = Ui.Brush(damaged > 0 ? "Verdict.Bad.Tint" : check > 0 ? "Verdict.Check.Tint" : "SurfaceRaised");
        ReviewCount.Foreground = Ui.Brush(damaged > 0 ? "Verdict.Bad" : check > 0 ? "Verdict.Check" : "Text");
        ReviewDeltas.Children.Clear();
        foreach (var p in reviewPills) ReviewDeltas.Children.Add(p);
        if (optimisedOnce && changes.Count > 0)
        {
            if (unitDrops > 0) ReviewDeltas.Children.Add(Pill($"{unitDrops} lost texture units", "Sev.Med", "Sev.Med.Tint"));
            if (damaged > 0) ReviewDeltas.Children.Add(Pill($"{damaged} likely damaged", "Verdict.Bad", "Verdict.Bad.Tint"));
            if (check > 0) ReviewDeltas.Children.Add(Pill($"{check} to check", "Verdict.Check", "Verdict.Check.Tint"));
            if (ok > 0) ReviewDeltas.Children.Add(Pill($"{ok} OK", "Verdict.Ok", "Verdict.Ok.Tint"));
            if (checking.Count > 0) ReviewDeltas.Children.Add(Pill($"checking {checking.Count} …", "TextMuted", "Sev.Info.Tint"));
        }
    }

    /// <summary>The check text without the unit delta in front (the list shows that as a chip).</summary>
    static string CheckWithoutUnits(Change c)
    {
        string text = InspectorModel.ChangeCheckText(c);
        return c.UnitDelta is string d && text.StartsWith(d + "; ", StringComparison.Ordinal) ? text[(d.Length + 2)..] : text;
    }

    /// <summary>The merged state set a change's texture check measures against: the recorded one, else the node's
    /// effective textures now.</summary>
    internal StateSet? CheckState(Change c) =>
        c.StateBefore ?? (Rep is { } rep ? new StateSet { Textures = rep.TexIndex.Effective(c.Node).Select(b => b.Tex).ToList() } : null);

    /// <summary>The seams mode a change's texture check uses: the run's for simplify changes, keep otherwise.</summary>
    internal string CheckSeams(Change c) => c.PassName == "simplify" ? runSeams : "keep";

    void CheckAsync(List<Change> todo, string seams)
    {
        if (todo.Count == 0) return;
        foreach (var c in todo) checking.Add(c);
        FillChanges();
        var work = todo.Select(c => (c, ss: CheckState(c), seams: c.PassName == "simplify" ? seams : "keep")).ToList();
        Task.Run(() =>
        {
            foreach (var (c, ss, sm) in work) c.Quality = InspectorModel.SafeCompare(c.Before, c.After, ss, sm);
        }).ContinueWith(_ =>
        {
            foreach (var c in todo) checking.Remove(c);
            FillChanges();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    List<Change> SelectedChanges() => ChangeList.SelectedItems.Cast<ChangeRow>().Select(r => r.C).ToList();

    internal void OnCheckSelectedChanges(object sender, RoutedEventArgs e)
    {
        var todo = SelectedChanges().Where(c => c.Before != null).ToList();
        foreach (var c in todo) c.Quality = null;
        CheckAsync(todo, runSeams);
    }

    internal void OnShowChange(object sender, RoutedEventArgs e)
    {
        var sel = SelectedChanges();
        if (sel.Count == 0) return;
        skipPreviewUntil = DateTime.Now.AddSeconds(1);
        Reveal(new[] { sel[0].Node });
        Preview.ShowChange(sel[0]);
        ShowPanel(PanelId.Inspect);
    }
}
