using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using YouTubeMusicNative.Services;
using YouTubeMusicNative.ViewModels;
using YouTubeMusicNative.Views;

namespace YouTubeMusicNative;

/// <summary>
/// Small always-on-top player. Drag anywhere to move; double-click the art for the full window.
/// Dropped against the left or right screen edge it docks there: it slides off screen leaving a slim tab,
/// slides back out while the pointer is over it, and peeks out briefly when the song changes.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private enum Dock { None, Left, Right }

    private const double TabWidth = 52;     // visible handle while tucked (a square cover tile)
    private const double TabHeight = 52;
    private const double SnapDistance = 14; // how close to the edge counts as "dropped on the edge"
    private const double CompactHeight = 136;
    private const double DrawerHeight = 380;

    private readonly SettingsStore _store;
    private readonly MainViewModel _vm;
    private readonly SeekBar _seekBar;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _revealTimer;
    private readonly DispatcherTimer _drawerHideTimer;  // auto-hide the drawer ~2 s after the pointer leaves
    private readonly DispatcherTimer _drawerShowTimer;  // ...and bring it back shortly after it returns
    private DispatcherTimer? _resize;
    private bool _drawerOpen;
    private DispatcherTimer? _slide;
    private readonly MiniPanelViewModel _panel;
    private Dock _dock;
    private bool _tucked;
    private double _shiftedUp; // how far the window moved up to fit the open drawer on screen
    private double _fullHeight, _fullTop; // size/position to restore when sliding out of the tucked handle

    /// <summary>The user wants the full window back.</summary>
    public event Action? ExpandRequested;

    public MiniPlayerWindow(MainViewModel vm, SettingsStore store)
    {
        _vm = vm;
        _store = store;
        DataContext = vm;
        InitializeComponent();

        if (store.Settings.MiniLeft is { } left && store.Settings.MiniTop is { } top && MainWindow.IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 24;
        }

        _panel = new MiniPanelViewModel(vm, store);
        Drawer.DataContext = _panel;
        _panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MiniPanelViewModel.IsExpanded))
            {
                SetDrawer(_panel.IsExpanded, animate: true);
                if (_panel.IsExpanded && _panel.IsSearchTab) SearchInput.Focus();
            }
            // Switching to Search puts the cursor in the box, ready to type.
            if (e.PropertyName == nameof(MiniPanelViewModel.Tab) && _panel.IsSearchTab)
                Dispatcher.BeginInvoke(() => { Activate(); SearchInput.Focus(); }, DispatcherPriority.Input);
        };
        SetDrawer(_panel.IsExpanded, animate: false, initial: true);

        _drawerHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _drawerHideTimer.Tick += (_, _) =>
        {
            _drawerHideTimer.Stop();
            if (!IsMouseOver && !SearchInput.IsKeyboardFocused && !_tucked) SetDrawer(false, animate: true);
        };
        _drawerShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _drawerShowTimer.Tick += (_, _) =>
        {
            _drawerShowTimer.Stop();
            if (IsMouseOver && _panel.IsExpanded && !_tucked) SetDrawer(true, animate: true);
        };
        // Opened expanded with the pointer elsewhere: tuck the drawer away after the usual delay.
        Loaded += (_, _) => { if (_drawerOpen) _drawerHideTimer.Start(); };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            // Don't hide while the user is typing a search.
            if (_dock != Dock.None && !IsMouseOver && !SearchInput.IsKeyboardFocused) Tuck(animate: true);
        };

        SourceInitialized += (_, _) =>
        {
            MainWindow.ApplyWindowFrame(new WindowInteropHelper(this).Handle);
            // Re-dock where it was last time (tucked away).
            if (Enum.TryParse<Dock>(store.Settings.MiniDock, out var saved) && saved != Dock.None)
                SetDock(saved, tuck: true, animate: false);
        };

        // Buttons and the seek bar handle their own clicks, so this only fires on empty space (and the tab).
        MouseLeftButtonDown += OnDragStart;
        // A short hover delay so brushing past the screen edge doesn't pop the player out.
        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer.Stop();
            if (_dock != Dock.None && _tucked && IsMouseOver) Reveal();
        };
        MouseEnter += (_, _) =>
        {
            _hideTimer.Stop();
            _drawerHideTimer.Stop();
            if (!_tucked && _panel.IsExpanded && !_drawerOpen) _drawerShowTimer.Start();
            if (_dock != Dock.None && _tucked) _revealTimer.Start();
        };
        MouseLeave += (_, _) =>
        {
            _revealTimer.Stop();
            _drawerShowTimer.Stop();
            if (_drawerOpen && !_tucked) _drawerHideTimer.Start();
            if (_dock != Dock.None) _hideTimer.Start();
        };
        LocationChanged += (_, _) =>
        {
            if (_tucked) return; // the handle's position isn't the player's
            if (_dock == Dock.None) _store.Settings.MiniLeft = Left;
            _store.Settings.MiniTop = Top;
        };

        _seekBar = new SeekBar(SeekSlider, PositionText);
        _seekBar.Attach(vm.Playback);
        vm.Playback.PropertyChanged += OnPlaybackChanged;
        Closed += (_, _) =>
        {
            _seekBar.Attach(null);
            vm.Playback.PropertyChanged -= OnPlaybackChanged;
            _hideTimer.Stop();
            _revealTimer.Stop();
            _drawerHideTimer.Stop();
            _drawerShowTimer.Stop();
            _slide?.Stop();
            _resize?.Stop();
        };
    }

    // ---- docking ----------------------------------------------------------------------------

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _slide?.Stop();
        _hideTimer.Stop();
        _revealTimer.Stop();

        // Grabbing the tucked tile: turn back into the full player right under the pointer (cover centred on
        // it) so the user drags the real thing, not a 52px strip.
        if (_tucked)
        {
            var grab = e.GetPosition(this);
            _tucked = false;
            ShowTab(false);
            Height = _fullHeight;
            Left += grab.X - 68;
            Top += grab.Y - 68;
        }

        DragMove(); // returns when the mouse is released

        // Where was it dropped?
        var area = WorkArea();
        if (Left <= area.Left + SnapDistance) SetDock(Dock.Left, tuck: true, animate: true);
        else if (Left + Width >= area.Right - SnapDistance) SetDock(Dock.Right, tuck: true, animate: true);
        else SetDock(Dock.None, tuck: false, animate: false);
    }

    private void SetDock(Dock dock, bool tuck, bool animate)
    {
        _dock = dock;
        _store.Settings.MiniDock = dock == Dock.None ? null : dock.ToString();
        if (dock == Dock.None)
        {
            ShowTab(false);
            if (_tucked) Height = _fullHeight;
            _tucked = false;
            _store.Settings.MiniLeft = Left;
            return;
        }

        // The tab sits on the side that stays on screen; its accent faces the screen's interior.
        Tab.HorizontalAlignment = dock == Dock.Right ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        var area = WorkArea();
        Top = Math.Clamp(Top, area.Top, area.Bottom - Height);
        if (tuck) Tuck(animate);
        else Reveal();
    }

    private void Tuck(bool animate)
    {
        var area = WorkArea();
        double target = _dock == Dock.Right ? area.Right - TabWidth : area.Left - Width + TabWidth;
        if (!_tucked)
        {
            _drawerHideTimer.Stop();
            _drawerShowTimer.Stop();
            if (_drawerOpen) SetDrawer(false, animate: false);
            // Shrink to a short handle centred on where the player was.
            _fullHeight = Height;
            _fullTop = Top;
            _tucked = true;
            Height = TabHeight;
            Top = Math.Clamp(_fullTop + (_fullHeight - TabHeight) / 2, area.Top, area.Bottom - TabHeight);
        }
        ShowTab(true);
        SlideTo(target, animate);
    }

    private void Reveal()
    {
        var area = WorkArea();
        double target = _dock == Dock.Right ? area.Right - Width : area.Left;
        if (_tucked)
        {
            _tucked = false;
            Height = _fullHeight;
            Top = Math.Clamp(_fullTop, area.Top, Math.Max(area.Top, area.Bottom - _fullHeight));
        }
        ShowTab(false);
        SlideTo(target, animate: true);
        if (_panel.IsExpanded && !_drawerOpen) _drawerShowTimer.Start();
    }

    private void ShowTab(bool show) => Tab.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Peek out for a moment when the song changes while tucked away.</summary>
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlaybackService.NowPlaying) || _dock == Dock.None || !_tucked || !IsVisible) return;
        Reveal();
        _hideTimer.Interval = TimeSpan.FromSeconds(2.5);
        _hideTimer.Start();
        // Back to the normal hover delay afterwards.
        _hideTimer.Tick += ResetHideDelay;
    }

    private void ResetHideDelay(object? sender, EventArgs e)
    {
        _hideTimer.Tick -= ResetHideDelay;
        _hideTimer.Interval = TimeSpan.FromMilliseconds(650);
    }

    /// <summary>Eased horizontal slide (~180 ms). The timer only runs during the slide.</summary>
    private void SlideTo(double target, bool animate)
    {
        _slide?.Stop();
        if (!animate || Math.Abs(Left - target) < 1)
        {
            Left = target;
            return;
        }
        double from = Left;
        var start = Environment.TickCount64;
        const double duration = 180;
        _slide = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _slide.Tick += (_, _) =>
        {
            double p = Math.Min(1, (Environment.TickCount64 - start) / duration);
            double eased = 1 - Math.Pow(1 - p, 3);
            Left = from + (target - from) * eased;
            if (p >= 1) _slide!.Stop();
        };
        _slide.Start();
    }

    /// <summary>Work area (screen minus taskbar) of the monitor the window is on, in WPF units.</summary>
    private Rect WorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
            return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = info.Work;
        return new Rect(w.Left / dpi.DpiScaleX, w.Top / dpi.DpiScaleY,
            (w.Right - w.Left) / dpi.DpiScaleX, (w.Bottom - w.Top) / dpi.DpiScaleY);
    }

    // ---- drawer -----------------------------------------------------------------------------

    /// <summary>
    /// Shows/hides the drawer by growing the window down (or up, if that would run off the screen) and back,
    /// with a short eased animation.
    /// </summary>
    private void SetDrawer(bool open, bool animate, bool initial = false)
    {
        if (open == _drawerOpen && !initial) return;
        _drawerOpen = open;
        double height = open ? CompactHeight + DrawerHeight : CompactHeight;

        var area = initial ? SystemParameters.WorkArea : WorkArea();
        double top = Top;
        if (open)
        {
            double overflow = Top + height - area.Bottom;
            _shiftedUp = overflow > 0 ? Math.Min(overflow, Top - area.Top) : 0;
            top = Top - _shiftedUp;
            Drawer.Visibility = Visibility.Visible;
        }
        else
        {
            top = Top + _shiftedUp;
            _shiftedUp = 0;
        }
        ResizeTo(height, top, animate, done: () => { if (!_drawerOpen) Drawer.Visibility = Visibility.Collapsed; });
    }

    /// <summary>Eased height (and top) change; the timer only runs while resizing.</summary>
    private void ResizeTo(double height, double top, bool animate, Action done)
    {
        _resize?.Stop();
        if (!animate)
        {
            Height = height;
            Top = top;
            done();
            return;
        }
        double fromH = Height, fromT = Top;
        var start = Environment.TickCount64;
        const double duration = 220;
        _resize = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _resize.Tick += (_, _) =>
        {
            double p = Math.Min(1, (Environment.TickCount64 - start) / duration);
            double eased = 1 - Math.Pow(1 - p, 3);
            Height = fromH + (height - fromH) * eased;
            Top = fromT + (top - fromT) * eased;
            if (p < 1) return;
            _resize!.Stop();
            done();
        };
        _resize.Start();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        _vm.SearchText = "";
        SearchInput.Focus();
    }

    // ---- buttons ----------------------------------------------------------------------------

    private void OnArtMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ExpandRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void OnExpandClick(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---- interop ----------------------------------------------------------------------------

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
