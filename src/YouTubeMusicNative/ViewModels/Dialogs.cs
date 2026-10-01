using System.Net.Http;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;

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

/// <summary>"Add to playlist": the user's editable playlists, plus "New playlist".</summary>
public sealed partial class AddToPlaylistDialog(
    InnerTubeClient api, Track track, Action close, Action<Track> newPlaylist, Action<string> added)
    : DialogViewModel(close)
{
    public Track Track { get; } = track;
    public ObservableCollection<PlaylistOption> Playlists { get; } = [];

    [ObservableProperty] private bool _isEmpty;

    public async Task LoadAsync() => await TryAsync(async () =>
    {
        var lists = await api.GetEditablePlaylistsAsync(Track.VideoId);
        // Liked Music is handled by the heart; Episodes for Later is for podcasts.
        foreach (var p in lists.Where(p => p.PlaylistId is not ("LM" or "SE"))) Playlists.Add(p);
        IsEmpty = Playlists.Count == 0;
    });

    [RelayCommand]
    private async Task AddAsync(PlaylistOption? playlist)
    {
        if (playlist is null) return;
        bool wasAdded = false;
        if (await TryAsync(async () => wasAdded = await api.AddToPlaylistAsync(playlist.PlaylistId, Track.VideoId)))
        {
            added(wasAdded ? $"Added to {playlist.Title}" : $"Already in {playlist.Title}");
            Close();
        }
    }

    [RelayCommand]
    private void NewPlaylist() => newPlaylist(Track);
}

/// <summary>"New playlist": title, description, privacy; optionally starts with a song.</summary>
public sealed partial class CreatePlaylistDialog(
    InnerTubeClient api, Track? firstSong, Action close, Action<string, string> created)
    : DialogViewModel(close)
{
    public Track? FirstSong { get; } = firstSong;
    public IReadOnlyList<PlaylistPrivacy> PrivacyOptions { get; } = Enum.GetValues<PlaylistPrivacy>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _title = "";

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private PlaylistPrivacy _privacy = PlaylistPrivacy.Private;

    private bool CanCreate => !string.IsNullOrWhiteSpace(Title);

    [RelayCommand]
    private void SetPrivacy(PlaylistPrivacy privacy) => Privacy = privacy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        string id = "";
        var title = Title.Trim();
        if (await TryAsync(async () => id = await api.CreatePlaylistAsync(
                title, Description.Trim(), Privacy, FirstSong is null ? null : [FirstSong.VideoId])))
        {
            Close();
            created(id, title);
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
