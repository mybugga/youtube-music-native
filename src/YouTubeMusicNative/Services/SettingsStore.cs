using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YouTubeMusicNative.Services;

public sealed class AppSettings
{
    public double Volume { get; set; } = 70;
    public bool Autoplay { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>Where the sign-in came from: a Firefox-family browser (re-read at startup), the sign-in window, or a pasted cookie (null).</summary>
    public string? CookieSource { get; set; }
    /// <summary>Most recent first.</summary>
    public List<string> RecentSearches { get; set; } = [];
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }
    /// <summary>What was on screen last: "Main", "Mini" or "Tray" (window closed to the tray). Restored at startup.</summary>
    public string? LastView { get; set; }
    /// <summary>If music was playing when the app closed, start it again (same song, position and queue) at startup.</summary>
    public bool ResumePlaybackOnStart { get; set; } = true;
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    /// <summary>"Left"/"Right" when the mini player is docked (tucked) against that screen edge.</summary>
    public string? MiniDock { get; set; }
    /// <summary>Mini player drawer (up next / search / library) open, and which tab.</summary>
    public bool MiniExpanded { get; set; }
    public string? MiniTab { get; set; }
    /// <summary>Look for new versions on GitHub, and install them quietly when the app closes.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallUpdatesOnExit { get; set; } = true;
    public string? LastUpdateAttempt { get; set; }
    public DateTime LastYtdlpUpdate { get; set; }
}

/// <summary>What was playing when the app last closed, so it can pick up where it left off.</summary>
public sealed class SessionState
{
    public List<YouTubeMusicNative.Api.Track> Queue { get; set; } = [];
    public int Index { get; set; }
    public double Position { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; }
    /// <summary>Music was playing (not paused) when this was saved.</summary>
    public bool WasPlaying { get; set; }
}

/// <summary>
/// Persists settings as JSON and the login cookies DPAPI-encrypted (current user only),
/// under %LocalAppData%\YouTubeMusicNative.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Directory { get; }
    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string CookiesPath => Path.Combine(Directory, "cookies.bin");
    private string SessionPath => Path.Combine(Directory, "session.json");

    /// <summary>Plain-text cookie jar that yt-dlp reads. Lives in the user's LocalAppData (user-only ACL).</summary>
    public string YtdlCookiesPath => Path.Combine(Directory, "ytdl-cookies.txt");

    public AppSettings Settings { get; private set; } = new();

    public SettingsStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Directory = Path.Combine(appData, "YouTubeMusicNative");
        // Earlier builds were called YouTune / Encore / YtMusicLite: carry settings, sign-in and session over once.
        foreach (var old in new[] { "YouTune", "Encore", "YtMusicLite" })
        {
            var legacy = Path.Combine(appData, old);
            if (System.IO.Directory.Exists(Directory) || !System.IO.Directory.Exists(legacy)) continue;
            try { System.IO.Directory.Move(legacy, Directory); }
            catch (IOException) { /* in use or partially there: start fresh rather than fail */ }
        }
        System.IO.Directory.CreateDirectory(Directory);
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (JsonException)
        {
            Settings = new();
        }
    }

    public void Save() => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, JsonOptions));

    public SessionState? LoadSession()
    {
        try
        {
            return File.Exists(SessionPath) ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionPath)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void SaveSession(SessionState? session)
    {
        try
        {
            if (session is null) File.Delete(SessionPath);
            else File.WriteAllText(SessionPath, JsonSerializer.Serialize(session));
        }
        catch (IOException)
        {
            // best effort; never let a resume snapshot break playback or shutdown
        }
    }

    public string? LoadCookies()
    {
        if (!File.Exists(CookiesPath)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(CookiesPath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void SaveCookies(string cookies, string netscapeForYtdl)
    {
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookies), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CookiesPath, enc);
        File.WriteAllText(YtdlCookiesPath, netscapeForYtdl);
    }

    public void ClearCookies()
    {
        File.Delete(CookiesPath);
        File.Delete(YtdlCookiesPath);
    }
}
