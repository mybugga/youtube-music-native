using System.Text.Json.Nodes;

namespace YouTubeMusicNative.Api.Parsers;

/// <summary>Home feed: a list of carousel shelves ("Quick picks", "Listen again", mixes, playlists…).</summary>
internal static class HomeParser
{
    public static HomePage Parse(JsonNode? root)
    {
        var sections = root.FindAll("musicCarouselShelfRenderer").Select(ParseCarousel).OfType<HomeSection>().ToList();

        // The feed's own continuation lives on the section list (carousels don't paginate).
        var list = (JsonNode?)root.FindFirst("sectionListRenderer") ?? root.FindFirst("sectionListContinuation");
        var next = list?["continuations"] is JsonArray conts ? JsonNav.Continuation(conts) : null;
        return new HomePage(sections, next);
    }

    /// <summary>One carousel shelf (home feed, artist page): its title and cards; null if empty.</summary>
    public static HomeSection? ParseCarousel(JsonObject shelf)
    {
        var header = shelf.Path("header.musicCarouselShelfBasicHeaderRenderer");
        var title = header?["title"].Text() ?? "";
        var strap = header?["strapline"].Text();

        var items = new List<MediaItem>();
        if (shelf["contents"] is JsonArray contents)
        {
            foreach (var c in contents)
            {
                var item = c?["musicTwoRowItemRenderer"] is JsonObject two ? ParseTwoRow(two)
                    : c?["musicResponsiveListItemRenderer"] is JsonObject row ? ParseSongRow(row)
                    : null;
                if (item is not null) items.Add(item);
            }
        }
        return title.Length > 0 && items.Count > 0
            ? new HomeSection(title, string.IsNullOrEmpty(strap) ? null : strap, items)
            : null;
    }

    public static MediaItem? ParseTwoRow(JsonObject r)
    {
        var title = r["title"].Text();
        var subtitle = r["subtitle"].Text();
        var thumb = JsonNav.Thumbnail(r.Path("thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails"), 200);

        var runs = SubtitleRuns(r["subtitle"]);

        if (r.Str("navigationEndpoint.watchEndpoint.videoId") is { } videoId)
            return new MediaItem(ItemKind.Song, title, StripTypePrefix(subtitle), thumb, videoId, null) { SubtitleRuns = StripTypePrefix(runs) };

        var browseId = r.Str("navigationEndpoint.browseEndpoint.browseId");
        if (browseId is null) return null;
        var pageType = r.Str("navigationEndpoint.browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType");
        var kind = pageType switch
        {
            "MUSIC_PAGE_TYPE_ALBUM" => ItemKind.Album,
            "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" => ItemKind.Artist,
            "MUSIC_PAGE_TYPE_PLAYLIST" => ItemKind.Playlist,
            _ => (ItemKind?)null,
        };
        return kind is null ? null : new MediaItem(kind.Value, title, subtitle, thumb, null, browseId) { SubtitleRuns = runs };
    }

    /// <summary>A subtitle's runs, keeping the browse id of each linked name.</summary>
    private static List<TextRun>? SubtitleRuns(JsonNode? subtitle)
    {
        if (subtitle?["runs"] is not JsonArray runs) return null;
        var result = new List<TextRun>();
        foreach (var run in runs)
            if (run.Str("text") is { Length: > 0 } text)
                result.Add(new TextRun(text, run.Str("navigationEndpoint.browseEndpoint.browseId")));
        return result.Count > 0 ? result : null;
    }

    /// <summary>[Song][ • ][Artist]… → [Artist]…</summary>
    private static List<TextRun>? StripTypePrefix(List<TextRun>? runs)
    {
        if (runs is { Count: > 2 } && runs[0].Text is "Song" or "Video" or "Single" && runs[1].Text.Trim() == "•")
            return runs.Skip(2).ToList();
        return runs;
    }

    private static MediaItem? ParseSongRow(JsonObject r)
    {
        var track = TrackParser.ParseListItem(r);
        return track is null ? null
            : new MediaItem(ItemKind.Song, track.Title, track.Artists, track.ThumbnailUrl, track.VideoId, null)
            {
                SubtitleRuns = track.ArtistLinks?.SelectMany((a, i) => i == 0
                    ? new[] { new TextRun(a.Name, a.BrowseId) }
                    : new[] { new TextRun(", ", null), new TextRun(a.Name, a.BrowseId) }).ToList(),
            };
    }

    /// <summary>"Song • Artist" → "Artist".</summary>
    private static string StripTypePrefix(string subtitle)
    {
        foreach (var prefix in new[] { "Song • ", "Video • ", "Single • " })
            if (subtitle.StartsWith(prefix)) return subtitle[prefix.Length..];
        return subtitle;
    }
}
