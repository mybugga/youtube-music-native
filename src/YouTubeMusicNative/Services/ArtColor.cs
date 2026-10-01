using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YouTubeMusicNative.Api.Parsers;

namespace YouTubeMusicNative.Services;

/// <summary>
/// Picks a background tint from album art: decodes a tiny version of the image and averages its
/// pixels, weighting vivid ones, then clamps the result to a dark, readable range.
/// </summary>
public static class ArtColor
{
    public static readonly Color Fallback = Color.FromRgb(0x3A, 0x3A, 0x3A);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, Color> Cache = new();

    public static async Task<Color> GetAsync(string? url)
    {
        url = JsonNav.ResizeThumb(url, 60);
        if (url is null) return Fallback;
        lock (Cache)
            if (Cache.TryGetValue(url, out var cached)) return cached;

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var color = await Task.Run(() => Compute(bytes));
            lock (Cache)
            {
                if (Cache.Count > 200) Cache.Clear();
                Cache[url] = color;
            }
            return color;
        }
        catch (Exception)
        {
            return Fallback;
        }
    }

    private static Color Compute(byte[] bytes)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(bytes);
        img.DecodePixelWidth = 24;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();

        var bgra = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double pb = px[i], pg = px[i + 1], pr = px[i + 2];
            double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
            double sat = max == 0 ? 0 : (max - min) / max;
            // Vivid, mid-bright pixels dominate; greys and near-black/white barely count.
            double weight = 0.03 + sat * sat * (max / 255.0) * (1 - Math.Abs(max / 255.0 - 0.6));
            r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
        }
        if (total == 0) return Fallback;

        var (hue, s, l) = ToHsl(r / total, g / total, b / total);
        return FromHsl(hue, Math.Min(s * 1.15, 0.65), Math.Clamp(l, 0.26, 0.38));
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        r /= 255; g /= 255; b /= 255;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        return Color.FromRgb(
            (byte)Math.Round(Hue(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(Hue(p, q, h) * 255),
            (byte)Math.Round(Hue(p, q, h - 1.0 / 3) * 255));
    }
}
