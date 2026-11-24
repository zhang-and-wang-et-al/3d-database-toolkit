using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SceneKit;

namespace SceneInspector;

/// <summary>Texture downscaling for the 'downscale_textures' pass with the WPF imaging codecs (PNG, JPEG, BMP, TIFF, GIF);
/// SGI (.rgb / .rgba / .sgi) and TGA go through SceneKit's own codecs. Also the encoder for baked textures whose sources
/// are JPEG, BMP, TIFF or GIF (the core writes SGI, TGA, PNG and DDS itself).</summary>
public sealed class WpfImageResizer : ImageResizer
{
    readonly ImageResizer fallback = new ManagedImageResizer();

    public override bool Resize(string src, string dst, int width, int height, bool nearest = false)
    {
        // class maps: never through the WPF filters; SceneKit's codecs read PNG, SGI and TGA and copy texels verbatim
        if (nearest) return fallback.Resize(src, dst, width, height, nearest: true);
        if (ManagedImageResizer.Handles(src) || ManagedImageResizer.Handles(dst)) return fallback.Resize(src, dst, width, height);
        try
        {
            BitmapSource img;
            using (var fs = File.OpenRead(src))
                img = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var scaled = new TransformedBitmap(img, new ScaleTransform((double)width / img.PixelWidth, (double)height / img.PixelHeight));
            string ext = Path.GetExtension(dst).ToLowerInvariant();
            if (ext != ".png" && !WpfExts.Contains(ext))
            {
                // e.g. .dds read by a Windows codec: written by SceneKit in the same file type (uncompressed), never as PNG
                if (!ImageCodecs.CanWrite(ext)) return false;
                var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                var px = new byte[width * height * 4];
                bgra.CopyPixels(px, width * 4, 0);
                var raw = new RawImage(width, height, 4);
                for (int p = 0; p < width * height; p++)
                {
                    raw.Pixels[4 * p] = px[4 * p + 2]; raw.Pixels[4 * p + 1] = px[4 * p + 1]; raw.Pixels[4 * p + 2] = px[4 * p]; raw.Pixels[4 * p + 3] = px[4 * p + 3];
                }
                return ImageCodecs.Write(ImageCodecs.HasAlpha(raw) ? raw : ImageCodecs.WithChannels(raw, 3), dst);
            }
            var enc = EncoderFor(dst);
            enc.Frames.Add(BitmapFrame.Create(scaled));
            using var outFs = File.Create(dst);
            enc.Save(outFs);
            return true;
        }
        catch (Exception exc) when (exc is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    static BitmapEncoder EncoderFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
        ".bmp" => new BmpBitmapEncoder(),
        ".tif" or ".tiff" => new TiffBitmapEncoder(),
        ".gif" => new GifBitmapEncoder(),
        _ => new PngBitmapEncoder(),
    };

    static readonly HashSet<string> WpfExts = new() { ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

    public override bool CanEncode(string ext) => base.CanEncode(ext) || WpfExts.Contains(ext.ToLowerInvariant());

    /// <summary>JPEG, BMP, TIFF and GIF through WPF (grey stays 8-bit grey, RGB 24-bit, alpha 32-bit BGRA); the rest
    /// through SceneKit's codecs.</summary>
    public override bool Encode(RawImage img, string path, int sgiBpc = 1)
    {
        if (!WpfExts.Contains(Path.GetExtension(path).ToLowerInvariant())) return base.Encode(img, path, sgiBpc);
        try
        {
            int w = img.Width, h = img.Height;
            BitmapSource src;
            if (img.Channels == 1) src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, img.Pixels, w);
            else if (img.Channels == 3) src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, img.Pixels, w * 3);
            else
            {
                var bgra = new byte[w * h * 4];
                for (int p = 0; p < w * h; p++)
                {
                    int s = p * img.Channels;
                    byte r = img.Pixels[s], g = img.Channels == 2 ? r : img.Pixels[s + 1], b = img.Channels == 2 ? r : img.Pixels[s + 2];
                    byte a = img.Pixels[s + img.Channels - 1];
                    bgra[4 * p] = b; bgra[4 * p + 1] = g; bgra[4 * p + 2] = r; bgra[4 * p + 3] = a;
                }
                src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            }
            var enc = EncoderFor(path);
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(path);
            enc.Save(fs);
            return true;
        }
        catch (Exception exc) when (exc is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
