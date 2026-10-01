using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
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
    private EventHandler? _resize;
    private bool _drawerOpen;
    private EventHandler? _slide;
    private Action? _finishSlide; // jumps a running tuck / reveal move to its end
    private Window? _tabWindow;   // the small cover tile shown at the screen edge while tucked
    private readonly MiniPanelViewModel _panel;
    private Dock _dock;
    private bool _tucked;
    private ContextMenu? _openMenu; // a right-click menu of this window is open: the pointer is on it, not "away"

    /// <summary>
    /// Context menus and the Add-to-playlist popup are separate windows, so moving onto them counts as leaving the
    /// mini player. While one is open the player must not tuck away or fold its drawer.
    /// </summary>
    private bool IsHeld => _openMenu is { IsOpen: true } || _vm.Dialog is not null;

    static MiniPlayerWindow()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((s, _) => MenuOpened?.Invoke((ContextMenu)s)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
            new RoutedEventHandler((s, _) => MenuClosed?.Invoke((ContextMenu)s)));
    }

    private static event Action<ContextMenu>? MenuOpened;
    private static event Action<ContextMenu>? MenuClosed;

    private void OnMenuOpened(ContextMenu menu)
    {
        if (menu.PlacementTarget is DependencyObject target && GetWindow(target) == this) _openMenu = menu;
    }

    private void OnMenuClosed(ContextMenu menu)
    {
        if (menu != _openMenu) return;
        _openMenu = null;
        ResumeAutoHide();
    }

    private bool _drawerOpenedForDialog;

    /// <summary>
    /// A dialog (Add to playlist…) shows over the whole mini player, so open the drawer for room while it's up and
    /// fold it again afterwards if it was closed before.
    /// </summary>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Dialog)) return;
        if (_vm.Dialog is not null)
        {
            _hideTimer.Stop();
            _drawerHideTimer.Stop();
            if (_tucked) Reveal();
            if (!_drawerOpen)
            {
                _drawerOpenedForDialog = true;
                SetDrawer(true, animate: true);
            }
            Activate();
        }
        else
        {
            if (_drawerOpenedForDialog && !_panel.IsExpanded) SetDrawer(false, animate: true);
            _drawerOpenedForDialog = false;
            ResumeAutoHide();
        }
    }

    private void OnDialogBackdropClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _vm.CloseDialogCommand.Execute(null);
    }

    private void OnDialogCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>A menu or popup closed: if the pointer isn't back on the player, hide as if it just left.</summary>
    public void ResumeAutoHide() => Dispatcher.BeginInvoke(() =>
    {
        if (IsMouseOver || IsHeld) return;
        if (_drawerOpen && !_tucked) _drawerHideTimer.Start();
        if (_dock != Dock.None) _hideTimer.Start();
    }, DispatcherPriority.Background);
    private double _shiftedUp; // how far the window moved up to fit the open drawer on screen

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
            if (!IsMouseOver && !IsHeld && !SearchInput.IsKeyboardFocused && !_tucked) SetDrawer(false, animate: true);
        };
        _drawerShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _drawerShowTimer.Tick += (_, _) =>
        {
            _drawerShowTimer.Stop();
            if (_slide is not null) { _drawerShowTimer.Start(); return; } // still sliding out
            if (IsMouseOver && _panel.IsExpanded && !_tucked) SetDrawer(true, animate: true);
        };
        // Opened expanded with the pointer elsewhere: tuck the drawer away after the usual delay.
        Loaded += (_, _) => { if (_drawerOpen) _drawerHideTimer.Start(); };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            // Don't hide while the user is typing a search.
            if (_dock != Dock.None && !IsMouseOver && !IsHeld && !SearchInput.IsKeyboardFocused) Tuck(animate: true);
        };

        CreateTabWindow();
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
            if (_dock != Dock.None && _tucked && _tabWindow!.IsMouseOver) Reveal();
        };
        MouseEnter += (_, _) =>
        {
            _hideTimer.Stop();
            _drawerHideTimer.Stop();
            if (!_tucked && _panel.IsExpanded && !_drawerOpen) _drawerShowTimer.Start();
        };
        MouseLeave += (_, _) =>
        {
            _drawerShowTimer.Stop();
            if (_drawerOpen && !_tucked) _drawerHideTimer.Start();
            if (_dock != Dock.None) _hideTimer.Start();
        };
        LocationChanged += (_, _) =>
        {
            if (_tucked || _slide is not null) return; // parked off screen / still sliding
            if (_dock == Dock.None) _store.Settings.MiniLeft = Left;
            _store.Settings.MiniTop = Top;
        };

        MenuOpened += OnMenuOpened;
        MenuClosed += OnMenuClosed;
        vm.PropertyChanged += OnMainChanged;

        _seekBar = new SeekBar(SeekSlider, PositionText);
        _seekBar.Attach(vm.Playback);
        vm.Playback.PropertyChanged += OnPlaybackChanged;
        Closed += (_, _) =>
        {
            _seekBar.Attach(null);
            MenuOpened -= OnMenuOpened;
            MenuClosed -= OnMenuClosed;
            vm.PropertyChanged -= OnMainChanged;
            vm.Playback.PropertyChanged -= OnPlaybackChanged;
            _hideTimer.Stop();
            _revealTimer.Stop();
            _drawerHideTimer.Stop();
            _drawerShowTimer.Stop();
            StopTween(ref _slide);
            StopTween(ref _resize);
            _tabWindow?.Close();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _tabWindow?.Hide();
            else if (_tucked) ShowTab(true);
        };
    }

    // ---- docking ----------------------------------------------------------------------------

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        CompleteSlide();
        _hideTimer.Stop();
        _revealTimer.Stop();

        DragMove(); // returns when the mouse is released
        AfterDrag();
    }

    /// <summary>Grabbing the tab: the full player comes back right under the pointer (cover centred on it) to be dragged.</summary>
    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        CompleteSlide();
        _hideTimer.Stop();
        _revealTimer.Stop();
        var pointer = _tabWindow!.PointToScreen(e.GetPosition(_tabWindow));
        var dpi = VisualTreeHelper.GetDpi(this);
        _tucked = false;
        ShowTab(false);
        Left = pointer.X / dpi.DpiScaleX - 68;
        Top = pointer.Y / dpi.DpiScaleY - 68;
        SetCloaked(false);
        Activate();
        DragMove();
        AfterDrag();
    }

    private void AfterDrag()
    {
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
            SetCloaked(false);
            _tucked = false;
            _store.Settings.MiniLeft = Left;
            return;
        }

        var area = WorkArea();
        Top = Math.Clamp(Top, area.Top, area.Bottom - Height);
        if (tuck) Tuck(animate);
        else Reveal();
    }

    // Tucked: the player slides fully off the screen edge and is hidden there (cloaked, so it stays drawn and up to
    // date), and a separate small tab window sits at the edge. Revealing hides the tab and slides the ready-drawn player
    // back out. The player window itself is never resized or cut, so nothing has to be redrawn on the way.
    private void Tuck(bool animate)
    {
        var area = WorkArea();
        double parked = _dock == Dock.Right ? area.Right : area.Left - Width;
        if (!_tucked)
        {
            _drawerHideTimer.Stop();
            _drawerShowTimer.Stop();
            if (_drawerOpen) SetDrawer(false, animate: false);
            CompleteSlide();
            _tucked = true;
        }
        // The tab only appears once the player has slid away (not on top of it while it's still going).
        SlideTo(parked, animate ? 200 : 0, Motion.EaseOut, done: () =>
        {
            if (!_tucked) return;
            SetCloaked(true);
            ShowTab(true);
        });
    }

    private void Reveal()
    {
        var area = WorkArea();
        double target = _dock == Dock.Right ? area.Right - Width : area.Left;
        CompleteSlide();
        _tucked = false;
        ShowTab(false);
        SetCloaked(false);
        SlideTo(target, 340, Spring, done: () =>
        {
            // Came out without the pointer on it (or it moved off during the slide): tuck again after the usual delay.
            if (!_tucked && _dock != Dock.None && !PointerOverWindow() && !IsHeld) _hideTimer.Start();
        });
        if (_panel.IsExpanded && !_drawerOpen) _drawerShowTimer.Start();
    }

    /// <summary>Hides / shows the window without unmapping it: a cloaked window keeps being drawn, just not shown.</summary>
    private void SetCloaked(bool cloaked)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
    }

    private bool PointerOverWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && GetCursorPos(out var p) && GetWindowRect(hwnd, out var r)
               && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    /// <summary>The cover tile at the screen edge: the Tab element from the XAML, moved into its own small window.</summary>
    private void CreateTabWindow()
    {
        ((Panel)Tab.Parent).Children.Remove(Tab);
        Tab.Visibility = Visibility.Visible;
        Tab.HorizontalAlignment = HorizontalAlignment.Stretch;
        Tab.VerticalAlignment = VerticalAlignment.Stretch;
        // A see-through window so it can fade in; it draws its own rounded corners and hairline border.
        Tab.Clip = new RectangleGeometry(new Rect(0, 0, TabWidth, TabHeight), 8, 8);
        var frame = new Grid();
        frame.Children.Add(Tab);
        frame.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), IsHitTestVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        });
        _tabWindow = new Window
        {
            Width = TabWidth, Height = TabHeight, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false,
            DataContext = DataContext, Content = frame, Title = "YouTube Music Native mini player tab",
        };
        _tabWindow.MouseEnter += (_, _) =>
        {
            _hideTimer.Stop();
            if (_tucked) _revealTimer.Start();
        };
        _tabWindow.MouseLeave += (_, _) => _revealTimer.Stop();
        _tabWindow.MouseLeftButtonDown += OnTabMouseDown;
    }

    private void ShowTab(bool show)
    {
        if (_tabWindow is null) return;
        if (!show)
        {
            _tabWindow.BeginAnimation(OpacityProperty, null);
            _tabWindow.Hide();
            return;
        }
        if (!IsVisible)
        {
            // Docked at startup, before this window is on screen: show the tab once it is.
            Dispatcher.BeginInvoke(() => { if (_tucked && IsVisible) ShowTab(true); }, DispatcherPriority.Loaded);
            return;
        }
        var area = WorkArea();
        _tabWindow.Left = _dock == Dock.Right ? area.Right - TabWidth : area.Left;
        _tabWindow.Top = Math.Clamp(Top + (CompactHeight - TabHeight) / 2, area.Top, area.Bottom - TabHeight);
        _tabWindow.Opacity = 0;
        _tabWindow.Show();
        _tabWindow.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>Ease-out that overshoots slightly and settles back (a small bounce).</summary>
    private static double Spring(double p)
    {
        const double c = 1.35;
        return 1 + (c + 1) * Math.Pow(p - 1, 3) + c * Math.Pow(p - 1, 2);
    }

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

    /// <summary>Horizontal slide of the window (tuck / reveal), one move per rendered frame.</summary>
    private void SlideTo(double target, double ms, Func<double, double> easing, Action? done = null)
    {
        StopTween(ref _slide);
        _finishSlide = null;
        void Finish()
        {
            Left = target;
            done?.Invoke();
        }
        double from = CurrentBounds()?.Left ?? Left;
        if (ms <= 0 || Math.Abs(from - target) < 1)
        {
            Finish();
            return;
        }
        _finishSlide = () =>
        {
            StopTween(ref _slide);
            _finishSlide = null;
            Finish();
        };
        _slide = Tween(ms, easing, eased => Left = from + (target - from) * eased, () =>
        {
            _slide = null;
            _finishSlide = null;
            Finish();
        });
    }

    /// <summary>Ends a running tuck / reveal move at its destination (before resizing or dragging the window).</summary>
    private void CompleteSlide() => _finishSlide?.Invoke();

    /// <summary>Where the window really is right now (mid-animation the WPF properties can lag behind).</summary>
    private (double Left, double Top, double Height)? CurrentBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        return (r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    /// <summary>
    /// Calls <paramref name="step"/> once per rendered frame, in step with the display, with eased progress 0..1.
    /// Returns the frame handler so the tween can be stopped.
    /// </summary>
    private static EventHandler Tween(double ms, Func<double, double> easing, Action<double> step, Action done)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            double p = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            step(easing(p));
            if (p < 1) return;
            CompositionTarget.Rendering -= frame;
            done();
        };
        CompositionTarget.Rendering += frame;
        return frame;
    }

    private static void StopTween(ref EventHandler? tween)
    {
        if (tween is not null) CompositionTarget.Rendering -= tween;
        tween = null;
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
        CompleteSlide(); // measure from where the window ends up, not from mid-slide
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

    /// <summary>Eased height (and top) change, one frame at a time.</summary>
    private void ResizeTo(double height, double top, bool animate, Action done)
    {
        StopTween(ref _resize);
        CompleteSlide();
        if (!animate)
        {
            Height = height;
            Top = top;
            done();
            return;
        }
        var (fromL, fromT, fromH) = CurrentBounds() ?? (Left, Top, Height);
        _resize = Tween(Motion.Duration(220), Motion.EaseOut,
            eased => SetBounds(fromL, fromT + (top - fromT) * eased, fromH + (height - fromH) * eased),
            () =>
            {
                _resize = null;
                Height = height;
                Top = top;
                done();
            });
    }

    /// <summary>
    /// Moves the top edge and resizes in one window move. Setting Top and then Height moves the window twice per
    /// frame, which made the bottom edge wobble while the drawer opened upwards.
    /// </summary>
    private void SetBounds(double left, double top, double height)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Left = left;
            Top = top;
            Height = height;
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(left * dpi.DpiScaleX), (int)Math.Round(top * dpi.DpiScaleY),
            (int)Math.Round(Width * dpi.DpiScaleX), (int)Math.Round(height * dpi.DpiScaleY), SwpNoZOrder | SwpNoActivate);
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

    private double _volumeBeforeMute = 70;

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        var playback = _vm.Playback;
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

    /// <summary>Wheel over the speaker / slider: 5% a notch.</summary>
    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        _vm.Playback.Volume = Math.Clamp(Math.Round(_vm.Playback.Volume / 5) * 5 + Math.Sign(e.Delta) * 5, 0, 100);
    }

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

    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
