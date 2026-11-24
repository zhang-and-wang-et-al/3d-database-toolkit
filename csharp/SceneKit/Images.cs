using System.Buffers.Binary;

namespace SceneKit;

/// <summary>Image size probing from file headers and texture / external file path resolving.</summary>
public static class Images
{
    public sealed record Info(int Width, int Height, int Channels, string Fmt, double? Bpp);

    static readonly Dictionary<string, double> DdsFourCC = new()
    { ["DXT1"] = 0.5, ["DXT2"] = 1, ["DXT3"] = 1, ["DXT4"] = 1, ["DXT5"] = 1, ["ATI1"] = 0.5, ["ATI2"] = 1, ["BC4U"] = 0.5, ["BC5U"] = 1 };

    public static Info? Probe(string path)
    {
        try
        {
            using var fh = File.OpenRead(path);
            var head = new byte[256];
            int n = fh.Read(head, 0, head.Length);
            if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            {
                fh.Seek(0, SeekOrigin.Begin);
                return Jpeg(fh);
            }
            return FromHeader(head[..n], System.IO.Path.GetExtension(path).ToLowerInvariant());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static bool Starts(ReadOnlySpan<byte> h, ReadOnlySpan<byte> s) => h.Length >= s.Length && h[..s.Length].SequenceEqual(s);

    static Info? FromHeader(byte[] hb, string ext)
    {
        ReadOnlySpan<byte> h = hb;
        if (Starts(h, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }) && h.Length >= 26)
        {
            int w = (int)BinaryPrimitives.ReadUInt32BigEndian(h[16..]), ht = (int)BinaryPrimitives.ReadUInt32BigEndian(h[20..]);
            int ch = h[25] switch { 0 => 1, 2 => 3, 3 => 3, 4 => 2, 6 => 4, _ => 4 };
            return new Info(w, ht, ch, "png", null);
        }
        if (Starts(h, "BM"u8) && h.Length >= 30)
        {
            int w = BinaryPrimitives.ReadInt32LittleEndian(h[18..]), ht = BinaryPrimitives.ReadInt32LittleEndian(h[22..]);
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            return new Info(Math.Abs(w), Math.Abs(ht), bits == 32 ? 4 : 3, "bmp", null);
        }
        if (Starts(h, "DDS "u8) && h.Length >= 88)
        {
            int ht = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[12..]), w = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            string fourcc = System.Text.Encoding.Latin1.GetString(h.Slice(84, 4));
            double? bpp = DdsFourCC.TryGetValue(fourcc, out var b) ? b : null;
            return new Info(w, ht, 4, "dds" + (bpp != null ? "/" + fourcc : ""), bpp);
        }
        if ((Starts(h, "GIF87a"u8) || Starts(h, "GIF89a"u8)) && h.Length >= 10)
            return new Info(BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), BinaryPrimitives.ReadUInt16LittleEndian(h[8..]), 4, "gif", null);
        if (h.Length >= 12 && h[0] == 0x01 && h[1] == 0xDA)
        {
            int w = BinaryPrimitives.ReadUInt16BigEndian(h[6..]), ht = BinaryPrimitives.ReadUInt16BigEndian(h[8..]), zs = BinaryPrimitives.ReadUInt16BigEndian(h[10..]);
            return new Info(w, ht, Math.Max(1, Math.Min(zs, 4)), "sgi", null);
        }
        if (Starts(h, new byte[] { 0xAB, (byte)'K', (byte)'T', (byte)'X', (byte)' ', (byte)'1', (byte)'1', 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }) && h.Length >= 44)
        {
            bool le = h[12] == 1 && h[13] == 2 && h[14] == 3 && h[15] == 4;
            uint R(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(hb.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(hb.AsSpan(o));
            uint glType = R(16);
            return new Info((int)R(36), (int)R(40), 4, "ktx", glType != 0 ? null : 1);
        }
        if (Starts(h, "II*\0"u8) || Starts(h, "MM\0*"u8)) return Tiff(hb);
        if (ext == ".tga" && h.Length >= 18)
        {
            int bits = h[16];
            return new Info(BinaryPrimitives.ReadUInt16LittleEndian(h[12..]), BinaryPrimitives.ReadUInt16LittleEndian(h[14..]),
                bits == 32 ? 4 : (bits == 8 ? 1 : 3), "tga", null);
        }
        return null;
    }

    static Info? Jpeg(Stream fh)
    {
        int Rb() => fh.ReadByte();
        Rb(); Rb();
        while (true)
        {
            int b = Rb();
            while (b >= 0 && b != 0xFF) b = Rb();
            while (b == 0xFF) b = Rb();
            if (b < 0) return null;
            int marker = b;
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            int a1 = Rb(), a2 = Rb();
            if (a2 < 0) return null;
            int ln = (a1 << 8) | a2;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                var d = new byte[6];
                if (fh.Read(d, 0, 6) < 6) return null;
                int ht = (d[1] << 8) | d[2], w = (d[3] << 8) | d[4];
                return new Info(w, ht, d[5], "jpeg", null);
            }
            fh.Seek(ln - 2, SeekOrigin.Current);
        }
    }

