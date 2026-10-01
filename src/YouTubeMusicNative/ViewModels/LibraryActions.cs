using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

/// <summary>
/// Account actions available on any song, wherever it's shown (track rows, menus, the player bar):
/// like / unlike and add to playlist. Also tracks which songs are liked so hearts render correctly.
/// One instance, reachable from XAML as <c>{x:Static vm:LibraryActions.Instance}</c> because context
/// menus and shared row templates live outside any page's data context.
/// </summary>
public sealed partial class LibraryActions : ObservableObject
{
    public static LibraryActions Instance { get; private set; } = null!;

    private readonly InnerTubeClient _api;
    private readonly PlaybackService _playback;
    private readonly LocalLibrary _local;
    private HashSet<string> _liked = [];
    private int _loadGeneration;

    /// <summary>Bumped whenever the liked set changes; bindings include it so hearts refresh.</summary>
    [ObservableProperty] private int _likesVersion;
    [ObservableProperty] private bool _isLoggedIn;

    /// <summary>(track, nowLiked) after a successful like/unlike.</summary>
    public event Action<Track, bool>? LikeChanged;

    /// <summary>(playlistId, track) once a song is removed from a playlist (optimistically; see RemoveFailed).</summary>
    public event Action<string, Track>? RemovedFromPlaylist;

    /// <summary>(playlistId, track) when YouTube refused a removal, so pages can put the row back.</summary>
    public event Action<string, Track>? RemoveFailed;

    /// <summary>Set by the shell: shows the big Now Playing view in the full window (from the mini player too).</summary>
    public Action? OpenPlayerRequested { get; set; }

    /// <summary>Set by the shell: opens the "Add to playlist" dialog for a song.</summary>
    public Action<Track>? AddToPlaylistRequested { get; set; }

    public LibraryActions(InnerTubeClient api, PlaybackService playback, LocalLibrary local)
    {
        _api = api;
        _playback = playback;
        _local = local;
        Instance = this;
    }

    /// <summary>Signed in, hearts are the YouTube likes; signed out, the liked songs kept on this PC.</summary>
    public bool IsLiked(string? videoId) =>
        videoId is not null && (_api.IsLoggedIn ? _liked.Contains(videoId) : _local.IsLiked(videoId));

    /// <summary>Loads the liked-songs set in the background (call after sign-in).</summary>
    public async Task LoadLikesAsync()
    {
        IsLoggedIn = _api.IsLoggedIn;
        int generation = ++_loadGeneration;
        if (!_api.IsLoggedIn)
        {
            _liked = [];
            LikesVersion++;
            return;
        }
        try
        {
            var ids = await _api.GetLikedVideoIdsAsync();
            if (generation != _loadGeneration) return; // signed out/in again meanwhile
            ids.UnionWith(_liked); // keep likes made while this was loading
            _liked = ids;
            LikesVersion++;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Hearts just start empty; rows that carry a like status still fill in via Observe.
        }
    }

    /// <summary>Takes like status from rows that include it (playlist pages), keeping hearts accurate.</summary>
    public void Observe(IEnumerable<Track> tracks)
    {
        bool changed = false;
        foreach (var t in tracks)
        {
            if (t.Liked == true) changed |= _liked.Add(t.VideoId);
            else if (t.Liked == false) changed |= _liked.Remove(t.VideoId);
        }
        if (changed) LikesVersion++;
    }

    /// <summary>Menu commands take a song row (Track) or a song card from the home feed (MediaItem).</summary>
    private static Track? AsTrack(object? item) => item switch
    {
        Track t => t,
        MediaItem { Kind: ItemKind.Song, VideoId: not null } m => m.ToTrack(),
        _ => null,
    };

