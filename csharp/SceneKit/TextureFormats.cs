namespace SceneKit;

/// <summary>How a baked texture is written: file extension, channels (1 grey, 2 grey + alpha, 3 RGB, 4 RGBA), SGI bytes
/// per channel, the source texture whose OpenFlight .attr file is copied (null: none), and a note when the format had
/// to differ from the source's.</summary>
public sealed record BakedFormat(string Ext, int Channels, int Bpc, Texture? AttrSource, string? Note);

/// <summary>Baked textures keep the data format of the textures they were baked from (B1): the extension that covers
/// most texels among the sources, its channel layout and SGI bytes per channel. The extension changes only when it
/// cannot carry what the result needs:
/// <list type="bullet">
/// <item>alpha (the coverage of cards / billboards, or alpha in the source): .bw / .int become .inta; SGI .rgb holds 4
/// channels and stays .rgb; .jpg / .jpeg / .bmp / .gif become .png (.tga when the scene's textures are mostly TGA).</item>
/// <item>colour in a base unit baked from greyscale (material or vertex colours): .bw / .int / .inta become .rgb / .rgba.</item>
/// <item>no encoder: .jpg, .bmp, .tif, .gif need the app's encoder (ImageResizer.Encode, WPF in the inspector); without
/// one the image is written as .png.</item>
/// <item>.dds is written uncompressed (A8R8G8B8, or R8G8B8 without alpha), one level: no block compression, no mipmaps;
/// with a mip rule other than the renderer's (<see cref="Texture.Mips"/>) its own mip chain follows.</item>
/// <item>unknown types become .png.</item>
/// </list>
/// Units above 0 (raw layers) keep the source's channel layout exactly. Without a source texture (material or vertex
/// colours only) the format most used in the scene is taken (by texel area); a scene without texture files gets .rgb
/// for OpenFlight output and .png otherwise.</summary>
public static class TextureFormats
{
    static readonly HashSet<string> Sgi = new() { ".rgb", ".rgba", ".sgi", ".bw", ".int", ".inta" };
    static readonly HashSet<string> Known = new() { ".rgb", ".rgba", ".sgi", ".bw", ".int", ".inta", ".tga", ".png", ".dds", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };
    static readonly HashSet<string> NoAlpha = new() { ".jpg", ".jpeg", ".bmp", ".gif" };

    public static bool IsSgi(string ext) => Sgi.Contains(ext.ToLowerInvariant());

    /// <summary>Lower-case extension of the texture's file (its resolved path first).</summary>
    public static string ExtOf(Texture t)
    {
        string f = t.Path ?? t.File;
        return f.Length == 0 ? "" : Path.GetExtension(f.Replace('\\', '/')).ToLowerInvariant();
    }

    /// <summary>True when the core or the app's encoder can write this file type.</summary>
    public static bool CanEncode(string ext) => ImageCodecs.CanWrite(ext) || (ImageResizer.Current?.CanEncode(ext) ?? false);

