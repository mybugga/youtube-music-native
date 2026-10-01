using System.ComponentModel;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage.Streams;

namespace YouTubeMusicNative.Services;

/// <summary>
/// Windows System Media Transport Controls: hardware media keys (work while unfocused or in the tray),
/// the volume-flyout / lock-screen overlay, and the track title/art shown there.
/// </summary>
public sealed class MediaControlsService : IDisposable
{
    private readonly SystemMediaTransportControls _smtc;
    private readonly PlaybackService _playback;
    private readonly Dispatcher _ui;

    public MediaControlsService(IntPtr hwnd, PlaybackService playback)
    {
        _playback = playback;
        _ui = Dispatcher.CurrentDispatcher;

        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;

        _playback.PropertyChanged += OnPlaybackChanged;
        _playback.TimelineReset += UpdateTimeline;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // Raised on a WinRT thread pool thread.
        _ui.BeginInvoke(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play: _playback.Play(); break;
                case SystemMediaTransportControlsButton.Pause: _playback.Pause(); break;
                case SystemMediaTransportControlsButton.Next: _playback.Next(); break;
                case SystemMediaTransportControlsButton.Previous: _playback.Previous(); break;
            }
        });
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        _ui.BeginInvoke(() => _playback.Seek(args.RequestedPlaybackPosition.TotalSeconds));

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackService.NowPlaying):
                UpdateDisplay();
                break;
            case nameof(PlaybackService.IsPaused):
                _smtc.PlaybackStatus = _playback.NowPlaying is null ? MediaPlaybackStatus.Stopped
                    : _playback.IsPaused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
                break;
            case nameof(PlaybackService.Duration):
                UpdateTimeline();
                break;
        }
    }

    private void UpdateDisplay()
    {
        var track = _playback.NowPlaying;
        var updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        if (track is null)
        {
            updater.Update();
            return;
        }

        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.Artists;
        updater.MusicProperties.AlbumTitle = track.Album ?? "";
        var art = Api.Parsers.JsonNav.ResizeThumb(track.ThumbnailUrl, 300);
        if (art is not null && Uri.TryCreate(art, UriKind.Absolute, out var uri))
            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
        updater.Update();
    }

    private void UpdateTimeline()
    {
        if (_playback.Duration <= 0) return;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = TimeSpan.FromSeconds(_playback.Position),
            MaxSeekTime = TimeSpan.FromSeconds(_playback.Duration),
            EndTime = TimeSpan.FromSeconds(_playback.Duration),
        });
    }

    public void Dispose()
    {
        _playback.PropertyChanged -= OnPlaybackChanged;
        _playback.TimelineReset -= UpdateTimeline;
        _smtc.ButtonPressed -= OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
        _smtc.IsEnabled = false;
    }
}
