namespace SceneKit;

/// <summary>Overviews for huge scenes: a treemap of the scene by cost, and findings grouped by type.</summary>
public static partial class InspectorModel
{
    /// <summary>Costs the map can be sized by (label, mode; modes as in Orders).</summary>
    public static readonly (string label, string mode)[] MapMetrics =
    {
        ("triangles", "tris"), ("visible triangles", "vis"), ("draw calls", "draws"), ("geometry memory", "geo"),
        ("texture memory", "tex"), ("memory", "bytes"), ("vertices", "verts"),
    };

    /// <summary>The cost of a node's subtree for a sort / map mode.</summary>
    public static double Metric(Report rep, Node c, string mode)
    {
        var st = rep.Stat(c);
        return mode switch
        {
            "tris" => st.Tris, "bytes" => st.Bytes, "draws" => st.Draws, "vis" => st.VisTris, "verts" => st.Verts,
            "geo" => st.GeoBytes, "tex" => st.TexBytes, "inst" => rep.Instances.GetValueOrDefault(c, 1),
            _ => st.Tris / Math.Max(rep.Total.Tris, 1.0) + st.Bytes / Math.Max(rep.Total.Bytes, 1.0),
        };
    }

    /// <summary>One rectangle of the map: a node below the map's root (depth 1 = its children).</summary>
    public sealed record MapRect(Node Node, double X, double Y, double W, double H, int Depth, double Value);

    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk 2000) of the subtree below <paramref name="root"/> in a
    /// w x h box: every node gets an area in proportion to its cost; children are nested inside their parent (inset by
    /// <paramref name="pad"/>, and <paramref name="header"/> more at the top for the parent's name). Nodes without cost,
    /// rectangles thinner than <paramref name="minSide"/> and levels below <paramref name="maxDepth"/> are left out; at most
    /// <paramref name="maxRects"/> rectangles (biggest first).</summary>
    public static List<MapRect> Treemap(Report rep, Node root, string mode, double w, double h, int maxDepth = 8,
        double minSide = 3, int maxRects = 20000, double pad = 2, double header = 0)
    {
        var o = new List<MapRect>();
        // breadth first, so a rectangle limit drops the deepest (smallest) rectangles
        var queue = new Queue<(Node n, double x, double y, double w, double h, int depth)>();
        queue.Enqueue((root, 0, 0, w, h, 0));
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0 && o.Count < maxRects)
        {
            var (n, x, y, cw, ch, depth) = queue.Dequeue();
            if (depth >= maxDepth || !seen.Add(n)) continue;
            var kids = n.AllChildren().Select(c => (c, v: Metric(rep, c, mode))).Where(k => k.v > 0)
                .OrderByDescending(k => k.v).ToList();
            if (kids.Count == 0 || cw <= 0 || ch <= 0) continue;
            foreach (var (c, v, rx, ry, rw, rh) in Squarify(kids, x, y, cw, ch))
            {
                if (Math.Min(rw, rh) < minSide) continue;
                if (o.Count >= maxRects) break;
                o.Add(new MapRect(c, rx, ry, rw, rh, depth + 1, v));
                if (rw > 2 * pad + minSide && rh > 2 * pad + header + minSide)
                    queue.Enqueue((c, rx + pad, ry + pad + header, rw - 2 * pad, rh - 2 * pad - header, depth + 1));
            }
        }
        return o;
    }

    static IEnumerable<(Node c, double v, double x, double y, double w, double h)> Squarify(List<(Node c, double v)> items,
        double x, double y, double w, double h)
    {
        double total = items.Sum(i => i.v);
        double scale = w * h / total;
        var row = new List<(Node c, double v)>();
        double rowArea = 0;
        int i = 0;
        double Worst(double sum, double maxA, double minA, double side) =>
            Math.Max(side * side * maxA / (sum * sum), sum * sum / (side * side * minA));
        while (i < items.Count)
        {
            double side = Math.Min(w, h);
            double a = items[i].v * scale;
            if (row.Count == 0)
            {
                row.Add(items[i]); rowArea = a; i++;
                continue;
            }
            double maxA = row[0].v * scale, minA = row[^1].v * scale;   // sorted biggest first
            if (Worst(rowArea + a, maxA, a, side) <= Worst(rowArea, maxA, minA, side))
            {
                row.Add(items[i]); rowArea += a; i++;
                continue;
            }
            foreach (var r in PlaceRow(row, rowArea, scale, ref x, ref y, ref w, ref h)) yield return r;
            row.Clear();
        }
        if (row.Count > 0)
            foreach (var r in PlaceRow(row, rowArea, scale, ref x, ref y, ref w, ref h)) yield return r;
    }

    /// <summary>Lays a row along the shorter side of the free box and shrinks the box.</summary>
    static List<(Node c, double v, double x, double y, double w, double h)> PlaceRow(List<(Node c, double v)> row, double rowArea,
        double scale, ref double x, ref double y, ref double w, ref double h)
    {
        var o = new List<(Node, double, double, double, double, double)>();
        if (w >= h)
        {
            // a column on the left
            double cw = rowArea / h, cy = y;
            foreach (var (c, v) in row)
            {
                double rh = v * scale / cw;
                o.Add((c, v, x, cy, cw, rh));
                cy += rh;
            }
            x += cw; w -= cw;
        }
        else
        {
            // a row on top
            double rh = rowArea / w, cx = x;
            foreach (var (c, v) in row)
            {
                double rw = v * scale / rh;
                o.Add((c, v, cx, y, rw, rh));
                cx += rw;
            }
            y += rh; h -= rh;
        }
        return o;
    }

    /// <summary>The deepest rectangle containing the point, or null.</summary>
    public static MapRect? HitTest(IReadOnlyList<MapRect> rects, double px, double py)
    {
        MapRect? best = null;
        foreach (var r in rects)
            if (px >= r.X && py >= r.Y && px < r.X + r.W && py < r.Y + r.H && (best == null || r.Depth > best.Depth)) best = r;
        return best;
    }

    // ------------------------------------------------------------------ findings by type

    public sealed record FindingGroup(string Code, string Label, int Count, string Worst, long Tris, long Bytes, long Draws);

    public static string CodeLabel(string code) => code.Length == 0 ? code : char.ToUpperInvariant(code[0]) + code[1..].Replace('_', ' ');

    /// <summary>One entry per finding type: worst severity first, then the most frequent.</summary>
    public static List<FindingGroup> GroupFindings(IEnumerable<Finding> findings) =>
        findings.GroupBy(f => f.Code)
            .Select(g => new FindingGroup(g.Key, CodeLabel(g.Key), g.Count(), g.MinBy(f => Report.SeverityOrder[f.Severity])!.Severity,
                g.Sum(f => f.SavingTris), g.Sum(f => f.SavingBytes), g.Sum(f => f.SavingDraws)))
            .OrderBy(g => Report.SeverityOrder[g.Worst]).ThenByDescending(g => g.Count).ThenBy(g => g.Code, StringComparer.Ordinal)
            .ToList();

    /// <summary>Findings of one type (null = all) at or above a severity.</summary>
    public static IEnumerable<Finding> FilterFindings(IEnumerable<Finding> findings, string? code, string minSeverity)
    {
        int limit = Report.SeverityOrder.GetValueOrDefault(minSeverity, 3);
        return findings.Where(f => (code == null || f.Code == code) && Report.SeverityOrder[f.Severity] <= limit);
    }
}
