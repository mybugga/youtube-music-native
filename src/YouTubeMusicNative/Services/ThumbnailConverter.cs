using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using YouTubeMusicNative.Api.Parsers;

namespace YouTubeMusicNative.Services;

/// <summary>
/// URL → small decoded bitmap. Requests a right-sized image from the CDN, decodes at display size,
/// and keeps a bounded LRU so scrolling back doesn't re-download, without letting memory grow.
/// ConverterParameter = display size in px (default 48).
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    private const int Capacity = 300;
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> Map = new();
    private static readonly LinkedList<(string Key, BitmapImage Image)> Lru = new();

    /// <summary>Monitor DPI scale (set by the main window) so images decode at physical pixel size, not 2x always.</summary>
    public static double DpiScale { get; set; } = 1.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || url.Length == 0) return null;
        int size = parameter is string s && int.TryParse(s, out var p) ? p : 48;
        var key = size + "|" + url;

        if (Map.TryGetValue(key, out var node))
        {
            Lru.Remove(node);
            Lru.AddFirst(node);
            return node.Value.Image;
        }

        // Fetch and decode at the physical pixel size only.
        int px = (int)Math.Ceiling(size * DpiScale);
        var img = new BitmapImage();
        img.BeginInit();
        var source = JsonNav.ResizeThumb(url, px)!;
        img.UriSource = new Uri(source);
        // Video stills are 16:9 and get centre-cropped to a square, so their height is what must match.
        if (source.Contains("i.ytimg.com")) img.DecodePixelHeight = px;
        else img.DecodePixelWidth = px;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        img.EndInit();

        Map[key] = Lru.AddFirst((key, img));
        if (Lru.Count > Capacity)
        {
            Map.Remove(Lru.Last!.Value.Key);
            Lru.RemoveLast();
        }
        return img;
    }

    /// <summary>Drops all cached bitmaps (used when the window is hidden to the tray).</summary>
    public static void Clear()
    {
        Map.Clear();
        Lru.Clear();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
