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
        return new TrackPage(TrackParser.ParseListItems(shelf["contents"]), JsonNav.Continuation(shelf), editable);
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
