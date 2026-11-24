using System.Globalization;
using System.Text.RegularExpressions;

namespace SceneKit;

/// <summary>How the simplifier treats textures and hard edges, named by what the user gets (R2-D4).</summary>
public enum SimplifyStrategy { KeepTexturesExact, LetEdgesMerge, NewTexture, ShapeOnly, FastRough }

/// <summary>One strategy: label (primary), tech name (secondary / tooltip), consequence-first help line and the pass
/// values it stands for.</summary>
public sealed record StrategyInfo(SimplifyStrategy Id, string Label, string Tech, string Help, string Method, string Seams);

public static class Strategies
{
    public static readonly IReadOnlyList<StrategyInfo> All = new[]
    {
        new StrategyInfo(SimplifyStrategy.KeepTexturesExact, "Keep textures exact", "qem + keep",
            "Only collapses that leave every texture seam and hard edge in place. Safest, reduces least.", "qem", "keep"),
        new StrategyInfo(SimplifyStrategy.LetEdgesMerge, "Let edges merge", "qem + permissive",
            "Hard edges may merge (the normals are rebuilt); texture seams still only slide. Textures may shift slightly.", "qem", "permissive"),
        new StrategyInfo(SimplifyStrategy.NewTexture, "New texture", "qem + rebake",
            "Simplifies freely, then bakes a new texture from the original. New texture file.", "qem", "rebake"),
        new StrategyInfo(SimplifyStrategy.ShapeOnly, "Shape only", "qem + ignore",
            "Ignores textures. For untextured parts or when the texture will be replaced.", "qem", "ignore"),
        new StrategyInfo(SimplifyStrategy.FastRough, "Fast, rough", "fast (clustering)",
            "Merges nearby vertices by grid cell. Quick, uneven. Good for a first look.", "fast", "keep"),
    };

    public static StrategyInfo Of(SimplifyStrategy s) => All.First(x => x.Id == s);

    /// <summary>The strategy of a pass's method / seams pair (unknown seams: keep textures exact).</summary>
    public static SimplifyStrategy From(string method, string seams)
    {
        // meshoptimizer's methods stand for the same strategies: sloppy is Fast, rough, meshopt follows the seams
        if (method is "fast" or "cluster" or "sloppy") return SimplifyStrategy.FastRough;
        return seams switch
        {
            "permissive" => SimplifyStrategy.LetEdgesMerge,
            "rebake" => SimplifyStrategy.NewTexture,
            "ignore" => SimplifyStrategy.ShapeOnly,
            _ => SimplifyStrategy.KeepTexturesExact,
        };
    }
}

/// <summary>Which simplifier runs a strategy (R3-D1): the built-in one or meshoptimizer. Orthogonal to the strategy,
/// except that New texture always uses the standard simplifier (D8).</summary>
public enum SimplifyEngine { Standard, Meshopt }

public static class SimplifyEngines
{
    /// <summary>False with the reason when the engine cannot run on this computer (the meshoptimizer library is missing).</summary>
    public static bool Available(SimplifyEngine e, out string reason)
    {
        reason = e == SimplifyEngine.Meshopt ? MeshoptNative.Unavailable : "";
        return reason.Length == 0;
    }

    /// <summary>The label (primary): "Standard" / "meshoptimizer".</summary>
    public static string Label(SimplifyEngine e) => e == SimplifyEngine.Meshopt ? "meshoptimizer" : "Standard";

    /// <summary>The tech names (secondary / tooltip).</summary>
    public static string Tech(SimplifyEngine e) => e == SimplifyEngine.Meshopt ? "meshopt / sloppy" : "qem / R-Simp";
}

