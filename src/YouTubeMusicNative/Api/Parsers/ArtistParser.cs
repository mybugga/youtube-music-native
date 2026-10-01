using System.Text.Json.Nodes;

namespace YouTubeMusicNative.Api.Parsers;

/// <summary>
/// An artist's browse page ("UC…"): the immersive header (name, audience, banner, Shuffle / Mix), the "Top songs"
/// shelf, and carousels (Albums, Singles &amp; EPs, Videos, Featured on, Fans might also like…).
/// </summary>
internal static class ArtistParser
{
    public static ArtistPage Parse(JsonNode? root, string browseId)
    {
        // Artists have an immersive header (banner); plain channels a visual one. Same fields either way.
        var header = root.Path("header.musicImmersiveHeaderRenderer") ?? root.Path("header.musicVisualHeaderRenderer")
                     ?? root?["header"]?.AsObject().FirstOrDefault().Value;

        var name = header?["title"].Text() ?? "";
        var audience = header?["monthlyListenerCount"].Text() is { Length: > 0 } monthly ? monthly
            : header.Path("subscriptionButton.subscribeButtonRenderer.subscriberCountText").Text() is { Length: > 0 } subs
                ? subs + " subscribers" : null;
        var description = header?["description"].Text() is { Length: > 0 } d ? d : null;

        var thumbs = header.Path("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails")
                     ?? header.Path("foregroundThumbnail.musicThumbnailRenderer.thumbnail.thumbnails");
        var banner = JsonNav.Thumbnail(thumbs, 1200);
        var avatar = JsonNav.Thumbnail(header.Path("foregroundThumbnail.musicThumbnailRenderer.thumbnail.thumbnails"), 200);

        var shuffle = Watch(header.Path("playButton.buttonRenderer.navigationEndpoint.watchEndpoint"));
        var mix = Watch(header.Path("startRadioButton.buttonRenderer.navigationEndpoint.watchEndpoint"));

        List<Track> topSongs = [];
        string? topSongsId = null;
        var sections = new List<HomeSection>();
        var list = root.FindFirst("sectionListRenderer")?["contents"] as JsonArray;
        foreach (var section in list ?? [])
        {
            if (section?["musicShelfRenderer"] is JsonObject shelf && topSongs.Count == 0)
            {
                topSongs = TrackParser.ParseListItems(shelf["contents"]);
                topSongsId = shelf.Str("title.runs.0.navigationEndpoint.browseEndpoint.browseId")
                             ?? shelf.Str("bottomEndpoint.browseEndpoint.browseId");
            }
            else if (section?["musicCarouselShelfRenderer"] is JsonObject carousel && HomeParser.ParseCarousel(carousel) is { } s)
            {
                sections.Add(s);
            }
        }

        // The artist's own page: credit rows that don't link (single-artist songs) to this artist.
        topSongs = topSongs.Select(t => t.ArtistLinks is { Count: > 0 } ? t : t with { ArtistLinks = [new ArtistRef(name, browseId)] }).ToList();

        return new ArtistPage(browseId, name, audience, description, banner, avatar ?? banner, topSongs, topSongsId, sections, shuffle, mix);
    }

    /// <summary>The top card of an unfiltered search when it's an artist, album or playlist (null for a song or none).</summary>
    public static MediaItem? ParseTopResult(JsonNode? root)
    {
        if (root.FindFirst("musicCardShelfRenderer") is not { } card) return null;
        var nav = card.Path("title.runs.0.navigationEndpoint.browseEndpoint");
        var browseId = nav.Str("browseId");
        if (browseId is null) return null;
        var kind = nav.Str("browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType") switch
        {
            "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" => ItemKind.Artist,
            "MUSIC_PAGE_TYPE_ALBUM" => ItemKind.Album,
            "MUSIC_PAGE_TYPE_PLAYLIST" => ItemKind.Playlist,
            _ => (ItemKind?)null,
        };
        if (kind is null) return null;
        var thumb = JsonNav.Thumbnail(card.Path("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails"), 120);
        return new MediaItem(kind.Value, card["title"].Text(), card["subtitle"].Text(), JsonNav.ResizeThumb(thumb, 226), null, browseId);
    }

    private static WatchTarget? Watch(JsonNode? endpoint) =>
        endpoint is null ? null : new WatchTarget(endpoint.Str("videoId"), endpoint.Str("playlistId"), endpoint.Str("params"));
}
