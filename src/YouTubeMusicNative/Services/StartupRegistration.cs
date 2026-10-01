using Microsoft.Win32;

namespace YouTubeMusicNative.Services;

/// <summary>
/// "Start with Windows": the per-user Run key, so no admin rights are needed. The app is started with
/// <see cref="Argument"/>, which tells it to come back the way it was left (window, mini player or tray only).
/// </summary>
public static class StartupRegistration
{
    public const string Argument = "--autostart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YouTubeMusicNative";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the entry pointing at this exe (after an update or a move), if it's on.</summary>
    public static void Refresh()
    {
        try
        {
            if (IsEnabled) Set(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // policy-locked Run key: leave it
        }
    }
}