/// <summary>The largest deviation a simplification may cause: metres, or % of the object's bounding diagonal.</summary>
public readonly record struct ErrorTarget(double Value, bool Relative)
{
    static readonly Regex Rx = new(@"^(?<num>(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?)\s*(?<unit>mm|cm|km|m|%(\s*of\s+size)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"2 cm", "0,5 %", "5mm", "0.3" (bare number = metres). False for empty, garbage, 0 or negative values.</summary>
    public static bool TryParse(string text, out ErrorTarget t)
    {
        t = default;
        var m = Rx.Match((text ?? "").Trim().Replace(',', '.'));
        if (!m.Success || !double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
        string unit = m.Groups["unit"].Value.ToLowerInvariant();
        bool rel = unit.StartsWith('%');
        v *= unit switch { "mm" => 0.001, "cm" => 0.01, "km" => 1000.0, _ => 1.0 };
        if (!double.IsFinite(v) || v <= 0) return false;
        t = new ErrorTarget(v, rel);
        return true;
    }

    /// <summary>The target in metres for an object with this bounding diagonal (metres).</summary>
    public double Metres(double diag) => Relative ? Value / 100.0 * diag : Value;

    /// <summary>A distance with a readable unit: "5 mm", "2 cm", "1.5 m", "1.2 km".</summary>
    public static string Distance(double metres)
    {
        // the unit is chosen after rounding: 0.9999 -> "1 m" (not "100 cm"), 999.6 -> "1 km"
        (double per, double below, string name)[] units = { (1000, 10, "mm"), (100, 100, "cm"), (1, 1000, "m"), (0.001, double.PositiveInfinity, "km") };
        double a = Math.Abs(metres);
        int k = a < 0.01 ? 0 : a < 1 ? 1 : a < 1000 ? 2 : 3;
        string text = PyFormat.G(metres * units[k].per, 3);
        while (k < 3 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && Math.Abs(r) >= units[k].below)
            text = PyFormat.G(metres * units[++k].per, 3);
        return $"{text} {units[k].name}";
    }

    /// <summary>"2 cm", "0.5 % of size", or with the diagonal "0.5 % of size (1.2 cm)".</summary>
    public string Text(double? diag = null)
    {
        if (!Relative) return Distance(Value);
        string s = $"{PyFormat.G(Value, 3)} % of size";
        return diag is double d && d > 0 ? $"{s} ({Distance(Metres(d))})" : s;
    }
}

/// <summary>The one parameter set of the simplify pass, shared by Try and Apply (R2-D3). Converts to and from the
/// pass parameters ("simplify.*").</summary>
public sealed record SimplifyParams
{
    /// <summary>True: stay within <see cref="Error"/> (the triangle count is the result). False: keep <see cref="Ratio"/>.</summary>
    public bool Within { get; init; } = true;
    public ErrorTarget Error { get; init; } = new(0.5, true);
    public double Ratio { get; init; } = 0.5;
    public SimplifyStrategy Strategy { get; init; } = SimplifyStrategy.KeepTexturesExact;
    public string Borders { get; init; } = "match";
    public string Parts { get; init; } = "merge";
    public double Crease { get; init; } = 40.0;
    public int RebakeSize { get; init; } = 512;
    /// <summary>Terrain under roads and decals keeps its vertices, so they do not float or sink (R2-D5 UX-10).</summary>
    public bool ProtectOverlays { get; init; } = true;
    /// <summary>The simplifier that runs the strategy (R3-D1).</summary>
    public SimplifyEngine Engine { get; init; } = SimplifyEngine.Standard;
    /// <summary>meshoptimizer only: drop small separate parts that the target makes worthless (R3-D3).</summary>
    public bool Prune { get; init; }

    /// <summary>The pass method of strategy and engine. New texture always bakes with the standard simplifier (D8), so
    /// it reads back as Standard.</summary>
    public string Method => (Strategy, Engine) switch
    {
        (SimplifyStrategy.FastRough, SimplifyEngine.Meshopt) => "sloppy",
        (SimplifyStrategy.FastRough, _) => "fast",
        (SimplifyStrategy.NewTexture, _) => "qem",
        (_, SimplifyEngine.Meshopt) => "meshopt",
        _ => "qem",
    };

    public static readonly string[] PassKeys = { "target", "ratio", "max_error", "max_error_pct", "method", "seams", "borders", "parts", "crease", "rebake_texture", "protect_overlays", "prune" };

    /// <summary>Fast, rough has no error limit: it always keeps a share of the triangles.</summary>
    public SimplifyParams Normalized() => Strategy == SimplifyStrategy.FastRough && Within ? this with { Within = false } : this;

    public Dictionary<string, object> ToPassParams()
    {
        var s = Strategies.Of(Strategy);
        return new Dictionary<string, object>
        {
            ["simplify.target"] = Within ? "within" : "keep",
            ["simplify.ratio"] = Ratio,
            ["simplify.max_error"] = Within && !Error.Relative ? Error.Value : 0.0,
            ["simplify.max_error_pct"] = Within && Error.Relative ? Error.Value : 0.0,
            ["simplify.method"] = Method,
            ["simplify.seams"] = s.Seams,
            ["simplify.borders"] = Borders,
            ["simplify.parts"] = Parts,
            ["simplify.crease"] = Crease,
            ["simplify.rebake_texture"] = RebakeSize,
            ["simplify.protect_overlays"] = ProtectOverlays,
            ["simplify.prune"] = Prune,
        };
    }

    /// <summary>The record of pass parameters ("simplify.key" or "key"); missing keys keep the defaults.</summary>
    public static SimplifyParams FromPassParams(IReadOnlyDictionary<string, object> prms)
    {
        var d = new SimplifyParams();
        object? Get(string k) => prms.TryGetValue("simplify." + k, out var v) ? v : prms.TryGetValue(k, out v) ? v : null;
        string Str(string k, string dflt) => Get(k) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? dflt : dflt;
        double Dbl(string k, double dflt) => Get(k) is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : dflt;
        bool within = Get("target") is null ? d.Within : Str("target", "keep") == "within";
        double me = Dbl("max_error", 0), pct = Dbl("max_error_pct", 0);
        // both given (Apply from the preview): the relative target, max_error is only its upper limit
        var err = !within ? d.Error : pct > 0 ? new ErrorTarget(pct, true) : me > 0 ? new ErrorTarget(me, false) : d.Error;
        return d with
        {
            Within = within,
            Error = err,
            Ratio = Dbl("ratio", d.Ratio),
            Strategy = Get("method") is null && Get("seams") is null ? d.Strategy : Strategies.From(Str("method", "qem"), Str("seams", "keep")),
            Borders = Str("borders", d.Borders),
            Parts = Str("parts", d.Parts),
            Crease = Dbl("crease", d.Crease),
            RebakeSize = (int)Dbl("rebake_texture", d.RebakeSize),
            ProtectOverlays = Get("protect_overlays") is { } po ? Convert.ToBoolean(po, CultureInfo.InvariantCulture) : d.ProtectOverlays,
            Engine = Get("method") is null ? d.Engine : Str("method", "qem") is "meshopt" or "sloppy" ? SimplifyEngine.Meshopt : SimplifyEngine.Standard,
            Prune = Get("prune") is { } pr ? Convert.ToBoolean(pr, CultureInfo.InvariantCulture) : d.Prune,
        };
    }

    /// <summary>"within 2 cm, keep textures exact", "keep 30 % of the triangles, let edges merge" (", meshoptimizer" when
    /// that engine runs it), or with the node's
    /// diagonal "within 0.5 % of each part's size (up to 1.41 m), keep textures exact" (each geometry keeps to its own
    /// size, never more than that share of the node's).</summary>
    public string Describe(double? diag = null)
    {
        string label = Strategies.Of(Strategy).Label.ToLowerInvariant();
        if (Engine == SimplifyEngine.Meshopt && Strategy != SimplifyStrategy.NewTexture) label += ", " + SimplifyEngines.Label(Engine);
        if (!Within) return $"keep {PyFormat.G(Ratio * 100, 3)} % of the triangles, {label}";
        string target = Error.Relative && diag is double d && d > 0
            ? $"{PyFormat.G(Error.Value, 3)} % of each part's size (up to {ErrorTarget.Distance(Error.Metres(d))})"
            : Error.Text();
        return $"within {target}, {label}";
    }
}
