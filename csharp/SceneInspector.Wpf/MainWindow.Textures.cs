using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SceneKit;

namespace SceneInspector;

/// <summary>Something with a texture thumbnail: decoded lazily the first time the list shows it, a placeholder glyph
/// until then (and for DDS / missing / embedded images).</summary>
public abstract class ThumbItem : INotifyPropertyChanged
{
    public abstract Texture Tex { get; }
    protected abstract int ThumbSize { get; }
    ImageSource? thumb;
    bool asked;

    public ImageSource? Thumb
    {
        get
        {
            if (!asked)
            {
                asked = true;
                thumb = Thumbnails.Get(Tex, ThumbSize, b =>
                {
                    thumb = b;
                    Raise(nameof(Thumb));
                    Raise(nameof(PlaceholderVisibility));
                });
            }
            return thumb;
        }
    }

    public Visibility PlaceholderVisibility => Thumb == null ? Visibility.Visible : Visibility.Collapsed;
    public string PlaceholderGlyph => TextureTracking.IsMissing(Tex) ? "" : "";
    public Brush PlaceholderBrush => Ui.Brush(TextureTracking.IsMissing(Tex) ? "Sev.High" : "TextDisabled");
    public string PlaceholderTip => TextureTracking.IsMissing(Tex) ? "Image file not found" :
        Tex.Embedded ? "Embedded image (no preview)" : "No preview for this format";

    /// <summary>"Classes companion ({stem}_Classes.png): path  [512x512 sgi]" per companion image.</summary>
    public static string CompanionTipOf(Texture t) => string.Join("\n", InspectorModel.CompanionLines(t));

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Chip for <see cref="TextureTracking.StateText"/>: label, text brush key, tint brush key.</summary>
    public static (string label, string fg, string bg) StateChip(string state) => state switch
    {
        "ok" => ("ok", "Verdict.Ok", "Verdict.Ok.Tint"),
        "MISSING" => ("missing", "Sev.High", "Sev.High.Tint"),
        "embedded" => ("embedded", "Sev.Info", "Sev.Info.Tint"),
        "baked" => ("baked", "Sev.Low", "Sev.Low.Tint"),
        _ when state.StartsWith("resize", StringComparison.Ordinal) => (state.Replace("resize -> ", "resize → "), "Accent", "AccentTint"),
        _ => (state, "Sev.Info", "Sev.Info.Tint"),
    };
}

/// <summary>One line of the Textures tab.</summary>
public sealed class TextureItem : ThumbItem
{
    public InspectorModel.TextureRow R { get; }
    public TextureItem(InspectorModel.TextureRow r) { R = r; }
    public override Texture Tex => R.Tex;
    protected override int ThumbSize => 96;

    public string File => R.File;
    public string FileName => System.IO.Path.GetFileName(R.File);   // the column shows the name, the tooltip the path
    public string FileTip => R.Path.Length > 0 ? R.Path : R.File;
    public string Unit => R.Unit;
    public string FilesText => R.Files == 1 ? "used in 1 file" : $"used in {R.Files.ToString(CultureInfo.InvariantCulture)} files";
    public string Size => R.Size;
    public string Format => R.Format;
    public string FileSize => R.FileBytes > 0 ? Analyzer.HumanBytes(R.FileBytes) : "";
    public string Gpu => Analyzer.HumanBytes(R.GpuBytes);
    public string Users => R.Drawables != R.Users ? $"{R.Users} / {R.Drawables}" : R.Users.ToString(CultureInfo.InvariantCulture);
    public string UsersTip => $"{R.Users} node(s) hold it in their own state set; {R.Drawables} geometr{(R.Drawables == 1 ? "y draws" : "ies draw")} with it (inherited too)" +
                              (R.Texs.Count > 1 ? $"\n{R.Texs.Count} texture entries share this file; edits apply to all of them" : "");
    public string StateLabel => StateChip(R.State).label;
    public Brush StateBrush => Ui.Brush(StateChip(R.State).fg);
    public Brush StateTint => Ui.Brush(StateChip(R.State).bg);
    public Visibility NoUvVisibility => R.NoTexCoords > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string NoUvTip => $"{R.NoTexCoords} geometr{(R.NoTexCoords == 1 ? "y draws" : "ies draw")} it without texture coordinates for unit {R.Unit}";
    public string Problems => R.Problems;
    public string CompanionText => R.Companions;
    public Visibility CompanionVisibility => R.Companions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string CompanionTip => CompanionTipOf(Tex);
    public string Wrap => Tex.WrapText;
    public Brush WrapBrush => Ui.Brush(Tex.WrapS == null && Tex.WrapT == null ? "TextMuted" : "Text");
    public string SamplingTip => $"wrap S / T: {Tex.WrapS ?? "default (REPEAT)"} / {Tex.WrapT ?? "default (REPEAT)"}\n" +
                                 $"filter min / mag: {Tex.MinFilter ?? "default (LINEAR_MIPMAP_LINEAR)"} / {Tex.MagFilter ?? "default (LINEAR)"}\n" +
                                 $"max anisotropy: {(Tex.MaxAnisotropy is double a ? a.ToString("0.##", CultureInfo.InvariantCulture) : "default (1)")}";
}

