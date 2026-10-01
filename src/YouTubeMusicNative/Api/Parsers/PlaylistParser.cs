using System.Text.Json.Nodes;

namespace YouTubeMusicNative.Api.Parsers;

internal static class PlaylistParser
{
    /// <summary>Playlists shown as grid cards (library page). Skips the "New playlist" card, which has no browseId.</summary>
    public static List<PlaylistInfo> ParseGrid(JsonNode? root)
    {
        var result = new List<PlaylistInfo>();
        foreach (var r in root.FindAll("musicTwoRowItemRenderer"))
        {
            var browseId = r.Str("navigationEndpoint.browseEndpoint.browseId");
            if (browseId is null || !browseId.StartsWith("VL")) continue;
            result.Add(new PlaylistInfo(
                browseId,
                r["title"].Text(),
                r["subtitle"].Text(),
                JsonNav.Thumbnail(r.Path("thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails"))));
        }
        return result;
    }

    /// <summary>
    /// Tracks of a playlist/liked-songs page. Only the first playlist shelf is read so that
    /// "suggestions"/"related" shelves on the same page don't leak into the list.
    /// </summary>
    public static TrackPage ParsePlaylistPage(JsonNode? root)
    {
        var shelf = (JsonNode?)root.FindFirst("musicPlaylistShelfRenderer") ?? root.FindFirst("musicShelfRenderer");
        // Your own playlists get an editable header (rename/privacy); saved/public ones don't.
        bool editable = root.FindFirst("musicEditablePlaylistDetailHeaderRenderer") is not null;
        if (shelf is null) return new TrackPage([], null, editable);
        var tracks = TrackParser.ParseListItems(shelf["contents"]);

        // Album rows leave the artist out (it's in the album header): fill it in, linked, for songs without one.
        if (AlbumArtists(root) is { Count: > 0 } artists)
        {
            var names = string.Join(", ", artists.Select(a => a.Name));
            tracks = tracks.Select(t => string.IsNullOrWhiteSpace(t.Artists) ? t with { Artists = names, ArtistLinks = artists } : t).ToList();
        }
        return new TrackPage(tracks, JsonNav.Continuation(shelf), editable);
    }

    /// <summary>The artists credited in an album page's header ("straplineTextOne"), with their channel ids.</summary>
    private static List<ArtistRef>? AlbumArtists(JsonNode? root)
    {
        if (root.FindFirst("musicResponsiveHeaderRenderer")?.Path("straplineTextOne.runs") is not JsonArray runs) return null;
        var artists = new List<ArtistRef>();
        foreach (var run in runs)
        {
            var text = run.Str("text")?.Trim();
            if (string.IsNullOrEmpty(text) || text is "•" or "&" or ",") continue;
            artists.Add(new ArtistRef(text, run.Str("navigationEndpoint.browseEndpoint.browseId")));
        }
        return artists;
    }

    /// <summary>A continuation response for a playlist: items live in continuationContents or appendContinuationItemsAction.</summary>
    public static TrackPage ParseContinuation(JsonNode? root)
    {
        var container =
            root.Path("continuationContents") ??
            root.FindFirst("appendContinuationItemsAction");
        return new TrackPage(TrackParser.ParseListItems(container), JsonNav.Continuation(container));
    }
}
