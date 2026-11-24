namespace SceneKit;

/// <summary>How a baked texture's mipmaps are made: by the renderer (one level written, today's files), coverage kept
/// (cut-out atlases), box-averaged (extra units) or nearest (class maps: the top-left texel of each 2 x 2 block).</summary>
public enum MipRule { Renderer, Coverage, Box, Nearest }

/// <summary>R3-C8: mipmaps that keep the leaf area of cut-out textures. A box-filtered mip of a sparse cut-out has lower
/// alpha everywhere, so fewer texels pass the alpha test and foliage thins out with distance. Here each level's alpha is
/// scaled so the share of passing texels (alpha / 255 &gt;= alphaRef) equals level 0's, per atlas cell when the cells are
/// given (a dense and a sparse picture each keep their own share). Colour is averaged over covered texels only, so the
/// transparent black around leaves does not darken them.</summary>
public static class AlphaMips
{
    /// <summary>The alpha test's reference (osg::AlphaFunc GREATER 0.5 in the card state sets).</summary>
    public const double AlphaRef = 0.5;

    static int AlphaChannel(RawImage img) => img.Channels switch { 2 => 1, 4 => 3, _ => -1 };

    /// <summary>The smallest alpha byte that passes.</summary>
    static int Threshold(double alphaRef) => Math.Clamp((int)Math.Ceiling(alphaRef * 255 - 1e-9), 0, 256);

    /// <summary>Share of texels whose alpha passes (1 for images without alpha).</summary>
    public static double Coverage(RawImage img, double alphaRef = AlphaRef)
    {
        int ac = AlphaChannel(img);
        if (ac < 0) return 1;
        int thr = Threshold(alphaRef), n = img.Width * img.Height, pass = 0;
        for (int p = 0; p < n; p++) if (img.Pixels[p * img.Channels + ac] >= thr) pass++;
        return n == 0 ? 0 : (double)pass / n;
    }

    static (int w, int h) Next(int w, int h) => (Math.Max(1, w >> 1), Math.Max(1, h >> 1));

    /// <summary>Box-filtered levels 1..n down to 1 x 1, each averaged from the level above (2 x 2, or 2 x 1 / 1 x 2 at a
    /// side of one texel). For extra units.</summary>
    public static List<RawImage> Box(RawImage level0) => Chain(level0, weighted: false);

    /// <summary>Levels 1..n down to 1 x 1, each texel the top-left texel of its 2 x 2 block in the level above (class maps:
    /// ids are never blended).</summary>
    public static List<RawImage> Nearest(RawImage level0)
    {
        var o = new List<RawImage>();
        var cur = level0;
        while (cur.Width > 1 || cur.Height > 1)
        {
            var (w, h) = Next(cur.Width, cur.Height);
            int ch = cur.Channels;
            var n = new RawImage(w, h, ch);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Array.Copy(cur.Pixels, (Math.Min(2 * y, cur.Height - 1) * cur.Width + Math.Min(2 * x, cur.Width - 1)) * ch, n.Pixels, (y * w + x) * ch, ch);
            o.Add(n);
            cur = n;
        }
        return o;
    }

