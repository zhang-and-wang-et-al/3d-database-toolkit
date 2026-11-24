using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SceneKit;

namespace SceneInspector;

/// <summary>One line of the Similar view: a group of copies of the same object.</summary>
public sealed class SimilarRow
{
    public SimilarGroup G { get; }
    readonly long sceneTris;
    public SimilarRow(SimilarGroup g, long sceneTris) { G = g; this.sceneTris = sceneTris; }

    public string Name => G.Name.Length > 0 ? G.Name : "(unnamed)";
    public string Copies => PyFormat.Int(G.Copies);
    public string CopiesTip => G.Members.Count == G.Copies ? $"{G.Copies} copies, each a node of its own"
        : $"{PyFormat.Int(G.Copies)} drawn copies from {PyFormat.Int(G.Members.Count)} node(s) (shared nodes are drawn several times)";
    public string Each => Analyzer.HumanCount(G.TrisEach);
    public string Total => Analyzer.HumanCount(G.TrisTotal);
    public double Share => G.TrisTotal / (double)Math.Max(sceneTris, 1);
    public double ShareWidth => 38 * Math.Clamp(Share, 0, 1);
    public string TotalTip => $"{PyFormat.Int(G.TrisTotal)} triangles in all copies, {PyFormat.Fixed(Math.Clamp(Share, 0, 1) * 100, 1)}% of the scene";
    public string Variants => MainWindow.VariantsText(G);
    public string VariantsTip => string.Join("\n", G.Variants.Select(v => $"{v.Count}× {MainWindow.VariantName(v.Label)}"));
    public string ClassLabel => G.ClassLabel;
    public Brush ClassBrush => Ui.Brush(MainWindow.ClassKeys(G.Class).fg);
    public Brush ClassTint => Ui.Brush(MainWindow.ClassKeys(G.Class).bg);
    public string ClassTip => MainWindow.ClassTip(G);
    public string Differences => G.Differences;
}

/// <summary>One copy in the member list of the Similar view.</summary>
public sealed record SimilarMemberRow(SimilarMember M, bool Reference)
{
    public string Path => M.Path;
    public string Variant => MainWindow.VariantName(M.VariantLabel);
    public string Position => string.Join(", ", M.Position.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)));
    public string Inst => M.Instances > 1 ? $"×{M.Instances}" : "";
    public string MatchLabel => Reference ? "reference" : M.Match.ToString().ToLowerInvariant();
    public Brush MatchBrush => Ui.Brush(Reference ? "TextMuted" : MainWindow.ClassKeys(M.Match).fg);
    public Brush MatchTint => Ui.Brush(Reference ? "Sev.Info.Tint" : MainWindow.ClassKeys(M.Match).bg);
    public string MatchTip => Reference ? $"First copy of shape {M.Shape + 1}: the others of this shape are compared with it" : M.Match switch
    {
        Similarity.Instanced => "The same shared content as the reference",
        Similarity.Identical => "The same vertex data as the reference; only placement / state differ",
        Similarity.Rigid => "The reference turned and moved (vertices pre-transformed)",
        _ => "Matches the criteria only",
    };
}

public partial class MainWindow
{
    // ============================================================== problems: similar objects (S4)

    /// <summary>Groups of the last search and the report they were found in (null: not searched since the scene changed).</summary>
    List<SimilarGroup>? simGroups;
    Report? simRep;
    bool simStale;
    string simOrder = "total";
    bool simReverse;
    bool fillingSimilar;
    /// <summary>Geometry node -> the group of copies it is part of (the Hotspots "×N similar" column).</summary>
    Dictionary<Node, SimilarGroup> simByGeom = new(ReferenceEqualityComparer.Instance);

    internal static (string fg, string bg) ClassKeys(Similarity s) => s switch
    {
        Similarity.Instanced => ("Verdict.Ok", "Verdict.Ok.Tint"),
        Similarity.Identical => ("External", "External.Tint"),
        Similarity.Rigid => ("Accent", "AccentTint"),
        _ => ("Sev.Med", "Sev.Med.Tint"),
    };