/// <summary>A texture unit card on the Inspect tab (TX2).</summary>
public sealed class UnitCard : ThumbItem
{
    public InspectorModel.TexUnitInfo U { get; }
    readonly string? from;
    public UnitCard(InspectorModel.TexUnitInfo u, string? inheritedFrom) { U = u; from = inheritedFrom; }
    public override Texture Tex => U.Tex;
    protected override int ThumbSize => 96;

    public string UnitText => $"UNIT {U.Unit}";
    public string File => Tex.Label;
    public string FileTip => Tex.Path ?? Tex.Label;
    public string SizeText => (Tex.Width != 0 ? $"{Tex.Width}x{Tex.Height}" : "size unknown") + (Tex.Fmt.Length > 0 ? $"  {Tex.Fmt}" : "") +
                              $"  ·  {Analyzer.HumanBytes(Tex.GpuBytes)}";
    public string FromText => from != null ? $"inherited from {from}" : "own state set";
    public string StateLabel => StateChip(U.State).label;
    public Brush StateBrush => Ui.Brush(StateChip(U.State).fg);
    public Brush StateTint => Ui.Brush(StateChip(U.State).bg);
    public Visibility NoUvVisibility => U.HasTexCoords ? Visibility.Collapsed : Visibility.Visible;
    public string CompanionText => Companions.Labels(Tex);
    public Visibility CompanionVisibility => CompanionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string CompanionTip => CompanionTipOf(Tex);
}

public partial class MainWindow
{
    // ============================================================== textures tab (TX3, TX4)

    string texOrder = "gpu";
    bool texReverse;
    bool fillingTextures;

