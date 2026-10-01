using System.Net.Http;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;

namespace YouTubeMusicNative.ViewModels;

/// <summary>The "Your Library" side panel: the signed-in user's playlists.</summary>
public sealed partial class LibraryViewModel(InnerTubeClient api, Action<PlaylistInfo> openPlaylist) : ObservableObject
{
    public static readonly PlaylistInfo LikedSongs = new("VLLM", "Liked Music", "Auto playlist", null);

    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    private bool _loaded;

    public bool IsLoggedIn => api.IsLoggedIn;

    public async Task EnsureLoadedAsync()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        if (_loaded || !api.IsLoggedIn) return;
        await RefreshAsync();
    }

    public void Reset()
    {
        _loaded = false;
        Playlists.Clear();
        OnPropertyChanged(nameof(IsLoggedIn));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading || !api.IsLoggedIn) return;
        IsLoading = true;
        Error = null;
        try
        {
            var listsTask = api.GetLibraryPlaylistsAsync();
            var ownedTask = OwnedPlaylistIdsAsync();
            var lists = await listsTask;
            var owned = await ownedTask;
            Playlists.Clear();
            Playlists.Add(lists.FirstOrDefault(p => p.BrowseId == LikedSongs.BrowseId) ?? LikedSongs);
            foreach (var p in lists.Where(p => p.BrowseId != LikedSongs.BrowseId))
                Playlists.Add(p with { IsOwned = owned.Contains(p.PlaylistId) });
            _loaded = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Ids of playlists the user can edit (their own), to offer Delete vs Remove from library.</summary>
    private async Task<HashSet<string>> OwnedPlaylistIdsAsync()
    {
        try
        {
            return (await api.GetEditablePlaylistsAsync()).Select(p => p.PlaylistId).ToHashSet();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    [RelayCommand]
    private void Open(PlaylistInfo? playlist)
    {
        if (playlist is not null) openPlaylist(playlist);
    }
}
