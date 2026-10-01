using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace YouTubeMusicNative.Services;

/// <summary>Notification-area icon: left-click restores the window, right-click shows playback controls.</summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly PlaybackService _playback;
    private readonly MenuItem _playPause;

    public event Action? ShowRequested;
    public event Action? MiniPlayerRequested;
    public event Action? ExitRequested;

    public TrayService(PlaybackService playback)
    {
        _playback = playback;

        _playPause = new MenuItem { Header = "Play" };
        _playPause.Click += (_, _) => _playback.TogglePause();
        var next = new MenuItem { Header = "Next" };
        next.Click += (_, _) => _playback.Next();
        var previous = new MenuItem { Header = "Previous" };
        previous.Click += (_, _) => _playback.Previous();
        var show = new MenuItem { Header = "Show YouTube Music Native", FontWeight = FontWeights.SemiBold };
        show.Click += (_, _) => ShowRequested?.Invoke();
        var mini = new MenuItem { Header = "Mini player" };
        mini.Click += (_, _) => MiniPlayerRequested?.Invoke();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(show);
        menu.Items.Add(mini);
        menu.Items.Add(new Separator());
        menu.Items.Add(_playPause);
        menu.Items.Add(next);
        menu.Items.Add(previous);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ToolTipText = "YouTube Music Native",
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => ShowRequested?.Invoke();
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _playback.PropertyChanged += OnPlaybackChanged;
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackService.IsPaused) or nameof(PlaybackService.NowPlaying))
        {
            _playPause.Header = _playback.IsPaused ? "Play" : "Pause";
            var t = _playback.NowPlaying;
            var tip = t is null ? "YouTube Music Native" : $"{t.Title} — {t.Artists}";
            _icon.ToolTipText = tip.Length > 120 ? tip[..120] : tip; // shell limit is 128 chars
        }
    }

    public void Dispose()
    {
        _playback.PropertyChanged -= OnPlaybackChanged;
        _icon.Dispose();
    }
}
