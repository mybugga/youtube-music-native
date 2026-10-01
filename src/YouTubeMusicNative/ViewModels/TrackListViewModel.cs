using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

/// <summary>Base for pages that show a list of tracks: shared play / play next / add to queue commands.</summary>
public abstract partial class TrackListViewModel : ObservableObject
{
    protected TrackListViewModel(PlaybackService playback)
    {
        Playback = playback;
        // The page header is the first "item" of the list so it scrolls away with the tracks
        // while the list stays virtualized.
        ListItems = new CompositeCollection { this, new CollectionContainer { Collection = Tracks } };
    }

    public PlaybackService Playback { get; }

    public ObservableCollection<Track> Tracks { get; } = [];

    /// <summary>Header (this view model) followed by the tracks.</summary>
    public CompositeCollection ListItems { get; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;

    /// <summary>Double-click / Enter / row play button.</summary>
    [RelayCommand]
    protected virtual void Play(Track? track)
    {
        if (track is null) return;
        int index = Tracks.IndexOf(track);
        Playback.PlayList(Tracks.ToList(), Math.Max(index, 0));
    }

    /// <summary>Play button on a row's art: plays the song, or pauses / resumes it if it is the current one.</summary>
    [RelayCommand]
    private void ArtPlay(Track? track)
    {
        if (track is null) return;
        if (track.VideoId == Playback.NowPlaying?.VideoId) Playback.TogglePause();
        else Play(track);
    }

    /// <summary>"Open in player": the big Now Playing view; a song that isn't playing starts, with the rest of this list.</summary>
    [RelayCommand]
    private void OpenInPlayer(Track? track)
    {
        if (track is null) return;
        if (track.VideoId != Playback.NowPlaying?.VideoId) Play(track);
        LibraryActions.Instance?.OpenPlayerRequested?.Invoke();
    }

    [RelayCommand]
    private void PlayNext(Track? track)
    {
        if (track is null) return;
        Playback.Queue.AddNext(track);
        Playback.ShowStatus($"\"{track.Title}\" will play next");
    }

    [RelayCommand]
    private void AddToQueue(Track? track)
    {
        if (track is null) return;
        Playback.Queue.AddToEnd(track);
        Playback.ShowStatus($"Added \"{track.Title}\" to queue");
    }

    [RelayCommand]
    private void StartRadio(Track? track)
    {
        if (track is null) return;
        Playback.Autoplay = true;
        Playback.PlayList([track], 0);
    }

    /// <summary>Runs a load operation with IsLoading/Error handling.</summary>
    protected async Task RunAsync(Func<Task> action)
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            await action();
        }
        catch (OperationCanceledException ex) when (ex.InnerException is not TimeoutException)
        {
            // Superseded by a newer request; nothing to report.
        }
        catch (Exception ex)
        {
            Error = ex is TaskCanceledException ? "Request timed out." : ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
