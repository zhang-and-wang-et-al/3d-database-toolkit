using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SceneKit;

namespace SceneInspector;

/// <summary>Edits one <see cref="SimplifyParams"/> record (R2-D3 / R2-D4): Within (error target) or Keep %, the strategy
/// with its help line, the simplifier (Standard / meshoptimizer), and the advanced settings (tile edges, separate parts,
/// hard edge, new texture size, overlay protection, dropping tiny parts). Two instances
/// show the same record: the Try strip and the Optimize tab's simplify pass. Unreadable text turns red and leaves
/// <see cref="Value"/> at its last valid state.</summary>
public partial class SimplifyEditor : UserControl
{
    SimplifyParams value = new();
    bool updating;
    double? diag;
    string unitNote = "metres";

    /// <summary>A control changed the record (not raised when <see cref="Value"/> is set).</summary>
    public event Action<SimplifyEditor, SimplifyParams>? Changed;

    /// <summary>A slider was released with "Update on release" ticked, or Enter was pressed in the Within box.</summary>
    public event Action? TryRequested;

    public SimplifyEditor()
    {
        InitializeComponent();
        foreach (var s in Strategies.All)
            StrategyBox.Items.Add(new ComboBoxItem { Content = $"{s.Label}  ({s.Tech})", Tag = s.Id, ToolTip = s.Help });
        foreach (var e in Enum.GetValues<SimplifyEngine>())
        {
            bool ok = SimplifyEngines.Available(e, out string reason);
            var item = new ComboBoxItem { Content = SimplifyEngines.Label(e), Tag = e, IsEnabled = ok, ToolTip = ok ? SimplifyEngines.Tech(e) : reason };
            ToolTipService.SetShowOnDisabled(item, true);   // the reason must show on the greyed-out entry
            EngineBox.Items.Add(item);
        }
        Show(value);
    }

    /// <summary>The record shown; setting it updates the controls without raising <see cref="Changed"/>.</summary>
    public SimplifyParams Value
    {
        get => value;
        set { this.value = value; Show(value); }
    }

    /// <summary>The advanced settings are open.</summary>
    public bool ShowAdvanced
    {
        get => Advanced.IsExpanded;
        set => Advanced.IsExpanded = value;
    }

