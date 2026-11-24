using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SceneInspector;

/// <summary>Attached properties the theme templates read (placeholder text, invalid input, empty-list hint, icon glyph)
/// and helpers to look up theme brushes by severity / verdict.</summary>
public static class Ui
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string v) => o.SetValue(PlaceholderProperty, v);

    /// <summary>Marks an input as invalid: the theme draws a red border.</summary>
    public static readonly DependencyProperty InvalidProperty = DependencyProperty.RegisterAttached(
        "Invalid", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static bool GetInvalid(DependencyObject o) => (bool)o.GetValue(InvalidProperty);
    public static void SetInvalid(DependencyObject o, bool v) => o.SetValue(InvalidProperty, v);

    /// <summary>Text shown in a list while it has no items.</summary>
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.RegisterAttached(
        "EmptyText", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetEmptyText(DependencyObject o) => (string)o.GetValue(EmptyTextProperty);
    public static void SetEmptyText(DependencyObject o, string v) => o.SetValue(EmptyTextProperty, v);

    /// <summary>Segoe Fluent Icons / MDL2 glyph drawn before a button's content.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetIcon(DependencyObject o) => (string)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string v) => o.SetValue(IconProperty, v);

    /// <summary>On a ListView with a GridView: the last column takes the width the other columns leave, but at least this
    /// many pixels (0 = off). Keeps the list free of horizontal scrolling until the window gets really narrow.</summary>
    public static readonly DependencyProperty FillLastColumnProperty = DependencyProperty.RegisterAttached(
        "FillLastColumn", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(0.0, OnFillLastColumn));
    public static double GetFillLastColumn(DependencyObject o) => (double)o.GetValue(FillLastColumnProperty);
    public static void SetFillLastColumn(DependencyObject o, double v) => o.SetValue(FillLastColumnProperty, v);

    /// <summary>Like <see cref="FillLastColumnProperty"/>, but the column whose header Tag is this name takes the width the
    /// others leave, never below <see cref="FillMinWidthProperty"/>.</summary>
    public static readonly DependencyProperty FillColumnProperty = DependencyProperty.RegisterAttached(
        "FillColumn", typeof(string), typeof(Ui), new FrameworkPropertyMetadata("", OnFillColumn));
    public static string GetFillColumn(DependencyObject o) => (string)o.GetValue(FillColumnProperty);
    public static void SetFillColumn(DependencyObject o, string v) => o.SetValue(FillColumnProperty, v);

    /// <summary>Minimum width of the <see cref="FillColumnProperty"/> column.</summary>
    public static readonly DependencyProperty FillMinWidthProperty = DependencyProperty.RegisterAttached(
        "FillMinWidth", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(0.0));
    public static double GetFillMinWidth(DependencyObject o) => (double)o.GetValue(FillMinWidthProperty);
    public static void SetFillMinWidth(DependencyObject o, double v) => o.SetValue(FillMinWidthProperty, v);

    static void OnFillLastColumn(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is ListView lv && (double)e.OldValue <= 0) HookFill(lv, gv => gv.Columns[^1], GetFillLastColumn);
    }

    static void OnFillColumn(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ListView lv || !string.IsNullOrEmpty((string)e.OldValue)) return;
        HookFill(lv, gv => gv.Columns.FirstOrDefault(c => c.Header is GridViewColumnHeader { Tag: string t } && t == GetFillColumn(lv)),
            GetFillMinWidth);
    }

    /// <summary>Sizes the target column to the ListView width minus the other columns (at least min) whenever the list or
    /// another column changes size.</summary>
    static void HookFill(ListView lv, Func<GridView, GridViewColumn?> target, Func<DependencyObject, double> min)
    {
        var widthProp = DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
        var hooked = new HashSet<GridViewColumn>();
        void Fit(object? s, EventArgs a)
        {
            if (lv.View is not GridView gv || gv.Columns.Count == 0 || lv.ActualWidth <= 0) return;
            if (target(gv) is not GridViewColumn fill) return;
            double others = 0;
            foreach (var c in gv.Columns)
            {
                if (c == fill) continue;
                if (hooked.Add(c)) widthProp.AddValueChanged(c, Fit);   // the user drags another column wider / narrower
                others += double.IsNaN(c.Width) ? c.ActualWidth : c.Width;
            }
            // room for the vertical scroll bar (10, see the theme) and the item container padding
            double room = lv.ActualWidth - lv.Padding.Left - lv.Padding.Right - lv.BorderThickness.Left - lv.BorderThickness.Right - others - 20;
            fill.Width = Math.Max(min(lv), Math.Floor(room));
        }
        lv.SizeChanged += Fit;
        lv.Loaded += Fit;
    }

    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    public static Color Color(string key) => Application.Current.TryFindResource(key) is Color c ? c : Colors.Gray;

    static string SevKey(string? sev) => sev switch { "high" => "Sev.High", "medium" => "Sev.Med", "low" => "Sev.Low", _ => "Sev.Info" };
    public static Brush SevBrush(string? sev) => Brush(SevKey(sev));
    public static Brush SevTint(string? sev) => Brush(SevKey(sev) + ".Tint");
    public static string SevLabel(string? sev) => sev switch { "high" => "High", "medium" => "Medium", "low" => "Low", "info" => "Info", _ => "" };

    static string VerdictKey(string? v) => v switch { "ok" => "Verdict.Ok", "check" => "Verdict.Check", "damaged" => "Verdict.Bad", _ => "Sev.Info" };
    public static Brush VerdictBrush(string? v) => v == null ? Brush("Text") : Brush(VerdictKey(v));
    public static Brush VerdictTint(string? v) => v == null ? Brushes.Transparent : v == "skipped" ? Brush("Sev.Info.Tint") : Brush(VerdictKey(v) + ".Tint");
    public static string VerdictLabel(string? v) => v switch { "ok" => "OK", "check" => "Check", "damaged" => "Damaged", "skipped" => "Skipped", null => "", _ => v };
}
