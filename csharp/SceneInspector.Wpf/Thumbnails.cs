using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SceneKit;

namespace SceneInspector;

/// <summary>Texture thumbnails, decoded on a worker thread and cached (frozen bitmaps). Generated (baked) images come from
/// <see cref="Texture.Baked"/>; png / jpg / bmp / tif / gif go through the WPF decoder (decoded at the thumbnail size),
/// SGI / TGA through <see cref="ImageCodecs"/>. DDS, missing and embedded images have no thumbnail (null: the caller
/// draws a placeholder).</summary>
public static class Thumbnails
{
    static readonly Dictionary<string, BitmapSource?> cache = new();
    static readonly Dictionary<string, Task<BitmapSource?>> pending = new();
    static readonly HashSet<string> WpfExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

    /// <summary>Cache key: the baked image object or the file (with its time stamp, so a changed file is read again).</summary>
    static string? Key(Texture t, int size)
    {
        if (t.Baked != null) return $"b:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t.Baked)}:{size}";
        if (t.Path == null || !t.Exists || t.Embedded) return null;
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(t.Path).Ticks; } catch (Exception) { return null; }
        return $"f:{t.Path}:{stamp}:{size}";
    }

    /// <summary>The thumbnail if it is decoded already; otherwise starts decoding and calls <paramref name="ready"/> on
    /// the UI thread when it is there (not when there is none).</summary>
    public static BitmapSource? Get(Texture t, int size, Action<BitmapSource> ready)
    {
        var key = Key(t, size);
        if (key == null) return null;
        if (cache.TryGetValue(key, out var bmp)) return bmp;
        if (!pending.TryGetValue(key, out var task))
        {
            var baked = t.Baked;
            string? path = t.Path;
            pending[key] = task = Task.Run(() => Decode(baked, path, size));
            task.ContinueWith(x =>
            {
                pending.Remove(key);
                cache[key] = x.Status == TaskStatus.RanToCompletion ? x.Result : null;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        task.ContinueWith(x =>
        {
            if (x.Status == TaskStatus.RanToCompletion && x.Result != null) ready(x.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    static BitmapSource? Decode(RawImage? baked, string? path, int size)
    {
        try
        {
            if (baked != null) return FromRaw(baked, size);
            if (path == null) return null;
            string ext = Path.GetExtension(path);
            if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase)) return null;
            if (WpfExts.Contains(ext))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.UriSource = new Uri(path);
                // decode at the thumbnail size (keeps the aspect: only the longer side is limited)
                var info = Images.Probe(path);
                if (info != null && info.Width >= info.Height) bi.DecodePixelWidth = Math.Min(size, info.Width);
                else if (info != null) bi.DecodePixelHeight = Math.Min(size, info.Height);
                else bi.DecodePixelWidth = size;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            var img = ImageCodecs.Read(path);
            return img == null ? null : FromRaw(img, size);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Point-sampled down to <paramref name="size"/> on the longer side, as BGRA.</summary>
    static BitmapSource FromRaw(RawImage img, int size)
    {
        double s = Math.Min(1.0, size / (double)Math.Max(img.Width, img.Height));
        int w = Math.Max(1, (int)Math.Round(img.Width * s)), h = Math.Max(1, (int)Math.Round(img.Height * s));
        var px = new byte[w * h * 4];
        int c = img.Channels;
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(img.Height - 1, (int)((y + 0.5) / s));
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(img.Width - 1, (int)((x + 0.5) / s));
                int i = (sy * img.Width + sx) * c, o = (y * w + x) * 4;
                byte r = img.Pixels[i], g = c >= 3 ? img.Pixels[i + 1] : r, b = c >= 3 ? img.Pixels[i + 2] : r;
                byte a = c == 4 ? img.Pixels[i + 3] : c == 2 ? img.Pixels[i + 1] : (byte)255;
                // premultiplied for Pbgra32
                px[o] = (byte)(b * a / 255); px[o + 1] = (byte)(g * a / 255); px[o + 2] = (byte)(r * a / 255); px[o + 3] = a;
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
