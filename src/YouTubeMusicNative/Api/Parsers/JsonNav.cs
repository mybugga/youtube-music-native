using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YouTubeMusicNative.Api.Parsers;

/// <summary>
/// Helpers for walking InnerTube JSON. YouTube reshuffles its renderer nesting often, so parsers
/// locate renderers by name anywhere in the tree instead of hard-coding full paths.
/// </summary>
internal static partial class JsonNav
{
    /// <summary>Follows a dotted path; numeric segments index arrays. Returns null on any miss.</summary>
    public static JsonNode? Path(this JsonNode? node, string path)
    {
        foreach (var seg in path.Split('.'))
        {
            if (node is null) return null;
            if (node is JsonArray arr && int.TryParse(seg, out int i))
                node = i < arr.Count ? arr[i] : null;
            else if (node is JsonObject obj)
                node = obj.TryGetPropertyValue(seg, out var child) ? child : null;
            else
                return null;
        }
        return node;
    }

    public static string? Str(this JsonNode? node, string path) =>
        node.Path(path) is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>Depth-first search for every object stored under <paramref name="key"/>.</summary>
    public static IEnumerable<JsonObject> FindAll(this JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj)
                {
                    if (k == key && v is JsonObject match)
                        yield return match;
                    else
                        foreach (var m in FindAll(v, key))
                            yield return m;
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    foreach (var m in FindAll(item, key))
                        yield return m;
                break;
        }
    }

    public static JsonObject? FindFirst(this JsonNode? node, string key) => FindAll(node, key).FirstOrDefault();

    /// <summary>Concatenates "runs[].text" or returns "simpleText".</summary>
    public static string Text(this JsonNode? textNode)
    {
        if (textNode is null) return "";
        if (textNode.Str("simpleText") is { } simple) return simple;
        if (textNode["runs"] is JsonArray runs)
            return string.Concat(runs.Select(r => r.Str("text")));
        return "";
    }

    /// <summary>Picks the smallest thumbnail at least <paramref name="minWidth"/> wide (or the largest available).</summary>
    public static string? Thumbnail(JsonNode? thumbnailsArray, int minWidth = 60)
    {
        if (thumbnailsArray is not JsonArray arr || arr.Count == 0) return null;
        var sorted = arr
            .Select(t => (Url: t.Str("url"), W: (int?)t?["width"]?.GetValue<int>() ?? 0))
            .Where(t => t.Url != null)
            .OrderBy(t => t.W)
            .ToList();
        if (sorted.Count == 0) return null;
        var pick = sorted.FirstOrDefault(t => t.W >= minWidth);
        return (pick.Url ?? sorted[^1].Url)!;
    }

    /// <summary>Finds the continuation token of the first shelf/panel in the response, in either style.</summary>
    public static ContinuationToken? Continuation(JsonNode? node)
    {
        if (node.FindFirst("continuationCommand")?.Str("token") is { } bodyToken)
            return new ContinuationToken(bodyToken, InBody: true);
        if (node.FindFirst("nextContinuationData")?.Str("continuation") is { } queryToken)
            return new ContinuationToken(queryToken, InBody: false);
        return null;
    }

    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    public static partial Regex DurationRegex();

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex GoogleSizeRegex();

    [GeneratedRegex(@"^https://i\.ytimg\.com/vi/([\w-]+)/")]
    private static partial Regex YtImgRegex();

    /// <summary>
    /// Rewrites thumbnail URLs to small variants: googleusercontent art to the requested square size,
    /// and video stills to the 320x180 "mqdefault" (no letterbox bars, far smaller than hqdefault).
    /// </summary>
    public static string? ResizeThumb(string? url, int size)
    {
        if (url is null) return null;
        if (GoogleSizeRegex().IsMatch(url)) return GoogleSizeRegex().Replace(url, $"=w{size}-h{size}");
        if (YtImgRegex().Match(url) is { Success: true } m) return $"https://i.ytimg.com/vi/{m.Groups[1].Value}/mqdefault.jpg";
        return url;
    }
}