    /// <summary>Source list for a baked texture: the given textures, baked ones replaced by their own sources; nulls,
    /// textures without a file name and repeats dropped. Null when nothing is left.</summary>
    public static List<Texture>? Sources(IEnumerable<Texture?> textures)
    {
        var o = new List<Texture>();
        var seen = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var t in textures)
        {
            if (t == null) continue;
            var roots = t.Baked != null && t.BakedFrom is { Count: > 0 } ? t.BakedFrom : new List<Texture> { t };
            foreach (var r in roots)
                if (ExtOf(r).Length > 0 && seen.Add(r)) o.Add(r);
        }
        return o.Count > 0 ? o : null;
    }

    static long Area(Texture t)
    {
        long a = (long)t.Width * t.Height;
        if (a <= 0 && t.Baked != null) a = (long)t.Baked.Width * t.Baked.Height;
        return Math.Max(1, a);
    }

    /// <summary>The extension covering the most texels (ties: the first seen); null for no textures.</summary>
    public static string? Dominant(IEnumerable<Texture> textures)
    {
        var sum = new Dictionary<string, long>();
        var order = new List<string>();
        foreach (var t in textures)
        {
            string e = ExtOf(t);
            if (e.Length == 0) continue;
            if (!sum.ContainsKey(e)) { sum[e] = 0; order.Add(e); }
            sum[e] += Area(t);
        }
        return order.Count == 0 ? null : order.OrderByDescending(e => sum[e]).First();   // stable: ties keep the first seen
    }

    /// <summary>Texture files of the scene (not baked), each once.</summary>
    static IEnumerable<Texture> SceneFiles(Scene? scene)
    {
        if (scene?.Root == null) yield break;
        var seen = new HashSet<Texture>(ReferenceEqualityComparer.Instance);
        foreach (var n in scene.IterNodes())
            if (n.StateSet != null)
                foreach (var t in n.StateSet.Textures)
                    if (t.Baked == null && !t.Embedded && seen.Add(t)) yield return t;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, StrongBox> mostUsed = new();
    sealed class StrongBox { public string? Ext; }

    /// <summary>Forget the scene's most used texture format (computed once per optimiser run / save).</summary>
    public static void Forget(Scene scene) => mostUsed.Remove(scene);

    /// <summary>Format for baked textures without a source: the scene's most used one, else .rgb (OpenFlight) / .png.</summary>
    public static string SceneDefault(Scene? scene, bool flt)
    {
        string? ext = null;
        if (scene != null)
        {
            var box = mostUsed.GetValue(scene, sc => new StrongBox { Ext = Dominant(SceneFiles(sc).Where(t => Known.Contains(ExtOf(t)))) });
            ext = box.Ext;
        }
        return ext ?? (flt ? ".rgb" : ".png");
    }

    /// <summary>The format a baked texture is written in (see the class notes). <paramref name="canEncode"/> defaults to
    /// <see cref="CanEncode"/>.</summary>
    public static BakedFormat Choose(Texture t, Scene? scene, bool flt, Func<string, bool>? canEncode = null)
    {
        canEncode ??= CanEncode;
        var img = t.Baked;
        var sources = t.BakedFrom ?? new List<Texture>();
        string? srcExt = Dominant(sources);
        string ext = srcExt ?? SceneDefault(scene, flt);
        var main = srcExt == null ? null : sources.Where(s => ExtOf(s) == srcExt).OrderByDescending(Area).First();
        bool raw = t.Unit != 0 || t.IsCompanion;
        string name = t.File.Length > 0 ? Path.GetFileNameWithoutExtension(t.File) : "baked texture";
        var notes = new List<string>();

        // channels: the source's layout (from its header); DDS / GIF headers do not say, nor does a missing source
        int ch = main != null && ext is not (".dds" or ".gif") ? Math.Clamp(main.Channels, 1, 4)
            : img != null && ImageCodecs.HasAlpha(img) ? 4 : 3;
        if (!raw)
        {
            if (ch < 3 && img != null && ImageCodecs.HasColour(img)) ch += 2;   // colour from the material / vertex colours
            if (t.BakedCoverage && ch is 1 or 3) ch++;                           // coverage needs alpha
        }
        bool alpha = ch is 2 or 4;
        int bpc = IsSgi(ext) && main != null ? ImageCodecs.SgiBpc(main.Path) : 1;

        string want = ext;
        if (!Known.Contains(ext)) { notes.Add($"{ext} cannot be written"); ext = ".png"; }
        if (IsSgi(ext))
        {
            if (ch == 2 && ext is ".bw" or ".int") ext = ".inta";
            else if (ch >= 3 && ext is ".bw" or ".int" or ".inta") ext = ch == 4 ? ".rgba" : ".rgb";
            if (ext != want) notes.Add(ch >= 3 ? $"{want} is greyscale and the result has colour" : $"{want} has no alpha and the result needs it");
        }
        else if (alpha && NoAlpha.Contains(ext))
        {
            notes.Add($"{ext} has no alpha and the result needs it");
            ext = SceneDefault(scene, flt) == ".tga" ? ".tga" : ".png";
        }
        if (!canEncode(ext))
        {
            notes.Add($"no {ext} encoder here");
            ext = ".png";
        }
        if (ext == ".dds")
        {
            ch = alpha ? 4 : 3;
            notes.Add(t.Mips == MipRule.Renderer ? "DDS written uncompressed (A8R8G8B8 / R8G8B8, one level): block compression and mipmaps are not reproduced"
                : t.Mips == MipRule.Coverage ? "DDS written uncompressed (A8R8G8B8 / R8G8B8) with its own mipmaps (coverage kept)"
                : "DDS written uncompressed (A8R8G8B8 / R8G8B8) with its own mipmaps");
        }
        else if (ext == ".tga" && ch == 2) ch = 4;
        if (!IsSgi(ext)) bpc = 1;

        Texture? attr = null;
        foreach (var s in sources.OrderByDescending(s => ExtOf(s) == srcExt).ThenByDescending(Area))
            if (s.Path != null && File.Exists(s.Path + ".attr")) { attr = s; break; }

        string? note = notes.Count == 0 ? null
            : ext == want ? $"{name}: {string.Join("; ", notes)}"
            : $"{name}: baked from {want} textures, written as {ext} ({string.Join("; ", notes)})";
        return new BakedFormat(ext, ch, bpc, attr, note);
    }

    /// <summary>The format adapted to a file extension a naming rule demands (companion images: the pattern applied to the
    /// written atlas): channels the type cannot hold are dropped (alpha for .jpg / .bmp / .gif, colour for .bw / .int /
    /// .inta), SGI bytes per channel only for SGI. Null when nothing can encode the type.</summary>
    public static BakedFormat? ForExt(BakedFormat f, string ext, string label, Func<string, bool>? canEncode = null)
    {
        canEncode ??= CanEncode;
        if (ext == f.Ext) return f;
        if (!Known.Contains(ext) || !canEncode(ext)) return null;
        int ch = f.Channels;
        bool alpha = ch is 2 or 4;
        if (NoAlpha.Contains(ext) && alpha) ch--;
        if (ext is ".bw" or ".int") ch = 1;
        else if (ext == ".inta") ch = 2;
        else if (ext == ".dds") ch = alpha ? 4 : 3;
        else if (ext == ".tga" && ch == 2) ch = 4;
        int bpc = IsSgi(ext) && IsSgi(f.Ext) ? f.Bpc : 1;
        string note = $"{label}: written as {ext} (its naming rule asks for it; the images it was baked from are {f.Ext})";
        return new BakedFormat(ext, ch, bpc, f.AttrSource, f.Note == null ? note : f.Note + "; " + note);
    }

    /// <summary>Write a baked image in the chosen format (the app's encoder for types the core cannot write). False
    /// when the encoder failed. A .dds of a texture whose <see cref="Texture.Mips"/> is not the renderer's carries its own
    /// mip chain made by that rule (coverage kept per <see cref="Texture.MipCells"/>, box, nearest).</summary>
    public static bool Write(RawImage img, BakedFormat f, string path, Texture? t = null)
    {
        var px = ImageCodecs.WithChannels(img, f.Channels);
        if (f.Ext == ".dds" && t != null && t.Mips != MipRule.Renderer)
        {
            var mips = t.Mips switch
            {
                MipRule.Coverage => AlphaMips.Build(px, AlphaMips.AlphaRef, t.MipCells),
                MipRule.Nearest => AlphaMips.Nearest(px),
                _ => AlphaMips.Box(px),
            };
            File.WriteAllBytes(path, ImageCodecs.WriteDds(px, mips));
            return true;
        }
        if (ImageCodecs.CanWrite(f.Ext)) return ImageCodecs.Write(px, path, f.Bpc);
        return ImageResizer.Current?.Encode(px, path, f.Bpc) ?? false;
    }
}
