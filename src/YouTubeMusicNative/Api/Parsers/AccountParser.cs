using System.Text.Json.Nodes;

namespace YouTubeMusicNative.Api.Parsers;

internal static class AccountParser
{
    /// <summary>account/account_menu: the active account (or brand channel) name, handle and photo.</summary>
    public static AccountInfo? ParseAccount(JsonNode? root)
    {
        var header = root.FindFirst("activeAccountHeaderRenderer");
        if (header is null) return null;
        var name = header["accountName"].Text();
        if (string.IsNullOrEmpty(name)) return null;
        var handle = header["channelHandle"].Text();
        // Photos come as "...=s108-c-k-..."; ask for a bigger square for the Account page.
        var photo = header.Path("accountPhoto.thumbnails") is JsonArray thumbs && thumbs.Count > 0
            ? thumbs[^1].Str("url")
            : null;
        if (photo is not null) photo = System.Text.RegularExpressions.Regex.Replace(photo, @"=s\d+", "=s240");
        return new AccountInfo(name, string.IsNullOrEmpty(handle) ? null : handle, photo);
    }

    /// <summary>playlist/get_add_to_playlist: the user's editable playlists.</summary>
    public static List<PlaylistOption> ParseAddToPlaylist(JsonNode? root) =>
        root.FindAll("playlistAddToOptionRenderer")
            .Select(r => (r.Str("playlistId"), r))
            .Where(x => x.Item1 is not null)
            .Select(x => new PlaylistOption(
                x.Item1!,
                x.r["title"].Text(),
                x.r["shortBylineText"].Text() is { Length: > 0 } sub ? sub : null,
                JsonNav.Thumbnail(x.r.Path("thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails"))))
            .ToList();
}
