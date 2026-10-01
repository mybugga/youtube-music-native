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

    /// <summary>
    /// The user's own playlist this song was opened from (with <see cref="SetVideoId"/>), so "Remove from playlist"
    /// works from the player too, not only on the playlist page.
    /// </summary>
    public string? SourcePlaylistId { get; init; }
    public string? SourcePlaylistTitle { get; init; }

    /// <summary>The artists one by one, with their channel ids, so each name can link to the artist's page.</summary>
    public IReadOnlyList<ArtistRef>? ArtistLinks { get; init; }

    /// <summary>The album's browse id ("MPRE…"), so the album name can open the album.</summary>
    public string? AlbumId { get; init; }
}

/// <summary>An artist credited on a song; <see cref="BrowseId"/> (a "UC…" channel id) is null for unlinked names.</summary>
public sealed record ArtistRef(string Name, string? BrowseId);

/// <summary>What a Shuffle / Mix button plays: a "next" (watch) request.</summary>
public sealed record WatchTarget(string? VideoId, string? PlaylistId, string? Params);

/// <summary>An artist's page: header, top songs, then shelves (albums, singles, videos, similar artists…).</summary>
public sealed record ArtistPage(
    string BrowseId,
    string Name,
    string? Audience,
    string? Description,
    string? BannerUrl,
    string? ThumbnailUrl,
    IReadOnlyList<Track> TopSongs,
    string? TopSongsBrowseId,
    IReadOnlyList<HomeSection> Sections,
    WatchTarget? Shuffle,
    WatchTarget? Mix);

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

    /// <summary>Liked Music and Episodes for Later (and the local liked songs) are built in: they can't be deleted or removed.</summary>
    public bool IsBuiltIn => PlaylistId is "LM" or "SE" or "local:liked";

    /// <summary>YouTube (live), a playlist kept on this PC, or a cached copy of a YouTube playlist (signed out / offline).</summary>
    public PlaylistSource Source { get; init; }

    public bool IsLocal => Source == PlaylistSource.Local;

    /// <summary>"Local" / "Cached" badge in Your Library; null for live YouTube playlists.</summary>
    public string? Tag => Source switch
    {
        PlaylistSource.Local => "Local",
        PlaylistSource.Cached => "Cached",
        _ => null,
    };

    public bool CanDelete => (IsOwned || IsLocal) && !IsBuiltIn && Source != PlaylistSource.Cached;
    public bool CanRemoveFromLibrary => !IsOwned && !IsBuiltIn && Source == PlaylistSource.YouTube;
}

public enum PlaylistSource { YouTube, Local, Cached }

/// <summary>The signed-in account (or brand channel) shown in the title bar and on the Account page.</summary>
public sealed record AccountInfo(string Name, string? Handle, string? PhotoUrl);

/// <summary>A playlist the user can add songs to.</summary>
public sealed record PlaylistOption(string PlaylistId, string Title, string? Subtitle, string? ThumbnailUrl)
{
    /// <summary>A playlist on this PC (shown with a "Local" tag in the Add to playlist list).</summary>
    public bool IsLocal { get; init; }
    public string? Tag => IsLocal ? "Local" : null;
}

public enum PlaylistPrivacy { Private, Unlisted, Public }

public enum ItemKind { Song, Playlist, Album, Artist }

/// <summary>A card on the home feed: either a playable song or something to open (playlist/album/artist).</summary>
/// <summary>A piece of text; <see cref="BrowseId"/> is set when YouTube links it (an artist "UC…", an album "MPRE…").</summary>
public sealed record TextRun(string Text, string? BrowseId);

public sealed record MediaItem(
    ItemKind Kind,
    string Title,
    string Subtitle,
    string? ThumbnailUrl,
    string? VideoId,
    string? BrowseId)
{
    public bool IsArtist => Kind == ItemKind.Artist;

    /// <summary>The subtitle in pieces, with the artist / album ids YouTube linked (for clickable names on cards).</summary>
    public IReadOnlyList<TextRun>? SubtitleRuns { get; init; }

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
