using System.Security.Cryptography;
using System.Text;

namespace YouTubeMusicNative.Api;

/// <summary>
/// Browser-cookie authentication (same approach as ytmusicapi's "browser auth"):
/// requests carry the youtube.com cookies plus an Authorization header derived from SAPISID.
/// </summary>
public sealed class Auth
{
    public const string Origin = "https://music.youtube.com";

    public IReadOnlyDictionary<string, string> Cookies { get; }
    private readonly string _sapisid;

    private Auth(Dictionary<string, string> cookies, string sapisid)
    {
        Cookies = cookies;
        _sapisid = sapisid;
    }

    public string CookieHeader => string.Join("; ", Cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>SAPISIDHASH {ts}_{sha1("{ts} {SAPISID} {origin}")}</summary>
    public string AuthorizationHeader(DateTimeOffset? now = null)
    {
        var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{ts} {_sapisid} {Origin}"));
        return $"SAPISIDHASH {ts}_{Convert.ToHexStringLower(hash)}";
    }

    /// <summary>
    /// Accepts either a raw "Cookie:" header value (copied from DevTools) or a Netscape cookies.txt.
    /// Returns null if no SAPISID cookie is present (i.e. not logged in).
    /// </summary>
    public static Auth? Parse(string input)
    {
        var cookies = input.Contains('\t') ? ParseNetscape(input) : ParseHeader(input);
        var sapisid = cookies.GetValueOrDefault("SAPISID") ?? cookies.GetValueOrDefault("__Secure-3PAPISID");
        return string.IsNullOrEmpty(sapisid) ? null : new Auth(cookies, sapisid);
    }

    private static Dictionary<string, string> ParseHeader(string header)
    {
        header = header.Trim();
        if (header.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            header = header[7..];

        var result = new Dictionary<string, string>();
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) result[part[..eq]] = part[(eq + 1)..];
        }
        return result;
    }

    private static Dictionary<string, string> ParseNetscape(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // "#HttpOnly_" prefixed lines are real cookies; other "#" lines are comments.
            if (line.StartsWith("#HttpOnly_")) line = line[10..];
            else if (line.StartsWith('#') || line.Length == 0) continue;

            var f = line.Split('\t');
            if (f.Length >= 7 && f[0].EndsWith("youtube.com"))
                result[f[5]] = f[6];
        }
        return result;
    }

    /// <summary>Netscape cookies.txt for yt-dlp, so stream resolution sees the same account.</summary>
    public string ToNetscape()
    {
        var expiry = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var (name, value) in Cookies)
            sb.Append($".youtube.com\tTRUE\t/\tTRUE\t{expiry}\t{name}\t{value}\n");
        return sb.ToString();
    }
}
