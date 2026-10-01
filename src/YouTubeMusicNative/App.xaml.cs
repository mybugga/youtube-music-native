using System.Windows;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;
using YouTubeMusicNative.ViewModels;

namespace YouTubeMusicNative;

public partial class App : Application
{
    private const string InstanceName = "YouTubeMusicNative.SingleInstance";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private SettingsStore? _store;
    private InnerTubeClient? _api;
    private PlaybackService? _playback;
    private MediaControlsService? _media;
    private TrayService? _tray;
    private MainWindow? _window;
    private MiniPlayerWindow? _mini;
    private MainViewModel? _vm;
    private bool _sessionEnding;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch just brings the first window back.
        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _playback?.ShowStatus("Error: " + args.Exception.Message);
            args.Handled = true;
        };

        _store = new SettingsStore();
        _store.Load();
        if (UpdateService.InstallPendingAtStartup(_store))
        {
            Shutdown(); // the installer starts the new version when it's done
            return;
        }
        SessionEnding += (_, _) => _sessionEnding = true;
        _api = new InnerTubeClient();

        try
        {
            _playback = new PlaybackService(_api, _store);
        }
        catch (DllNotFoundException)
        {
            MessageBox.Show("libmpv-2.dll was not found next to YouTubeMusicNative.exe.\n\nRun scripts\\fetch-deps.ps1 and rebuild.",
                "YouTube Music Native", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _vm = new MainViewModel(_api, _store, _playback);
        _window = new MainWindow(_vm, _store);
        _media = new MediaControlsService(_window.Handle, _playback);
        _playback.RestoreSession(); // last session's track, paused where it was left

        _tray = new TrayService(_playback);
        _tray.ShowRequested += ShowMainWindow;
        _tray.MiniPlayerRequested += ShowMiniPlayer;
        _tray.ExitRequested += ExitApp;
        _vm.MiniPlayerRequested += ShowMiniPlayer;
        _vm.Updates.ExitRequested += ExitApp;
        _window.Closed += (_, _) => Shutdown(); // only reached when close-to-tray is off or Exit was chosen

        _window.Show();
        ListenForSecondInstance();
    }

    private void ShowMainWindow()
    {
        _mini?.Close();
        _window?.ShowFromTray();
    }

    /// <summary>Swap the full window (whose UI is released to save memory) for the always-on-top mini player.</summary>
    private void ShowMiniPlayer()
    {
        if (_mini is null)
        {
            _mini = new MiniPlayerWindow(_vm!, _store!);
            _mini.ExpandRequested += ShowMainWindow;
            _mini.Closed += (_, _) => _mini = null;
        }
        _window?.HideAndRelease();
        _mini.Show();
        _mini.Activate();
    }

    private void ListenForSecondInstance()
    {
        var thread = new Thread(() =>
        {
            while (_showSignal!.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        })
        { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }

    private void ExitApp()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _playback?.SaveSession();
        _store?.Save();
        // Not while Windows is shutting down: the update then installs at the next start instead.
        if (!_sessionEnding) _vm?.Updates.InstallPendingOnExit();
        _vm?.Updates.Dispose();
        _tray?.Dispose();
        _media?.Dispose();
        _playback?.Dispose();
        _api?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
