using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

/// <summary>Backs the queue side panel and the full Now Playing page.</summary>
public sealed partial class QueueViewModel(PlaybackService playback)
{
    public PlaybackService Playback { get; } = playback;
    public QueueService Queue => Playback.Queue;

    [RelayCommand]
    private void Play(Track? track)
    {
        if (track is null) return;
        Playback.PlayQueueIndex(Queue.Items.IndexOf(track));
    }

    /// <summary>Play button on a row's art: plays the song, or pauses / resumes it if it's the current one.</summary>
    [RelayCommand]
    private void ArtPlay(Track? track)
    {
        if (track is null) return;
        if (ReferenceEquals(track, Queue.Current)) Playback.TogglePause();
        else Play(track);
    }

    [RelayCommand]
    private void Remove(Track? track)
    {
        if (track is not null) Queue.RemoveAt(Queue.Items.IndexOf(track));
    }

    [RelayCommand]
    private void ClearUpcoming() => Queue.ClearUpcoming();

    [RelayCommand]
    private void ToggleAutoplay() => Playback.Autoplay = !Playback.Autoplay;
}
