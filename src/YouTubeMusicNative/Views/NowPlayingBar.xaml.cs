using System.Windows;
using System.Windows.Controls;
using YouTubeMusicNative.ViewModels;

namespace YouTubeMusicNative.Views;

public partial class NowPlayingBar : UserControl
{
    private readonly SeekBar _seekBar;
    private double _volumeBeforeMute = 70;

    public NowPlayingBar()
    {
        InitializeComponent();
        _seekBar = new SeekBar(SeekSlider, PositionText);
        DataContextChanged += (_, e) => _seekBar.Attach((e.NewValue as MainViewModel)?.Playback);
        Unloaded += (_, _) => _seekBar.Attach(null);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if ((DataContext as MainViewModel)?.Playback is not { } playback) return;
        if (playback.Volume > 0)
        {
            _volumeBeforeMute = playback.Volume;
            playback.Volume = 0;
        }
        else
        {
            playback.Volume = _volumeBeforeMute;
        }
    }

    /// <summary>
    /// Clicking the bar itself (anywhere that isn't a button or slider) opens / closes the Now Playing screen,
    /// like Spotify's mobile mini bar.
    /// </summary>
    private void OnBarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as System.Windows.DependencyObject; d is not null && d != this;
             d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? System.Windows.LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.RangeBase) return;
        }
        if (DataContext is ViewModels.MainViewModel { Playback.NowPlaying: not null } vm)
            vm.ToggleNowPlayingCommand.Execute(null);
    }
}
