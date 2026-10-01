using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public sealed partial class PlaylistViewModel : TrackListViewModel
{
    private readonly InnerTubeClient _api;
    private readonly LocalLibrary _local;
    private readonly LibraryCache _cache;
    private ContinuationToken? _next;

    public PlaylistInfo Info { get; }

    [ObservableProperty] private bool _hasMore;

    /// <summary>The user's own playlist: songs can be removed and the playlist deleted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveTracks), nameof(CanDelete))]
    private bool _isEditable;

    public bool CanRemoveTracks => IsEditable && (!Info.IsBuiltIn || Info.BrowseId == LocalLibrary.LikedId);
    public bool CanDelete => IsEditable && !Info.IsBuiltIn;
    public bool IsLikedSongs => Info.PlaylistId == "LM";
    public bool IsLocal => LocalLibrary.IsLocalId(Info.BrowseId);

    /// <summary>Owner line in the header; local playlists' library subtitle is just the song count, already shown.</summary>
    public string HeaderSubtitle => IsLocal ? "On this PC" : Info.Subtitle;

    /// <summary>"Local", or "Cached" while showing the saved copy of a YouTube playlist; null for live ones.</summary>
    [ObservableProperty] private string? _tag;
    [ObservableProperty] private Color _headerColor = ArtColor.Fallback;

    public string? TrackCountText => Tracks.Count switch
    {
        0 => null,
        1 when !HasMore => "1 song",
        _ => HasMore ? $"{Tracks.Count}+ songs" : $"{Tracks.Count} songs",
    };

    /// <summary>Header art: the playlist's own, or for a local playlist its first song's.</summary>
    [ObservableProperty] private string? _artUrl;

    public PlaylistViewModel(InnerTubeClient api, PlaybackService playback, PlaylistInfo info, LocalLibrary local, LibraryCache cache)
        : base(playback)
    {
        _api = api;
        _local = local;
        _cache = cache;
        Info = info;
        Tag = info.Tag;
        ArtUrl = info.ThumbnailUrl;
        Tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TrackCountText));
    }

    public async Task LoadAsync()
    {
        _ = LoadHeaderColorAsync();
        if (IsLocal)
        {
            ReloadLocal();
            return;
        }
        if (Info.Source == PlaylistSource.Cached && ShowCached()) return;
        await RunAsync(async () =>
        {
            try
            {
                var page = await _api.GetPlaylistAsync(Info.BrowseId);
                IsEditable = page.IsEditable || Info.IsOwned;
                Tag = null;
                Tracks.Clear();
                AddTracks(page.Tracks);
                if (ArtUrl is null && Tracks.FirstOrDefault()?.ThumbnailUrl is { } art)
                {
                    ArtUrl = art;
                    _ = LoadHeaderColorAsync();
                }
                _next = page.Next;
                HasMore = _next != null;
                // Keep library playlists' songs for offline / signed-out use.
                if (_api.IsLoggedIn && _cache.Playlists.Any(p => p.BrowseId == Info.BrowseId))
                    _cache.SetTracks(Info.BrowseId, page.Tracks);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && ShowCached())
            {
                // Offline: the cached copy is showing instead.
            }
        });
    }

    /// <summary>Shows the cached songs (read-only); false if this playlist was never cached.</summary>
    private bool ShowCached()
    {
        if (_cache.TracksOf(Info.BrowseId) is not { } tracks) return false;
        IsEditable = false;
        Tag = "Cached";
        Tracks.Clear();
        AddTracks(tracks);
        _next = null;
        HasMore = false;
        return true;
    }

    /// <summary>Loads (again) a playlist kept on this PC; called whenever the local library changes.</summary>
    public void ReloadLocal()
    {
        if (!IsLocal) return;
        IsEditable = true;
        Tracks.Clear();
        AddTracks(_local.TracksOf(Info.BrowseId));
        _next = null;
        HasMore = false;
        Error = null;
        var art = Tracks.FirstOrDefault()?.ThumbnailUrl;
        if (art != ArtUrl)
        {
            ArtUrl = art;
            _ = LoadHeaderColorAsync();
        }
    }

    private async Task LoadHeaderColorAsync() => HeaderColor = await ArtColor.GetAsync(ArtUrl);

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_next is not { } token || IsLoading) return;
        await RunAsync(async () =>
        {
            var page = await _api.ContinuePlaylistAsync(token);
            AddTracks(page.Tracks);
            _next = page.Next;
            HasMore = _next != null;
        });
    }

    /// <summary>Album rows carry no art of their own; give them the album cover.</summary>
    private void AddTracks(IReadOnlyList<Track> tracks)
    {
        LibraryActions.Instance?.Observe(tracks);
        foreach (var t in tracks)
        {
            var track = t.ThumbnailUrl is null ? t with { ThumbnailUrl = Info.ThumbnailUrl, Album = t.Album ?? Info.Title } : t;
            if (CanRemoveTracks && (track.SetVideoId is not null || IsLocal))
                track = track with { SourcePlaylistId = Info.PlaylistId, SourcePlaylistTitle = Info.Title };
            Tracks.Add(track);
        }
    }

    partial void OnHasMoreChanged(bool value) => OnPropertyChanged(nameof(TrackCountText));

    [RelayCommand]
    private async Task RemoveFromPlaylistAsync(Track? track)
    {
        if (track is null || !CanRemoveTracks) return;
        await LibraryActions.Instance.RemoveFromPlaylistAsync(track with
        {
            SourcePlaylistId = Info.PlaylistId,
            SourcePlaylistTitle = Info.Title,
        });
    }

    /// <summary>A song was removed from this playlist (here, from the player or the mini player): drop its row.</summary>
    public void OnRemovedFromPlaylist(string playlistId, Track track)
    {
        if (playlistId != Info.PlaylistId) return;
        var row = Tracks.FirstOrDefault(t => t.SetVideoId == track.SetVideoId && t.VideoId == track.VideoId);
        if (row is null) return;
        _removedAt[row.SetVideoId!] = Tracks.IndexOf(row);
        Tracks.Remove(row);
    }

    private readonly Dictionary<string, int> _removedAt = []; // where removed rows were, to undo a failed removal

    /// <summary>The removal failed after all: put the row back.</summary>
    public void OnRemoveFailed(string playlistId, Track track)
    {
        if (playlistId != Info.PlaylistId || track.SetVideoId is null || !_removedAt.Remove(track.SetVideoId, out var index)) return;
        Tracks.Insert(Math.Clamp(index, 0, Tracks.Count), track);
    }

    /// <summary>Keeps the Liked Music page in step with hearts clicked anywhere in the app.</summary>
    public void OnLikeChanged(Track track, bool liked)
    {
        if (!IsLikedSongs) return;
        var existing = Tracks.FirstOrDefault(t => t.VideoId == track.VideoId);
        if (liked && existing is null) Tracks.Insert(0, track);
        else if (!liked && existing is not null) Tracks.Remove(existing);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count == 0) return;
        Playback.Queue.Shuffle = false;
        Playback.PlayList(Tracks.ToList(), 0);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        if (Tracks.Count == 0) return;
        var list = Tracks.ToArray();
        Random.Shared.Shuffle(list);
        Playback.Queue.Shuffle = true;
        Playback.PlayList(list, 0);
    }
}