    /// <summary>The averaged chain; <paramref name="weighted"/>: colour weighted by alpha (covered texels only).</summary>
    static List<RawImage> Chain(RawImage level0, bool weighted)
    {
        var o = new List<RawImage>();
        int ac = weighted ? AlphaChannel(level0) : -1;
        var cur = level0;
        while (cur.Width > 1 || cur.Height > 1)
        {
            var (w, h) = Next(cur.Width, cur.Height);
            int ch = cur.Channels;
            var n = new RawImage(w, h, ch);
            var sum = new long[ch];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx0 = 2 * x, sy0 = 2 * y, sx1 = Math.Min(sx0 + 2, cur.Width), sy1 = Math.Min(sy0 + 2, cur.Height);
                    int cnt = 0;
                    long aSum = 0;
                    Array.Clear(sum);
                    for (int sy = sy0; sy < sy1; sy++)
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int i = (sy * cur.Width + sx) * ch;
                            int a = ac >= 0 ? cur.Pixels[i + ac] : 1;
                            for (int c = 0; c < ch; c++)
                                sum[c] += c == ac ? cur.Pixels[i + c] : (long)cur.Pixels[i + c] * (ac >= 0 ? a : 1);
                            aSum += a;
                            cnt++;
                        }
                    int t = (y * w + x) * ch;
                    if (ac >= 0 && aSum == 0)
                    {
                        // nothing covered: the plain average (the colour bleed of the transparent texels)
                        for (int c = 0; c < ch; c++)
                        {
                            long s = 0;
                            for (int sy = sy0; sy < sy1; sy++)
                                for (int sx = sx0; sx < sx1; sx++) s += cur.Pixels[(sy * cur.Width + sx) * ch + c];
                            n.Pixels[t + c] = (byte)((s + cnt / 2) / cnt);
                        }
                        continue;
                    }
                    for (int c = 0; c < ch; c++)
                    {
                        long div = c == ac || ac < 0 ? cnt : aSum;
                        n.Pixels[t + c] = (byte)Math.Min(255, (sum[c] + div / 2) / div);
                    }
                }
            o.Add(n);
            cur = n;
        }
        return o;
    }

    /// <summary>Ordered-dither offsets (4 x 4 Bayer, in [0, 1)): scaled alpha is rounded with them, so texels of equal alpha
    /// can split around the threshold (a uniform level still keeps half its texels when level 0 had half).</summary>
    static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    static int Dither(int x, int y) => Bayer[(y & 3) * 4 + (x & 3)];

    /// <summary>Scaled alpha byte: floor(a s + (d + 0.5) / 16), clamped.</summary>
    static byte Scaled(int a, double s, int d) => (byte)Math.Clamp((int)Math.Floor(a * s + (d + 0.5) / 16), 0, 255);

    /// <summary>Scale the alpha inside [x0, x1) x [y0, y1) of <paramref name="img"/> (in place) so its passing share is as
    /// close to <paramref name="target"/> as the texels allow; unchanged when that is already as close.</summary>
    static void ScaleRegion(RawImage img, int x0, int y0, int x1, int y1, double target, int thr)
    {
        int ac = AlphaChannel(img), ch = img.Channels, w = img.Width;
        if (ac < 0 || x1 <= x0 || y1 <= y0) return;
        // histogram over (alpha, dither offset): the share for any s without touching the texels
        var hist = new int[256 * 16];
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                hist[img.Pixels[(y * w + x) * ch + ac] * 16 + Dither(x, y)]++;
        double n = (double)(x1 - x0) * (y1 - y0);
        double Share(double s)
        {
            long pass = 0;
            for (int a = 1; a < 256; a++)
                for (int d = 0; d < 16; d++)
                    if (hist[a * 16 + d] != 0 && Scaled(a, s, d) >= thr) pass += hist[a * 16 + d];
            if (thr == 0) pass += Enumerable.Range(0, 16).Sum(d => hist[d]);
            return pass / n;
        }
        double here = Share(1);
        if (here == target) return;
        double lo = 0, hi = 256;
        for (int it = 0; it < 40; it++)
        {
            double mid = (lo + hi) / 2;
            if (Share(mid) < target) lo = mid; else hi = mid;
        }
        double sLo = Share(lo), sHi = Share(hi);
        double best = Math.Abs(sLo - target) < Math.Abs(sHi - target) ? lo : hi;
        if (Math.Abs(here - target) <= Math.Abs(Share(best) - target)) return;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * ch + ac;
                img.Pixels[i] = Scaled(img.Pixels[i], best, Dither(x, y));
            }
    }

    static bool Uniform(RawImage img, int ac, byte v)
    {
        for (int i = ac; i < img.Pixels.Length; i += img.Channels) if (img.Pixels[i] != v) return false;
        return true;
    }

    /// <summary>Coverage-kept levels 1..n down to 1 x 1 (max(1, w &gt;&gt; k)): colour averaged over covered texels, alpha
    /// box-averaged, then per level and per cell (the whole image without <paramref name="perCell"/>) scaled by the factor
    /// found by binary search that makes the passing share equal level 0's there. Cells smaller than one texel of a level
    /// are left as averaged. Images without alpha, all opaque or all empty get the box chain.</summary>
    public static List<RawImage> Build(RawImage level0, double alphaRef = AlphaRef, IReadOnlyList<Cards.CellRect>? perCell = null)
    {
        int ac = AlphaChannel(level0);
        if (ac < 0 || Uniform(level0, ac, 255) || Uniform(level0, ac, 0)) return Box(level0);
        int thr = Threshold(alphaRef);
        var regions = perCell is { Count: > 0 } ? perCell : new[] { new Cards.CellRect(0, 0, level0.Width, level0.Height) };
        var targets = regions.Select(c => RegionCoverage(level0, c.X, c.Y, c.X + c.W, c.Y + c.H, thr)).ToList();
        var raw = Chain(level0, weighted: true);
        var o = new List<RawImage>(raw.Count);
        foreach (var lvl in raw)
        {
            var m = new RawImage(lvl.Width, lvl.Height, lvl.Channels);
            Array.Copy(lvl.Pixels, m.Pixels, m.Pixels.Length);
            for (int k = 0; k < regions.Count; k++)
            {
                var c = regions[k];
                int x0 = (int)((long)c.X * m.Width / level0.Width), x1 = (int)((long)(c.X + c.W) * m.Width / level0.Width);
                int y0 = (int)((long)c.Y * m.Height / level0.Height), y1 = (int)((long)(c.Y + c.H) * m.Height / level0.Height);
                x0 = Math.Clamp(x0, 0, m.Width); x1 = Math.Clamp(x1, 0, m.Width); y0 = Math.Clamp(y0, 0, m.Height); y1 = Math.Clamp(y1, 0, m.Height);
                if (x1 - x0 < 1 || y1 - y0 < 1) continue;   // the cell collapsed below one texel
                ScaleRegion(m, x0, y0, x1, y1, targets[k], thr);
            }
            o.Add(m);
        }
        return o;
    }

    static double RegionCoverage(RawImage img, int x0, int y0, int x1, int y1, int thr)
    {
        int ac = AlphaChannel(img);
        x0 = Math.Clamp(x0, 0, img.Width); x1 = Math.Clamp(x1, 0, img.Width); y0 = Math.Clamp(y0, 0, img.Height); y1 = Math.Clamp(y1, 0, img.Height);
        long pass = 0, n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                n++;
                if (img.Pixels[(y * img.Width + x) * img.Channels + ac] >= thr) pass++;
            }
        return n == 0 ? 0 : (double)pass / n;
    }

    /// <summary>A copy of <paramref name="scaled"/> (a resized cut-out) whose alpha is scaled so its passing share is
    /// <paramref name="targetCoverage"/> (the source's). Images without alpha, all opaque or all empty are returned as
    /// they are.</summary>
    public static RawImage KeepCoverage(RawImage scaled, double targetCoverage, double alphaRef = AlphaRef)
    {
        int ac = AlphaChannel(scaled);
        if (ac < 0 || Uniform(scaled, ac, 255) || Uniform(scaled, ac, 0)) return scaled;
        var m = new RawImage(scaled.Width, scaled.Height, scaled.Channels);
        Array.Copy(scaled.Pixels, m.Pixels, m.Pixels.Length);
        ScaleRegion(m, 0, 0, m.Width, m.Height, targetCoverage, Threshold(alphaRef));
        return m;
    }

    /// <summary>The passing share at 1/2, 1/4 and 1/8 size divided by level 0's, for the box-filtered chain and the
    /// coverage-kept one (1 where level 0 has none; the last level when the image is smaller).</summary>
    public static (double[] Box, double[] Kept) Measure(RawImage atlas, IReadOnlyList<Cards.CellRect>? cells, double alphaRef = AlphaRef)
    {
        double c0 = Coverage(atlas, alphaRef);
        var box = Box(atlas);
        var kept = Build(atlas, alphaRef, cells);
        double[] Ratios(List<RawImage> chain)
        {
            var r = new double[3];
            for (int k = 0; k < 3; k++)
                r[k] = c0 <= 0 || chain.Count == 0 ? 1 : Coverage(chain[Math.Min(k, chain.Count - 1)], alphaRef) / c0;
            return r;
        }
        return (Ratios(box), Ratios(kept));
    }
}
