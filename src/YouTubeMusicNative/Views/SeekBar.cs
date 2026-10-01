using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.Views;

/// <summary>
/// Connects a Slider to the player position: follows playback, and seeks on click, click-and-drag
/// along the track, or knob drag.
/// <para>
/// Mouse handlers are registered with handledEventsToo because Slider (IsMoveToPointEnabled) marks
/// the mouse-down as handled; ordinary XAML handlers never see clicks on the track.
/// </para>
/// </summary>
internal sealed class SeekBar
{
    private static readonly TimeConverter Time = new();

    private readonly Slider _slider;
    private readonly TextBlock? _positionText;
    private PlaybackService? _playback;
    private bool _dragging;

    public SeekBar(Slider slider, TextBlock? positionText)
    {
        _slider = slider;
        _positionText = positionText;
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDown), handledEventsToo: true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnUp), handledEventsToo: true);
        slider.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMove), handledEventsToo: true);
        slider.LostMouseCapture += OnLostCapture;
        slider.ValueChanged += (_, _) => { if (_dragging) ShowTime(_slider.Value); };
    }

    public void Attach(PlaybackService? playback)
    {
        if (_playback is not null) _playback.PropertyChanged -= OnPlaybackChanged;
        _playback = playback;
        if (_playback is not null)
        {
            _playback.PropertyChanged += OnPlaybackChanged;
            Follow();
        }
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackService.Position) or nameof(PlaybackService.Duration)) Follow();
    }

    /// <summary>Mirror the playback position unless the user is holding the bar.</summary>
    private void Follow()
    {
        if (_playback is null || _dragging) return;
        // Max >= 1 so an unknown duration (0) shows an empty bar rather than a full one.
        _slider.Maximum = Math.Max(1, _playback.Duration);
        // Unknown length (e.g. a restored home-feed song before it loads): keep the bar empty, not full.
        _slider.Value = _playback.Duration > 0 ? _playback.Position : 0;
        ShowTime(_playback.Position);
    }

    private void ShowTime(double seconds)
    {
        if (_positionText is not null)
            _positionText.Text = (string)Time.Convert(seconds, typeof(string), null, null!);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_playback?.NowPlaying is null || _playback.Duration <= 0) return;
        _dragging = true;
        // Set the value from the click point ourselves too, rather than trusting the slider's own jump.
        if (Track is { } track && !(track.Thumb?.IsMouseOver ?? false))
            _slider.Value = ValueAt(track, e.GetPosition(track).X);
        // The slider has already jumped the value to the click point. Unless the knob itself was grabbed
        // (it captures the mouse on its own), capture here so the user can keep dragging along the track
        // and the release is seen even outside the slider.
        if (!(Track?.Thumb?.IsMouseOver ?? false)) _slider.CaptureMouse();
        ShowTime(_slider.Value);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !_slider.IsMouseCaptured || Track is not { } track) return;
        _slider.Value = ValueAt(track, e.GetPosition(track).X);
    }

    /// <summary>
    /// Maps an x position on the track to a value. Done by hand because Track.ValueFromPoint is relative
    /// to the knob's last *arranged* position, which is stale right after a click moves it — that made
    /// the first drag step jump far past the cursor.
    /// </summary>
    private double ValueAt(Track track, double x)
    {
        double knob = track.Thumb?.ActualWidth ?? 0;
        double usable = track.ActualWidth - knob;
        if (usable <= 0) return _slider.Value;
        double fraction = Math.Clamp((x - knob / 2) / usable, 0, 1);
        return _slider.Minimum + fraction * (_slider.Maximum - _slider.Minimum);
    }

    private void OnUp(object sender, MouseButtonEventArgs e) => Commit();

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        // Capture lost without a release we saw (e.g. Alt+Tab mid-drag): still apply where the knob is.
        if (_dragging && Mouse.LeftButton == MouseButtonState.Released) Commit();
    }

    private void Commit()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_slider.IsMouseCaptured) _slider.ReleaseMouseCapture();
        _playback?.Seek(_slider.Value);
    }

    private Track? Track => _slider.Template?.FindName("PART_Track", _slider) as Track;
}
