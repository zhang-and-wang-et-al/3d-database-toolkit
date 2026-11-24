using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SceneKit;

namespace SceneInspector;

/// <summary>3D view of the selected node (software renderer, see SceneKit.SoftRender): before / after / side by side,
/// deviation heat map, try simplify with a live quality check. Port of inspector_preview.PreviewPanel.</summary>
public partial class PreviewPanel : UserControl
{
    MainWindow? insp;
    public Node? Node { get; private set; }
    List<SoftRender.Mesh> mBefore = new(), mAfter = new();
    List<SoftRender.Mesh> mContext = new();      // the rest of the scene around the selection (whole scene view)

    /// <summary>The scene around the selection, built once per analysis (Report) and shared by every PreviewPanel (the
    /// Inspect preview and the selection view under the tree). A new Report (open, re-analysis after an edit) builds a new
    /// one; the old entry goes with its Report.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Report, Task<(List<SoftRender.Mesh> meshes, long tris, bool cut)>> contexts = new();

    static Task<(List<SoftRender.Mesh> meshes, long tris, bool cut)> ContextFor(Node root, Report rep) =>
        contexts.GetValue(rep, _ => Task.Run(() => Meshes(root, null, MaxContextTris, 1)));

    /// <summary>Reduced mode for the selection view under the tree: no option bar, no try strip, no legend / verdict / info
    /// lines, colouring fixed to Textured, no mouse camera (double-click raises <see cref="Opened"/>), a quarter of the
    /// triangle budgets.</summary>
    public bool Compact
    {
        get => compact;
        set
        {
            compact = value;
            // cleared, not set to Visible: the legend's style shows it only in heat map mode
            foreach (UIElement el in new UIElement[] { OptionBar, TryStrip, LegendBar, VerdictText, InfoText })
                if (value) el.Visibility = Visibility.Collapsed; else el.ClearValue(VisibilityProperty);
            ViewBorder.Margin = value ? new Thickness(0) : new Thickness(10, 0, 10, 0);
            ViewBorder.Cursor = value ? Cursors.Hand : Cursors.SizeAll;
            ViewBorder.ToolTip = value ? "Double-click to open it in Inspect" : null;
        }
    }
    bool compact;

    /// <summary>Compact mode: draw the scene around the selection (the selection in orange). The full preview uses its own
    /// Scene around check box.</summary>
    public bool SceneAround { get; set; }

    /// <summary>Double-click on the view (compact mode).</summary>
    public event Action? Opened;
    string title = "";
    SoftRender.Camera? cam;
    int req;
    (string what, Point at)? drag;
    bool busyTry, suppress;
    readonly DispatcherTimer delayed = new();

    public PreviewPanel()
    {
        InitializeComponent();
        delayed.Tick += (_, _) => { delayed.Stop(); Redraw(); };
        DrawLegend();
        TryEditor.TryRequested += TrySimplify;
    }

    public void Attach(MainWindow w) => insp = w;

    // ------------------------------------------------------------------ options

    string ViewMode => ViewBefore.IsChecked == true ? "before" : ViewSide.IsChecked == true ? "side" : "after";
    string ShadeMode => compact ? "shaded" : (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "shaded + wireframe";
    bool HeatMap => !compact && ColorBox.SelectedIndex == 1;
    SoftRender.Paint Paint => compact ? SoftRender.Paint.Textured : ColorBox.SelectedIndex switch { 2 => SoftRender.Paint.Textured, 3 => SoftRender.Paint.UvChecker, _ => SoftRender.Paint.Material };

    double HeatMax()
    {
        return double.TryParse(HeatMaxBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Max(v, 1e-6) : 1.0;
    }

    void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || suppress) return;
        if (ReferenceEquals(sender, ColorBox)) userPaint = true;
        DrawLegend();
        Redraw();
    }

