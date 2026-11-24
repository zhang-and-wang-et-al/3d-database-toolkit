using System.Buffers.Binary;

namespace SceneKit;

/// <summary>Texture wrap / filter modes (TX6) in OSG text documents: read from a texture object, and patched into it
/// when saving so the file says what the model says (like the FileName patch for replaced images).</summary>
public static class TextureSampling
{
    // serializer order of osg::Texture (osgt reads properties in this order; WRAP_R is kept, not modelled)
    static readonly string[] OrderOsgt = { "WRAP_S", "WRAP_T", "WRAP_R", "MIN_FILTER", "MAG_FILTER", "MaxAnisotropy" };
    static readonly string[] OrderOsg = { "wrap_s", "wrap_t", "wrap_r", "min_filter", "mag_filter", "maxAnisotropy" };
    static readonly HashSet<string> HeadOsgt = new() { "Name", "DataVariance", "UserData", "UserDataContainer" };

    static string[] Order(bool osgt) => osgt ? OrderOsgt : OrderOsg;

    /// <summary>Fill the texture's sampling fields from its texture object.</summary>
    public static void Read(Obj to, bool osgt, Texture tex)
    {
        var k = Order(osgt);
        tex.WrapS = Texture.NormMode(to.Value(k[0]));
        tex.WrapT = Texture.NormMode(to.Value(k[1]));
        tex.MinFilter = Texture.NormMode(to.Value(k[3]));
        tex.MagFilter = Texture.NormMode(to.Value(k[4]));
        var a = to.Value(k[5]);
        tex.MaxAnisotropy = a == null ? null : OsgReader.ToFloat(a, 1);
    }

    /// <summary>Make the texture object's statements match the model: change differing values, insert missing ones in
    /// serializer order, remove ones the model leaves unset. Statements that already match stay as they are.</summary>
    public static void Patch(Obj to, bool osgt, Texture tex)
    {
        var k = Order(osgt);
        Set(to, k, 0, tex.WrapS);
        Set(to, k, 1, tex.WrapT);
        Set(to, k, 3, tex.MinFilter);
        Set(to, k, 4, tex.MagFilter);
        Set(to, k, 5, tex.MaxAnisotropy is double a ? NumFmt.Num(a) : null, numeric: true);
        // osgt files written before WRAP_* existed keep working; nothing else to do for legacy .osg (any order)
    }

    static void Set(Obj to, string[] order, int i, string? value, bool numeric = false)
    {
        string key = order[i];
        int at = to.Stmts.FindIndex(s => s.Key == key && s.Block == null);
        if (value == null)
        {
            if (at >= 0) to.Stmts.RemoveAt(at);
            return;
        }
        if (at >= 0)
        {
            var s = to.Stmts[at];
            string? cur = s.Toks.Length > 1 ? s.Toks[1] : null;
            bool same = numeric ? cur != null && OsgReader.ToFloat(cur, double.NaN) == OsgReader.ToFloat(value, double.NaN)
                                : Texture.NormMode(cur) == value;
            if (!same) to.Stmts[at] = new Stmt(new[] { key, value });
            return;
        }
        to.Stmts.Insert(InsertAt(to, order, i), new Stmt(new[] { key, value }));
    }

    static int InsertAt(Obj to, string[] order, int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            int p = to.Stmts.FindIndex(s => s.Key == order[j] && s.Block == null);
            if (p >= 0) return p + 1;
        }
        for (int j = i + 1; j < order.Length; j++)
        {
            int p = to.Stmts.FindIndex(s => s.Key == order[j] && s.Block == null);
            if (p >= 0) return p;
        }
        if (order == OrderOsg) return to.Stmts.Count;           // the legacy reader takes fields in any order
        int h = 0;
        while (h < to.Stmts.Count && HeadOsgt.Contains(to.Stmts[h].Key)) h++;
        return h;
    }
}

/// <summary>OpenFlight texture attribute file ('image.rgb.attr', big-endian): int32 at byte 0 / 4 the texel counts in
/// u / v; the sampling fields: int32 at byte 28 minification filter, 32 magnification filter, 36 repetition (0 repeat,
/// 1 clamp), 40 / 44 repetition in u / v (0 repeat, 1 clamp, 2 none = use the repetition field, 3 mirrored repeat).
/// Clamp maps to CLAMP_TO_EDGE (as OSG's .attr reader does). Files that came with the scene are never changed; a texture
/// the saver writes next to a .flt (baked, downscaled) gets a copy of its source's .attr (see <see cref="WriteCopy"/>).</summary>
public static class FltAttr
{
    sealed record Fields(string WrapS, string WrapT, string MinFilter, string MagFilter);

