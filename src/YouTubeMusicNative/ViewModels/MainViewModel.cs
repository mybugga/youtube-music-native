using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public enum AppPage { Home, Search, Playlist, Account, NowPlaying }

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
        });
        Library = new LibraryViewModel(api, OpenPlaylist);
        Queue = new QueueViewModel(playback);
        Actions = new LibraryActions(api, playback);
        Actions.AddToPlaylistRequested = ShowAddToPlaylist;
        Actions.LikeChanged += (track, liked) =>
        {
            foreach (var page in _history.Select(h => h.Page).OfType<PlaylistViewModel>().Distinct())
                page.OnLikeChanged(track, liked);
        };
        Login = new LoginViewModel(api, store, playback, OnLoggedInChanged);
        Login.TryRestore();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Search.SearchCommand.Execute(null);
        };

        Navigate(AppPage.Home);
        _ = Library.EnsureLoadedAsync();
        _ = Login.LoadAccountAsync();
        _ = Actions.LoadLikesAsync();
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
    public PlaylistViewModel CreatePlaylistPage(PlaylistInfo info) => new(_api, Playback, info);

    public void OpenPlaylist(PlaylistInfo info)
    {
        var vm = new PlaylistViewModel(_api, Playback, info);
        Push(AppPage.Playlist, vm);
        _ = vm.LoadAsync();
    }

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

    [RelayCommand]
    private void CloseDialog() => Dialog = null;

    [RelayCommand]
    private void CreatePlaylist() => ShowCreatePlaylist(null);

    private void ShowCreatePlaylist(Track? firstSong) =>
        Dialog = new CreatePlaylistDialog(_api, firstSong, () => Dialog = null, async (id, title) =>
        {
            Playback.ShowStatus(firstSong is null ? $"Created {title}" : $"Created {title} with \"{firstSong.Title}\"");
            var info = new PlaylistInfo("VL" + id, title, "Playlist", null) { IsOwned = true };
            // YouTube takes a moment to list a new playlist, so show it in the sidebar right away.
            if (Library.Playlists.All(p => p.BrowseId != info.BrowseId))
                Library.Playlists.Insert(Math.Min(1, Library.Playlists.Count), info);
            OpenPlaylist(info);
        });

    private void ShowAddToPlaylist(Track track)
    {
        var dialog = new AddToPlaylistDialog(_api, track, () => Dialog = null, ShowCreatePlaylist, message =>
        {
            Playback.ShowStatus(message);
            // An open playlist page that just got the song is now stale.
            foreach (var page in _history.Select(h => h.Page).OfType<PlaylistViewModel>().Distinct())
                if (message.EndsWith(page.Info.Title)) _ = page.LoadAsync();
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
        if (info is null || info.IsBuiltIn) return;

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
        var match = Library.Playlists.FirstOrDefault(p => p.BrowseId == info.BrowseId);
        if (match is not null) Library.Playlists.Remove(match);
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
