using System.Globalization;

namespace SceneKit;

/// <summary>R3-C3: which cards replace_cards builds. A layout is a set of rows (mode, planes, horizontal planes, ...);
/// the eight named layouts are shortcuts for rows. BuildCards builds the cards from it (<see cref="Views"/>); a
/// <see cref="Legacy"/> layout takes today's crossed / billboard path byte for byte.</summary>
public sealed record CardLayout(string Mode, int Planes, int Horizontal, IReadOnlyList<double> Heights, bool HeightsAuto, string Slab,
    string Faces, string Back, bool BoxBottom, string Cells, string Padding, string AlphaMips, string? From)
{
    public static readonly string[] Names = { "billboard", "crossed4", "cross2", "star3", "pixyz", "ground", "aerial", "box" };
    public static readonly string[] RowKeys = { "mode", "planes", "horizontal", "heights", "slab", "faces", "back", "box_bottom", "cells", "padding", "alpha_mips" };

    static readonly string[] Modes = { "crossed", "billboard", "layered", "box" };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Custom with untouched rows: today's crossed 4.</summary>
    public static CardLayout Default => Named("crossed4") with { From = null };

    public static CardLayout Named(string name)
    {
        name = (name ?? "").Trim().ToLowerInvariant();   // layout names: trimmed, any case
        var d = new CardLayout("crossed", 4, 0, Array.Empty<double>(), true, "full", "both", "own", false, "equal", "8", "renderer", name);
        switch (name)
        {
            case "billboard": return d with { Mode = "billboard" };
            case "crossed4": return d;
            case "cross2": return d with { Planes = 2 };
            case "star3": return d with { Planes = 3 };
            case "pixyz": return d with { Mode = "layered", Planes = 2, Horizontal = 1, Heights = new[] { 0.5 }, HeightsAuto = false };
            case "ground": return d with { Mode = "layered", Planes = 2, Horizontal = 2, Heights = new[] { 0.4, 0.7 }, HeightsAuto = false, Slab = "slice" };
            case "aerial": return d with { Mode = "layered", Planes = 3, Horizontal = 3, Heights = AutoHeights(3), HeightsAuto = true, Slab = "slice", Faces = "top" };
            case "box": return d with { Mode = "box" };
            default: throw new ArgumentException($"Unknown card layout '{name}'", nameof(name));
        }
    }

    public static IReadOnlyList<double> AutoHeights(int n) => n switch
    {
        <= 0 => Array.Empty<double>(),
        1 => new[] { 0.5 },
        2 => new[] { 0.4, 0.7 },
        3 => new[] { 0.3, 0.55, 0.8 },
        _ => Enumerable.Range(1, n).Select(i => Math.Round(0.05 + 0.9 * i / (n + 1), 3)).ToArray(),
    };

    static string Choice(object? v, string[] allowed, string fallback, string row, List<string>? notes)
    {
        var s = Convert.ToString(v, Inv)?.Trim().ToLowerInvariant() ?? "";
        if (Array.IndexOf(allowed, s) >= 0) return s;
        notes?.Add($"{row} '{s}' is not known: '{fallback}' is used");
        return fallback;
    }

    static int Int(object? v, int fallback)
    {
        try { return (int)Math.Round(Convert.ToDouble(v, Inv)); } catch { return fallback; }
    }

    static bool Bool(object? v, bool fallback)
    {
        try { return v is string s ? bool.Parse(s.Trim()) : Convert.ToBoolean(v, Inv); } catch { return fallback; }
    }

    public static CardLayout Resolve(string layout, IReadOnlyDictionary<string, object> explicitRows, List<string>? notes = null)
    {
        var baseL = string.IsNullOrWhiteSpace(layout) || layout.Trim().Equals("custom", StringComparison.OrdinalIgnoreCase) ? Default : Named(layout);
        var l = baseL with { From = baseL.From ?? "custom" };
        object? Get(string k) => explicitRows.TryGetValue(k, out var v) ? v : null;
        bool Has(string k) => explicitRows.ContainsKey(k);
        if (Has("mode")) l = l with { Mode = Choice(Get("mode"), Modes, l.Mode, "mode", notes) };
        if (Has("planes")) l = l with { Planes = Math.Clamp(Int(Get("planes"), l.Planes), 1, 8) };
        if (Has("horizontal")) l = l with { Horizontal = Math.Clamp(Int(Get("horizontal"), l.Horizontal), 0, 3) };
        if (Has("slab")) l = l with { Slab = Choice(Get("slab"), new[] { "full", "slice" }, l.Slab, "slab", notes) };
        if (Has("faces")) l = l with { Faces = Choice(Get("faces"), new[] { "both", "top" }, l.Faces, "faces", notes) };
        if (Has("back")) l = l with { Back = Choice(Get("back"), new[] { "own", "mirror" }, l.Back, "back", notes) };
        if (Has("box_bottom")) l = l with { BoxBottom = Bool(Get("box_bottom"), l.BoxBottom) };
        if (Has("cells")) l = l with { Cells = Choice(Get("cells"), new[] { "equal", "by_area" }, l.Cells, "cells", notes) };
        if (Has("padding")) l = l with { Padding = Choice(Get("padding"), new[] { "8", "full" }, l.Padding, "padding", notes) };
        if (Has("alpha_mips")) l = l with { AlphaMips = Choice(Get("alpha_mips"), new[] { "renderer", "coverage" }, l.AlphaMips, "alpha_mips", notes) };
        // heights: parsed when given; otherwise the base heights, re-fitted when the horizontal count changed
        var text = Has("heights") ? Convert.ToString(Get("heights"), Inv) ?? "auto"
            : l.HeightsAuto || l.Horizontal != baseL.Horizontal ? "auto" : string.Join(",", l.Heights.Select(h => h.ToString("R", Inv)));
        var (hs, auto) = ParseHeights(text, l.Horizontal, notes);
        return l with { Heights = hs, HeightsAuto = auto };
    }

    static (IReadOnlyList<double>, bool) ParseHeights(string text, int n, List<string>? notes)
    {
        text = (text ?? "").Trim();
        if (n == 0) return (Array.Empty<double>(), true);
        if (text.Length == 0 || text.Equals("auto", StringComparison.OrdinalIgnoreCase)) return (AutoHeights(n), true);
        var vals = new List<double>();
        foreach (var part in text.Split(','))
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, Inv, out var d) || !double.IsFinite(d))
            {
                notes?.Add($"heights '{text}' could not be read (numbers separated by commas): automatic heights are used");
                return (AutoHeights(n), true);
            }
            vals.Add(Math.Clamp(d, 0.05, 0.95));
        }
        if (vals.Count != n)
        {
            notes?.Add($"heights '{text}' lists {vals.Count} values for {n} horizontal plane(s): automatic heights are used");
            return (AutoHeights(n), true);
        }
        vals.Sort();
        return (vals, false);
    }

    internal static CardLayout FromContext(OptContext ctx, List<string>? notes = null)
    {
        var rows = new Dictionary<string, object>();
        foreach (var k in RowKeys)
            if (ctx.Has("replace_cards", k)) rows[k] = ctx.P("replace_cards", k);
        var name = Convert.ToString(ctx.P("replace_cards", "layout"), Inv) ?? "custom";
        try { return Resolve(name, rows, notes); }
        catch (ArgumentException)
        {
            notes?.Add($"layout '{name}' is not known: custom is used");
            return Resolve("custom", rows, notes);
        }
    }

    public IReadOnlyDictionary<string, object> Rows() => new Dictionary<string, object>
    {
        ["mode"] = Mode, ["planes"] = Planes, ["horizontal"] = Horizontal,
        ["heights"] = HeightsAuto ? "auto" : string.Join(", ", Heights.Select(h => h.ToString("R", Inv))),
        ["slab"] = Slab, ["faces"] = Faces, ["back"] = Back, ["box_bottom"] = BoxBottom,
        ["cells"] = Cells, ["padding"] = Padding, ["alpha_mips"] = AlphaMips,
    };

    bool SameRows(CardLayout o)
    {
        if (Mode != o.Mode) return false;
        bool ignoresPlanes = Mode is "billboard" or "box";
        if (!ignoresPlanes && Planes != o.Planes) return false;
        return Horizontal == o.Horizontal && Heights.SequenceEqual(o.Heights) && Slab == o.Slab && Faces == o.Faces && Back == o.Back
            && BoxBottom == o.BoxBottom && Cells == o.Cells && Padding == o.Padding && AlphaMips == o.AlphaMips;
    }

    public string? MatchesNamed() => Names.FirstOrDefault(n => SameRows(Named(n)));

    public string Label => MatchesNamed() ?? (From is { } f && f != "custom" ? $"custom (from {f})" : "custom");

    public bool Legacy => Mode is "crossed" or "billboard" && Horizontal == 0 && Back == "own" && Cells == "equal" && Padding == "8";

    int Crown => Horizontal * (Faces == "both" ? 2 : 1);
    bool IsBox => Mode == "box";
    int BoxFaces => BoxBottom ? 6 : 5;

    public int VerticalCount => Mode switch { "billboard" => 1, "box" => 0, _ => Planes };

    public int Triangles => Mode switch
    {
        "billboard" => 2 + 2 * Crown,
        "box" => 2 * BoxFaces,
        _ => 4 * Planes + 2 * Crown,
    };

    public int Pictures => Mode switch
    {
        "billboard" => 1 + Crown,
        "box" => BoxFaces,
        _ => Planes * (Back == "own" ? 2 : 1) + Crown,
    };

    public string Key => $"mode={Mode}{(Mode is "billboard" or "box" ? "" : $" planes={Planes}")} horizontal={Horizontal} heights={(Horizontal == 0 ? "none" : string.Join(",", Heights.Select(h => h.ToString("R", Inv))))}"
        + $" slab={Slab} faces={Faces} back={Back} box_bottom={(BoxBottom ? "true" : "false")} cells={Cells} padding={Padding} alpha_mips={AlphaMips}";

    public string CountLine(int size)
    {
        int atlas = Legacy ? size : NextPow2(size);
        int n = Math.Max(1, Pictures);
        int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling(n / (double)cols);
        int px = Math.Min(atlas / cols, atlas / rows);
        string lead = IsBox ? $"box, {BoxFaces} faces"
            : (Mode == "billboard" ? "1 billboard" : $"{Planes} vertical") + (Horizontal > 0 ? $" + {Horizontal} horizontal" : "");
        return $"{lead} · {Triangles} triangles · {Pictures} picture{(Pictures == 1 ? "" : "s")} of {px} px";
    }

    /// <summary>The atlas size of a new layout: the next power of two (512 stays 512).</summary>
    public static int NextPow2(int s) { int p = 1; while (p < s) p <<= 1; return p; }

    /// <summary>The pictures this layout takes of an object with bounds lo..hi (object frame), in atlas order. Offsets,
    /// slabs and extents are measured from the render centre (footprint centre, base height lo.z).
    /// Vertical planes at azimuths 180 p / planes (with back own also + 180, interleaved as today); the billboard looks
    /// along +y; horizontal plane i at lo.z + h_i (hi.z - lo.z), a Top view and with faces both a Bottom view, with slab
    /// slice limited to the midpoints to its neighbours (the lowest from the base, the highest to the top). The box: the
    /// four sides and the top (and with box_bottom the bottom), each on the box face that looks at the viewer and
    /// framed to it; a face without area (the object is flat along x or y) is left out. Weight = the plane's world area.</summary>
    public IReadOnlyList<Cards.View> Views(double[] lo, double[] hi)
    {
        double cx = (lo[0] + hi[0]) / 2, cy = (lo[1] + hi[1]) / 2, cz = lo[2];
        double dx = hi[0] - lo[0], dy = hi[1] - lo[1], dz = hi[2] - lo[2];
        var o = new List<Cards.View>();
        if (IsBox)
        {
            o.AddRange(new[] { Cards.Vertical(0), Cards.Vertical(90), Cards.Vertical(180), Cards.Vertical(270), Cards.Top() });
            if (BoxBottom) o.Add(Cards.Bottom());
            var corners = new List<double[]>();
            foreach (double x in new[] { lo[0], hi[0] })
                foreach (double y in new[] { lo[1], hi[1] })
                    foreach (double z in new[] { lo[2], hi[2] }) corners.Add(new[] { x - cx, y - cy, z - cz });
            (double lo, double hi) Range(double[] e)
            {
                var d = corners.Select(c => c[0] * e[0] + c[1] * e[1] + c[2] * e[2]).ToList();
                return (d.Min(), d.Max());
            }
            foreach (var v in o)
            {
                var (a0, a1) = Range(v.Right);
                var (b0, b1) = Range(v.Up);
                v.Offset = Range(v.Dir).lo;   // the face nearest the viewer: the card faces -Dir, outward
                v.Extent = (a0, a1, b0, b1);
                v.Weight = (a1 - a0) * (b1 - b0);
            }
            // a face without area (the object is flat along x or y: a sign, a wall, a single card) has nothing to show;
            // relative to the box size, as an axis-aligned face seen edge-on keeps a rounding-sized width (sin 180 deg)
            double tiny = 1e-9 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
            o.RemoveAll(v => !(v.Extent!.Value.A1 - v.Extent.Value.A0 > tiny && v.Extent.Value.B1 - v.Extent.Value.B0 > tiny));
            return o;
        }
        double side = Math.Sqrt(dx * dx + dy * dy) * dz;
        if (Mode == "billboard") o.Add(Cards.Vertical(90));   // as today: the card lies in the x-z plane, rendered along +y
        else
            for (int p = 0; p < Planes; p++)
            {
                o.Add(Cards.Vertical(180.0 * p / Planes));
                if (Back == "own") o.Add(Cards.Vertical(180.0 * p / Planes + 180));
            }
        foreach (var v in o) v.Weight = side;
        int n = Math.Min(Horizontal, Heights.Count);
        for (int i = 0; i < n; i++)
        {
            double z = lo[2] + Heights[i] * dz;
            double zl = i == 0 ? lo[2] : lo[2] + (Heights[i - 1] + Heights[i]) / 2 * dz;
            double zu = i == n - 1 ? hi[2] : lo[2] + (Heights[i] + Heights[i + 1]) / 2 * dz;
            bool slice = Slab == "slice";
            // depth along Dir from the centre: Top looks along -z, Bottom along +z
            var top = Cards.Top();
            top.Offset = -(z - cz); top.Weight = dx * dy;
            if (slice) top.Slab = (-(zu - cz), -(zl - cz));
            o.Add(top);
            if (Faces != "both") continue;
            var bottom = Cards.Bottom();
            bottom.Offset = z - cz; bottom.Weight = dx * dy;
            if (slice) bottom.Slab = (zl - cz, zu - cz);
            o.Add(bottom);
        }
        return o;
    }
}

/// <summary>What one card bake made: the layout, the atlas size, the pictures and their cell size, the card triangles and
/// the card geometry; R3-C9: the leaf area the atlas keeps at 1/2, 1/4 and 1/8 size (passing share / level 0's) in the
/// box-filtered chain and the coverage-kept one, who makes the alpha mips (<see cref="Mips"/>), the atlas extension and the
/// parts whose class map was filled with the class fill.</summary>
public sealed class CardBake
{
    public CardLayout Layout = CardLayout.Default;
    public int Size, Pictures, CellPx;
    public long Triangles;
    public Node? Geometry;
    public double[] CoverageBox = new double[3], CoverageKept = new double[3];
    /// <summary>"renderer", "coverage written" or "coverage not written: .rgb" (the atlas's real extension).</summary>
    public string Mips = "renderer";
    /// <summary>The base atlas's extension (".png", ".dds", ...).</summary>
    public string Ext = "";
    /// <summary>Parts without a class map where the bake has class maps: their class-map cells hold <see cref="ClassFillId"/>.</summary>
    public int ClassFilledParts;
    public int ClassFillId;
}