    void OnHeatKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOptionChanged(sender, e);
    }

    void SetView(string v)
    {
        suppress = true;
        (v == "before" ? ViewBefore : v == "side" ? ViewSide : ViewAfter).IsChecked = true;
        suppress = false;
    }

    void SetViewEnabled(bool on)
    {
        ViewBefore.IsEnabled = ViewSide.IsEnabled = on;
        if (!on) SetView("after");
    }

    void DrawLegend()
    {
        const int steps = 40;
        var px = new byte[steps * 4];
        for (int i = 0; i < steps; i++)
        {
            var (r, g, b) = SoftRender.HeatColor(i / (double)(steps - 1), 1.0);
            px[4 * i] = (byte)(b * 255); px[4 * i + 1] = (byte)(g * 255); px[4 * i + 2] = (byte)(r * 255); px[4 * i + 3] = 255;
        }
        var bmp = BitmapSource.Create(steps, 1, 96, 96, PixelFormats.Bgra32, null, px, steps * 4);
        Legend.Source = bmp;
        RenderOptions.SetBitmapScalingMode(Legend, BitmapScalingMode.NearestNeighbor);
        LegendMax.Text = $"0 ... {PyFormat.G(HeatMax())} %";
    }

    // ------------------------------------------------------------------ content

    int showReq;
    /// <summary>The try simplify result on screen (null once a node or a change is shown).</summary>
    InspectorModel.TryResult? lastTry;
    internal InspectorModel.TryResult? LastTry => lastTry;
    bool userPaint;   // the user picked a colouring since the last node / change was shown: try simplify must not override it
    const long MaxPreviewTris = 8_000_000, MaxContextTris = 12_000_000;

    static (List<SoftRender.Mesh> meshes, long tris, bool cut) Meshes(Node node, double[]? start, long budget, int style)
    {
        var meshes = new List<SoftRender.Mesh>();
        long total = 0;
        bool cut = false;
        foreach (var (n, w, ss) in SoftRender.GeometryItems(node, start: start))
        {
            var m = SoftRender.MeshFromGeom(n.Geom, w, ss);
            if (m == null) continue;
            if (total + m.NumTris > budget && meshes.Count > 0) { cut = true; break; }
            total += m.NumTris;
            meshes.Add(style == 0 ? m : m.WithStyle(style));
        }
        return (meshes, total, cut);
    }

    void OnWholeSceneChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (shown.Count > 0) ShowNodes(shown);                          // one or more nodes are shown
        else if (Node != null && mBefore.Count == 0) Redraw();          // a change without Before geometry is shown: same view, redrawn
    }

    /// <summary>The nodes the current picture shows (empty after a change or a try).</summary>
    IReadOnlyList<Node> shown = Array.Empty<Node>();

    /// <summary>Show the current state of a node (and make it the try-simplify target).</summary>
    public void ShowNode(Node node) => ShowNodes(new[] { node });

    /// <summary>Show the current state of one or more nodes (the first is the try-simplify target). The meshes are built on
    /// a worker thread. With "scene around" the nodes are drawn in orange where they sit in the scene and the rest of the
    /// scene in grey (built once per analysis and shared, see <see cref="ContextFor"/>). Several nodes are always drawn
    /// where they sit; one node alone in its own coordinates. The camera is fitted to the nodes (three-quarter view).</summary>
    public void ShowNodes(IReadOnlyList<Node> nodes)
    {
        int my = ++showReq;
        nodes = nodes.ToList();
        shown = nodes;
        userPaint = false;
        ClearTry();
        VerdictText.Text = "";
        if (nodes.Count == 0)
        {
            Node = null;
            title = "";
            mAfter = new(); mBefore = new(); mContext = new();
            cam = null;
            View.Source = null;
            InfoText.Text = "Select a node in the tree. Drag = rotate, right-drag = move, wheel = zoom.";
            return;
        }
        var node = nodes[0];
        Node = node;
        title = nodes.Count == 1 ? (node.Name.Length > 0 ? node.Name : node.Cls) : $"{nodes.Count} nodes";
        InfoText.Text = $"{title}: preparing the preview ...";
        var diagStart = insp?.SelectedAncestors(node) is { } dAnc ? SoftRender.PathMatrix(dAnc)
            : insp?.Rep != null ? SoftRender.ParentMatrix(node, insp.Rep.Parents) : null;
        var root = insp?.Scene?.Root;
        var rep = insp?.Rep;
        bool around = compact ? SceneAround : WholeScene.IsChecked == true;
        bool whole = around && root != null && rep != null && !nodes.Any(n => ReferenceEquals(n, root));
        bool placed = (whole || nodes.Count > 1) && rep != null;   // drawn where they sit in the scene
        var ctxTask = whole ? ContextFor(root!, rep!) : null;
        var ancestors = placed ? nodes.Select(n => insp!.SelectedAncestors(n)).ToList() : null;   // the clicked place of a shared node
        long selBudget = compact ? MaxPreviewTris / 4 : MaxPreviewTris, ctxBudget = compact ? MaxContextTris / 4 : MaxContextTris;
        Task.Run(() =>
        {
            var meshes = new List<SoftRender.Mesh>();
            long tris = 0;
            bool cut = false;
            for (int i = 0; i < nodes.Count && !cut; i++)
            {
                var start = !placed ? null : ancestors![i] is { } anc ? SoftRender.PathMatrix(anc) : SoftRender.ParentMatrix(nodes[i], rep!.Parents);
                var part = Meshes(nodes[i], start, Math.Max(selBudget - tris, 1), whole ? 2 : 0);
                meshes.AddRange(part.meshes);
                tris += part.tris;
                cut = part.cut || (tris >= selBudget && i < nodes.Count - 1);
            }
            double diag = InspectorModel.NodeDiagonal(node, diagStart);
            var ctx = ctxTask?.Result ?? (new List<SoftRender.Mesh>(), 0L, false);
            if (ctx.tris > ctxBudget)
            {
                // compact: draw only the first part of the shared context
                var some = new List<SoftRender.Mesh>();
                long t = 0;
                foreach (var m in ctx.meshes)
                {
                    if (t + m.NumTris > ctxBudget && some.Count > 0) break;
                    t += m.NumTris;
                    some.Add(m);
                }
                ctx = (some, t, true);
            }
            return ((meshes, tris, cut), ctx, diag);
        }).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                if (my == showReq) InfoText.Text = $"preview failed: {(t.Exception.InnerException ?? t.Exception).Message}";
                return;
            }
            var ((meshes, tris, cut), ctx, nodeDiag) = t.Result;
            if (my != showReq || !ReferenceEquals(Node, node)) return;   // another node / change was shown meanwhile
            if (lastTry == null && nodeDiag > 0 && double.IsFinite(nodeDiag)) TryEditor.Diag = nodeDiag;   // "≈ metres" before the first Try
            mAfter = meshes;
            mBefore = new();
            mContext = whole ? ctx.meshes : new();
            SetViewEnabled(false);
            cam = SoftRender.Camera.Fit(mAfter.Count > 0 ? mAfter : mContext);
            InfoText.Text = $"{title}: {PyFormat.Int(tris)} triangles in {mAfter.Count} geometr{(mAfter.Count == 1 ? "y" : "ies")}" +
                            (cut ? $" (only the first {selBudget / 1_000_000} million triangles are shown)" : "") +
                            (whole ? $"; scene around it: {PyFormat.Int(ctx.tris)} triangles{(ctx.cut ? $" (first {ctxBudget / 1_000_000} million)" : "")}" : "") +
                            "   (drag = rotate, right-drag = move, wheel = zoom out to see the scene)";
            if (mAfter.Count == 0 && mContext.Count == 0) View.Source = null;   // nothing to draw: no picture of the last node
            Redraw();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Before / after of one recorded change (geometry in its own coordinates).</summary>
    public void ShowChange(Change ch)
    {
        if (insp != null) insp.PendingPreview = null;
        showReq++;
        userPaint = false;
        ClearTry();
        mContext = new();
        shown = Array.Empty<Node>();
        Node = ch.Node;
        title = $"{ch.PassName}: {(ch.Node.Name.Length > 0 ? ch.Node.Name : ch.Node.Cls)}";
        int my = showReq;
        if (ch.Quality == null && ch.Before != null && ch.After != null)
        {
            // not checked yet (outside the first changes the list checks): on a worker, then shown with the verdict if
            // the change is still the one on screen
            var ss = insp?.CheckState(ch) ?? ch.StateBefore;
            string seams = insp?.CheckSeams(ch) ?? "keep";
            ShowChangeMeshes(ch, null, first: true);
            VerdictText.Text = "checking ...";
            Task.Run(() => InspectorModel.SafeCompare(ch.Before, ch.After, ss, seams)).ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    // SafeCompare catches the check's own errors; anything else must not leave "checking ..." up
                    if (my == showReq) VerdictText.Text = $"check failed: {(t.Exception.InnerException ?? t.Exception).Message}";
                    return;
                }
                ch.Quality ??= t.Result;
                if (my == showReq && ReferenceEquals(Node, ch.Node)) ShowChangeMeshes(ch, ch.Quality, first: false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        ShowChangeMeshes(ch, ch.Quality, first: true);
    }

    /// <summary>The before / after meshes of a change, the heat map from its check (when there is one) and the verdict.
    /// <paramref name="first"/>: fit the camera and pick the view (false when the check arrives later: the user may
    /// have turned the view meanwhile).</summary>
    void ShowChangeMeshes(Change ch, QualityReport? q, bool first)
    {
        mBefore = new[] { SoftRender.MeshFromGeom(ch.Before, null, ch.StateBefore ?? ch.Node.StateSet) }.OfType<SoftRender.Mesh>().ToList();
        mAfter = new[] { SoftRender.MeshFromGeom(ch.After, null, ch.StateAfter ?? ch.Node.StateSet, q?.VertexDev) }.OfType<SoftRender.Mesh>().ToList();
        if (first || cam == null)
        {
            cam = SoftRender.Camera.Fit(mBefore.Count > 0 ? mBefore : mAfter);
            SetViewEnabled(mBefore.Count > 0);
            SetView(mBefore.Count > 0 ? "side" : "after");
        }
        if (q?.VertexDev != null && (first || !userPaint)) { suppress = true; ColorBox.SelectedIndex = 1; suppress = false; }
        var (b, a, vb, va) = ch.Counts();
        ShowQuality(q, (b, a, vb, va));
        Redraw();
    }

    void ShowQuality(QualityReport? q, (long, long, long, long)? counts)
    {
        var (info, verdict) = InspectorModel.QualityText(title, q, counts);
        InfoText.Text = info;
        VerdictText.Text = verdict;
        VerdictText.Foreground = Ui.VerdictBrush(q?.Verdict);
    }

    // ------------------------------------------------------------------ try simplify

    (string Button, SimplifyParams Next)? nextStep;

    void OnNext(object sender, RoutedEventArgs e)
    {
        if (nextStep is not { } step || insp == null) return;
        insp.SetSimplify(step.Next, null);
        TrySimplify();
    }

    /// <summary>After a dead end the reason in the info line and a button with the next setting; otherwise no button.</summary>
    void ShowNextStep(InspectorModel.TryResult? res)
    {
        nextStep = res != null ? InspectorModel.NextStep(res.Params, res) : null;
        NextButton.Visibility = nextStep != null ? Visibility.Visible : Visibility.Collapsed;
        if (nextStep is { } s) NextButton.Content = s.Button;
    }

    void ClearTry()
    {
        ShowNextStep(null);
        lastTry = null;
        applied = false;
        TryEditor.ResultText = "";
        TryEditor.Diag = null;
        RefreshApply();
    }

    bool applied;
    const string ApplyTip = "Run simplify with these settings on the parts the preview simplified (undo with Ctrl+Z)";

    /// <summary>Apply is enabled only while the shown try was made with the current record (and removed triangles).</summary>
    public void RefreshApply()
    {
        if (applied)
        {
            ApplyButton.Content = "Applied";
            ApplyButton.IsEnabled = false;
            ApplyButton.ToolTip = "Applied as previewed (undo with Ctrl+Z)";
            return;
        }
        ApplyButton.Content = "Apply to Scene";
        bool match = lastTry != null && insp != null && lastTry.Params.Normalized() == insp.Simplify.Normalized();
        ApplyButton.IsEnabled = match && lastTry!.TrisAfter < lastTry.TrisBefore;
        ApplyButton.ToolTip = lastTry != null && !match ? "Settings changed since the preview: press Preview first" : ApplyTip;
        if (lastTry != null && !match) ShowNextStep(null);   // the suggestion belongs to the settings that were tried
    }

    /// <summary>The previewed simplify was applied: keep the side-by-side and the verdict, show the button as done.</summary>
    public void Applied()
    {
        applied = true;
        RefreshApply();
    }

    void OnTry(object sender, RoutedEventArgs e) => TrySimplify();

    /// <summary>Simplify a copy of the node with the shared record (<see cref="MainWindow.Simplify"/>), placed where the
    /// tree row sits so "% of size" and world distances match Apply.</summary>
    public void TrySimplify()
    {
        var node = Node;
        if (node == null || busyTry || insp == null) return;
        var p = insp.Simplify;
        double[]? start = insp.SelectedAncestors(node) is { } anc ? SoftRender.PathMatrix(anc)
            : insp.Rep != null ? SoftRender.ParentMatrix(node, insp.Rep.Parents) : null;
        busyTry = true;
        ShowNextStep(null);
        InfoText.Text = $"simplifying ({p.Describe()}) ...";
        var rep = insp.Rep;
        // "Include externals" as Apply will run it: off, geometry inside external references is neither tried nor applied
        bool withExt = insp.IncludeExternals;
        // the unselected geometries around the node: the verdict reports new gaps to them (R2-D5 UX-09)
        Task.Run(() => InspectorModel.TrySimplify(node, p,
            rep != null ? InspectorModel.TryContextFor(rep, node, start, withExt) : new InspectorModel.TryContext(start, IncludeExternals: withExt))).ContinueWith(t =>
        {
            busyTry = false;
            if (t.Exception != null)
            {
                var exc = t.Exception.InnerException ?? t.Exception;
                InfoText.Text = $"simplify failed: {exc.GetType().Name}: {exc.Message}";
                return;
            }
            if (!ReferenceEquals(node, Node)) return;
            showReq++;
            var res = t.Result;
            if (res == null)
            {
                InfoText.Text = withExt ? "Nothing to simplify below this node."
                    : "Nothing to simplify below this node (externals excluded: tick Optimize › Include externals to try them).";
                return;
            }
            mBefore = res.Before;
            mAfter = res.After;
            mContext = new();
            shown = Array.Empty<Node>();
            lastTry = res;
            title = $"Try simplify {res.Params.Describe(res.Diag)} on {(node.Name.Length > 0 ? node.Name : node.Cls)}"
                    + (res.ExternalsSkipped ? " (externals excluded)" : "");
            TryEditor.ResultText = $"→ {PyFormat.Int(res.TrisAfter)} of {PyFormat.Int(res.TrisBefore)}";
            TryEditor.Diag = res.Diag;
            SetViewEnabled(true);
            SetView("side");
            if (!userPaint) { suppress = true; ColorBox.SelectedIndex = 1; suppress = false; }
            ShowQuality(res.Quality, (res.TrisBefore, res.TrisAfter, res.VertsBefore, res.VertsAfter));
            if (res.TrisAfter >= res.TrisBefore) InfoText.Text = InspectorModel.NothingLine(res);
            ShowNextStep(res);
            applied = false;
            RefreshApply();
            DrawLegend();
            Redraw();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Run the simplify pass on the node with the shared record (CollectParams merges it).</summary>
    void OnApply(object sender, RoutedEventArgs e)
    {
        if (Node == null || insp == null) return;
        var p = insp.Simplify;
        if (lastTry == null || !ApplyButton.IsEnabled) return;
        if (!Dialogs.Confirm(insp.DialogOwner, "Apply to scene?",
                $"Apply as previewed: {p.Describe(lastTry.Diag)}.\n\nYou can check the result in Review and undo it (Ctrl+Z).", "Apply")) return;
        ApplyButton.IsEnabled = false;
        // exactly the geometries the try simplified, with the previewed limit: coarser LOD levels, switched-off children
        // and hidden parts the preview never showed stay as they are
        var (targets, prms) = InspectorModel.ApplyArgs(lastTry, insp.CollectParams());
        if (targets.Count == 0)
        {
            // every previewed part lies inside an external reference and "Include externals" is off now (no targets
            // would mean the whole scene)
            InfoText.Text = "Nothing applied: the previewed parts are inside external references and Include externals is off.";
            RefreshApply();
            return;
        }
        insp.RunPasses(new List<string> { "simplify" }, targets, prms, fromPreview: true, scopeNodes: new List<Node> { Node });
    }

    // ------------------------------------------------------------------ camera / mouse

    void OnResetView(object sender, RoutedEventArgs e)
    {
        cam = SoftRender.Camera.Fit(mBefore.Count > 0 ? mBefore : mAfter);
        Redraw();
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (compact)
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && Node != null) Opened?.Invoke();
            return;
        }
        drag = (e.ChangedButton == MouseButton.Left ? "orbit" : "pan", e.GetPosition(ViewBorder));
        ViewBorder.CaptureMouse();
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (drag == null || cam == null) return;
        var p = e.GetPosition(ViewBorder);
        var (what, at) = drag.Value;
        double dx = p.X - at.X, dy = p.Y - at.Y;
        drag = (what, p);
        if (what == "orbit") cam.Orbit(-dx * 0.5, dy * 0.4);
        else
        {
            double w = Math.Max(ViewBorder.ActualWidth, 1);
            cam.Pan(dx / w * 2, dy / w * 2);
        }
        Redraw(fast: true);
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (compact) return;
        drag = null;
        ViewBorder.ReleaseMouseCapture();
        RedrawLater(60);
    }

    void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (cam == null || compact) return;
        cam.Zoom(e.Delta > 0 ? 0.85 : 1 / 0.85);
        Redraw(fast: true);
        RedrawLater(250);
    }

    void OnViewSize(object sender, SizeChangedEventArgs e) => RedrawLater(150);

    // ------------------------------------------------------------------ drawing

    void RedrawLater(int ms)
    {
        delayed.Stop();
        delayed.Interval = TimeSpan.FromMilliseconds(ms);
        delayed.Start();
    }

    /// <summary>Render on a worker thread; only the newest request is shown (a fast, low resolution one while dragging).</summary>
    public void Redraw(bool fast = false)
    {
        if (cam == null || (mAfter.Count == 0 && mBefore.Count == 0 && mContext.Count == 0)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int W = (int)Math.Max(ViewBorder.ActualWidth * dpi.DpiScaleX, 200), H = (int)Math.Max(ViewBorder.ActualHeight * dpi.DpiScaleY, 150);
        double scale = fast ? 0.4 : 1.0;
        string view = ViewMode, mode = ShadeMode;
        bool wire = mode != "shaded" && !fast, shade = mode != "wireframe", back = !compact && BackFaces.IsChecked == true;
        double? heat = HeatMap ? HeatMax() : null;
        var paint = Paint;
        var c = cam.Copy();
        var before = mBefore;
        // selection first: it wins depth ties with its copy in the scene; a big scene is left out while dragging
        bool ctx = mContext.Count > 0 && (!fast || mContext.Sum(cm => (long)cm.NumTris) < 2_000_000);
        var after = ctx ? mAfter.Concat(mContext).ToList() : mAfter;
        int myReq = ++req;
        Task.Run(() =>
        {
            int w = Math.Max(1, (int)(W * scale)), h = Math.Max(1, (int)(H * scale));
            if (view == "side" && before.Count > 0)
            {
                int half = Math.Max(1, (w - 2) / 2);
                var (a, _) = SoftRender.Render(before, c, half, h, wire, shade, back, paint: paint);
                var (b, _) = SoftRender.Render(after, c, half, h, wire, shade, back, heat, paint: paint);
                int total = half * 2 + 2;
                var img = new byte[total * h * 4];
                for (int y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(a, y * half * 4, img, y * total * 4, half * 4);
                    for (int x = half; x < half + 2; x++) for (int k = 0; k < 4; k++) img[(y * total + x) * 4 + k] = 255;
                    Buffer.BlockCopy(b, y * half * 4, img, (y * total + half + 2) * 4, half * 4);
                }
                return (img, total, h);
            }
            var meshes = view == "before" && before.Count > 0 ? before : after;
            var (im, _) = SoftRender.Render(meshes, c, w, h, wire, shade, back, ReferenceEquals(meshes, after) ? heat : null, paint: paint);
            return (im, w, h);
        }).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                InfoText.Text = $"preview failed: {(t.Exception.InnerException ?? t.Exception).Message}";
                return;
            }
            if (myReq != req && drag == null) return;
            var (img, w, h) = t.Result;
            var bmp = new WriteableBitmap(w, h, 96 * dpi.DpiScaleX / scale, 96 * dpi.DpiScaleY / scale, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, w, h), img, w * 4, 0);
            RenderOptions.SetBitmapScalingMode(View, fast ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            View.Source = bmp;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
