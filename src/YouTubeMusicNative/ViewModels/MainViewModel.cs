using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public enum AppPage { Home, Search, Playlist, Artist, Account, NowPlaying }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly InnerTubeClient _api;
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _searchDebounce;
    private readonly List<(AppPage Kind, object Page)> _history = [];
    private int _historyIndex = -1;

    public PlaybackService Playback { get; }
    public HomeViewModel Home { get; }
    public SearchViewModel Search { get; }
    public LibraryViewModel Library { get; }

    /// <summary>Playlists and liked songs kept on this PC (work signed out too).</summary>
    public LocalLibrary Local { get; }

    /// <summary>The saved copy of the YouTube library, shown as "Cached" when signed out or offline.</summary>
    public LibraryCache Cache { get; }
    public QueueViewModel Queue { get; }
    public LoginViewModel Login { get; }
    public LibraryActions Actions { get; }
    public UpdateService Updates { get; }

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private AppPage _currentPageKind;
    [ObservableProperty] private bool _isQueueOpen;
    [ObservableProperty] private string? _currentPlaylistId;
    [ObservableProperty] private string _searchText = "";

    /// <summary>The modal card shown over the app (add to playlist, new playlist, confirm), or null.</summary>
    [ObservableProperty] private DialogViewModel? _dialog;

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex < _history.Count - 1;
    public bool IsLoggedIn => _api.IsLoggedIn;

    /// <summary>Launch with Windows (per-user Run key), coming back as it was left.</summary>
    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            try
            {
                StartupRegistration.Set(value);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                Playback.ShowStatus("Couldn't change the startup setting: " + ex.Message);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>Settings: software rendering to save memory (takes effect the next time the app starts).</summary>
    /// <summary>Settings: smooth animations. The motion itself switches live; GPU drawing at full refresh rate applies on restart.</summary>
    public bool SmoothAnimations
    {
        get => _store.Settings.SmoothAnimations;
        set
        {
            if (_store.Settings.SmoothAnimations == value) return;
            _store.Settings.SmoothAnimations = value;
            _store.Save();
            Views.Motion.Enabled = value;
            OnPropertyChanged();
            if (_store.Settings.LowMemoryRendering)
                Playback.ShowStatus(value ? "Restart the app for the smoothest animations" : "Restart the app to use less memory again");
        }
    }

    public bool LowMemoryRendering
    {
        get => _store.Settings.LowMemoryRendering;
        set
        {
            if (_store.Settings.LowMemoryRendering == value) return;
            _store.Settings.LowMemoryRendering = value;
            _store.Save();
            OnPropertyChanged();
            Playback.ShowStatus("Takes effect the next time the app starts");
        }
    }

    public bool ResumePlaybackOnStart
    {
        get => _store.Settings.ResumePlaybackOnStart;
        set
        {
            _store.Settings.ResumePlaybackOnStart = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Closing the window keeps playing from the tray (settings, on the account page).</summary>
    public bool CloseToTray
    {
        get => _store.Settings.CloseToTray;
        set
        {
            _store.Settings.CloseToTray = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Raised when the user asks for the compact always-on-top player.</summary>
    public event Action? MiniPlayerRequested;

    public MainViewModel(InnerTubeClient api, SettingsStore store, PlaybackService playback)
    {
        _api = api;
        _store = store;
        Updates = new UpdateService(store);
        Playback = playback;
        Home = new HomeViewModel(api, playback, OpenPlaylist, () => Navigate(AppPage.NowPlaying));
        Search = new SearchViewModel(api, playback, store, query =>
        {
            SearchText = query;
            SubmitSearch();
        }, item => OpenPlaylist(item.ToPlaylist()));
        Views.ArtistLinks.OpenArtist = OpenArtist;
        Views.ArtistLinks.OpenAlbum = (id, title, art) => OpenPlaylist(new PlaylistInfo(id, title ?? "Album", "Album", art));
        Local = new LocalLibrary(store.Directory);
        Cache = new LibraryCache(store.Directory);
        Library = new LibraryViewModel(api, Local, Cache, OpenPlaylist);
        Queue = new QueueViewModel(playback);
        Actions = new LibraryActions(api, playback, Local);
        Local.Changed += () =>
        {
            foreach (var page in _history.Select(h => h.Page).OfType<PlaylistViewModel>().Distinct())
                page.ReloadLocal();
        };
        Actions.AddToPlaylistRequested = ShowAddToPlaylist;
        Actions.OpenPlayerRequested = () =>
        {
            RevealRequested?.Invoke();
            if (CurrentPageKind != AppPage.NowPlaying) Navigate(AppPage.NowPlaying);
        };
        Actions.LikeChanged += (track, liked) =>
        {
            foreach (var page in _history.Select(h => h.Page).OfType<PlaylistViewModel>().Distinct())
                page.OnLikeChanged(track, liked);
        };
        Actions.RemovedFromPlaylist += (playlistId, track) =>
        {
            foreach (var page in _history.Select(h => h.Page).OfType<PlaylistViewModel>().Distinct())
                page.OnRemovedFromPlaylist(playlistId, track);
        };
        Actions.RemoveFailed += (playlistId, track) =>
        {
            foreach (var page in _history.Select(h => h.Page).OfType<PlaylistViewModel>().Distinct())
                page.OnRemoveFailed(playlistId, track);
        };
        Login = new LoginViewModel(api, store, playback, OnLoggedInChanged);
        Login.TryRestore();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Search.SearchCommand.Execute(null);
        };

        Connectivity.Instance.Reconnected += ReloadAfterReconnect;

        Navigate(AppPage.Home);
        _ = Library.EnsureLoadedAsync();
        _ = Login.LoadAccountAsync();
        _ = Actions.LoadLikesAsync();
    }

    /// <summary>Back online: load again whatever failed while the connection was down.</summary>
    private void ReloadAfterReconnect()
    {
        if (Home.Error is not null || Home.Sections.Count == 0) Home.RefreshCommand.Execute(null);
        if (IsLoggedIn && !Library.IsLive) Library.RefreshCommand.Execute(null);
        if (IsLoggedIn && Login.Account is null) _ = Login.LoadAccountAsync();
        if (IsLoggedIn) _ = Actions.LoadLikesAsync();
        switch (CurrentPage)
        {
            case PlaylistViewModel { Error: not null } or PlaylistViewModel { Tag: "Cached" } when IsLoggedIn:
                _ = ((PlaylistViewModel)CurrentPage!).LoadAsync();
                break;
            case PlaylistViewModel { Error: not null } playlist:
                _ = playlist.LoadAsync();
                break;
            case ArtistViewModel { Error: not null } artist:
                _ = artist.LoadAsync();
                break;
            case SearchViewModel { Error: not null } search:
                search.SearchCommand.Execute(null);
                break;
        }
    }

    // ---- navigation -------------------------------------------------------------------------

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        object target = page switch
        {
            AppPage.Home => Home,
            AppPage.Search => Search,
            AppPage.Account => Login,
            AppPage.NowPlaying => Queue,
            _ => CurrentPage!,
        };
        Push(page, target);
        if (page == AppPage.Home) _ = Home.EnsureLoadedAsync();
    }

    /// <summary>Shows the Account page and starts the browser sign-in straight away.</summary>
    [RelayCommand]
    private void SignIn()
    {
        Navigate(AppPage.Account);
        if (!Login.IsLoggedIn && !Login.IsWaitingForBrowser) Login.SignInWithBrowserCommand.Execute(null);
    }

    /// <summary>Sidebar "Search": go to the search page and put the cursor in the box.</summary>
    [RelayCommand]
    private void GoToSearch()
    {
        Navigate(AppPage.Search);
        FocusSearchRequested?.Invoke();
    }

    public event Action? FocusSearchRequested;

    /// <summary>A playlist page view model not shown in this window (the mini player's library tab uses it).</summary>
    public PlaylistViewModel CreatePlaylistPage(PlaylistInfo info) => new(_api, Playback, info, Local, Cache);

    public void OpenPlaylist(PlaylistInfo info)
    {
        // Artist cards (home feed, "Fans might also like", search) carry a channel id: open the artist page.
        if (info.BrowseId.StartsWith("UC"))
        {
            OpenArtist(info.BrowseId, info.Title);
            return;
        }
        var vm = CreatePlaylistPage(info);
        Push(AppPage.Playlist, vm);
        _ = vm.LoadAsync();
    }

    /// <summary>The artist's page (from a clicked artist name, an artist card or the search top result).</summary>
    public void OpenArtist(string browseId, string? name)
    {
        RevealRequested?.Invoke(); // clicked in the mini player: bring the full window up to show the page
        if (CurrentPage is ArtistViewModel open && open.BrowseId == browseId) return;
        var vm = new ArtistViewModel(_api, Playback, browseId, name, OpenPlaylist);
        Push(AppPage.Artist, vm);
        _ = vm.LoadAsync();
    }

    /// <summary>A page was opened from the mini player; the app shows the main window for it.</summary>
    public event Action? RevealRequested;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Show(--_historyIndex);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Show(++_historyIndex);

    private void Push(AppPage kind, object page)
    {
        if (_historyIndex >= 0 && ReferenceEquals(_history[_historyIndex].Page, page)) return;
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add((kind, page));
        if (_history.Count > 30) _history.RemoveAt(0); // keep a few pages; old playlist VMs get collected
        Show(_history.Count - 1);
    }

    private void Show(int index)
    {
        _historyIndex = index;
        var (kind, page) = _history[index];
        CurrentPageKind = kind;
        CurrentPage = page;
        CurrentPlaylistId = (page as PlaylistViewModel)?.Info.BrowseId;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value)
    {
        Search.Query = value;
        _searchDebounce.Stop();
        if (string.IsNullOrWhiteSpace(value)) return;
        if (CurrentPageKind != AppPage.Search) Navigate(AppPage.Search);
        _searchDebounce.Start();
    }

    /// <summary>Enter in the search box: search immediately.</summary>
    [RelayCommand]
    private void SubmitSearch()
    {
        _searchDebounce.Stop();
        Navigate(AppPage.Search);
        Search.SearchCommand.Execute(null);
        Search.RememberQuery();
    }

    [RelayCommand]
    private void ToggleNowPlaying()
    {
        if (CurrentPageKind == AppPage.NowPlaying && CanGoBack) GoBack();
        else Navigate(AppPage.NowPlaying);
    }

    [RelayCommand] private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;
    [RelayCommand] private void ShowMiniPlayer() => MiniPlayerRequested?.Invoke();

    private void OnLoggedInChanged()
    {
        Library.Reset();
        Home.Reset();
        Dialog = null;
        OnPropertyChanged(nameof(IsLoggedIn));
        _ = Library.EnsureLoadedAsync();
        _ = Login.LoadAccountAsync();
        _ = Actions.LoadLikesAsync();
        if (_api.IsLoggedIn) Navigate(AppPage.Home);
    }

    // ---- playlists & dialogs ----------------------------------------------------------------

    /// <summary>Settings: forget the saved copy of the YouTube library (local playlists stay).</summary>
    [RelayCommand]
    private void ClearCache()
    {
        Cache.Clear();
        Playback.ShowStatus("Cached library cleared");
    }

    [RelayCommand]
    private void CloseDialog() => Dialog = null;

    [RelayCommand]
    private void CreatePlaylist() => ShowCreatePlaylist(null);

    private void ShowCreatePlaylist(Track? firstSong) =>
        Dialog = new CreatePlaylistDialog(_api, Local, firstSong, () => Dialog = null, (id, title, isLocal) =>
        {
            Playback.ShowStatus(firstSong is null ? $"Created {title}" : $"Created {title} with \"{firstSong.Title}\"");
            if (isLocal)
            {
                // Your Library lists it already (LocalLibrary.Changed).
                OpenPlaylist(Library.Playlists.FirstOrDefault(p => p.BrowseId == id)
                             ?? new PlaylistInfo(id, title, "Playlist", firstSong?.ThumbnailUrl) { Source = PlaylistSource.Local });
                return;
            }
            var info = new PlaylistInfo("VL" + id, title, "Playlist", firstSong?.ThumbnailUrl) { IsOwned = true };
            // YouTube takes a moment to list a new playlist, so show it in the sidebar right away.
            Library.AddRemote(info);
            OpenPlaylist(info);
        });

    private void ShowAddToPlaylist(Track track)
    {
        var dialog = new AddToPlaylistDialog(_api, Local, track, () => Dialog = null, ShowCreatePlaylist, (message, playlist, wasAdded) =>
        {
            Playback.ShowStatus(message);
            if (!wasAdded || playlist.IsLocal) return; // local pages and the library update from LocalLibrary.Changed
            // An open page of that playlist is now stale, and so is its library entry (art, song count).
            foreach (var page in _history.Select(h => h.Page).OfType<PlaylistViewModel>().Distinct())
                if (page.Info.PlaylistId == playlist.PlaylistId) _ = page.LoadAsync();
            Library.OnSongAdded(playlist.PlaylistId, track);
        });
        Dialog = dialog;
        _ = dialog.LoadAsync();
    }

    /// <summary>
    /// Deletes one of the user's playlists (after confirming), or for a saved playlist someone else
    /// owns, removes it from the library. Parameter: a PlaylistInfo (sidebar) or PlaylistViewModel (page).
    /// </summary>
    [RelayCommand]
    private void DeletePlaylist(object? parameter)
    {
        var (info, owned) = parameter switch
        {
            PlaylistViewModel page => (page.Info, page.IsEditable || page.Info.IsOwned),
            PlaylistInfo p => (p, p.IsOwned),
            _ => (null, false),
        };
        if (info is null || info.IsBuiltIn || info.Source == PlaylistSource.Cached) return;

        if (LocalLibrary.IsLocalId(info.BrowseId))
        {
            Dialog = new ConfirmDialog("Delete playlist?",
                $"\"{info.Title}\" will be deleted from this PC. This can't be undone.",
                "Delete", () =>
                {
                    Local.Delete(info.BrowseId);
                    AfterPlaylistRemoved(info, $"Deleted {info.Title}");
                    return Task.CompletedTask;
                }, () => Dialog = null);
            return;
        }

        Dialog = owned
            ? new ConfirmDialog("Delete playlist?",
                $"\"{info.Title}\" will be deleted from YouTube Music for good. This can't be undone.",
                "Delete", async () =>
                {
                    await _api.DeletePlaylistAsync(info.PlaylistId);
                    AfterPlaylistRemoved(info, $"Deleted {info.Title}");
                }, () => Dialog = null)
            : new ConfirmDialog("Remove from your library?",
                $"\"{info.Title}\" will no longer show in Your Library. You can save it again later.",
                "Remove", async () =>
                {
                    await _api.SetPlaylistSavedAsync(info.PlaylistId, saved: false);
                    AfterPlaylistRemoved(info, $"Removed {info.Title} from your library");
                }, () => Dialog = null);
    }

    private void AfterPlaylistRemoved(PlaylistInfo info, string message)
    {
        Playback.ShowStatus(message);
        Library.RemoveRemote(info.BrowseId);
        // Drop the deleted playlist's pages from history so Back can't return to them.
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Page is PlaylistViewModel p && p.Info.BrowseId == info.BrowseId)
            {
                _history.RemoveAt(i);
                if (i <= _historyIndex) _historyIndex--;
            }
        }
        if (_history.Count == 0 || _historyIndex < 0) Navigate(AppPage.Home);
        else Show(Math.Min(_historyIndex, _history.Count - 1));
    }

    // ---- transport --------------------------------------------------------------------------

    [RelayCommand] private void PlayPause() => Playback.TogglePause();
    [RelayCommand] private void Next() => Playback.Next();
    [RelayCommand] private void Previous() => Playback.Previous();
    [RelayCommand] private void ToggleShuffle() => Playback.Queue.Shuffle = !Playback.Queue.Shuffle;
    [RelayCommand] private void CycleRepeat() => Playback.Queue.CycleRepeat();
}
