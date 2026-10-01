using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.Views;

/// <summary>
/// The app logo (three rounded red bars) drawn live: while music plays the bars bounce like an equalizer,
/// each to its own random beat; paused, they settle back into the logo. Idle while the window is minimized.
/// </summary>
public sealed class LiveLogo : Canvas
{
    // Bar geometry measured from Assets/logo.png (256 px), as fractions of the logo's size: x, width, top, height.
    private static readonly (double X, double W, double Top, double H)[] Bars =
    [
        (21 / 256.0, 66 / 256.0, 97 / 256.0, 97 / 256.0),
        (95 / 256.0, 66 / 256.0, 57 / 256.0, 158 / 256.0),
        (170 / 256.0, 65 / 256.0, 10 / 256.0, 236 / 256.0),
    ];

    public static readonly DependencyProperty PlaybackProperty = DependencyProperty.Register(
        nameof(Playback), typeof(PlaybackService), typeof(LiveLogo), new PropertyMetadata(null, OnPlaybackChanged));

    public PlaybackService? Playback
    {
        get => (PlaybackService?)GetValue(PlaybackProperty);
        set => SetValue(PlaybackProperty, value);
    }

    private readonly ScaleTransform[] _scales = new ScaleTransform[Bars.Length];
    private readonly Random _random = new();
    private bool _dancing;
    private Window? _window;

    public LiveLogo()
    {
        var fill = new SolidColorBrush(Color.FromRgb(0xC4, 0x01, 0x0B));
        fill.Freeze();
        for (int i = 0; i < Bars.Length; i++)
        {
            _scales[i] = new ScaleTransform(1, 1);
            var bar = new Rectangle { Fill = fill, RenderTransform = _scales[i] };
            Children.Add(bar);
        }
        SizeChanged += (_, _) => LayoutBars();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += OnWindowStateChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
            _window = null;
            Settle();
        };
        IsVisibleChanged += (_, _) => Update();
    }

    private void LayoutBars()
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        for (int i = 0; i < Bars.Length; i++)
        {
            var (x, w, top, h) = Bars[i];
            var bar = (Rectangle)Children[i];
            bar.Width = w * size;
            bar.Height = h * size;
            bar.RadiusX = bar.RadiusY = w * size / 2;
            SetLeft(bar, x * size);
            SetTop(bar, top * size);
            // Bounce from the bar's bottom, like an equalizer.
            bar.RenderTransformOrigin = new Point(0.5, 1);
        }
    }

    private static void OnPlaybackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var logo = (LiveLogo)d;
        if (e.OldValue is PlaybackService old) old.PropertyChanged -= logo.OnPlaybackPropertyChanged;
        if (e.NewValue is PlaybackService now) now.PropertyChanged += logo.OnPlaybackPropertyChanged;
        logo.Update();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackService.IsPaused) or nameof(PlaybackService.IsBuffering) or nameof(PlaybackService.NowPlaying))
            Dispatcher.BeginInvoke(Update);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => Update();

    private bool ShouldDance =>
        Playback is { NowPlaying: not null, IsPaused: false, IsBuffering: false }
        && IsVisible && _window?.WindowState != WindowState.Minimized;

    private void Update()
    {
        bool dance = ShouldDance;
        if (dance == _dancing) return;
        _dancing = dance;
        if (dance)
            for (int i = 0; i < _scales.Length; i++) Step(i);
        else
            Settle();
    }

    /// <summary>One beat for one bar: ease to a random height, then pick the next when it gets there.</summary>
    private void Step(int i)
    {
        if (!_dancing) return;
        double to = 0.35 + _random.NextDouble() * 0.65;
        var beat = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140 + _random.Next(200)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        beat.Completed += (_, _) => Step(i);
        _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, beat);
    }

    /// <summary>Back to the still logo.</summary>
    private void Settle()
    {
        _dancing = false;
        foreach (var scale in _scales)
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
