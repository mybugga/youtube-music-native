using System.Net.Http;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

/// <summary>Base for the small modal cards shown over the shell (MainViewModel.Dialog).</summary>
public abstract partial class DialogViewModel(Action close) : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    protected void Close() => close();

    /// <summary>Runs an API call with busy/error handling; returns false if it failed.</summary>
    protected async Task<bool> TryAsync(Func<Task> action)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Error = null;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Error = ex is TaskCanceledException ? "Request timed out." : ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>"Add to playlist": the user's editable YouTube playlists (when signed in) and the local ones, plus "New playlist".</summary>
public sealed partial class AddToPlaylistDialog(
    InnerTubeClient api, LocalLibrary local, Track track, Action close, Action<Track> newPlaylist, Action<string, PlaylistOption, bool> added)
    : DialogViewModel(close)
{
    public Track Track { get; } = track;
    public ObservableCollection<PlaylistOption> Playlists { get; } = [];

    [ObservableProperty] private bool _isEmpty;

    public async Task LoadAsync()
    {
        if (api.IsLoggedIn)
        {
            await TryAsync(async () =>
            {
                var lists = await api.GetEditablePlaylistsAsync(Track.VideoId);
                // Liked Music is handled by the heart; Episodes for Later is for podcasts.
                foreach (var p in lists.Where(p => p.PlaylistId is not ("LM" or "SE"))) Playlists.Add(p);
            });
        }
        foreach (var p in local.Playlists)
            Playlists.Add(new PlaylistOption(p.Id, p.Title, p.Tracks.Count == 1 ? "1 song" : $"{p.Tracks.Count} songs",
                p.Tracks.FirstOrDefault()?.ThumbnailUrl) { IsLocal = true });
        IsEmpty = Playlists.Count == 0;
    }

    [RelayCommand]
    private async Task AddAsync(PlaylistOption? playlist)
    {
        if (playlist is null) return;
        if (playlist.IsLocal)
        {
            bool isNew = local.Add(playlist.PlaylistId, Track);
            added(isNew ? $"Added to {playlist.Title}" : $"Already in {playlist.Title}", playlist, isNew);
            Close();
            return;
        }
        bool wasAdded = false;
        if (await TryAsync(async () => wasAdded = await api.AddToPlaylistAsync(playlist.PlaylistId, Track.VideoId)))
        {
            added(wasAdded ? $"Added to {playlist.Title}" : $"Already in {playlist.Title}", playlist, wasAdded);
            Close();
        }
    }

    [RelayCommand]
    private void NewPlaylist() => newPlaylist(Track);
}

/// <summary>How the playlist dialog was opened: a new empty playlist, an imported file, or a local playlist going to YouTube.</summary>
public enum PlaylistDialogMode { Create, Import, Upload }

/// <summary>
/// "New playlist": where (on this PC or on YouTube Music, when signed in), title, description, privacy;
/// optionally starts with a song, or with a whole list of songs (Import / Upload to YouTube Music).
/// <c>created(id, title, isLocal)</c>.
/// </summary>
public sealed partial class CreatePlaylistDialog(
    InnerTubeClient api, LocalLibrary local, Track? firstSong, Action close, Action<string, string, bool> created,
    PlaylistDialogMode mode = PlaylistDialogMode.Create, IReadOnlyList<Track>? songs = null, string initialTitle = "", string initialDescription = "")
    : DialogViewModel(close)
{
    public Track? FirstSong { get; } = firstSong;

    /// <summary>The songs the playlist starts with when importing / uploading.</summary>
    public IReadOnlyList<Track> Songs { get; } = songs ?? (firstSong is null ? [] : [firstSong]);

    public string Heading { get; } = mode switch
    {
        PlaylistDialogMode.Import => "Import playlist",
        PlaylistDialogMode.Upload => "Upload to YouTube Music",
        _ => "New playlist",
    };

    public string ConfirmText { get; } = mode switch
    {
        PlaylistDialogMode.Import => "Import",
        PlaylistDialogMode.Upload => "Upload",
        _ => "Create",
    };

    /// <summary>"42 songs" under the heading when importing / uploading.</summary>
    public string? SongsText { get; } = mode == PlaylistDialogMode.Create ? null
        : (songs?.Count ?? 0) == 1 ? "1 song" : $"{songs?.Count ?? 0} songs";

    /// <summary>Signed out, playlists can only be local, so the choice isn't shown; an upload always goes to YouTube.</summary>
    public bool CanChooseDestination { get; } = api.IsLoggedIn && mode != PlaylistDialogMode.Upload;

    /// <summary>The "sign in to create on YouTube Music too" hint (not when uploading).</summary>
    public bool ShowSignInHint { get; } = !api.IsLoggedIn;

    [ObservableProperty] private bool _isLocal = !api.IsLoggedIn;

    [RelayCommand]
    private void SetDestination(string where) => IsLocal = where == "Local" || !api.IsLoggedIn;

    public IReadOnlyList<PlaylistPrivacy> PrivacyOptions { get; } = Enum.GetValues<PlaylistPrivacy>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _title = initialTitle;

    [ObservableProperty] private string _description = initialDescription;
    [ObservableProperty] private PlaylistPrivacy _privacy = PlaylistPrivacy.Private;

    private bool CanCreate => !string.IsNullOrWhiteSpace(Title);

    [RelayCommand]
    private void SetPrivacy(PlaylistPrivacy privacy) => Privacy = privacy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        string id = "";
        var title = Title.Trim();
        if (IsLocal)
        {
            id = local.Create(local.UniqueTitle(title), Description.Trim(), Songs).Id;
            Close();
            created(id, title, true);
            return;
        }
        if (await TryAsync(async () => id = await api.CreatePlaylistAsync(
                title, Description.Trim(), Privacy, Songs.Select(t => t.VideoId))))
        {
            Close();
            created(id, title, false);
        }
    }
}

/// <summary>A yes/no confirmation for destructive actions (deleting a playlist).</summary>
public sealed partial class ConfirmDialog(string title, string message, string confirmText, Func<Task> confirm, Action close)
    : DialogViewModel(close)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (await TryAsync(confirm)) Close();
    }
}
