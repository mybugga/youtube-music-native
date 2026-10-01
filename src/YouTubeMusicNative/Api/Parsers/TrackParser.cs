using System.Text.Json.Nodes;

namespace YouTubeMusicNative.Api.Parsers;

/// <summary>Turns InnerTube list renderers (search rows, playlist rows, radio panel rows) into <see cref="Track"/>s.</summary>
internal static class TrackParser
{
    private static readonly HashSet<string> TypeLabels =
        ["Song", "Video", "Episode", "Single", "EP", "Album", "Podcast"];

    /// <summary>All playable tracks found under any musicResponsiveListItemRenderer in <paramref name="root"/>.</summary>
    public static List<Track> ParseListItems(JsonNode? root) =>
        root.FindAll("musicResponsiveListItemRenderer")
            .Select(ParseListItem)
            .OfType<Track>()
            .ToList();

    public static Track? ParseListItem(JsonObject r)
    {
        var videoId =
            r.Str("playlistItemData.videoId") ??
            r.Str("overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint.watchEndpoint.videoId") ??
            r.Str("flexColumns.0.musicResponsiveListItemFlexColumnRenderer.text.runs.0.navigationEndpoint.watchEndpoint.videoId");
        if (videoId is null) return null; // unavailable/greyed-out item, or not a song (artist/album row)

        var flex = r["flexColumns"] as JsonArray;
        var title = flex.Path("0.musicResponsiveListItemFlexColumnRenderer.text").Text();

        var artists = new List<ArtistRef>();
        string? album = null, albumId = null, duration = null;
        var plainTexts = new List<string>();

        for (int col = 1; flex != null && col < flex.Count; col++)
        {
            if (flex.Path($"{col}.musicResponsiveListItemFlexColumnRenderer.text.runs") is not JsonArray runs) continue;
            foreach (var run in runs)
            {
                var text = run.Str("text")?.Trim();
                if (string.IsNullOrEmpty(text) || text == "•" || text == "&") continue;

                var pageType = run.Str("navigationEndpoint.browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType");
                var browseId = run.Str("navigationEndpoint.browseEndpoint.browseId");

                if (pageType == "MUSIC_PAGE_TYPE_ARTIST" || pageType == "MUSIC_PAGE_TYPE_USER_CHANNEL" || browseId?.StartsWith("UC") == true)
                    artists.Add(new ArtistRef(text, browseId));
                else if (pageType == "MUSIC_PAGE_TYPE_ALBUM" && album is null)
                {
                    album = text;
                    albumId = browseId;
                }
                else if (JsonNav.DurationRegex().IsMatch(text))
                    duration ??= text;
                else if (run?["navigationEndpoint"] is null && !TypeLabels.Contains(text) && !text.Contains(" plays") && !text.Contains(" views"))
                    plainTexts.Add(text);
            }
        }

        duration ??= r.Path("fixedColumns.0.musicResponsiveListItemFixedColumnRenderer.text").Text() is { Length: > 0 } fixedText
            && JsonNav.DurationRegex().IsMatch(fixedText) ? fixedText : null;

        // Artists without links (e.g. "Various Artists", or uploads) show up as plain text.
        if (artists.Count == 0 && plainTexts.Count > 0)
            artists.Add(new ArtistRef(plainTexts[0], null));

        var thumb = JsonNav.Thumbnail(r.Path("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails"));
        var likeStatus = r.Path("menu.menuRenderer").FindFirst("likeButtonRenderer")?.Str("likeStatus");
        return new Track(videoId, title, string.Join(", ", artists.Select(a => a.Name)), album, duration, thumb)
        {
            ArtistLinks = artists,
            AlbumId = albumId,
            SetVideoId = r.Str("playlistItemData.playlistSetVideoId"),
            Liked = likeStatus is null ? null : likeStatus == "LIKE",
        };
    }

    /// <summary>All tracks in a watch-next / radio playlist panel.</summary>
    public static List<Track> ParsePanelItems(JsonNode? root) =>
        root.FindAll("playlistPanelVideoRenderer")
            .Select(ParsePanelItem)
            .OfType<Track>()
            .ToList();

    public static Track? ParsePanelItem(JsonObject r)
    {
        var videoId = r.Str("videoId") ?? r.Str("navigationEndpoint.watchEndpoint.videoId");
        if (videoId is null) return null;

        var artists = new List<ArtistRef>();
        string? album = null, albumId = null;
        if (r.Path("longBylineText.runs") is JsonArray runs)
        {
            foreach (var run in runs)
            {
                var text = run.Str("text")?.Trim();
                if (string.IsNullOrEmpty(text) || text == "•" || text == "&" || text == ",") continue;
                var pageType = run.Str("navigationEndpoint.browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType");
                if (pageType == "MUSIC_PAGE_TYPE_ALBUM" && album is null)
                {
                    album = text;
                    albumId = run.Str("navigationEndpoint.browseEndpoint.browseId");
                }
                else if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
                    artists.Add(new ArtistRef(text, run.Str("navigationEndpoint.browseEndpoint.browseId")));
            }
            if (artists.Count == 0 && runs.Count > 0 && runs[0].Str("text") is { } first)
                artists.Add(new ArtistRef(first, null));
        }

        return new Track(
            videoId,
            r["title"].Text(),
            string.Join(", ", artists.Select(a => a.Name)),
            album,
            r["lengthText"].Text() is { Length: > 0 } len ? len : null,
            JsonNav.Thumbnail(r.Path("thumbnail.thumbnails")))
        {
            ArtistLinks = artists,
            AlbumId = albumId,
        };
    }
}
