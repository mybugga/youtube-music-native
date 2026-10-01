using System.IO;

namespace YouTubeMusicNative.Services;

/// <summary>Small per-session log at %LocalAppData%\YouTubeMusicNative\mpv.log (mpv warnings, seeks, playback errors).</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static void Open(string directory)
    {
        lock (Gate)
            _writer ??= new StreamWriter(Path.Combine(directory, "mpv.log"), append: false) { AutoFlush = true };
    }

    public static void Write(string line)
    {
        lock (Gate) _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }
}