    static Info? Tiff(byte[] h)
    {
        bool le = h[0] == (byte)'I';
        uint U32(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(o));
        ushort U16(int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(o));
        if (h.Length < 8) return null;
        long off = U32(4);
        if (off + 2 > h.Length) return new Info(0, 0, 4, "tiff", null);
        int n = U16((int)off);
        int w = 0, ht = 0, spp = 3;
        for (int i = 0; i < n; i++)
        {
            int p = (int)off + 2 + i * 12;
            if (p + 12 > h.Length) break;
            int tag = U16(p), typ = U16(p + 2);
            int val = typ == 3 ? U16(p + 8) : (int)U32(p + 8);
            if (tag == 256) w = val; else if (tag == 257) ht = val; else if (tag == 277) spp = val;
        }
        return new Info(w, ht, spp, "tiff", null);
    }

    static string Norm(string p) => System.IO.Path.GetFullPath(p);

    /// <summary>Find a texture / external file referenced from a scene file.</summary>
    public static string? Resolve(string? name, string baseDir, IEnumerable<string>? searchDirs = null)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string cand = name.Replace('\\', '/');
        if (System.IO.Path.IsPathRooted(cand) && File.Exists(cand)) return cand;
        var dirs = new List<string> { baseDir };
        if (searchDirs != null) dirs.AddRange(searchDirs.Where(d => !string.IsNullOrEmpty(d)));
        foreach (var d in dirs)
        {
            var p = Norm(System.IO.Path.Combine(d, cand));
            if (File.Exists(p)) return p;
        }
        string bn = System.IO.Path.GetFileName(cand);
        foreach (var d in dirs)
        {
            foreach (var sub in new[] { "", "textures", "Textures", "tex", "images", "maps", "..", "../textures" })
            {
                var p = Norm(System.IO.Path.Combine(d, sub, bn));
                if (File.Exists(p)) return p;
            }
            try
            {
                string low = bn.ToLowerInvariant();
                foreach (var f in Directory.EnumerateFileSystemEntries(d))
                    if (System.IO.Path.GetFileName(f).ToLowerInvariant() == low) return System.IO.Path.Combine(d, System.IO.Path.GetFileName(f));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Resolve <c>tex.File</c> and read its size.</summary>
    public static Texture FillTexture(Texture tex, string baseDir, IEnumerable<string>? searchDirs = null)
    {
        if (tex.Embedded) return tex;
        var path = Resolve(tex.File, baseDir, searchDirs);
        tex.Path = path;
        tex.Exists = path != null;
        if (path != null)
        {
            try { tex.FileBytes = new FileInfo(path).Length; } catch (IOException) { }
            var info = Probe(path);
            if (info != null)
            {
                if (tex.Width == 0) { tex.Width = info.Width; tex.Height = info.Height; }
                tex.Channels = info.Channels;
                tex.Fmt = info.Fmt;
                tex.Bpp = info.Bpp;
            }
        }
        return tex;
    }
}