    /// <summary>Shows "Update on release" (the Try strip); without it a released slider does not ask for a try.</summary>
    public bool ShowAutoTry
    {
        get => AutoTry.Visibility == Visibility.Visible;
        set => AutoTry.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The bounding diagonal (metres) "% of size" refers to; shows "up to 1.2 cm" next to a relative target (each part keeps to its own size, never more).</summary>
    public double? Diag
    {
        set { diag = value; ShowMetres(); }
    }

    /// <summary>"→ 899 of 1,800" next to the target.</summary>
    public string ResultText
    {
        set => ResultBlock.Text = value;
    }

    /// <summary>The unit distances are in (tooltip of the Within box).</summary>
    public string UnitNote
    {
        set { unitNote = value; WithinTip(true); }
    }

    // ------------------------------------------------------------------ record → controls

    void Show(SimplifyParams p)
    {
        updating = true;
        try
        {
            (p.Within ? TargetWithin : TargetKeep).IsChecked = true;
            WithinRow.Visibility = p.Within ? Visibility.Visible : Visibility.Collapsed;
            KeepRow.Visibility = p.Within ? Visibility.Collapsed : Visibility.Visible;
            WithinBox.Text = p.Error.Text();
            Ui.SetInvalid(WithinBox, false);
            WithinTip(true);
            if (p.Error.Relative) WithinSlider.Value = Math.Clamp(p.Error.Value, WithinSlider.Minimum, WithinSlider.Maximum);
            RatioSlider.Value = Math.Clamp(Math.Round(p.Ratio * 100), RatioSlider.Minimum, RatioSlider.Maximum);
            RatioLabel.Text = $"{RatioSlider.Value:F0} %";
            StrategyBox.SelectedItem = StrategyBox.Items.Cast<ComboBoxItem>().First(i => (SimplifyStrategy)i.Tag == p.Strategy);
            ShowStrategy(p.Strategy);
            Select(BordersBox, p.Borders);
            Select(PartsBox, p.Parts);
            CreaseBox.Text = PyFormat.G(p.Crease);
            RebakeBox.Text = p.RebakeSize.ToString(CultureInfo.InvariantCulture);
            Ui.SetInvalid(CreaseBox, false);
            Ui.SetInvalid(RebakeBox, false);
            CreaseBox.ToolTip = RebakeBox.ToolTip = null;
            ProtectOverlays.IsChecked = p.ProtectOverlays;
            EngineBox.SelectedItem = EngineBox.Items.Cast<ComboBoxItem>().First(i => (SimplifyEngine)i.Tag == p.Engine);
            ShowEngine(p.Engine);
            PruneBox.IsChecked = p.Prune;
        }
        finally { updating = false; }
        ShowMetres();
    }

    static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    void ShowStrategy(SimplifyStrategy s)
    {
        var info = Strategies.Of(s);
        StrategyBox.ToolTip = info.Tech;
        StrategyHelpText.Text = info.Help + " ";
    }

    void ShowEngine(SimplifyEngine e)
    {
        EngineBox.ToolTip = SimplifyEngines.Available(e, out string reason) ? SimplifyEngines.Tech(e) : reason;
        ShowPrune();
    }

    /// <summary>Prune only acts with meshoptimizer's edge collapse: not under Fast, rough (sloppy gets no options) or
    /// New texture (always the standard simplifier).</summary>
    void ShowPrune()
    {
        if (PruneBox == null || EngineBox == null || StrategyBox == null) return;
        PruneBox.IsEnabled = EngineBox.SelectedItem is ComboBoxItem { Tag: SimplifyEngine.Meshopt }
            && StrategyBox.SelectedItem is ComboBoxItem { Tag: SimplifyStrategy s }
            && s is not (SimplifyStrategy.FastRough or SimplifyStrategy.NewTexture);
    }

    void ShowMetres() =>
        WithinMetres.Text = value.Error.Relative && diag is double d && d > 0 ? $"up to {ErrorTarget.Distance(value.Error.Metres(d))}" : "";

    void WithinTip(bool ok) => WithinBox.ToolTip = ok
        ? $"Largest allowed distance from the original surface: a distance (2 cm, 5 mm) or a share of each part's size (0.5 % of size; never more than that share of the selected node's size). Distances are in {unitNote}. Enter runs the preview."
        : "Enter a distance like 2 cm or 5 mm, or a share like 0.5 % of size. The last valid value is used meanwhile.";

    // ------------------------------------------------------------------ controls → record

    void Set(SimplifyParams p)
    {
        value = p;
        ShowMetres();
        Changed?.Invoke(this, p);
    }

    void OnTargetChanged(object sender, RoutedEventArgs e)
    {
        if (WithinRow == null || KeepRow == null) return;   // while the XAML is read
        bool within = TargetWithin.IsChecked == true;
        WithinRow.Visibility = within ? Visibility.Visible : Visibility.Collapsed;
        KeepRow.Visibility = within ? Visibility.Collapsed : Visibility.Visible;
        if (!updating && within != value.Within) Set(value with { Within = within });
    }

    void OnWithinText(object sender, TextChangedEventArgs e)
    {
        if (updating || !IsInitialized) return;
        bool ok = ErrorTarget.TryParse(WithinBox.Text, out var t);
        Ui.SetInvalid(WithinBox, !ok);
        WithinTip(ok);
        if (!ok) return;
        if (t.Relative)
        {
            updating = true;
            WithinSlider.Value = Math.Clamp(t.Value, WithinSlider.Minimum, WithinSlider.Maximum);
            updating = false;
        }
        if (t != value.Error) Set(value with { Error = t });
    }

    void OnWithinKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ErrorTarget.TryParse(WithinBox.Text, out _)) TryRequested?.Invoke();
    }

    void OnWithinSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating || !IsInitialized) return;
        var t = new ErrorTarget(Math.Round(e.NewValue, 1), true);
        updating = true;
        WithinBox.Text = t.Text();
        Ui.SetInvalid(WithinBox, false);
        WithinTip(true);
        updating = false;
        if (t != value.Error) Set(value with { Error = t });
    }

    void OnRatioChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RatioLabel == null) return;
        RatioLabel.Text = $"{e.NewValue:F0} %";
        double r = Math.Round(e.NewValue) / 100.0;
        if (!updating && r != value.Ratio) Set(value with { Ratio = r });
    }

    void OnSliderReleased(object sender, MouseButtonEventArgs e)
    {
        if (ShowAutoTry && AutoTry.IsChecked == true) TryRequested?.Invoke();
    }

    void OnStrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StrategyBox.SelectedItem is not ComboBoxItem { Tag: SimplifyStrategy s }) return;
        ShowStrategy(s);
        ShowPrune();
        if (!updating && s != value.Strategy) Set(value with { Strategy = s });
    }

    void OnAdvancedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || !IsInitialized) return;
        string borders = (BordersBox.SelectedItem as ComboBoxItem)?.Tag as string ?? value.Borders;
        string parts = (PartsBox.SelectedItem as ComboBoxItem)?.Tag as string ?? value.Parts;
        if (borders != value.Borders || parts != value.Parts) Set(value with { Borders = borders, Parts = parts });
    }

    void OnProtectChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !IsInitialized) return;
        bool on = ProtectOverlays.IsChecked == true;
        if (on != value.ProtectOverlays) Set(value with { ProtectOverlays = on });
    }

    void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EngineBox.SelectedItem is not ComboBoxItem { Tag: SimplifyEngine en }) return;
        ShowEngine(en);
        if (!updating && en != value.Engine) Set(value with { Engine = en });
    }

    void OnPruneChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !IsInitialized) return;
        bool on = PruneBox.IsChecked == true;
        if (on != value.Prune) Set(value with { Prune = on });
    }

    static bool ParseNumber(string text, out double v) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    void OnNumberText(object sender, TextChangedEventArgs e)
    {
        if (updating || !IsInitialized) return;
        var box = (TextBox)sender;
        bool crease = ReferenceEquals(box, CreaseBox);
        bool ok = ParseNumber(box.Text, out double v) && (crease ? v >= 0 && v <= 180 : v >= 16 && v <= 8192);
        Ui.SetInvalid(box, !ok);
        box.ToolTip = ok ? null : crease ? "Enter an angle from 0 to 180 degrees, e.g. 40" : "Enter a size from 16 to 8192 pixels, e.g. 512";
        if (!ok) return;
        var p = crease ? value with { Crease = v } : value with { RebakeSize = (int)Math.Round(v) };
        if (p != value) Set(p);
    }

    void OnHelp(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.OpenHelp("strategies");
}