    internal static string ClassTip(SimilarGroup g) => g.Class switch
    {
        Similarity.Instanced => "Instanced: the copies already share one node (drawn several times). Memory is already saved; Billboard / Proxy All still bake it once.",
        Similarity.Identical => "Identical: the same vertex data stored once per copy, only placement and state differ.\nMake Instances keeps one geometry and places it with transforms; Billboard All bakes one atlas per texture variant.",
        Similarity.Rigid => "Rigid: the same shape, turned and moved with the vertices pre-transformed.\nMake Instances keeps one geometry and places it with transforms; Billboard All bakes one atlas per texture variant.",
        _ => $"Similar: the criteria match, but the shapes differ ({g.Shapes} shapes). Check the copies first; cards / proxies are baked once per shape and variant, Make Instances only joins copies of the same shape.",
    };

    internal static string VariantName(string label) => label.Length > 0 ? label : "no texture";

    /// <summary>"2: 12× tree.rgb, 4× tree_autumn.rgb", or the one look.</summary>
    internal static string VariantsText(SimilarGroup g) => g.Variants.Count <= 1
        ? VariantName(g.Variants.FirstOrDefault()?.Label ?? "")
        : $"{g.Variants.Count}: " + string.Join(", ", g.Variants.Select(v => $"{v.Count}× {VariantName(v.Label)}"));

    SimilarCriteria SimCriteria() => new()
    {
        Name = SimName.IsChecked == true, Size = SimSize.IsChecked == true, Topology = SimTopology.IsChecked == true, Texture = SimTexture.IsChecked == true,
    };

    bool SimCriteriaValid => SimName.IsChecked == true || SimSize.IsChecked == true || SimTopology.IsChecked == true;

    bool SimilarVisible => PanelShown(PanelId.Problems) && ProbSimilar.IsChecked == true;

    /// <summary>The scene changed (open, run, undo): the groups point at old nodes, search again when the view is shown.</summary>
    void InvalidateSimilar()
    {
        if (ReferenceEquals(simRep, Rep)) return;
        simGroups = null;
        simRep = null;
        simByGeom = new Dictionary<Node, SimilarGroup>(ReferenceEqualityComparer.Instance);
        FillSimilar();
        // after the caller is done (a run switches to Review): search only if the view is still shown
        if (Rep != null) Dispatcher.BeginInvoke(() => { if (simGroups == null && !busy && SimilarVisible) FindSimilar(); });
    }

    /// <summary>Search in the background, then fill the list (and show a waiting "Select Similar Objects").</summary>
    void FindSimilar()
    {
        if (Rep == null || !SimCriteriaValid) return;
        var rep = Rep;
        var crit = SimCriteria();
        Ui.SetEmptyText(SimList, "Searching for similar objects …");
        Work("Finding similar objects ...", progress => SimilarObjects.Find(rep, crit, progress), groups =>
        {
            if (!ReferenceEquals(rep, Rep)) return;   // the scene changed meanwhile
            simGroups = groups;
            simRep = rep;
            simStale = false;
            simByGeom = new Dictionary<Node, SimilarGroup>(ReferenceEqualityComparer.Instance);
            foreach (var g in groups)
                foreach (var m in g.Members)
                    foreach (var n in GeometryBelow(m.Node))
                        if (!simByGeom.TryGetValue(n, out var had) || had.Copies < g.Copies) simByGeom[n] = g;
            FillSimilar();
            FillHotspots();
            Status.Text = groups.Count == 0 ? "No similar objects found" :
                $"{PyFormat.Int(groups.Count)} group{(groups.Count == 1 ? "" : "s")} of similar objects, {PyFormat.Int(groups.Sum(g => g.Copies))} copies";
            ShowPendingSimilar();
        });
    }

