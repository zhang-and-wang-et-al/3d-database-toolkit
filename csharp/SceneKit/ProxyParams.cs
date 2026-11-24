using System.Globalization;

namespace SceneKit;

/// <summary>One hull for the whole object, or one per part (wheels, mirrors, moving parts stay separate).</summary>
public enum ProxyParts { Whole, PerPart }

/// <summary>The underside: kept, dropped where it is never seen from below, or cut off at the ground.</summary>
public enum ProxyGround { Full, Above, Cut }

/// <summary>Shading from the hull, or from the original surface (keeps curved bodies round).</summary>
public enum ProxyNormals { Hull, Original }

/// <summary>The settings of the textured proxy (replace_proxy and the proxy far level of generate_lod). R3-C3: record
/// only, the passes do not read it yet. Converts to and from the pass parameters ("replace_proxy.*").</summary>
public sealed record ProxyParams
{
    public ProxyParts Parts { get; init; } = ProxyParts.Whole;
    /// <summary>True: stay within <see cref="MaxError"/> / <see cref="MaxErrorPx"/>. False: keep <see cref="Ratio"/>.</summary>
    public bool Within { get; init; } = false;
    public double Ratio { get; init; } = 0.05;
    public double MaxError { get; init; } = 0.04;
    public double MaxErrorPx { get; init; } = 0;
    public double AtDistance { get; init; } = 100;
    public ProxyGround Ground { get; init; } = ProxyGround.Full;
    public ProxyNormals Normals { get; init; } = ProxyNormals.Hull;
    public double Crease { get; init; } = 45;
    public int Voxels { get; init; } = 128;

    public static readonly string[] PassKeys = { "parts", "target", "max_error", "max_error_px", "at_distance", "ground", "normals", "crease" };

    /// <summary>The largest deviation in metres: the pixel limit seen from <see cref="AtDistance"/>, else <see cref="MaxError"/>.</summary>
    public double ErrorMetres(int screenHeight = 1080, double fovDeg = 45)
        => MaxErrorPx > 0 ? PixelsToMetres(MaxErrorPx, AtDistance, screenHeight, fovDeg) : MaxError;

    /// <summary>What a deviation of this many pixels is at this distance (the formula of generate_lod's switching).</summary>
    public static double PixelsToMetres(double px, double distance, int screenHeight, double fovDeg)
        => px * 2 * Math.Tan(fovDeg * Math.PI / 360) * distance / screenHeight;

    static string Name(ProxyParts v) => v == ProxyParts.PerPart ? "per_part" : "whole";
    internal static string Name(ProxyGround v) => v switch { ProxyGround.Above => "above", ProxyGround.Cut => "cut", _ => "full" };
    internal static string Name(ProxyNormals v) => v == ProxyNormals.Original ? "original" : "hull";

    public Dictionary<string, object> ToPassParams() => new()
    {
        ["replace_proxy.parts"] = Name(Parts),
        ["replace_proxy.target"] = Within ? "within" : "keep",
        ["replace_proxy.ratio"] = Ratio,
        ["replace_proxy.max_error"] = MaxError,
        ["replace_proxy.max_error_px"] = MaxErrorPx,
        ["replace_proxy.at_distance"] = AtDistance,
        ["replace_proxy.ground"] = Name(Ground),
        ["replace_proxy.normals"] = Name(Normals),
        ["replace_proxy.crease"] = Crease,
        ["replace_proxy.voxels"] = Voxels,
    };

    static string Str(object? v, string dflt) => v is null ? dflt : Convert.ToString(v, CultureInfo.InvariantCulture) ?? dflt;
    /// <summary>A number from a number, a numeric string or a JSON value; anything unreadable keeps the default.</summary>
    static double Dbl(object? v, double dflt)
    {
        try
        {
            switch (v)
            {
                case null: return dflt;
                case System.Text.Json.JsonElement je:
                    if (je.ValueKind == System.Text.Json.JsonValueKind.Number) return je.GetDouble();
                    if (je.ValueKind == System.Text.Json.JsonValueKind.String) return ParseDbl(je.GetString(), dflt);
                    return dflt;
                case string str: return ParseDbl(str, dflt);
                default: return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return dflt; }
    }
    static double ParseDbl(string? s, double dflt)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) ? r : dflt;
    static ProxyGround ParseGround(string s, ProxyGround d) => s switch { "full" => ProxyGround.Full, "above" => ProxyGround.Above, "cut" => ProxyGround.Cut, _ => d };

