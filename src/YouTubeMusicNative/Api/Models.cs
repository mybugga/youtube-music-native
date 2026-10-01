namespace YouTubeMusicNative.Api;

public sealed record Track(
    string VideoId,
    string Title,
    string Artists,
    string? Album,
    string? Duration,
    string? ThumbnailUrl)
{
    /// <summary>The row's id inside a playlist (needed to remove it from that playlist).</summary>
    public string? SetVideoId { get; init; }

    /// <summary>Like status when the response included it (playlist rows); null = unknown.</summary>
    public bool? Liked { get; init; }
}

public sealed record PlaylistInfo(
    string BrowseId,
    string Title,
    string Subtitle,
    string? ThumbnailUrl)
{
    /// <summary>"Album", "Artist" or "Playlist", from the browse id prefix.</summary>
    public string KindLabel => BrowseId.StartsWith("MPRE") ? "Album" : BrowseId.StartsWith("UC") ? "Artist" : "Playlist";

    /// <summary>Playlist id without the "VL" browse prefix (what the edit endpoints take).</summary>
    public string PlaylistId => BrowseId.StartsWith("VL") ? BrowseId[2..] : BrowseId;

    /// <summary>The signed-in user's own playlist (editable/deletable), as opposed to a saved one.</summary>
    public bool IsOwned { get; init; }

    /// <summary>Liked Music and Episodes for Later are built in: they can't be deleted or removed.</summary>
    public bool IsBuiltIn => PlaylistId is "LM" or "SE";

    public bool CanDelete => IsOwned && !IsBuiltIn;
    public bool CanRemoveFromLibrary => !IsOwned && !IsBuiltIn;
}

/// <summary>The signed-in account (or brand channel) shown in the title bar and on the Account page.</summary>
public sealed record AccountInfo(string Name, string? Handle, string? PhotoUrl);

/// <summary>A playlist the user can add songs to.</summary>
public sealed record PlaylistOption(string PlaylistId, string Title, string? Subtitle, string? ThumbnailUrl);

public enum PlaylistPrivacy { Private, Unlisted, Public }

public enum ItemKind { Song, Playlist, Album, Artist }

/// <summary>A card on the home feed: either a playable song or something to open (playlist/album/artist).</summary>
public sealed record MediaItem(
    ItemKind Kind,
    string Title,
    string Subtitle,
    string? ThumbnailUrl,
    string? VideoId,
    string? BrowseId)
{
    public bool IsArtist => Kind == ItemKind.Artist;

    public Track ToTrack() => new(VideoId!, Title, Subtitle, null, null, ThumbnailUrl);

    public PlaylistInfo ToPlaylist() => new(BrowseId!, Title, Subtitle, ThumbnailUrl);
}

public sealed record HomeSection(string Title, string? Strapline, IReadOnlyList<MediaItem> Items);

public sealed record HomePage(IReadOnlyList<HomeSection> Sections, ContinuationToken? Next);

/// <summary>
/// InnerTube has two continuation styles: legacy "nextContinuationData" (sent as ctoken query params)
/// and newer "continuationCommand" (sent in the request body).
/// </summary>
public sealed record ContinuationToken(string Token, bool InBody);

public sealed record TrackPage(IReadOnlyList<Track> Tracks, ContinuationToken? Next, bool IsEditable = false);
