using System.Buffers.Binary;
using System.IO.Compression;

namespace SceneKit;

/// <summary>An 8-bit image: Pixels row by row from the top, Channels interleaved (1 grey, 2 grey + alpha, 3 RGB, 4 RGBA).</summary>
public sealed class RawImage
{
    public int Width, Height, Channels;
    public byte[] Pixels;
    public RawImage(int w, int h, int c) { Width = w; Height = h; Channels = c; Pixels = new byte[w * h * c]; }
}

/// <summary>Small managed codecs for texture formats common in simulation databases: SGI (.rgb / .rgba / .sgi / .bw),
/// TGA and PNG; plus a Lanczos resize (like Pillow's LANCZOS filter).</summary>
public static class ImageCodecs
{
    public static RawImage? Read(string path)
    {
        var data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P') return ReadPng(data);
        if (data.Length >= 512 && BinaryPrimitives.ReadInt16BigEndian(data) == 474) return ReadSgi(data);
        if (ext == ".tga") return ReadTga(data);
        // JPEG, BMP, GIF, PSD, HDR (StbImageSharp, public domain)
        try
        {
            var r = StbImageSharp.ImageResult.FromMemory(data, StbImageSharp.ColorComponents.Default);
            var img = new RawImage(r.Width, r.Height, (int)r.Comp);
            Buffer.BlockCopy(r.Data, 0, img.Pixels, 0, img.Pixels.Length);
            return img;
        }
        catch (Exception) { return null; }
    }

    /// <summary>File types <see cref="Write"/> can encode: SGI, TGA, PNG and uncompressed DDS.</summary>
    public static bool CanWrite(string ext) => ext.ToLowerInvariant() is ".rgb" or ".rgba" or ".sgi" or ".bw" or ".int" or ".inta" or ".tga" or ".png" or ".dds";

