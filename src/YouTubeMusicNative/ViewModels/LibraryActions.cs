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
    private HashSet<string> _liked = [];
    private int _loadGeneration;

    /// <summary>Bumped whenever the liked set changes; bindings include it so hearts refresh.</summary>
    [ObservableProperty] private int _likesVersion;
    [ObservableProperty] private bool _isLoggedIn;

    /// <summary>(track, nowLiked) after a successful like/unlike.</summary>
    public event Action<Track, bool>? LikeChanged;

    /// <summary>Set by the shell: opens the "Add to playlist" dialog for a song.</summary>
    public Action<Track>? AddToPlaylistRequested { get; set; }

    public LibraryActions(InnerTubeClient api, PlaybackService playback)
    {
        _api = api;
        _playback = playback;
        Instance = this;
    }

    public bool IsLiked(string? videoId) => videoId is not null && _liked.Contains(videoId);

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

    [RelayCommand]
    private async Task ToggleLikeAsync(Track? track)
    {
        if (track is null || !_api.IsLoggedIn) return;
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
    private void AddToPlaylist(Track? track)
    {
        if (track is not null && _api.IsLoggedIn) AddToPlaylistRequested?.Invoke(track);
    }

    private void SetLiked(string videoId, bool liked)
    {
        if (liked ? _liked.Add(videoId) : _liked.Remove(videoId)) LikesVersion++;
    }
}