    [RelayCommand]
    private async Task ToggleLikeAsync(object? item)
    {
        if (AsTrack(item) is not { } track) return;
        if (!_api.IsLoggedIn)
        {
            bool liked = _local.ToggleLike(track);
            LikesVersion++;
            _playback.ShowStatus(liked ? "Added to Liked Songs (on this PC)" : "Removed from Liked Songs");
            return;
        }
        bool like = !_liked.Contains(track.VideoId);

        // Optimistic: flip the heart now, roll back if YouTube refuses.
        SetLiked(track.VideoId, like);
        try
        {
            await _api.RateSongAsync(track.VideoId, like);
            LikeChanged?.Invoke(track, like);
            _playback.ShowStatus(like ? "Added to Liked Music" : "Removed from Liked Music");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetLiked(track.VideoId, !like);
            _playback.ShowStatus("Couldn't update your likes: " + ex.Message);
        }
    }

    [RelayCommand]
    private void AddToPlaylist(object? item)
    {
        if (AsTrack(item) is { } track) AddToPlaylistRequested?.Invoke(track);
    }

    /// <summary>
    /// "Open in player": the song in the big Now Playing view. Not playing yet: it starts (from its place in the
    /// queue when it's queued, otherwise on its own).
    /// </summary>
    [RelayCommand]
    private void OpenInPlayer(object? item)
    {
        if (AsTrack(item) is not { } track) return;
        if (track.VideoId != _playback.NowPlaying?.VideoId)
        {
            int index = -1;
            for (int i = 0; i < _playback.Queue.Items.Count; i++)
                if (ReferenceEquals(_playback.Queue.Items[i], track)) index = i;
            if (index >= 0) _playback.PlayQueueIndex(index);
            else _playback.PlayList([track], 0);
        }
        OpenPlayerRequested?.Invoke();
    }

    [RelayCommand]
    private void PlaySong(object? item)
    {
        if (AsTrack(item) is { } track) _playback.PlayList([track], 0);
    }

    [RelayCommand]
    private void PlayNext(object? item)
    {
        if (AsTrack(item) is not { } track) return;
        _playback.Queue.AddNext(track);
        _playback.ShowStatus($"\"{track.Title}\" will play next");
    }

    [RelayCommand]
    private void AddToQueue(object? item)
    {
        if (AsTrack(item) is not { } track) return;
        _playback.Queue.AddToEnd(track);
        _playback.ShowStatus($"Added \"{track.Title}\" to queue");
    }

    /// <summary>Removes a song from the playlist it was opened from (from a row, the player bar or the mini player).</summary>
    [RelayCommand]
    public async Task RemoveFromPlaylistAsync(Track? track)
    {
        if (track?.SourcePlaylistId is not { } playlistId) return;
        var title = track.SourcePlaylistTitle ?? "the playlist";
        if (LocalLibrary.IsLocalId(playlistId))
        {
            _local.Remove(playlistId, track.VideoId); // open pages of it reload from LocalLibrary.Changed
            _playback.ShowStatus($"Removed from {title}");
            return;
        }
        if (track.SetVideoId is null || !_api.IsLoggedIn) return;
        RemovedFromPlaylist?.Invoke(playlistId, track); // optimistic: rows disappear now
        try
        {
            await _api.RemoveFromPlaylistAsync(playlistId, track);
            _playback.ShowStatus($"Removed from {title}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            RemoveFailed?.Invoke(playlistId, track);
            _playback.ShowStatus("Couldn't remove the song: " + ex.Message);
        }
    }

    [RelayCommand]
    private void StartRadio(object? item)
    {
        if (AsTrack(item) is not { } track) return;
        _playback.Autoplay = true;
        _playback.PlayList([track], 0);
    }

    [RelayCommand]
    private void CopyLink(object? item)
    {
        if (AsTrack(item) is not { } track) return;
        try
        {
            System.Windows.Clipboard.SetText($"https://music.youtube.com/watch?v={track.VideoId}");
            _playback.ShowStatus("Link copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _playback.ShowStatus("Couldn't copy the link (the clipboard is busy)");
        }
    }

    private void SetLiked(string videoId, bool liked)
    {
        if (liked ? _liked.Add(videoId) : _liked.Remove(videoId)) LikesVersion++;
    }
}
