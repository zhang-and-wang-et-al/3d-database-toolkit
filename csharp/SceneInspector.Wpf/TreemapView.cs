using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SceneKit;

namespace SceneInspector;

/// <summary>Draws a scene treemap (InspectorModel.Treemap): one rectangle per node, area = cost, colour = worst finding
/// at or below it (grey-blue without findings, darker the deeper). Names are drawn where they fit.</summary>
public sealed class TreemapView : FrameworkElement
{
    public IReadOnlyList<InspectorModel.MapRect> Rects { get; private set; } = Array.Empty<InspectorModel.MapRect>();
    Func<Node, string?> severityOf = _ => null;
    Func<double, string> valueText = v => v.ToString("N0", CultureInfo.InvariantCulture);
    InspectorModel.MapRect? hover;

    /// <summary>Hovered rectangle changed (null = none).</summary>
    public event Action<InspectorModel.MapRect?>? HoverChanged;
    /// <summary>Single click on a rectangle (with the click count).</summary>
    public event Action<InspectorModel.MapRect, int>? Clicked;

    public void Show(IReadOnlyList<InspectorModel.MapRect> rects, Func<Node, string?> severity, Func<double, string> text)
    {
        Rects = rects;
        severityOf = severity;
        valueText = text;
        hover = null;
        InvalidateVisual();
    }

    static readonly Typeface Face = new("Segoe UI");
    /// <summary>Height of the name strip at the top of a box (children start below it).</summary>
    public const double LabelHeight = 14;

    protected override void OnRender(DrawingContext dc)
    {
        // colours from the theme (Themes/Colors.*.xaml)
        dc.DrawRectangle(Frozen(Ui.Color("Map.Background")), null, new Rect(0, 0, ActualWidth, ActualHeight));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var border = new Pen(Frozen(Ui.Color("Map.Border")), 1);
        border.Freeze();
        foreach (var r in Rects)
        {
            var fill = FillFor(r);
            dc.DrawRectangle(fill, border, new Rect(r.X, r.Y, r.W, r.H));
        }
        if (hover != null)
        {
            var hl = new Pen(Frozen(Ui.Color("Map.Hover")), 2);
            dc.DrawRectangle(null, hl, new Rect(hover.X + 1, hover.Y + 1, Math.Max(0, hover.W - 2), Math.Max(0, hover.H - 2)));
        }
        var labelBrush = Frozen(Ui.Color("Map.Label"));
        // labels: deepest first would hide parents; draw parents first, children paint over them
        foreach (var r in Rects.OrderBy(x => x.Depth))
        {
            if (r.W < 40 || r.H < 14) continue;
            string name = r.Node.Name.Length > 0 ? r.Node.Name : OsgReader.Short(r.Node.Cls);
            var ft = new FormattedText(name + "  " + valueText(r.Value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
                11, labelBrush, dpi) { MaxTextWidth = Math.Max(1, r.W - 6), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(ft, new Point(r.X + 3, r.Y + 1));
        }
    }

    Brush FillFor(InspectorModel.MapRect r)
    {
        double shade = Math.Max(0.55, 1 - 0.08 * (r.Depth - 1));
        var sev = severityOf(r.Node);
        Color c = Ui.Color(sev switch { "high" => "Map.High", "medium" => "Map.Med", "low" => "Map.Low", _ => "Map.None" });
        var b = new SolidColorBrush(Color.FromRgb((byte)(c.R * shade), (byte)(c.G * shade), (byte)(c.B * shade)));
        b.Freeze();
        return b;
    }

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var h = InspectorModel.HitTest(Rects, p.X, p.Y);
        if (!ReferenceEquals(h, hover))
        {
            hover = h;
            InvalidateVisual();
            HoverChanged?.Invoke(h);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        hover = null;
        InvalidateVisual();
        HoverChanged?.Invoke(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var h = InspectorModel.HitTest(Rects, p.X, p.Y);
        if (h != null) Clicked?.Invoke(h, e.ClickCount);
    }
}
