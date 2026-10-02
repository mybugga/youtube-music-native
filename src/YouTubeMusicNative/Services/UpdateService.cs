using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YouTubeMusicNative.Services;

/// <summary>
/// Keeps an installed copy current from GitHub releases: checks the latest release now and then,
/// downloads its setup in the background, and runs it silently (when the app closes, or right away
/// with "Restart to update"). Also keeps the bundled yt-dlp current, since YouTube changes break old ones.
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    public const string Repository = "mybugga/youtube-music-native";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan YtdlpInterval = TimeSpan.FromDays(1);

    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private readonly string _downloads;
    private string? _installerPath;

    public UpdateService(SettingsStore store)
    {
        _store = store;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"YouTubeMusicNative/{CurrentVersion}");
        _downloads = Path.Combine(store.Directory, "updates");

        // First check shortly after startup (not during it), then every few hours.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (_store.Settings.CheckForUpdates) await CheckAsync(userInitiated: false);
            await UpdateYtdlpAsync();
        };
        if (IsInstalled) _timer.Start();
        else Status = "Development build: updates are turned off.";

        // First start after an update: say so (and offer the release notes) for a little while.
        var last = ParseVersion(store.Settings.LastRunVersion);
        if (last is not null && last < CurrentVersion)
        {
            JustUpdatedTo = CurrentVersionText;
            var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
            hide.Tick += (_, _) => { hide.Stop(); JustUpdatedTo = null; };
            hide.Start();
        }
        if (store.Settings.LastRunVersion != CurrentVersionText)
        {
            store.Settings.LastRunVersion = CurrentVersionText;
            store.Save();
        }
    }

    public static Version CurrentVersion { get; } = ReadVersion();

    public string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>Installed through the setup (it leaves an uninstaller next to the exe); a dev build never self-updates.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// The install folder can't be written without admin rights (installed for all users, in Program Files):
    /// updates then go through an elevated installer, and yt-dlp is run from a copy in the user's data folder.
    /// </summary>
    public static bool IsReadOnlyInstall => ReadOnlyInstall.Value;

    private static readonly Lazy<bool> ReadOnlyInstall = new(() =>
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    });

    /// <summary>
    /// The yt-dlp to run: the bundled one, or for a read-only install a copy in the data folder that "yt-dlp -U"
    /// can update (refreshed from the bundled one when an app update brings a newer build).
    /// </summary>
    public static string YtdlpPath(SettingsStore store)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (!IsReadOnlyInstall || !File.Exists(bundled)) return bundled;
        var own = Path.Combine(store.Directory, "yt-dlp", "yt-dlp.exe");
        try
        {
            if (!File.Exists(own) || File.GetLastWriteTimeUtc(bundled) > File.GetLastWriteTimeUtc(own))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(own)!);
                File.Copy(bundled, own, overwrite: true);
            }
            return own;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("couldn't copy yt-dlp to the data folder: " + ex.Message);
            return bundled;
        }
    }

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string? _status;

    /// <summary>Version of a downloaded, ready-to-install update (null when there is none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateReady))]
    private string? _readyVersion;

    public bool IsUpdateReady => ReadyVersion is not null;

    /// <summary>An update is downloading in the background (the title bar shows a progress ring).</summary>
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string? _downloadingVersion;

    /// <summary>Download progress 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadPercent))]
    private double _downloadProgress;

    public string DownloadPercent => $"{Math.Round(DownloadProgress * 100)}%";

    /// <summary>"Restart to update" was clicked: the app shows the updating screen, then hands over to the installer.</summary>
    [ObservableProperty] private bool _isInstalling;

    /// <summary>Set on the first start after an update (the version now running), for the "Updated" notice.</summary>
    [ObservableProperty] private string? _justUpdatedTo;

    [RelayCommand]
    private void DismissUpdated() => JustUpdatedTo = null;

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        JustUpdatedTo = null;
        try
        {
            Process.Start(new ProcessStartInfo($"{ReleasesUrl}/tag/v{CurrentVersionText}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write("open release notes failed: " + ex.Message);
        }
    }

    public bool CheckForUpdates
    {
        get => _store.Settings.CheckForUpdates;
        set
        {
            _store.Settings.CheckForUpdates = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    public bool InstallOnExit
    {
        get => _store.Settings.InstallUpdatesOnExit;
        set
        {
            _store.Settings.InstallUpdatesOnExit = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Raised when "Restart to update" has started the installer and the app should quit.</summary>
    public event Action? ExitRequested;

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(userInitiated: true);

    private async Task CheckAsync(bool userInitiated)
    {
        if (IsChecking || IsUpdateReady) return;
        if (!IsInstalled)
        {
            if (userInitiated) Status = "Development build: updates are turned off.";
            return;
        }

        IsChecking = true;
        if (userInitiated) Status = "Checking for updates…";
        try
        {
            var json = await _http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            var release = JsonNode.Parse(json);
            var tag = release?["tag_name"]?.GetValue<string>();
            var latest = ParseVersion(tag);
            if (latest is null || latest <= CurrentVersion)
            {
                Status = $"You're on the latest version ({CurrentVersionText}).";
                return;
            }

            var asset = release?["assets"]?.AsArray()
                .FirstOrDefault(a => a?["name"]?.GetValue<string>().EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) == true);
            var url = asset?["browser_download_url"]?.GetValue<string>();
            if (url is null)
            {
                Status = $"Version {tag} is out, but it has no installer yet.";
                return;
            }

            Status = $"Downloading version {latest.ToString(3)}…";
            Directory.CreateDirectory(_downloads);
            var target = Path.Combine(_downloads, asset!["name"]!.GetValue<string>());
            long size = asset["size"]?.GetValue<long>() ?? -1;
            if (!File.Exists(target) || new FileInfo(target).Length != size)
            {
                DownloadingVersion = latest.ToString(3);
                DownloadProgress = 0;
                IsDownloading = true;
                try
                {
                    await DownloadAsync(url, target, size);
                }
                finally
                {
                    IsDownloading = false;
                }
            }

            // Old installers are no use once a newer one is here.
            foreach (var old in Directory.EnumerateFiles(_downloads).Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase)))
                TryDelete(old);

            _installerPath = target;
            ReadyVersion = latest.ToString(3);
            Status = InstallOnExit
                ? $"Version {ReadyVersion} is ready. It installs when you close the app, or restart now."
                : $"Version {ReadyVersion} is ready to install.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            AppLog.Write("update check failed: " + ex.Message);
            Status = userInitiated ? "Couldn't check for updates: " + ex.Message : null;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Saves the setup to disk, reporting progress (a few times a second) as it goes.</summary>
    private async Task DownloadAsync(string url, string target, long size)
    {
        var partial = target + ".part";
        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? size;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(partial);
            var buffer = new byte[256 * 1024];
            long done = 0, lastReport = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0 && (Environment.TickCount64 - lastReport > 120 || done == total))
                {
                    lastReport = Environment.TickCount64;
                    DownloadProgress = (double)done / total;
                }
            }
        }
        File.Move(partial, target, overwrite: true);
    }

    /// <summary>
    /// Installs the downloaded update now and starts the new version when it's done. The "Updating" screen shows
    /// for a moment first, so the app doesn't just vanish.
    /// </summary>
    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (IsInstalling || _installerPath is null) return;
        IsInstalling = true;
        await Task.Delay(1600);
        if (StartInstaller(relaunch: true)) ExitRequested?.Invoke();
        else IsInstalling = false;
    }

    /// <summary>Called as the app exits: quietly installs a waiting update if that's allowed.</summary>
    public void InstallPendingOnExit()
    {
        if (IsUpdateReady && InstallOnExit) StartInstaller(relaunch: false);
    }

    private bool StartInstaller(bool relaunch)
    {
        if (_installerPath is null || !File.Exists(_installerPath)) return false;
        try
        {
            Launch(_installerPath, relaunch);
            _installerPath = null; // never twice
            return true;
        }
        catch (Exception ex)
        {
            Status = "Couldn't start the update: " + ex.Message;
            return false;
        }
    }

    private static void Launch(string installer, bool relaunch)
    {
        // Same install mode as now: an all-users copy updates in Program Files (Windows asks for admin rights).
        var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
                   + (IsReadOnlyInstall ? " /ALLUSERS" : " /CURRENTUSER") + (relaunch ? " /RELAUNCH=1" : "");
        Process.Start(new ProcessStartInfo(installer, args) { UseShellExecute = false });
    }

    /// <summary>
    /// At startup: an update downloaded last time but never installed (say the app stayed in the tray until
    /// Windows shut down) is installed now, and the new version starts by itself. Returns true if the app should quit.
    /// </summary>
    public static bool InstallPendingAtStartup(SettingsStore store)
    {
        var dir = Path.Combine(store.Directory, "updates");
        if (!IsInstalled || !Directory.Exists(dir)) return false;
        foreach (var file in Directory.EnumerateFiles(dir, "*-setup.exe"))
        {
            // YouTubeMusicNative-1.2.3-setup.exe
            var name = Path.GetFileNameWithoutExtension(file);
            var version = ParseVersion(name.Split('-').ElementAtOrDefault(1));
            if (version is null || version <= CurrentVersion)
            {
                TryDelete(file);
                continue;
            }
            if (!store.Settings.InstallUpdatesOnExit) return false;
            if (store.Settings.LastUpdateAttempt == version.ToString(3))
            {
                // Tried this one at the last start and we're still on the old version: don't loop on a bad installer.
                TryDelete(file);
                return false;
            }
            store.Settings.LastUpdateAttempt = version.ToString(3);
            store.Save();
            try
            {
                Launch(file, relaunch: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("pending update failed to start: " + ex.Message);
                return false;
            }
        }
        return false;
    }

    /// <summary>"yt-dlp -U" about once a day: streams stop resolving when YouTube changes and yt-dlp hasn't caught up.</summary>
    private async Task UpdateYtdlpAsync()
    {
        var exe = YtdlpPath(_store);
        if (!File.Exists(exe) || DateTime.UtcNow - _store.Settings.LastYtdlpUpdate < YtdlpInterval) return;
        _store.Settings.LastYtdlpUpdate = DateTime.UtcNow;
        _store.Save();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "-U")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            AppLog.Write("yt-dlp -U: " + output.Trim().Replace(Environment.NewLine, " | "));
        }
        catch (Exception ex)
        {
            AppLog.Write("yt-dlp -U failed: " + ex.Message);
        }
    }

    /// <summary>"v1.2.3" / "1.2" → 1.2.3 / 1.2.0; null if it isn't a version.</summary>
    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) text = text[..dash];
        if (!Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    private static Version ReadVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ParseVersion(info) ?? new Version(0, 0, 0);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }
}
