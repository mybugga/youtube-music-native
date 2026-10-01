using System.ComponentModel;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using YouTubeMusicNative.Services;
using YouTubeMusicNative.ViewModels;
using YouTubeMusicNative.Views;

namespace YouTubeMusicNative;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SettingsStore _store;

    /// <summary>Set by App when the user chooses Exit (tray menu or close with close-to-tray off).</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm, SettingsStore store)
    {
        _vm = vm;
        _store = store;
        DataContext = vm;
        InitializeComponent();

        var s = store.Settings;
        Width = Math.Max(s.WindowWidth, MinWidth);
        Height = Math.Max(s.WindowHeight, MinHeight);
        if (s.WindowLeft is { } left && s.WindowTop is { } top && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        SourceInitialized += (_, _) =>
        {
            ThumbnailConverter.DpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ApplyWindowFrame();
        };
        DpiChanged += (_, e) => ThumbnailConverter.DpiScale = e.NewDpi.DpiScaleX;
        StateChanged += (_, _) => FitMaximized();
        PreviewKeyDown += OnPreviewKeyDown;
        BuildContent();
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public void ShowFromTray()
    {
        if (Content is null) BuildContent();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void BuildContent() => Content = new ShellView();

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();
        if (!AllowClose && _store.Settings.CloseToTray)
        {
            e.Cancel = true;
            HideAndRelease();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Hides the window and throws away its whole visual tree and thumbnail cache. Audio keeps
    /// playing; the UI is rebuilt from the view models when the window is shown again.
    /// Used for close-to-tray and when switching to the mini player.
    /// </summary>
    public void HideAndRelease()
    {
        SaveBounds();
        Hide();
        Content = null;
        ThumbnailConverter.Clear();

        Dispatcher.BeginInvoke(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // Let Windows reclaim the pages the UI was using; they're re-faulted only if the window comes back.
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ---- caption buttons --------------------------------------------------------------------

    private void OnMinimize(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);
    private void OnRestore(object sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();

    /// <summary>With custom chrome a maximized window overhangs the screen by its frame; pad it back in.</summary>
    private void FitMaximized()
    {
        if (WindowState == WindowState.Maximized)
        {
            var t = SystemParameters.WindowResizeBorderThickness;
            BorderThickness = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
        }
        else
        {
            BorderThickness = new Thickness(0);
        }
    }

    // ---- keyboard ---------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            (Content as ShellView)?.FocusSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.ToggleQueueCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Don't steal keys from text input.
        if (Keyboard.FocusedElement is TextBoxBase) return;

        switch (e.Key)
        {
            case Key.Escape when _vm.CurrentPageKind == AppPage.NowPlaying && _vm.Dialog is null:
                _vm.ToggleNowPlayingCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                _vm.Playback.TogglePause();
                e.Handled = true;
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.Playback.Next();
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.Playback.Previous();
                e.Handled = true;
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.None:
                _vm.Playback.SeekRelative(10);
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.None:
                _vm.Playback.SeekRelative(-10);
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Alt:
                if (_vm.GoBackCommand.CanExecute(null)) _vm.GoBackCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        // Mouse "back" / "forward" side buttons navigate history.
        if (e.ChangedButton == MouseButton.XButton1 && _vm.GoBackCommand.CanExecute(null)) _vm.GoBackCommand.Execute(null);
        if (e.ChangedButton == MouseButton.XButton2 && _vm.GoForwardCommand.CanExecute(null)) _vm.GoForwardCommand.Execute(null);
        base.OnMouseUp(e);
    }

    // ---- placement --------------------------------------------------------------------------

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        var s = _store.Settings;
        s.WindowLeft = Left;
        s.WindowTop = Top;
        s.WindowWidth = Width;
        s.WindowHeight = Height;
    }

    internal static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 &&
        top >= SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    /// <summary>Dark frame + Windows 11 rounded corners for the chrome-less window.</summary>
    private void ApplyWindowFrame() => ApplyWindowFrame(new WindowInteropHelper(this).Handle);

    internal static void ApplyWindowFrame(IntPtr hwnd)
    {
        int dark = 1, round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        // A subtle dark border instead of the Windows accent colour (when "accent on borders" is on).
        int border = 0x002A2A2A; // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private const int DWMWA_BORDER_COLOR = 34;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
