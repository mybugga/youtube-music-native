using System.Net.Http;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

/// <summary>
/// The "Your Library" side panel. Signed in: the YouTube playlists (kept in the cache as they load), then the
/// playlists on this PC. Signed out (or offline): the local liked songs and playlists, then the cached copy of the
/// YouTube library, if there is one.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    public static readonly PlaylistInfo LikedSongs = new("VLLM", "Liked Music", "Auto playlist", null);

    private readonly InnerTubeClient _api;
    private readonly LocalLibrary _local;
    private readonly LibraryCache _cache;
    private readonly Action<PlaylistInfo> _openPlaylist;
    private List<PlaylistInfo>? _remote; // the live YouTube list, once loaded this session
    private readonly HashSet<string> _prefetched = [];
    private readonly HashSet<string> _justCreated = []; // made in this session, maybe not listed by YouTube yet
    private bool _loaded;

    public LibraryViewModel(InnerTubeClient api, LocalLibrary local, LibraryCache cache, Action<PlaylistInfo> openPlaylist)
    {
        _api = api;
        _local = local;
        _cache = cache;
        _openPlaylist = openPlaylist;
        _local.Changed += Rebuild;
        _cache.Cleared += Rebuild;
        Rebuild();
    }

    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;

    public bool IsLoggedIn => _api.IsLoggedIn;

    /// <summary>The YouTube playlists were loaded this session (not just the cached copy).</summary>
    public bool IsLive => _remote is not null;

    public async Task EnsureLoadedAsync()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        if (!_api.IsLoggedIn)
        {
            Rebuild();
            return;
        }
        if (!_loaded) await RefreshAsync();
    }

    public void Reset()
    {
        _loaded = false;
        _remote = null;
        _prefetched.Clear();
        Error = null;
        Rebuild();
        OnPropertyChanged(nameof(IsLoggedIn));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading || !_api.IsLoggedIn) return;
        IsLoading = true;
        Error = null;
        try
        {
            var listsTask = _api.GetLibraryPlaylistsAsync();
            var ownedTask = OwnedPlaylistIdsAsync();
            var lists = await listsTask;
            var owned = await ownedTask;
            var remote = new List<PlaylistInfo> { lists.FirstOrDefault(p => p.BrowseId == LikedSongs.BrowseId) ?? LikedSongs };
            remote.AddRange(lists.Where(p => p.BrowseId != LikedSongs.BrowseId).Select(p => p with { IsOwned = owned.Contains(p.PlaylistId) }));
            // Playlists made here that YouTube doesn't list yet stay until it does.
            _justCreated.RemoveWhere(id => remote.Any(p => p.BrowseId == id));
            foreach (var mine in _remote?.Where(p => _justCreated.Contains(p.BrowseId)).Reverse() ?? [])
                remote.Insert(Math.Min(1, remote.Count), mine);
            _remote = remote;
            _cache.SetPlaylists(remote);
            _loaded = true;
            Rebuild();
            _ = PrefetchAsync(remote);
        }
        catch (Exception ex)
        {
            // Offline or YouTube trouble: the cached copy (if any) stays listed, so only say so when there's nothing.
            if (_cache.IsEmpty) Error = ex.Message;
            Rebuild();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Adds a just-created YouTube playlist right away (YouTube takes a moment to list it).</summary>
    public void AddRemote(PlaylistInfo info)
    {
        if (_remote is null || _remote.Any(p => p.BrowseId == info.BrowseId)) return;
        _remote.Insert(Math.Min(1, _remote.Count), info);
        _justCreated.Add(info.BrowseId);
        Rebuild();
    }

    /// <summary>
    /// A song went into a YouTube playlist: show its art on the entry right away if it had none (a new playlist),
    /// then reload the library shortly after, once YouTube has caught up, for the real art and song count.
    /// </summary>
    public void OnSongAdded(string playlistId, Track track)
    {
        int i = _remote?.FindIndex(p => p.PlaylistId == playlistId) ?? -1;
        if (i >= 0 && _remote![i].ThumbnailUrl is null && track.ThumbnailUrl is not null)
        {
            _remote[i] = _remote[i] with { ThumbnailUrl = track.ThumbnailUrl };
            Rebuild();
        }
        _refreshSoon?.Stop();
        _refreshSoon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _refreshSoon.Tick += (_, _) =>
        {
            _refreshSoon?.Stop();
            _refreshSoon = null;
            RefreshCommand.Execute(null);
        };
        _refreshSoon.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _refreshSoon;

    public void RemoveRemote(string browseId)
    {
        _justCreated.Remove(browseId);
        if (_remote?.RemoveAll(p => p.BrowseId == browseId) > 0) Rebuild();
    }

    /// <summary>Re-creates the list from the live / cached YouTube library and the local one.</summary>
    public void Rebuild()
    {
        var youtube = _remote ?? _cache.Playlists.Select(p => p with { Source = PlaylistSource.Cached }).ToList();
        var local = new List<PlaylistInfo>();
        // The local liked songs: always offered when signed out (that's where hearts go); signed in, only if used.
        if (!_api.IsLoggedIn || _local.Liked.Count > 0)
            local.Add(new PlaylistInfo(LocalLibrary.LikedId, "Liked Songs", SongCount(_local.Liked.Count),
                null) { Source = PlaylistSource.Local });
        foreach (var p in _local.Playlists)
            local.Add(new PlaylistInfo(p.Id, p.Title, SongCount(p.Tracks.Count), p.Tracks.FirstOrDefault()?.ThumbnailUrl)
            { Source = PlaylistSource.Local });

        Playlists.Clear();
        foreach (var p in _api.IsLoggedIn ? youtube.Concat(local) : local.Concat(youtube)) Playlists.Add(p);
    }

    private static string SongCount(int n) => n == 1 ? "1 song" : $"{n} songs";

    /// <summary>
    /// Saves the songs of each library playlist (first page) in the background, so the whole library is still there
    /// after signing out or while offline. Gentle: one playlist at a time.
    /// </summary>
    private async Task PrefetchAsync(IReadOnlyList<PlaylistInfo> playlists)
    {
        foreach (var p in playlists.Where(p => p.PlaylistId != "SE").Take(50))
        {
            if (!_api.IsLoggedIn || _remote is null) return;
            if (!_prefetched.Add(p.BrowseId)) continue;
            try
            {
                var page = await _api.GetPlaylistAsync(p.BrowseId);
                _cache.SetTracks(p.BrowseId, page.Tracks);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return; // offline or rate-limited: try again next time
            }
            await Task.Delay(400);
        }
    }

    /// <summary>Ids of playlists the user can edit (their own), to offer Delete vs Remove from library.</summary>
    private async Task<HashSet<string>> OwnedPlaylistIdsAsync()
    {
        try
        {
            return (await _api.GetEditablePlaylistsAsync()).Select(p => p.PlaylistId).ToHashSet();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    [RelayCommand]
    private void Open(PlaylistInfo? playlist)
    {
        if (playlist is not null) _openPlaylist(playlist);
    }
}
