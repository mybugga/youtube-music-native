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

    // Retries per image after a failed download, and images whose resized variant failed (use the original URL).
    private static readonly Dictionary<string, int> Failures = new();
    private static readonly HashSet<string> UseOriginal = new();

    /// <summary>
    /// Raised (once, shortly after) when an image failed to load and may be asked for again: bindings that show it
    /// (the playing song's art) re-read it, so a passing network hiccup doesn't leave the cover blank.
    /// </summary>
    public static event Action? RetryRequested;
    private static System.Windows.Threading.DispatcherTimer? _retry;

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
        var source = UseOriginal.Contains(url) ? url : JsonNav.ResizeThumb(url, px)!;
        img.UriSource = new Uri(source);
        // Video stills are 16:9 and get centre-cropped to a square, so their height is what must match.
        if (source.Contains("i.ytimg.com")) img.DecodePixelHeight = px;
        else img.DecodePixelWidth = px;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        // A download that fails (offline, say) must not stay cached, or the art would stay blank for good.
        img.DownloadFailed += (_, e) => Failed(key, url, source, img, e.ErrorException);
        img.DecodeFailed += (_, e) => Failed(key, url, source, img, e.ErrorException);
        img.EndInit();

        Map[key] = Lru.AddFirst((key, img));
        if (Lru.Count > Capacity)
        {
            Map.Remove(Lru.Last!.Value.Key);
            Lru.RemoveLast();
        }
        return img;
    }

    /// <summary>Drops the broken image and, a few times per image, asks for it again (the original URL if a resized one failed).</summary>
    private static void Failed(string key, string url, string source, BitmapImage img, Exception? error)
    {
        Forget(key, img);
        int attempts = Failures[key] = Failures.GetValueOrDefault(key) + 1;
        AppLog.Write($"art failed ({attempts}): {source}: {error?.Message}");
        if (source != url) UseOriginal.Add(url);
        if (attempts > 3) return;
        _retry ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retry.Tick -= OnRetry;
        _retry.Tick += OnRetry;
        _retry.Stop();
        _retry.Start();
    }

    private static void OnRetry(object? sender, EventArgs e)
    {
        _retry?.Stop();
        RetryRequested?.Invoke();
    }

    private static void Forget(string key, BitmapImage img)
    {
        if (Map.TryGetValue(key, out var node) && ReferenceEquals(node.Value.Image, img))
        {
            Map.Remove(key);
            Lru.Remove(node);
        }
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
