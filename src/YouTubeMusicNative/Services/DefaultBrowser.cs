using System.IO;
using Microsoft.Win32;

namespace YouTubeMusicNative.Services;

/// <summary>
/// The user's default web browser (the https handler), and where its cookies live if the app can read them.
/// Firefox-family browsers keep cookies in a plain SQLite file. Chromium browsers (Chrome, Edge, Brave, Opera, …)
/// encrypt and lock theirs, so those sign in through the built-in Google sign-in window instead.
/// </summary>
public sealed record BrowserInfo(string Name, string? GeckoProfilesDir, string? Exe = null)
{
    /// <summary>The app can pick the YouTube session up from this browser's own cookie store.</summary>
    public bool CanShareSession => GeckoProfilesDir is not null && Directory.Exists(GeckoProfilesDir);
}

public static class DefaultBrowser
{
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // ProgId prefix → display name, profiles folder (Firefox and its forks) and exe.
    private static readonly (string ProgId, string Name, string? Profiles, string? Exe)[] Known =
    [
        ("FirefoxURL", "Firefox", @"Mozilla\Firefox\Profiles", "firefox.exe"),
        ("LibreWolf", "LibreWolf", @"librewolf\Profiles", "librewolf.exe"),
        ("WaterfoxURL", "Waterfox", @"Waterfox\Profiles", "waterfox.exe"),
        ("FloorpHTML", "Floorp", @"Floorp\Profiles", "floorp.exe"),
        ("Zen", "Zen", @"zen\Profiles", "zen.exe"),
        ("ChromeHTML", "Chrome", null, null),
        ("MSEdgeHTM", "Edge", null, null),
        ("BraveHTML", "Brave", null, null),
        ("Opera", "Opera", null, null),
        ("VivaldiHTM", "Vivaldi", null, null),
        ("ArcHTML", "Arc", null, null),
    ];

    /// <summary>Every Firefox-family browser with a profile on this PC (the app can link to any of them).</summary>
    public static IReadOnlyList<BrowserInfo> InstalledGecko() =>
        Known.Where(k => k.Profiles is not null)
            .Select(k => new BrowserInfo(k.Name, Path.Combine(AppData, k.Profiles!), k.Exe))
            .Where(b => GeckoCookies.HasProfile(b.GeckoProfilesDir!))
            .ToList();

    /// <summary>Firefox's profiles folder, used when the session is linked to Firefox regardless of the default.</summary>
    public static string FirefoxProfiles => Path.Combine(AppData, @"Mozilla\Firefox\Profiles");

    public static BrowserInfo Current()
    {
        var progId = ReadProgId();
        if (progId is not null)
        {
            foreach (var (prefix, name, profiles, exe) in Known)
                if (progId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return new BrowserInfo(name, profiles is null ? null : Path.Combine(AppData, profiles), exe);
            return new BrowserInfo(ReadAppName(progId) ?? "your browser", null);
        }
        return new BrowserInfo("your browser", null);
    }

    private static string? ReadProgId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return key?.GetValue("ProgId") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadAppName(string progId)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(progId + @"\Application");
            var name = key?.GetValue("ApplicationName") as string;
            // Some browsers store "@C:\...\browser.exe,-123" resource references here; not worth resolving.
            return string.IsNullOrWhiteSpace(name) || name.StartsWith('@') ? null : name;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