    static IEnumerable<Node> GeometryBelow(Node n)
    {
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>();
        stack.Push(n);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (!seen.Add(x)) continue;
            if (x.Kind == Kind.Geometry) yield return x;
            foreach (var c in x.AllChildren()) stack.Push(c);
        }
    }

    internal void OnSimFind(object sender, RoutedEventArgs e) => FindSimilar();

    internal void OnSimCriteria(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        simStale = simGroups != null;
        UpdateSimToolbar();
    }

    void UpdateSimToolbar()
    {
        SimFindButton.IsEnabled = Rep != null && SimCriteriaValid;
        SimFindButton.ToolTip = !SimCriteriaValid ? "Tick at least one of Name, Size, Topology" : Rep == null ? "Open a scene first"
            : "Search the scene for copies of the same object with these criteria";
        SimFindButton.Style = (Style)FindResource(simStale ? "Button.Accent" : (object)typeof(Button));
        if (simGroups == null) { SimSummary.Text = ""; return; }
        SimSummary.Text = simStale ? "Criteria changed - press Find" :
            $"{PyFormat.Int(simGroups.Count)} group{(simGroups.Count == 1 ? "" : "s")}  ·  {PyFormat.Int(simGroups.Sum(g => g.Copies))} copies  ·  " +
            $"{Analyzer.HumanCount(simGroups.Sum(g => g.TrisTotal))} tris in copies";
    }

    static readonly HashSet<string> SimTextOrders = new() { "name", "class", "variants", "diff" };

    internal void OnSimHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: GridViewColumn col } || col.Header is not GridViewColumnHeader { Tag: string mode }) return;
        simReverse = mode == simOrder && !simReverse;
        simOrder = mode;
        FillSimilar();
    }

    void FillSimilar()
    {
        UpdateSimToolbar();
        var keep = SelectedGroups().Select(g => g.Name).ToHashSet();
        if (simGroups == null || Rep == null)
        {
            SimList.ItemsSource = null;
            Ui.SetEmptyText(SimList, Rep == null ? "Open a scene first." : "Press Find to search for copies of the same object.");
            UpdateSimDetail();
            return;
        }
        long sceneTris = Rep.Total.Tris;
        var rowsIn = simGroups.Select(g => new SimilarRow(g, sceneTris));
        IEnumerable<SimilarRow> sorted = simOrder switch
        {
            "name" => rowsIn.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "copies" => rowsIn.OrderByDescending(r => r.G.Copies),
            "each" => rowsIn.OrderByDescending(r => r.G.TrisEach),
            "class" => rowsIn.OrderBy(r => r.G.Class),
            "variants" => rowsIn.OrderByDescending(r => r.G.Variants.Count),
            "diff" => rowsIn.OrderBy(r => r.Differences, StringComparer.OrdinalIgnoreCase),
            _ => rowsIn.OrderByDescending(r => r.G.TrisTotal),
        };
        var items = sorted.ToList();
        if (simReverse) items.Reverse();
        fillingSimilar = true;
        SimList.ItemsSource = items;
        foreach (var it in items.Where(i => keep.Contains(i.G.Name))) SimList.SelectedItems.Add(it);
        fillingSimilar = false;
        if (SimList.SelectedItems.Count == 0 && items.Count > 0) SimList.SelectedItem = items[0];

        bool asc = SimTextOrders.Contains(simOrder) != simReverse;
        foreach (var col in ((GridView)SimList.View).Columns)
        {
            if (col.Header is not GridViewColumnHeader h || h.Content is not string text) continue;
            var label = text.TrimEnd(' ', '▴', '▾');
            h.Content = (h.Tag as string) == simOrder ? label + (asc ? " ▴" : " ▾") : label;
        }
        Ui.SetEmptyText(SimList, "No similar objects with these criteria.\nUntick Name to group objects with other names, or tick fewer criteria.");
        UpdateSimDetail();
    }

    List<SimilarGroup> SelectedGroups() => SimList.SelectedItems.Cast<SimilarRow>().Select(r => r.G).ToList();

    internal void OnSimSelect(object sender, SelectionChangedEventArgs e)
    {
        if (!fillingSimilar) UpdateSimDetail();
    }

    /// <summary>Copies of the selected groups, without copies that lie inside another one (a crown inside a selected tree).</summary>
    List<SimilarMember> SelectedMembers()
    {
        var all = SelectedGroups().SelectMany(g => g.Members).ToList();
        var nodes = new HashSet<Node>(all.Select(m => m.Node), ReferenceEqualityComparer.Instance);
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        return all.Where(m => seen.Add(m.Node) && !HasAncestorIn(m.Node, nodes)).ToList();
    }

    bool HasAncestorIn(Node n, HashSet<Node> set)
    {
        if (Rep == null) return false;
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>(Rep.Parents.TryGetValue(n, out var ps) ? ps : new List<Node>());
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p)) continue;
            if (set.Contains(p)) return true;
            if (Rep.Parents.TryGetValue(p, out var pp)) foreach (var x in pp) stack.Push(x);
        }
        return false;
    }

    /// <summary>Sets of copies that one bake / one shared geometry covers: (group, variant, shape).</summary>
    static int Looks(IEnumerable<(SimilarGroup g, SimilarMember m)> members) => members.Select(x => (x.g, x.m.Variant, x.m.Shape)).Distinct().Count();

    List<(SimilarGroup g, SimilarMember m)> SelectedPairs()
    {
        var keep = SelectedMembers().ToHashSet();
        return SelectedGroups().SelectMany(g => g.Members.Where(keep.Contains).Select(m => (g, m))).ToList();
    }

    /// <summary>Why Make Instances has nothing to do for the groups (null: it has).</summary>
    static string? InstanceBlock(List<SimilarGroup> groups)
    {
        if (groups.Count == 0) return "Select a group first";
        foreach (var g in groups)
            foreach (var set in g.Members.GroupBy(m => (m.Variant, m.Shape)))
                if (set.Count() >= 2 && set.Any(m => m.Match is Similarity.Identical or Similarity.Rigid)) return null;
        if (groups.All(g => g.Class == Similarity.Instanced)) return "The copies already share one node (instanced): nothing to share";
        if (groups.All(g => g.Class == Similarity.Similar && g.Shapes >= g.Members.Count)) return "Every copy has a shape of its own (similar only): there is no identical or rigid copy to share";
        return "No texture variant has two identical or rigid copies (copies that look different stay separate)";
    }

    void UpdateSimDetail()
    {
        var groups = SelectedGroups();
        bool any = groups.Count > 0 && !busy && Scene != null;
        SimSelectButton.IsEnabled = SimBillboardButton.IsEnabled = SimCrossButton.IsEnabled = SimCardsLayoutButton.IsEnabled = SimProxyButton.IsEnabled = any;
        var block = InstanceBlock(groups);
        SimInstanceButton.IsEnabled = any && block == null;
        SimInstanceButton.ToolTip = block ?? "Keep one geometry per texture variant and shape; every other identical / rigid copy becomes a transform placing it (saves memory, the look stays)";
        if (groups.Count == 0)
        {
            SimDetailTitle.Text = "";
            SimDetailTitle.Visibility = Visibility.Collapsed;
            SimDetailText.Text = simGroups == null ? "Copies of the same object under different nodes show up here, with what differs between them." :
                "Select a group (Ctrl / Shift for several) to see its copies and replace or instance all of them.";
            SimMembers.ItemsSource = null;
            return;
        }
        SimDetailTitle.Visibility = Visibility.Visible;
        var g0 = groups[0];
        if (groups.Count == 1)
        {
            SimDetailTitle.Text = $"{(g0.Name.Length > 0 ? g0.Name : "(unnamed)")}  ·  {PyFormat.Int(g0.Copies)} copies  ·  {g0.ClassLabel}";
            SimDetailText.Text = g0.Differences.Length > 0 ? g0.Differences : "No differences: the copies look the same.";
        }
        else
        {
            SimDetailTitle.Text = $"{groups.Count} groups  ·  {PyFormat.Int(groups.Sum(g => g.Copies))} copies";
            SimDetailText.Text = string.Join(",  ", groups.Take(6).Select(g => $"{g.Name} ({g.Copies}, {g.ClassLabel})")) + (groups.Count > 6 ? ", …" : "");
        }
        var members = new List<SimilarMemberRow>();
        foreach (var g in groups)
        {
            var refs = new HashSet<int>();
            foreach (var m in g.Members) members.Add(new SimilarMemberRow(m, refs.Add(m.Shape)));
        }
        SimMembers.ItemsSource = members;
    }

    internal void OnSimKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnSimSelectAll(sender, e);
        e.Handled = true;
    }

    internal void OnSimMemberKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SimMemberGoto();
        e.Handled = true;
    }

    internal void OnSimMemberGoto(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(SimMembers, d) is ListViewItem) SimMemberGoto();
    }

    void SimMemberGoto()
    {
        var nodes = SimMembers.SelectedItems.Cast<SimilarMemberRow>().Select(r => r.M.Node).ToList();
        if (nodes.Count == 0) return;
        LeftTree.IsChecked = true;
        Reveal(nodes, 500);
    }

    internal void OnSimSelectAll(object sender, RoutedEventArgs e)
    {
        var nodes = SelectedMembers().Select(m => m.Node).ToList();
        if (nodes.Count == 0) return;
        const int limit = 500;
        LeftTree.IsChecked = true;
        Reveal(nodes, limit);
        Status.Text = nodes.Count > limit ? $"The first {limit} of {PyFormat.Int(nodes.Count)} copies selected in the tree"
            : $"{PyFormat.Int(nodes.Count)} copies selected in the tree - the Optimize tab now runs on them";
    }

    internal void OnSimMenuOpening(object sender, ContextMenuEventArgs e)
    {
        UpdateSimDetail();
        var menu = SimMenu;
        menu.Items.Clear();
        menu.Items.Add(MenuEntry("Select All in Tree", "", OnSimSelectAll, SimSelectButton.IsEnabled, gesture: "Enter"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Billboard All", "", OnSimCards, SimBillboardButton.IsEnabled, tag: "billboard"));
        menu.Items.Add(MenuEntry("Cross Cards All", "", OnSimCards, SimCrossButton.IsEnabled, tag: "crossed"));
        menu.Items.Add(MenuEntry("Cards All…", "", OnSimCardsLayout, SimCardsLayoutButton.IsEnabled));
        menu.Items.Add(MenuEntry("Proxy All", "", OnSimProxy, SimProxyButton.IsEnabled));
        var inst = MenuEntry("Make Instances", "", OnSimInstances, SimInstanceButton.IsEnabled, tip: SimInstanceButton.ToolTip as string);
        ToolTipService.SetShowOnDisabled(inst, true);
        menu.Items.Add(inst);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Find Again", "", OnSimFind, !busy && Rep != null && SimCriteriaValid));
    }

    /// <summary>Always asks: the consequence sentence and the undo hint close every run.</summary>
    bool ConfirmSimRun(string title, string what, string button)
    {
        return Dialogs.Confirm(DialogOwner, title, what + "\n\nYou can check the result in Review and undo it (Ctrl+Z).", button);
    }

    /// <summary>What a replacement costs: the originals are gone from the saved file.</summary>
    internal static string ReplaceNote(int objects, long tris, string with) =>
        $"Replaces {Plural(objects, "object", "objects")} ({PyFormat.Int(tris)} triangles) with {with}; the originals are gone from the saved file; undo keeps them this session.";

    /// <summary>The proxy estimate of <paramref name="first"/> (the first target) as a paragraph for a confirm dialog,
    /// above <see cref="ReplaceNote"/>; empty without a target, and when it cannot be made (it never blocks the run).</summary>
    string ProxyEstimateParagraph(Node? first, Dictionary<string, object> prms)
    {
        if (first == null || Rep == null) return "";
        try { return EstimateText(first, prms, Rep.Parents) + "\n\n"; }
        catch (Exception) { return ""; }
    }

    /// <summary>The estimate's text (throws when it cannot be made). Touches no window state, so it may run off the UI thread.</summary>
    static string EstimateText(Node first, Dictionary<string, object> prms, IReadOnlyDictionary<Node, List<Node>> parents)
    {
        // within mode's error as the pass resolves it: generate_lod's screen height and field of view
        var pp = ProxyParams.FromPassParams(prms);
        int height = prms.TryGetValue("generate_lod.screen_height", out var h) ? Convert.ToInt32(h, CultureInfo.InvariantCulture) : 1080;
        double fov = prms.TryGetValue("generate_lod.fov", out var f) ? Convert.ToDouble(f, CultureInfo.InvariantCulture) : 45;
        return PartProxy.Estimate(first, pp, parents, pp.ErrorMetres(height, Math.Clamp(fov, 1, 179))).Text;
    }

    bool proxyEstimateDue;

    /// <summary>The estimate request in flight (latest wins: a newer one cancels it, and a cancelled one never writes the line).</summary>
    CancellationTokenSource? proxyEstimateCts;

    /// <summary>Refreshes the estimate line with the next tick of <c>selectionTimer</c> (the render's debounce), so clicks,
    /// arrow keys and keystrokes in the pass controls cost one estimate once they settle.</summary>
    void ScheduleProxyEstimate()
    {
        proxyEstimateDue = true;
        selectionTimer.Stop();
        selectionTimer.Start();
    }

    /// <summary>The estimate line under the replace_proxy rows: the first selected node's, empty without a selection. The
    /// estimate runs off the UI thread (discovery can take a while on a big selection; above the 2 M triangle cap it gives
    /// up at once); the newest request wins, its text is put back through the dispatcher, and an exception gives an
    /// empty line.</summary>
    void RefreshProxyEstimate()
    {
        proxyEstimateDue = false;
        proxyEstimateCts?.Cancel();
        proxyEstimateCts = null;
        if (proxyEstimate == null) return;
        // only while the replace_proxy pass is open or ticked; otherwise the line stays empty and nothing is discovered
        if (!(passExpanders.TryGetValue("replace_proxy", out var exp) && exp.IsExpanded) && passBoxes.GetValueOrDefault("replace_proxy")?.IsChecked != true)
        {
            proxyEstimate.Text = "";
            return;
        }
        var first = Scene != null ? SelectedNodes().FirstOrDefault() : null;
        if (first == null || Rep == null) { proxyEstimate.Text = ""; return; }
        var prms = CollectParams();
        var parents = Rep.Parents;
        var cts = new CancellationTokenSource();
        proxyEstimateCts = cts;
        var token = cts.Token;
        Task.Run(() => EstimateText(first, prms, parents), token).ContinueWith(t =>
        {
            _ = t.Exception;   // observed: a failed estimate is an empty line, not an unobserved task exception
            string text = t.Status == TaskStatus.RanToCompletion ? t.Result : "";   // faulted: an empty line
            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(proxyEstimateCts, cts)) return;   // a newer request owns the line
                proxyEstimate.Text = text.TrimEnd();
                proxyEstimateCts = null;
                cts.Dispose();
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    static string Plural(int n, string one, string many) => $"{PyFormat.Int(n)} {(n == 1 ? one : many)}";

    static long PairTris(List<(SimilarGroup g, SimilarMember m)> pairs) => pairs.Sum(x => x.m.Tris * Math.Max(1, x.m.Instances));

    internal void OnSimCards(object sender, RoutedEventArgs e)
    {
        string mode = (sender as FrameworkElement)?.Tag as string ?? "billboard";
        var pairs = SelectedPairs();
        if (pairs.Count == 0 || Scene == null) return;
        int variants = pairs.Select(x => (x.g, x.m.Variant)).Distinct().Count(), atlases = Looks(pairs);
        bool bb = mode == "billboard";
        string kind = bb ? "billboard" : "cross card";
        if (!ConfirmSimRun(bb ? "Billboard all?" : "Cross cards all?",
                $"{Plural(pairs.Count, "object", "objects")}, {Plural(variants, "variant", "variants")} → {Plural(atlases, kind + " atlas", kind + " atlases")}.\n\n" +
                (bb ? "Every copy becomes an axial billboard that turns to the viewer. " : "Every copy becomes crossed cards. ") +
                "Copies that look the same (same shape and textures) share one atlas and card; each copy keeps its place and size. Tilted copies get their own.\n\n" +
                ReplaceNote(pairs.Count, PairTris(pairs), bb ? "billboard cards" : "cross cards"),
                bb ? "Billboard All" : "Cross Cards All"))
            return;
        var prms = CollectParams();
        InspectorModel.SimCardsParams(prms, mode);
        RunPasses(new List<string> { "replace_cards" }, pairs.Select(x => x.m.Node).ToList(), prms);
    }

    internal void OnSimProxy(object sender, RoutedEventArgs e)
    {
        var pairs = SelectedPairs();
        if (pairs.Count == 0 || Scene == null) return;
        int variants = pairs.Select(x => (x.g, x.m.Variant)).Distinct().Count(), hulls = Looks(pairs);
        var prms = CollectParams();
        if (!ConfirmSimRun("Proxy all?",
                $"{Plural(pairs.Count, "object", "objects")}, {Plural(variants, "variant", "variants")} → {Plural(hulls, "proxy", "proxies")} with a baked texture.\n\n" +
                "Every copy becomes a simplified textured hull; copies that look the same share one hull and texture.\n\n" +
                ProxyEstimateParagraph(pairs[0].m.Node, prms) +
                ReplaceNote(pairs.Count, PairTris(pairs), "a proxy mesh"), "Proxy All"))
            return;
        prms["replace_proxy.share"] = true;
        RunPasses(new List<string> { "replace_proxy" }, pairs.Select(x => x.m.Node).ToList(), prms);
    }

    internal void OnSimInstances(object sender, RoutedEventArgs e)
    {
        var groups = SelectedGroups();
        if (InstanceBlock(groups) is string why) { Status.Text = why; return; }
        var pairs = SelectedPairs();
        if (pairs.Count == 0 || Scene == null) return;
        var sets = pairs.GroupBy(x => (x.g, x.m.Variant, x.m.Shape)).Where(s => s.Count() >= 2).ToList();
        int copies = sets.Sum(s => s.Count() - 1);
        if (!ConfirmSimRun("Make instances?",
                $"{Plural(pairs.Count, "object", "objects")} → about {Plural(copies, "copy", "copies")} replaced by instances of {Plural(sets.Count, "shared object", "shared objects")}.\n\n" +
                "Per texture variant and shape one geometry is kept; every other identical / rigid copy becomes a transform placing it. " +
                "Names below the replaced copies are lost.", "Make Instances"))
            return;
        RunPasses(new List<string> { "make_instances" }, pairs.Select(x => x.m.Node).ToList());
    }

    // ---- entry points from the tree and the hotspots

    /// <summary>Groups holding <paramref name="node"/> as a copy, or else its nearest ancestor that is one (first parent).</summary>
    List<SimilarGroup> GroupsOf(Node node)
    {
        if (simGroups == null || Rep == null) return new List<SimilarGroup>();
        Node? cur = node;
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        while (cur != null && seen.Add(cur))
        {
            var c = cur;
            var hit = simGroups.Where(g => g.Members.Any(m => ReferenceEquals(m.Node, c))).ToList();
            if (hit.Count > 0) return hit;
            cur = Rep.Parents.TryGetValue(cur, out var ps) && ps.Count > 0 ? ps[0] : null;
        }
        return new List<SimilarGroup>();
    }

    /// <summary>A "Select Similar Objects" waiting for the search.</summary>
    Node? pendingSimilarOf;

    /// <summary>Switch to the Similar view with the groups of the node selected (searching first when needed).</summary>
    void SelectSimilarOf(Node node)
    {
        pendingSimilarOf = node;
        ShowPanel(PanelId.Problems);
        ProbSimilar.IsChecked = true;   // the first open starts the search itself
        if (simGroups != null && !simStale) ShowPendingSimilar();
        else if (!busy) FindSimilar();
        else Status.Text = "Searching for similar objects …";
    }

    void ShowPendingSimilar()
    {
        var node = pendingSimilarOf;
        pendingSimilarOf = null;
        if (node == null) return;
        var groups = GroupsOf(node);
        if (groups.Count == 0)
        {
            Status.Text = $"No similar objects found for {ReportText.NodeLabel(node)} with these criteria";
            return;
        }
        var rowsOf = SimList.Items.Cast<SimilarRow>().Where(r => groups.Contains(r.G)).ToList();
        SimList.SelectedItems.Clear();
        foreach (var r in rowsOf) SimList.SelectedItems.Add(r);
        if (rowsOf.Count > 0) SimList.ScrollIntoView(rowsOf[0]);
        var mine = SimMembers.Items.Cast<SimilarMemberRow>().FirstOrDefault(r => ReferenceEquals(r.M.Node, node) ||
            HasAncestorIn(node, new HashSet<Node>(new[] { r.M.Node }, ReferenceEqualityComparer.Instance)));
        if (mine != null) { SimMembers.SelectedItem = mine; SimMembers.ScrollIntoView(mine); }
        Status.Text = $"{ReportText.NodeLabel(node)}: " + string.Join(", ", groups.Select(g => $"{g.Copies} copies of '{g.Name}' ({g.ClassLabel})"));
    }

    void OnSelectSimilar(object sender, RoutedEventArgs e)
    {
        var n = SelectedNodes().FirstOrDefault();
        if (n != null) SelectSimilarOf(n);
    }

    internal void OnHotGeoMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var r = HotGeo.SelectedItem as HotRow;
        HotGeoMenu.Items.Clear();
        HotGeoMenu.Items.Add(MenuEntry("Show in Tree", "", (_, _) => HotGoto(HotGeo), r != null, gesture: "Enter"));
        HotGeoMenu.Items.Add(MenuEntry("Select Similar Objects", "", (_, _) => { if (r?.Nodes.Count > 0) SelectSimilarOf(r.Nodes[0]); }, r != null && !busy,
            tip: "Show the copies of this object in the Similar view"));
    }

    /// <summary>"×16 similar" for a hotspot geometry that is part of a group of copies.</summary>
    (string text, string? tip) SimilarNote(Node geom)
    {
        if (!simByGeom.TryGetValue(geom, out var g)) return ("", null);
        return ($"×{PyFormat.Int(g.Copies)} similar", $"Part of '{g.Name}': {PyFormat.Int(g.Copies)} copies ({g.ClassLabel}, {VariantsText(g)}). Right click: Select Similar Objects");
    }
}