    static Fields? Decode(byte[] d)
    {
        if (d.Length < 48) return null;
        int I(int o) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o));
        int min = I(28), mag = I(32), wrap = I(36), wu = I(40), wv = I(44);
        if (min is < 0 or > 12 || mag is < 0 or > 12 || wrap is < 0 or > 3 || wu is < 0 or > 3 || wv is < 0 or > 3) return null;   // not an .attr layout
        static string? Wrap(int w) => w switch { 0 => "REPEAT", 1 => "CLAMP_TO_EDGE", 3 => "MIRROR", _ => null };
        string minF = min switch
        {
            0 => "NEAREST", 1 => "LINEAR", 3 => "NEAREST_MIPMAP_NEAREST", 4 => "NEAREST_MIPMAP_LINEAR",
            5 => "LINEAR_MIPMAP_NEAREST", 7 => "LINEAR", _ => "LINEAR_MIPMAP_LINEAR",
        };
        return new Fields(Wrap(wu == 2 ? wrap : wu) ?? "REPEAT", Wrap(wv == 2 ? wrap : wv) ?? "REPEAT", minF, mag == 0 ? "NEAREST" : "LINEAR");
    }

    public static bool Read(Texture tex)
    {
        if (tex.Path == null) return false;
        string p = tex.Path + ".attr";
        byte[] d;
        try
        {
            if (!File.Exists(p)) return false;
            d = File.ReadAllBytes(p);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        var f = Decode(d);
        if (f == null) return false;
        tex.WrapS = f.WrapS; tex.WrapT = f.WrapT; tex.MinFilter = f.MinFilter; tex.MagFilter = f.MagFilter;
        return true;
    }

    static int WrapCode(string w) => w switch { "REPEAT" => 0, "MIRROR" => 3, _ => 1 };   // CLAMP, CLAMP_TO_EDGE, CLAMP_TO_BORDER: clamp
    static int MinCode(string f) => f switch
    {
        "NEAREST" => 0, "LINEAR" => 1, "NEAREST_MIPMAP_NEAREST" => 3, "NEAREST_MIPMAP_LINEAR" => 4, "LINEAR_MIPMAP_NEAREST" => 5,
        _ => 6,   // LINEAR_MIPMAP_LINEAR: mipmap trilinear
    };

    /// <summary>Write <paramref name="dst"/> as a copy of the .attr file <paramref name="src"/> with the texel counts
    /// set to <paramref name="width"/> x <paramref name="height"/> and the sampling fields set from the texture where it
    /// has them and they say something else (fields that already match keep their bytes). False when the source is not
    /// an .attr file or cannot be read / written.</summary>
    public static bool WriteCopy(string src, string dst, int width, int height, Texture tex)
    {
        byte[] d;
        try { d = File.ReadAllBytes(src); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        var cur = Decode(d);
        if (cur == null) return false;
        void Set(int at, int v) => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(at), v);
        Set(0, width);
        Set(4, height);
        string? ws = Texture.NormMode(tex.WrapS), wt = Texture.NormMode(tex.WrapT);
        if (ws != null && WrapCode(ws) != WrapCode(cur.WrapS)) Set(40, WrapCode(ws));
        if (wt != null && WrapCode(wt) != WrapCode(cur.WrapT)) Set(44, WrapCode(wt));
        if (ws != null && wt != null && WrapCode(ws) == WrapCode(wt) && WrapCode(ws) != 3 && BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(36)) != WrapCode(ws))
            Set(36, WrapCode(ws));   // the overall repetition too, for readers that look only at it
        if (tex.MinFilter is { } mn && Texture.NormMode(mn) is { } m && m != cur.MinFilter) Set(28, MinCode(m));
        if (tex.MagFilter is { } mg && Texture.NormMode(mg) is { } g && g != cur.MagFilter) Set(32, g == "NEAREST" ? 0 : 1);
        try { File.WriteAllBytes(dst, d); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        return true;
    }
}
