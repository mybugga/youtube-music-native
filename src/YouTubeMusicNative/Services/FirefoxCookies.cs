using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace YouTubeMusicNative.Services;

/// <summary>
/// Reads the user's YouTube session straight from Firefox's cookie database (cookies.sqlite is not
/// encrypted, unlike Chrome/Edge). Uses Windows' built-in winsqlite3.dll, so no extra dependency.
/// </summary>
public static class FirefoxCookies
{
    private static string ProfilesDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");

    /// <summary>Firefox is installed with at least one profile that has cookies.</summary>
    public static bool IsAvailable => FindProfileDb() is not null;

    /// <summary>True when Firefox is the default https handler (so "open in browser" lands in Firefox).</summary>
    public static bool IsDefaultBrowser
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return (key?.GetValue("ProgId") as string)?.StartsWith("Firefox", StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    /// <summary>
    /// youtube.com cookies as a Cookie header, or null if Firefox isn't signed in to YouTube.
    /// Works while Firefox is running: the database (and its write-ahead log, where fresh
    /// sign-in cookies land first) is copied to a temp folder and read from there.
    /// </summary>
    public static string? ReadYouTubeCookieHeader()
    {
        var db = FindProfileDb();
        if (db is null) return null;

        var temp = Path.Combine(Path.GetTempPath(), "YouTubeMusicNative-ff-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        var copy = Path.Combine(temp, "cookies.sqlite");
        try
        {
            File.Copy(db, copy, overwrite: true);
            if (File.Exists(db + "-wal")) File.Copy(db + "-wal", copy + "-wal", overwrite: true);
            else File.Delete(copy + "-wal");

            // First-party cookies only (empty originAttributes); prefer the .youtube.com domain cookie
            // over host-specific duplicates.
            const string sql =
                "SELECT name, value FROM moz_cookies " +
                "WHERE (host = '.youtube.com' OR host = 'youtube.com' OR host = 'music.youtube.com' OR host = 'www.youtube.com') " +
                "AND originAttributes = '' " +
                "ORDER BY CASE host WHEN '.youtube.com' THEN 0 ELSE 1 END";

            var cookies = new Dictionary<string, string>();
            foreach (var (name, value) in Query(copy, sql))
                cookies.TryAdd(name, value);

            if (!cookies.ContainsKey("SAPISID") && !cookies.ContainsKey("__Secure-3PAPISID")) return null;
            return string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The profile whose cookie database was written most recently (i.e. the one in use).</summary>
    private static string? FindProfileDb()
    {
        if (!Directory.Exists(ProfilesDir)) return null;
        return Directory.EnumerateDirectories(ProfilesDir)
            .Select(d => Path.Combine(d, "cookies.sqlite"))
            .Where(File.Exists)
            .OrderByDescending(p => Max(File.GetLastWriteTimeUtc(p),
                File.Exists(p + "-wal") ? File.GetLastWriteTimeUtc(p + "-wal") : DateTime.MinValue))
            .FirstOrDefault();

        static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    }

    // ---- minimal winsqlite3 interop ---------------------------------------------------------

    private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_OPEN_READWRITE = 0x2;

    private static IEnumerable<(string, string)> Query(string path, string sql)
    {
        if (sqlite3_open_v2(path, out var db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
        {
            sqlite3_close(db);
            yield break;
        }
        try
        {
            if (sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != SQLITE_OK) yield break;
            try
            {
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var name = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0));
                    var value = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 1));
                    if (!string.IsNullOrEmpty(name) && value is not null) yield return (name, value);
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
        finally
        {
            sqlite3_close(db);
        }
    }

    [DllImport("winsqlite3.dll", CharSet = CharSet.Ansi)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll")]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll")]
    private static extern int sqlite3_close(IntPtr db);
}