    internal void OnTexFilter(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) FillTextures();
    }

    internal void OnTexFilterKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { TexFilterBox.Text = ""; e.Handled = true; }
        else if (e.Key == Key.Down) { TexList.Focus(); e.Handled = true; }
    }

    static readonly HashSet<string> TexTextOrders = new() { "file", "format", "state", "problems", "unit", "wrap", "companions" };

    internal void OnTexHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: GridViewColumn col } || col.Header is not GridViewColumnHeader { Tag: string mode }) return;
        texReverse = mode == texOrder && !texReverse;
        texOrder = mode;
        FillTextures();
    }

    void FillTextures()
    {
        if (Rep == null) { TexList.ItemsSource = null; return; }
        var keep = TexList.SelectedItems.Cast<TextureItem>().SelectMany(t => t.R.Texs).ToHashSet(ReferenceEqualityComparer.Instance);
        var all = InspectorModel.TextureTable(Rep);
        var rows = InspectorModel.TextureTable(Rep, TexFilterBox.Text.Trim(), TexProblemsOnly.IsChecked == true);
        IEnumerable<InspectorModel.TextureRow> sorted = texOrder switch
        {
            "file" => rows.OrderBy(r => r.File, StringComparer.OrdinalIgnoreCase),
            "unit" => rows.OrderBy(r => r.Tex.Unit),
            "files" => rows.OrderByDescending(r => r.Files),
            "size" => rows.OrderByDescending(r => (long)r.Width * r.Height),
            "format" => rows.OrderBy(r => r.Format, StringComparer.OrdinalIgnoreCase),
            "filebytes" => rows.OrderByDescending(r => r.FileBytes),
            "users" => rows.OrderByDescending(r => r.Drawables),
            "state" => rows.OrderBy(r => r.State, StringComparer.Ordinal),
            "wrap" => rows.OrderBy(r => r.Tex.WrapS == null && r.Tex.WrapT == null).ThenBy(r => r.Tex.WrapText, StringComparer.Ordinal),
            "companions" => rows.OrderBy(r => r.Companions.Length == 0).ThenBy(r => r.Companions, StringComparer.Ordinal),
            "problems" => rows.OrderBy(r => r.Problems.Length == 0).ThenBy(r => r.Problems, StringComparer.OrdinalIgnoreCase),
            _ => rows,   // gpu: the table's own order
        };
        var items = sorted.Select(r => new TextureItem(r)).ToList();
        if (texReverse) items.Reverse();
        fillingTextures = true;
        TexList.ItemsSource = items;
        foreach (var it in items.Where(i => i.R.Texs.Any(keep.Contains))) TexList.SelectedItems.Add(it);
        fillingTextures = false;

        // header arrows like the scene tree
        bool asc = TexTextOrders.Contains(texOrder) != texReverse;
        foreach (var col in ((GridView)TexList.View).Columns)
        {
            if (col.Header is not GridViewColumnHeader h || h.Content is not string text) continue;
            var label = text.TrimEnd(' ', '▴', '▾');
            h.Content = (h.Tag as string) == texOrder ? label + (asc ? " ▴" : " ▾") : label;
        }

        int problems = all.Count(r => r.Problems.Length > 0);
        int withCompanions = all.Count(r => r.Companions.Length > 0);
        TexSummary.Text = (items.Count == all.Count ? $"{PyFormat.Int(all.Count)} texture{(all.Count == 1 ? "" : "s")}" : $"{PyFormat.Int(items.Count)} of {PyFormat.Int(all.Count)}") +
                          $"  ·  {Analyzer.HumanBytes(items.Sum(i => i.R.GpuBytes))} GPU" +
                          (withCompanions > 0 ? $"  ·  {PyFormat.Int(withCompanions)} with companions" : "");
        TexBadge.Visibility = problems > 0 ? Visibility.Visible : Visibility.Collapsed;
        TexCount.Text = problems.ToString(CultureInfo.InvariantCulture);
        TexBadge.ToolTip = $"{problems} texture(s) with problems";
        Ui.SetEmptyText(TexList, all.Count == 0 ? "The scene has no textures." :
            TexProblemsOnly.IsChecked == true && TexFilterBox.Text.Trim().Length == 0 ? "No texture has a problem." : "No texture matches the filter.");
        UpdateTexActions();
    }

    List<TextureItem> SelectedTextures() => TexList.SelectedItems.Cast<TextureItem>().ToList();

    internal void OnTexSelect(object sender, SelectionChangedEventArgs e)
    {
        if (!fillingTextures) UpdateTexActions();
    }

    /// <summary>State sets holding the texture (from the nodes that hold it in their own state set).</summary>
    static List<StateSet> HoldingStateSets(Texture t, TextureItem item) => item.R.UserNodes.Select(n => n.StateSet).OfType<StateSet>()
        .Where(ss => ss.Textures.Any(x => ReferenceEquals(x, t))).Distinct(ReferenceEqualityComparer.Instance).Cast<StateSet>().ToList();

    const string RemoveUnitNo = "Only texture units of state sets the optimizer made (cards, proxy, rebake) can be removed. " +
                                "State sets read from the file are written back as they are, so this is not supported for them yet.";

    /// <summary>Why Remove Unit is not possible for the selection (null: it is).</summary>
    string? RemoveUnitBlock(List<TextureItem> sel)
    {
        if (Scene == null || sel.Count == 0) return "Select a texture first";
        foreach (var t in sel)
        {
            var sets = t.R.Texs.SelectMany(x => HoldingStateSets(x, t)).ToList();
            if (sets.Count == 0) return "No state set holds this texture directly";
            if (sets.Any(ss => !TextureEdits.IsGenerated(Scene, ss))) return RemoveUnitNo;
        }
        return null;
    }

    void UpdateTexActions()
    {
        var sel = SelectedTextures();
        bool any = sel.Count > 0 && !busy && Scene != null;
        var first = sel.FirstOrDefault();
        TexResizeHalf.IsEnabled = TexResizeQuarter.IsEnabled = any && sel.All(t => t.R.Texs.All(x => x.Width > 1 && !x.Embedded));
        TexResizeCustom.IsEnabled = any && sel.Count == 1 && first!.Tex.Width > 0 && !first.Tex.Embedded;
        TexResetSize.IsEnabled = any && sel.Any(t => t.R.Texs.Any(x => x.Resize != null));
        TexReplace.IsEnabled = any && sel.Count == 1 && !first!.Tex.Embedded;
        TexSampling.IsEnabled = any;
        TexSampling.ToolTip = Scene?.Flt != null
            ? "Wrap modes, filters and anisotropy. OpenFlight keeps them in the image's .attr file, which is not written: they are kept when saving as .osgt"
            : "Wrap modes, filters and anisotropy of the texture" + (sel.Count > 1 ? "s (all selected)" : "");
        var block = RemoveUnitBlock(sel);
        TexRemoveUnit.IsEnabled = any && block == null;
        TexRemoveUnit.ToolTip = block ?? (sel.Count == 1 ? $"Remove texture unit {first!.R.Unit} ({first.File}) from its state set" : "Remove these texture units from their state sets");
        TexShowUsers.IsEnabled = sel.Any(t => t.R.UserNodes.Count > 0);

        // detail card of the first selected texture
        if (first == null)
        {
            TexDetailTitle.Text = "";
            TexDetailPath.Text = "";
            TexDetailTitle.Visibility = TexDetailPath.Visibility = Visibility.Collapsed;
            TexDetailInfo.Text = "Select a texture to see where it is used and to resize or replace it.";
            TexDetailProblems.Visibility = Visibility.Collapsed;
            TexPreview.DataContext = null;
            TexPreview.Visibility = Visibility.Collapsed;
            return;
        }
        var r = first.R;
        TexPreview.Visibility = TexDetailTitle.Visibility = TexDetailPath.Visibility = Visibility.Visible;
        TexPreview.DataContext = new UnitCardPreview(first.Tex);
        TexDetailTitle.Text = sel.Count > 1 ? $"{r.File}  (+{sel.Count - 1} more selected)" : r.File;
        TexDetailPath.Text = r.Path.Length > 0 ? r.Path : first.Tex.Embedded ? "embedded in the scene file" : first.Tex.Baked != null ? "made by the optimizer (written when saving)" : "";
        string orig = first.Tex.Resize != null && first.Tex.Path != null && Images.Probe(first.Tex.Path) is { } info ? $" (image {info.Width}x{info.Height})" : "";
        TexDetailInfo.Text = $"{r.Size}{orig}  {r.Format}   ·   unit {r.Unit}   ·   GPU {Analyzer.HumanBytes(r.GpuBytes)}" +
                             (r.FileBytes > 0 ? $"   ·   file {Analyzer.HumanBytes(r.FileBytes)}" : "") +
                             $"\nwrap {first.Tex.WrapText}   ·   filter {first.Tex.FilterText}" +
                             $"\nused by {r.Users} node(s), drawn by {r.Drawables} geometr{(r.Drawables == 1 ? "y" : "ies")}" +
                             string.Concat(InspectorModel.CompanionLines(first.Tex).Select(l => "\n" + l));
        TexDetailProblems.Text = r.Problems;
        TexDetailProblems.Visibility = r.Problems.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The big preview of the textures tab (same lazy thumbnail, larger).</summary>
    sealed class UnitCardPreview : ThumbItem
    {
        readonly Texture tex;
        public UnitCardPreview(Texture t) { tex = t; }
        public override Texture Tex => tex;
        protected override int ThumbSize => 256;
    }

    internal void OnTexMenuOpening(object sender, ContextMenuEventArgs e)
    {
        UpdateTexActions();
        var menu = TexMenu;
        menu.Items.Clear();
        menu.Items.Add(MenuEntry("Resize to ½", "", OnTexResize, TexResizeHalf.IsEnabled, tag: "2"));
        menu.Items.Add(MenuEntry("Resize to ¼", "", OnTexResize, TexResizeQuarter.IsEnabled, tag: "4"));
        menu.Items.Add(MenuEntry("Custom Size…", "", OnTexResizeCustom, TexResizeCustom.IsEnabled));
        menu.Items.Add(MenuEntry("Reset Size", "", OnTexResetSize, TexResetSize.IsEnabled));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Replace File…", "", OnTexReplace, TexReplace.IsEnabled));
        menu.Items.Add(MenuEntry("Sampling…", "", OnTexSampling, TexSampling.IsEnabled));
        var remove = MenuEntry("Remove Unit", "", OnTexRemoveUnit, TexRemoveUnit.IsEnabled, tip: TexRemoveUnit.ToolTip as string);
        ToolTipService.SetShowOnDisabled(remove, true);
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Select Users in Tree", "", OnTexShowUsers, TexShowUsers.IsEnabled, gesture: "Enter"));
    }

    internal void OnTexKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnTexShowUsers(sender, e);
        e.Handled = true;
    }

    internal void OnTexDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(TexList, d) is ListViewItem) OnTexShowUsers(sender, e);
    }

    internal void OnTexShowUsers(object sender, RoutedEventArgs e)
    {
        var nodes = SelectedTextures().SelectMany(t => t.R.UserNodes).Distinct(ReferenceEqualityComparer.Instance).Cast<Node>().ToList();
        if (nodes.Count == 0) return;
        LeftTree.IsChecked = true;
        Reveal(nodes);
        Status.Text = $"{nodes.Count} node(s) use the selected texture(s)";
    }

    /// <summary>Run texture edits as one undo step: snapshot first, kept only when one of the edits did something.</summary>
    void TextureEdit(string label, Func<Texture, TextureEdits.Result> edit, List<Texture> textures)
    {
        if (Scene == null || busy || textures.Count == 0) return;
        var snap = new Snapshot(Scene, label);
        var results = textures.Select(edit).ToList();
        var ok = results.Where(r => r.Ok).ToList();
        var failed = results.Where(r => !r.Ok).ToList();
        if (ok.Count == 0)
        {
            Status.Text = "Nothing changed: " + failed[0].Message;
            Dialogs.Warn(DialogOwner, "Texture not changed", string.Join("\n", failed.Select(r => r.Message).Distinct()));
            return;
        }
        snap.Scope = string.Join(", ", textures.Take(3).Select(t => t.Label)) + (textures.Count > 3 ? ", …" : "");
        PushHistory(snap);
        foreach (var r in results) Log((r.Ok ? "TEXTURE: " : "TEXTURE (not changed): ") + r.Message);
        SetModified(true);
        string msg = ok.Count == 1 ? ok[0].Message : $"{ok.Count} textures changed";
        if (failed.Count > 0) msg += $"; {failed.Count} not changed: {failed[0].Message}";
        Reanalyse(msg + "  (undo with Ctrl+Z)");
    }

    internal void OnTexResize(object sender, RoutedEventArgs e)
    {
        int div = (sender as FrameworkElement)?.Tag is string s && int.TryParse(s, out var d) ? d : 2;
        var sel = SelectedTextures().SelectMany(t => t.R.Texs).ToList();
        string what = sel.Count == 1 ? sel[0].Label : SelectedTextures().Count == 1 ? $"{sel[0].Label} ({sel.Count} entries)" : $"{sel.Count} textures";
        TextureEdit($"resize {what} to {(div == 2 ? "½" : "¼")}",
            t => TextureEdits.SetResize(Scene!, t, Math.Max(1, t.Width / div), Math.Max(1, t.Height / div), null, TexResizeClasses.IsChecked == true), sel);
    }

    internal void OnTexResizeCustom(object sender, RoutedEventArgs e)
    {
        var item = SelectedTextures().FirstOrDefault();
        var t = item?.Tex;
        if (t == null || Scene == null) return;
        var size = Dialogs.AskSize(DialogOwner, "Resize texture", $"New size of {t.Label} (it is {t.Width}x{t.Height}). The image is scaled when saving; " +
                                                          "the original file stays untouched.", t.Width, t.Height);
        if (size is not var (w, h)) return;
        TextureEdit($"resize {t.Label} to {w}x{h}", x => TextureEdits.SetResize(Scene!, x, w, h, null, TexResizeClasses.IsChecked == true), item!.R.Texs.ToList());
    }

    internal void OnTexResetSize(object sender, RoutedEventArgs e)
    {
        var sel = SelectedTextures().SelectMany(t => t.R.Texs).Where(t => t.Resize != null).ToList();
        TextureEdit(sel.Count == 1 ? $"reset size of {sel[0].Label}" : $"reset size of {sel.Count} textures", t =>
        {
            var info = t.Path != null ? Images.Probe(t.Path) : null;
            return info == null ? new TextureEdits.Result(false, $"{t.Label}: the image size is unknown") : TextureEdits.SetResize(Scene!, t, info.Width, info.Height);
        }, sel);
    }

    internal void OnTexReplace(object sender, RoutedEventArgs e)
    {
        var item = SelectedTextures().FirstOrDefault();
        var t = item?.Tex;
        if (t == null || Scene == null) return;
        var dlg = new OpenFileDialog
        {
            Title = $"Replace {t.Label}", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.tga;*.rgb;*.rgba;*.sgi;*.int;*.inta;*.bw;*.dds|All files|*.*",
            InitialDirectory = t.Path != null ? Path.GetDirectoryName(t.Path) : Path.GetDirectoryName(Scene.ConvertedFrom ?? Scene.Path),
        };
        if (dlg.ShowDialog(DialogOwner) != true) return;
        string? warn = null;
        TextureEdit($"replace {t.Label} with {Path.GetFileName(dlg.FileName)}", x =>
        {
            var r = TextureEdits.ReplaceFile(Scene!, x, dlg.FileName);
            int at = r.Message.IndexOf("; warning: ", StringComparison.Ordinal);
            if (r.Ok && at >= 0) warn = r.Message[(at + 11)..];
            return r;
        }, item!.R.Texs.ToList());
        if (warn != null) Dialogs.Warn(DialogOwner, "Companion image missing", char.ToUpperInvariant(warn[0]) + warn[1..] +
            ". Parts baked from it get the extra_fill colour in the companion atlas, and the image generator finds no companion for it.");
    }

    // ============================================================== companion images (Classes / IR): naming rules, per session

    /// <summary>The naming rules of companion images for this session (the app keeps no settings file): applied to every
    /// scene opened, changed in the Optimize tab.</summary>
    List<string> companionPatterns = Companions.DefaultPatterns.ToList();

    void InitCompanions()
    {
        CompanionBox.Text = Companions.Format(companionPatterns);
        CompanionHintText(null);
    }

    void CompanionHintText(int? found, List<string>? problems = null)
    {
        if (problems is { Count: > 0 }) { CompanionHint.Text = string.Join("; ", problems); CompanionHint.Foreground = Ui.Brush("Sev.High"); return; }
        CompanionHint.Foreground = Ui.Brush("TextMuted");
        CompanionHint.Text = "Classes / companion images next to the textures (class maps, infrared): tokens {stem} {ext} {dir}, ';' between rules" +
                             (found is int n ? $"  ·  {PyFormat.Int(n)} texture(s) with companions" : "");
    }

    internal void OnCompanionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnCompanionCommit(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { CompanionBox.Text = Companions.Format(companionPatterns); e.Handled = true; }
    }

    internal void OnCompanionCommit(object sender, RoutedEventArgs e)
    {
        var wanted = Companions.Parse(CompanionBox.Text);
        var problems = wanted.Select(Companions.Check).OfType<string>().ToList();
        var valid = wanted.Where(p => Companions.Check(p) == null).ToList();
        if (valid.SequenceEqual(companionPatterns) && problems.Count == 0) return;
        companionPatterns = valid;
        if (Scene == null || busy) { CompanionHintText(null, problems); return; }
        var (found, _) = Companions.SetPatterns(Scene, valid);
        CompanionHintText(found, problems);
        Log($"COMPANIONS: rules {Companions.Format(valid)} -> {found} texture(s) with companions");
        Reanalyse($"Companion rules changed: {found} texture(s) with companions");
    }

    internal void OnTexSampling(object sender, RoutedEventArgs e)
    {
        var sel = SelectedTextures().SelectMany(t => t.R.Texs).ToList();
        if (sel.Count == 0 || Scene == null) return;
        var t = sel[0];
        string what = sel.Count == 1 ? t.Label : $"{sel.Count} textures";
        string msg = $"How {what} {(sel.Count == 1 ? "is" : "are")} sampled. \"Default\" leaves the setting out of the file (OpenSceneGraph's default)." +
                     (Scene.Flt != null ? "\n\nOpenFlight keeps these in the image's .attr file, which is not written; save as .osgt to keep them." : "");
        var r = Dialogs.AskSampling(DialogOwner, "Texture sampling", msg, t.WrapS, t.WrapT, t.MinFilter, t.MagFilter, t.MaxAnisotropy);
        if (r is not var (ws, wt, mi, ma, an)) return;
        TextureEdit($"sampling of {what}", x => TextureEdits.SetSampling(Scene!, x, ws, wt, mi, ma, an), sel);
    }

    internal void OnTexRemoveUnit(object sender, RoutedEventArgs e)
    {
        if (Scene == null) return;
        if (busy)
        {
            if (checkpointing) Status.Text = "Writing checkpoint … please wait";
            return;
        }
        var sel = SelectedTextures();
        if (RemoveUnitBlock(sel) is string why) { Status.Text = why; return; }
        var jobs = sel.SelectMany(t => t.R.Texs.SelectMany(x => HoldingStateSets(x, t).Select(ss => (t: x, ss)))).ToList();
        if (!Dialogs.Confirm(DialogOwner, "Remove texture unit?", sel.Count == 1
                ? $"Remove {sel[0].File} (unit {sel[0].R.Unit}) from {jobs.Count} state set(s)? The geometry keeps its texture coordinates. Undo brings it back (Ctrl+Z)."
                : $"Remove {sel.Count} textures from {jobs.Count} state set(s)? Undo brings them back (Ctrl+Z).", "Remove"))
            return;
        var snap = new Snapshot(Scene, sel.Count == 1 ? $"remove texture unit {sel[0].R.Unit} ({sel[0].File})" : $"remove {sel.Count} texture units");
        var results = jobs.Select(j => TextureEdits.RemoveUnit(Scene, j.ss, j.t.Unit)).ToList();
        if (!results.Any(r => r.Ok))
        {
            Dialogs.Warn(DialogOwner, "Texture unit not removed", string.Join("\n", results.Select(r => r.Message).Distinct()));
            return;
        }
        PushHistory(snap);
        foreach (var r in results) Log("TEXTURE: " + r.Message);
        SetModified(true);
        Reanalyse(results.First(r => r.Ok).Message + "  (undo with Ctrl+Z)");
    }

    // ============================================================== inspect tab: texture unit cards (TX2)

    void FillUnitCards(Node node)
    {
        if (Rep == null) { UnitCards.ItemsSource = null; UnitStrip.Visibility = Visibility.Collapsed; return; }
        var units = InspectorModel.TextureUnits(Rep, node);
        UnitCards.ItemsSource = units.Select(u => new UnitCard(u, u.InheritedFrom != null ? ReportText.NodeLabel(u.InheritedFrom) : null)).ToList();
        UnitStrip.Visibility = units.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Click on a unit card: show that texture in the Textures tab.</summary>
    internal void OnUnitCardClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnitCard card) return;
        ShowPanel(PanelId.Textures);
        var item = TexList.Items.Cast<TextureItem>().FirstOrDefault(t => t.R.Texs.Any(x => ReferenceEquals(x, card.Tex)));
        if (item == null && (TexFilterBox.Text.Length > 0 || TexProblemsOnly.IsChecked == true))
        {
            TexFilterBox.Text = "";
            TexProblemsOnly.IsChecked = false;
            FillTextures();
            item = TexList.Items.Cast<TextureItem>().FirstOrDefault(t => t.R.Texs.Any(x => ReferenceEquals(x, card.Tex)));
        }
        if (item == null) return;
        TexList.SelectedItem = item;
        TexList.ScrollIntoView(item);
        TexList.Focus();
    }
}
