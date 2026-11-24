using System.Windows;
using System.Windows.Media;
using SceneKit;

namespace SceneInspector;

/// <summary>R3-C11: a 90 x 60 sketch of a card layout: a side view (left) and a top view (right). Vertical planes are
/// lines through the centre, horizontal planes dashed lines at their heights, the box a rectangle.</summary>
public sealed class CardDiagram : FrameworkElement
{
    CardLayout layout = CardLayout.Default;

    public CardLayout Layout
    {
        get => layout;
        set { layout = value; InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size availableSize) => new(90, 60);

    protected override void OnRender(DrawingContext dc)
    {
        var line = Ui.Brush("Text");
        var muted = Ui.Brush("TextMuted");
        var accent = Ui.Brush("Accent");
        var solid = new Pen(accent, 1.4);
        var dashed = new Pen(accent, 1.2) { DashStyle = new DashStyle(new[] { 2.0, 2.0 }, 0) };
        var frame = new Pen(muted, 1);
        dc.DrawRectangle(null, new Pen(muted, 0.6), new Rect(0.5, 0.5, 89, 59));

        // side view: x 4..41, y 6..54 (ground at the bottom)
        var side = new Rect(4, 6, 37, 48);
        dc.DrawLine(frame, new Point(side.Left - 2, side.Bottom), new Point(side.Right + 2, side.Bottom));
        // top view: centred at (67, 30), half-size 19 x 19
        var top = new Rect(48, 11, 38, 38);
        var c = new Point(top.Left + top.Width / 2, top.Top + top.Height / 2);

        var l = layout;
        if (l.Mode == "box")
        {
            dc.DrawRectangle(null, solid, side);
            dc.DrawRectangle(null, solid, top);
            return;
        }
        dc.DrawRectangle(null, frame, side);
        dc.DrawRectangle(null, frame, top);
        int n = l.Mode == "billboard" ? 1 : Math.Max(1, l.Planes);
        for (int p = 0; p < n; p++)
        {
            double az = l.Mode == "billboard" ? 0 : Math.PI * p / n;   // 180 p / planes
            double cos = Math.Cos(az), sin = Math.Sin(az);
            // side view: a plane at azimuth az spans cos(az) of the width, seen from the front
            double x = side.Left + side.Width / 2 + cos * side.Width * 0.42;
            dc.DrawLine(solid, new Point(x, side.Top + 1), new Point(x, side.Bottom - 1));
            // top view: a line through the centre at that azimuth
            double hx = cos * top.Width * 0.46, hy = sin * top.Height * 0.46;
            dc.DrawLine(solid, new Point(c.X - hx, c.Y + hy), new Point(c.X + hx, c.Y - hy));
        }
        foreach (double h in l.Heights.Take(l.Horizontal))
        {
            double y = side.Bottom - Math.Clamp(h, 0, 1) * side.Height;
            dc.DrawLine(dashed, new Point(side.Left + 1, y), new Point(side.Right - 1, y));
        }
        if (l.Horizontal > 0)   // the top view of a horizontal plane is its outline
            dc.DrawRectangle(null, dashed, new Rect(top.Left + 3, top.Top + 3, top.Width - 6, top.Height - 6));
    }
}