    /// <summary>The record of pass parameters ("replace_proxy.key" or "key"); missing keys keep the defaults. Values may be
    /// strings (the CLI's --set) or numbers. Screen height and field of view are only needed for pixels, see
    /// <see cref="ErrorMetres"/>; they come from generate_lod.screen_height / fov when the caller reads them.</summary>
    public static ProxyParams FromPassParams(IReadOnlyDictionary<string, object> prms)
    {
        var d = new ProxyParams();
        object? Get(string k) => prms.TryGetValue("replace_proxy." + k, out var v) ? v : prms.TryGetValue(k, out v) ? v : null;
        return d with
        {
            Parts = Get("parts") is { } p ? (Str(p, "whole") == "per_part" ? ProxyParts.PerPart : ProxyParts.Whole) : d.Parts,
            Within = Get("target") is { } t ? Str(t, "keep") == "within" : d.Within,
            Ratio = Dbl(Get("ratio"), d.Ratio),
            MaxError = Dbl(Get("max_error"), d.MaxError),
            MaxErrorPx = Dbl(Get("max_error_px"), d.MaxErrorPx),
            AtDistance = Dbl(Get("at_distance"), d.AtDistance),
            Ground = Get("ground") is { } g ? ParseGround(Str(g, "full"), d.Ground) : d.Ground,
            Normals = Get("normals") is { } n ? (Str(n, "hull") == "original" ? ProxyNormals.Original : ProxyNormals.Hull) : d.Normals,
            Crease = Dbl(Get("crease"), d.Crease),
            Voxels = (int)Dbl(Get("voxels"), d.Voxels),
        };
    }

    /// <summary>The record of generate_lod's proxy far level ("generate_lod.proxy_*"). "proxy_from" &gt; 0 means Within,
    /// with the pixel error of a switch seen from that distance. The per-part keep share is today's lod proxy ratio.</summary>
    public static ProxyParams FromLodParams(IReadOnlyDictionary<string, object> prms)
    {
        var d = new ProxyParams();
        object? Get(string k) => prms.TryGetValue("generate_lod." + k, out var v) ? v : prms.TryGetValue(k, out v) ? v : null;
        int screen = (int)Dbl(Get("screen_height"), 1080);
        // fov and the pixel error are clamped as generate_lod's switching clamps them
        double fov = Math.Clamp(Dbl(Get("fov"), 45), 1, 179), px = Math.Max(Dbl(Get("pixel_error"), 1), 1e-3), from = Dbl(Get("proxy_from"), 0);
        var p = d with
        {
            Ratio = 0.06,
            Parts = Str(Get("proxy_parts"), "whole") == "per_part" ? ProxyParts.PerPart : ProxyParts.Whole,
            Ground = ParseGround(Str(Get("proxy_ground"), "full"), d.Ground),
            Normals = Str(Get("proxy_normals"), "hull") == "original" ? ProxyNormals.Original : ProxyNormals.Hull,
            Voxels = (int)Dbl(Get("proxy_voxels"), d.Voxels),
        };
        return from > 0 ? p with { Within = true, MaxError = PixelsToMetres(px, from, screen, fov) } : p;
    }

    /// <summary>"one hull, keep 5 % of the triangles", "per part, within 4 cm, underside cut, shading from the original",
    /// "one hull, within 1 px at 100 m (7.7 cm)".</summary>
    public string Describe(int screenHeight = 1080, double fovDeg = 45)
    {
        string parts = Parts == ProxyParts.PerPart ? "per part" : "one hull";
        string target;
        if (!Within) target = $"keep {PyFormat.G(Ratio * 100, 3)} % of the triangles";
        else if (MaxErrorPx > 0)
        {
            double m = PixelsToMetres(MaxErrorPx, AtDistance, screenHeight, fovDeg);
            string shown = ErrorTarget.Distance(double.Parse(PyFormat.G(m, 2), CultureInfo.InvariantCulture));
            target = $"within {PyFormat.G(MaxErrorPx, 3)} px at {ErrorTarget.Distance(AtDistance)} ({shown})";
        }
        else target = $"within {ErrorTarget.Distance(MaxError)}";
        string s = $"{parts}, {target}";
        if (Ground == ProxyGround.Cut) s += ", underside cut";
        else if (Ground == ProxyGround.Above) s += ", underside not seen from below";
        if (Normals == ProxyNormals.Original) s += ", shading from the original";
        return s;
    }
}