    /// <summary>Encode by the file extension (SGI with <paramref name="sgiBpc"/> bytes per channel); false for other types.</summary>
    public static bool Write(RawImage img, string path, int sgiBpc = 1)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] bytes = ext switch
        {
            ".rgb" or ".rgba" or ".sgi" or ".bw" or ".int" or ".inta" => WriteSgi(img, sgiBpc),
            ".tga" => WriteTga(img),
            ".png" => WritePng(img),
            ".dds" => WriteDds(img),
            _ => Array.Empty<byte>(),
        };
        if (bytes.Length == 0) return false;
        File.WriteAllBytes(path, bytes);
        return true;
    }

    // ------------------------------------------------------------------ SGI

    public static RawImage? ReadSgi(byte[] d)
    {
        int storage = d[2], bpc = d[3];
        int xs = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(6)), ys = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8));
        int zs = Math.Max(1, (int)BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10)));
        if (bpc is not (1 or 2) || xs == 0 || ys == 0 || zs > 4) return null;
        var img = new RawImage(xs, ys, zs);
        var row = new byte[xs * bpc];
        for (int z = 0; z < zs; z++)
            for (int y = 0; y < ys; y++)
            {
                if (storage == 0)
                {
                    int off = 512 + (z * ys + y) * xs * bpc;
                    if (off + row.Length > d.Length) return null;
                    Buffer.BlockCopy(d, off, row, 0, row.Length);
                }
                else
                {
                    int tab = 512 + 4 * (z * ys + y);
                    int start = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(tab));
                    if (!SgiRle(d, start, bpc, row)) return null;
                }
                int dstRow = ys - 1 - y;   // SGI rows go bottom up
                for (int x = 0; x < xs; x++)
                    img.Pixels[(dstRow * xs + x) * zs + z] = bpc == 1 ? row[x] : row[2 * x];
            }
        return img;
    }

    static bool SgiRle(byte[] d, int pos, int bpc, byte[] row)
    {
        int o = 0, n = row.Length / bpc;
        int Get() { int v = bpc == 1 ? d[pos] : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos)); pos += bpc; return v; }
        void Put(int v) { if (bpc == 1) row[o++] = (byte)v; else { row[o++] = (byte)(v >> 8); row[o++] = (byte)v; } }
        while (o < n * bpc)
        {
            if (pos + bpc > d.Length) return false;
            int c = Get(), count = c & 0x7F;
            if (count == 0) break;
            if ((c & 0x80) != 0) { for (int i = 0; i < count && o < row.Length; i++) { if (pos + bpc > d.Length) return false; Put(Get()); } }
            else { if (pos + bpc > d.Length) return false; int v = Get(); for (int i = 0; i < count && o < row.Length; i++) Put(v); }
        }
        return true;
    }

    /// <summary>Bytes per channel of an SGI file (1 or 2); 1 when it is not one or cannot be read.</summary>
    public static int SgiBpc(string? path)
    {
        if (path == null) return 1;
        try
        {
            if (!File.Exists(path)) return 1;
            using var fs = File.OpenRead(path);
            var h = new byte[4];
            if (fs.Read(h, 0, 4) < 4 || BinaryPrimitives.ReadInt16BigEndian(h) != 474) return 1;
            return h[3] == 2 ? 2 : 1;
        }
        catch (IOException) { return 1; }
        catch (UnauthorizedAccessException) { return 1; }
    }

    /// <summary>Uncompressed SGI image, 8 bits per channel (16 with <paramref name="bpc"/> 2: each value times 257).</summary>
    public static byte[] WriteSgi(RawImage img, int bpc = 1)
    {
        bpc = bpc == 2 ? 2 : 1;
        int xs = img.Width, ys = img.Height, zs = img.Channels;
        var o = new byte[512 + xs * ys * zs * bpc];
        BinaryPrimitives.WriteInt16BigEndian(o, 474);
        o[2] = 0; o[3] = (byte)bpc;
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(4), (ushort)(zs == 1 ? 2 : 3));
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(6), (ushort)xs);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(8), (ushort)ys);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(10), (ushort)zs);
        BinaryPrimitives.WriteInt32BigEndian(o.AsSpan(16), bpc == 2 ? 65535 : 255);   // pixmax
        for (int z = 0; z < zs; z++)
            for (int y = 0; y < ys; y++)
                for (int x = 0; x < xs; x++)
                {
                    byte v = img.Pixels[((ys - 1 - y) * xs + x) * zs + z];
                    int at = 512 + ((z * ys + y) * xs + x) * bpc;
                    o[at] = v;
                    if (bpc == 2) o[at + 1] = v;   // v * 257, big-endian
                }
        return o;
    }

    // ------------------------------------------------------------------ DDS

    /// <summary>Uncompressed DDS (no block compression): 32-bit A8R8G8B8 for images with alpha (2 or 4 channels), else
    /// 24-bit R8G8B8. Without <paramref name="mips"/> one level (the renderer makes the mipmaps); with them the levels
    /// follow level 0 in the same layout (MIPMAPCOUNT, COMPLEX | MIPMAP), see <see cref="AlphaMips"/>.</summary>
    public static byte[] WriteDds(RawImage img, IReadOnlyList<RawImage>? mips = null)
    {
        bool alpha = img.Channels is 2 or 4;
        int bpp = alpha ? 4 : 3, w = img.Width, h = img.Height;
        long total = (long)w * h;
        if (mips != null) foreach (var m in mips) total += (long)m.Width * m.Height;
        var o = new byte[128 + total * bpp];
        "DDS "u8.CopyTo(o);
        void U(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(at), v);
        U(4, 124);                                   // header size
        U(8, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | (mips != null ? 0x20000u : 0));   // CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT (| MIPMAPCOUNT)
        U(12, (uint)h); U(16, (uint)w);
        U(20, (uint)(w * bpp));                      // pitch (level 0)
        if (mips != null) U(28, (uint)(mips.Count + 1));   // mip count, level 0 included
        U(76, 32);                                   // pixel format size
        U(80, alpha ? 0x40u | 0x1u : 0x40u);         // RGB (| ALPHAPIXELS)
        U(88, (uint)(bpp * 8));
        U(92, 0x00FF0000); U(96, 0x0000FF00); U(100, 0x000000FF); U(104, alpha ? 0xFF000000u : 0);
        U(108, mips != null ? 0x1000u | 0x8u | 0x400000u : 0x1000u);   // TEXTURE (| COMPLEX | MIPMAP)
        long at = 128;
        void Level(RawImage l)
        {
            int ch = l.Channels;
            for (int p = 0; p < l.Width * l.Height; p++)
            {
                int s = p * ch;
                long t = at + (long)p * bpp;
                byte r, g, b, a = 255;
                if (ch <= 2) { r = g = b = l.Pixels[s]; if (ch == 2) a = l.Pixels[s + 1]; }
                else { r = l.Pixels[s]; g = l.Pixels[s + 1]; b = l.Pixels[s + 2]; if (ch == 4) a = l.Pixels[s + 3]; }
                o[t] = b; o[t + 1] = g; o[t + 2] = r;
                if (alpha) o[t + 3] = a;
            }
            at += (long)l.Width * l.Height * bpp;
        }
        Level(img);
        if (mips != null) foreach (var m in mips) Level(m);
        return o;
    }

    // ------------------------------------------------------------------ channel layout

    /// <summary>The image with <paramref name="ch"/> channels: grey from the luminance, alpha dropped or added opaque.</summary>
    public static RawImage WithChannels(RawImage img, int ch)
    {
        if (img.Channels == ch) return img;
        var o = new RawImage(img.Width, img.Height, ch);
        int sc = img.Channels;
        for (int p = 0; p < img.Width * img.Height; p++)
        {
            int s = p * sc, t = p * ch;
            byte r, g, b, a = 255;
            if (sc <= 2) { r = g = b = img.Pixels[s]; if (sc == 2) a = img.Pixels[s + 1]; }
            else { r = img.Pixels[s]; g = img.Pixels[s + 1]; b = img.Pixels[s + 2]; if (sc == 4) a = img.Pixels[s + 3]; }
            if (ch <= 2)
            {
                o.Pixels[t] = r == g && g == b ? r : (byte)Math.Clamp((int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
                if (ch == 2) o.Pixels[t + 1] = a;
            }
            else
            {
                o.Pixels[t] = r; o.Pixels[t + 1] = g; o.Pixels[t + 2] = b;
                if (ch == 4) o.Pixels[t + 3] = a;
            }
        }
        return o;
    }

    /// <summary>True when some pixel is not fully opaque (false for images without alpha).</summary>
    public static bool HasAlpha(RawImage img)
    {
        if (img.Channels is not (2 or 4)) return false;
        for (int i = img.Channels - 1; i < img.Pixels.Length; i += img.Channels) if (img.Pixels[i] != 255) return true;
        return false;
    }

    /// <summary>True when the image has colour (some pixel's R, G, B differ by more than 1).</summary>
    public static bool HasColour(RawImage img)
    {
        if (img.Channels < 3) return false;
        for (int i = 0; i + 2 < img.Pixels.Length; i += img.Channels)
        {
            int r = img.Pixels[i], g = img.Pixels[i + 1], b = img.Pixels[i + 2];
            if (Math.Abs(r - g) > 1 || Math.Abs(g - b) > 1 || Math.Abs(r - b) > 1) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ TGA

    public static RawImage? ReadTga(byte[] d)
    {
        if (d.Length < 18) return null;
        int idLen = d[0], cmapType = d[1], type = d[2];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12)), h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14));
        int bpp = d[16], desc = d[17];
        if (cmapType != 0 || type is not (2 or 3 or 10 or 11) || w == 0 || h == 0) return null;
        int bytes = bpp / 8;
        if (bytes is not (1 or 3 or 4)) return null;
        int ch = bytes == 1 ? 1 : bytes;
        var raw = new byte[w * h * bytes];
        int pos = 18 + idLen, o = 0;
        if (type is 2 or 3)
        {
            if (pos + raw.Length > d.Length) return null;
            Buffer.BlockCopy(d, pos, raw, 0, raw.Length);
        }
        else
        {
            while (o < raw.Length)
            {
                if (pos >= d.Length) return null;
                int c = d[pos++], count = (c & 0x7F) + 1;
                if ((c & 0x80) != 0)
                {
                    if (pos + bytes > d.Length) return null;
                    for (int i = 0; i < count && o < raw.Length; i++) { Buffer.BlockCopy(d, pos, raw, o, bytes); o += bytes; }
                    pos += bytes;
                }
                else
                {
                    int n = Math.Min(count * bytes, raw.Length - o);
                    if (pos + n > d.Length) return null;
                    Buffer.BlockCopy(d, pos, raw, o, n); o += n; pos += n;
                }
            }
        }
        var img = new RawImage(w, h, ch);
        bool topDown = (desc & 0x20) != 0;
        for (int y = 0; y < h; y++)
        {
            int sy = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int s = (sy * w + x) * bytes, t = (y * w + x) * ch;
                if (ch == 1) img.Pixels[t] = raw[s];
                else { img.Pixels[t] = raw[s + 2]; img.Pixels[t + 1] = raw[s + 1]; img.Pixels[t + 2] = raw[s]; if (ch == 4) img.Pixels[t + 3] = raw[s + 3]; }
            }
        }
        return img;
    }

    public static byte[] WriteTga(RawImage img)
    {
        var src = img.Channels == 2 ? Expand(img, 4) : img;
        int ch = src.Channels;
        var o = new byte[18 + src.Width * src.Height * ch];
        o[2] = (byte)(ch == 1 ? 3 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(12), (ushort)src.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(14), (ushort)src.Height);
        o[16] = (byte)(ch * 8);
        o[17] = (byte)(0x20 | (ch == 4 ? 8 : 0));   // top-down, alpha bits
        for (int p = 0; p < src.Width * src.Height; p++)
        {
            int s = p * ch, t = 18 + p * ch;
            if (ch == 1) o[t] = src.Pixels[s];
            else { o[t] = src.Pixels[s + 2]; o[t + 1] = src.Pixels[s + 1]; o[t + 2] = src.Pixels[s]; if (ch == 4) o[t + 3] = src.Pixels[s + 3]; }
        }
        return o;
    }

    static RawImage Expand(RawImage img, int ch)
    {
        var o = new RawImage(img.Width, img.Height, ch);
        for (int p = 0; p < img.Width * img.Height; p++)
        {
            byte g = img.Pixels[p * img.Channels], a = img.Channels == 2 ? img.Pixels[p * 2 + 1] : (byte)255;
            o.Pixels[p * ch] = g; o.Pixels[p * ch + 1] = g; o.Pixels[p * ch + 2] = g;
            if (ch == 4) o.Pixels[p * ch + 3] = a;
        }
        return o;
    }

    // ------------------------------------------------------------------ PNG

    public static RawImage? ReadPng(byte[] d)
    {
        int pos = 8, w = 0, h = 0, depth = 0, ctype = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        var idat = new MemoryStream();
        while (pos + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            if (len < 0 || pos + 12 + len > d.Length) return null;
            var body = d.AsSpan(pos + 8, len);
            if (type == "IHDR") { w = BinaryPrimitives.ReadInt32BigEndian(body); h = BinaryPrimitives.ReadInt32BigEndian(body[4..]); depth = body[8]; ctype = body[9]; interlace = body[12]; }
            else if (type == "PLTE") palette = body.ToArray();
            else if (type == "tRNS") trns = body.ToArray();
            else if (type == "IDAT") idat.Write(body);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        if (w <= 0 || h <= 0 || depth != 8 || interlace != 0) return null;
        int sch = ctype switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (sch == 0 || (ctype == 3 && palette == null)) return null;
        int stride = w * sch;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < raw.Length) { int n = z.Read(raw, got, raw.Length - got); if (n <= 0) return null; got += n; }
        }
        var cur = new byte[stride];
        var prev = new byte[stride];
        int och = ctype == 3 ? (trns != null ? 4 : 3) : sch;
        var img = new RawImage(w, h, och);
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= sch ? cur[i - sch] : 0, b = prev[i], c = i >= sch ? prev[i - sch] : 0;
                int add = f switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
                cur[i] = (byte)(cur[i] + add);
            }
            if (ctype == 3)
                for (int x = 0; x < w; x++)
                {
                    int idx = cur[x], t = (y * w + x) * och;
                    for (int k = 0; k < 3; k++) img.Pixels[t + k] = 3 * idx + k < palette!.Length ? palette[3 * idx + k] : (byte)0;
                    if (och == 4) img.Pixels[t + 3] = idx < trns!.Length ? trns[idx] : (byte)255;
                }
            else Buffer.BlockCopy(cur, 0, img.Pixels, y * stride, stride);
            (prev, cur) = (cur, prev);
        }
        return img;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static byte[] WritePng(RawImage img)
    {
        int ch = img.Channels, stride = img.Width * ch;
        var raw = new byte[(stride + 1) * img.Height];
        for (int y = 0; y < img.Height; y++) Buffer.BlockCopy(img.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
        var comp = new MemoryStream();
        using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var o = new MemoryStream();
        o.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(ch switch { 1 => 0, 2 => 4, 3 => 2, _ => 6 });
        Chunk(o, "IHDR", ihdr);
        Chunk(o, "IDAT", comp.ToArray());
        Chunk(o, "IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] body)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(head, 4);
        s.Write(head);
        s.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(head.AsSpan(4), body));
        s.Write(crc);
    }

    static uint[]? crcTable;
    static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (crcTable == null)
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            crcTable = t;
        }
        uint crc = 0xFFFFFFFFu;
        foreach (var x in a) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // ------------------------------------------------------------------ resize

    static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
    static double Lanczos3(double x) => Math.Abs(x) < 3 ? Sinc(x) * Sinc(x / 3) : 0;

    /// <summary>Separable Lanczos-3 resampling (the filter widens when shrinking, like Pillow).</summary>
    public static RawImage Resize(RawImage src, int w, int h)
    {
        var tmp = ResizeAxis(src.Pixels, src.Width, src.Height, src.Channels, w, horizontal: true);
        var outp = ResizeAxis(tmp, w, src.Height, src.Channels, h, horizontal: false);
        return new RawImage(w, h, src.Channels) { Pixels = outp };
    }

    /// <summary>Nearest-neighbour resampling for class maps: source texel = floor((x + 0.5) * sw / w), every channel copied
    /// verbatim (no blending, so class ids stay class ids).</summary>
    public static RawImage ResizeNearest(RawImage src, int w, int h)
    {
        int ch = src.Channels;
        var o = new byte[w * h * ch];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(src.Height - 1, (int)((y + 0.5) * src.Height / h));
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(src.Width - 1, (int)((x + 0.5) * src.Width / w));
                Array.Copy(src.Pixels, (sy * src.Width + sx) * ch, o, (y * w + x) * ch, ch);
            }
        }
        return new RawImage(w, h, ch) { Pixels = o };
    }

    static byte[] ResizeAxis(byte[] px, int w, int h, int ch, int n, bool horizontal)
    {
        int inLen = horizontal ? w : h;
        double scale = (double)inLen / n, support = 3 * Math.Max(scale, 1.0), fs = Math.Max(scale, 1.0);
        int ow = horizontal ? n : w, oh = horizontal ? h : n;
        var o = new byte[ow * oh * ch];
        var weights = new double[n][];
        var first = new int[n];
        for (int i = 0; i < n; i++)
        {
            double center = (i + 0.5) * scale;
            int lo = Math.Max(0, (int)Math.Floor(center - support)), hi = Math.Min(inLen - 1, (int)Math.Ceiling(center + support));
            var wts = new double[hi - lo + 1];
            double sum = 0;
            for (int j = lo; j <= hi; j++) { wts[j - lo] = Lanczos3((j + 0.5 - center) / fs); sum += wts[j - lo]; }
            if (sum != 0) for (int k = 0; k < wts.Length; k++) wts[k] /= sum;
            weights[i] = wts;
            first[i] = lo;
        }
        int lines = horizontal ? h : w;
        Parallel.For(0, lines, line =>
        {
            for (int i = 0; i < n; i++)
            {
                var wts = weights[i];
                for (int c = 0; c < ch; c++)
                {
                    double acc = 0;
                    for (int k = 0; k < wts.Length; k++)
                    {
                        int j = first[i] + k;
                        int si = horizontal ? (line * w + j) : (j * w + line);
                        acc += wts[k] * px[si * ch + c];
                    }
                    int di = horizontal ? (line * ow + i) : (i * ow + line);
                    o[di * ch + c] = (byte)Math.Clamp((int)Math.Round(acc), 0, 255);
                }
            }
        });
        return o;
    }
}

/// <summary>Texture downscaling without external libraries: SGI, TGA and PNG (see ImageCodecs).</summary>
public sealed class ManagedImageResizer : ImageResizer
{
    static readonly HashSet<string> Exts = new() { ".rgb", ".rgba", ".sgi", ".bw", ".int", ".inta", ".tga" };

    /// <summary>Formats the WPF codecs cannot read or write.</summary>
    public static bool Handles(string path) => Exts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public override bool Resize(string src, string dst, int width, int height, bool nearest = false)
    {
        try
        {
            var img = ImageCodecs.Read(src);
            if (img == null || width <= 0 || height <= 0) return false;
            // same file type, channels and SGI bytes per channel as the source
            return ImageCodecs.Write(nearest ? ImageCodecs.ResizeNearest(img, width, height) : ImageCodecs.Resize(img, width, height), dst, ImageCodecs.SgiBpc(src));
        }
        catch (Exception exc) when (exc is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }
}
