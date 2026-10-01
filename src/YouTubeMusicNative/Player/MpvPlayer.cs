using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace YouTubeMusicNative.Player;

/// <summary>
/// Audio-only libmpv wrapper. Stream URLs are resolved by mpv's built-in ytdl hook (yt-dlp).
/// All events are raised on the mpv event thread; subscribers must marshal to the UI thread.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private const ulong ObsTimePos = 1, ObsDuration = 2, ObsPause = 3, ObsVolume = 4, ObsBuffering = 5;

    private readonly IntPtr _ctx;
    private readonly Thread _eventThread;
    private volatile bool _disposed;

    public event Action<double>? PositionChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<double>? VolumeChanged;
    public event Action<bool>? BufferingChanged;
    public event Action? FileLoaded;
    /// <summary>Raised when a track ends on its own (EOF) or fails; not raised when replaced or stopped.</summary>
    public event Action<bool /*error*/>? TrackEnded;
    public event Action<string>? Log;

    public MpvPlayer(string ytdlpPath)
    {
        _ctx = MpvNative.Create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create failed");

        // Audio only, small buffers, no config files or UI.
        Opt("config", "no");
        Opt("terminal", "no");
        Opt("video", "no");
        Opt("vid", "no");
        Opt("audio-display", "no");
        Opt("osc", "no");
        Opt("input-default-bindings", "no");
        Opt("idle", "yes");
        Opt("cache", "yes");
        Opt("demuxer-max-bytes", "8MiB");
        Opt("demuxer-max-back-bytes", "2MiB");
        Opt("ytdl", "yes");
        Opt("ytdl-format", "bestaudio[acodec=opus]/bestaudio/best");
        // all_formats=no: newer mpv builds default to stitching *every* format (incl. video and HLS) into one
        // EDL timeline, which is unseekable (seeks jump to the end) and breaks if any one format 403s.
        // We only want the single selected audio stream.
        Opt("script-opts", "ytdl_hook-ytdl_path=" + Quote(ytdlpPath) + ",ytdl_hook-all_formats=no");
        Opt("audio-client-name", "YouTube Music Native");

        Check(MpvNative.Initialize(_ctx), "mpv_initialize");

        // YTMN_MPV_LOG=v (or debug) turns on verbose mpv logging into mpv.log for troubleshooting.
        MpvNative.RequestLogMessages(_ctx, Environment.GetEnvironmentVariable("YTMN_MPV_LOG") ?? "warn");
        MpvNative.ObserveProperty(_ctx, ObsTimePos, "time-pos", MpvFormat.Double);
        MpvNative.ObserveProperty(_ctx, ObsDuration, "duration", MpvFormat.Double);
        MpvNative.ObserveProperty(_ctx, ObsPause, "pause", MpvFormat.Flag);
        MpvNative.ObserveProperty(_ctx, ObsVolume, "volume", MpvFormat.Double);
        MpvNative.ObserveProperty(_ctx, ObsBuffering, "paused-for-cache", MpvFormat.Flag);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    /// <summary>Extra yt-dlp options (cookies file, JS runtime). Read by the ytdl hook on every load.</summary>
    public void SetYtdlOptions(string? cookiesFile, string? jsRuntime)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(cookiesFile) && File.Exists(cookiesFile))
            parts.Add("cookies=" + Quote(cookiesFile));
        if (!string.IsNullOrEmpty(jsRuntime))
            parts.Add("js-runtimes=" + Quote(jsRuntime));
        MpvNative.SetPropertyString(_ctx, "ytdl-raw-options", string.Join(",", parts));
    }

    /// <param name="startAt">Seconds to start from (resuming); 0 = the beginning.</param>
    public void PlayVideo(string videoId, double startAt = 0)
    {
        // "start" applies to every later file too, so always set it (to "none" when not resuming).
        MpvNative.SetPropertyString(_ctx, "start",
            startAt > 0 ? startAt.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "none");
        Command("loadfile", "https://music.youtube.com/watch?v=" + videoId, "replace");
        SetPaused(false);
    }

    public void Stop() => Command("stop");

    public void SetPaused(bool paused) => MpvNative.SetPropertyString(_ctx, "pause", paused ? "yes" : "no");

    public void TogglePause() => Command("cycle", "pause");

    public void Seek(double seconds) =>
        Command("seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");

    public void SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        MpvNative.SetProperty(_ctx, "volume", MpvFormat.Double, ref volume);
    }

    private void Command(params string[] args)
    {
        var argv = MpvNative.AllocArgv(args);
        try
        {
            // Async so a slow loadfile never blocks the UI thread.
            int err = MpvNative.CommandAsync(_ctx, 0, argv);
            if (err < 0) Log?.Invoke($"command {args[0]} failed: {MpvNative.ErrorText(err)}");
        }
        finally
        {
            MpvNative.FreeArgv(argv, args.Length);
        }
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            var evPtr = MpvNative.WaitEvent(_ctx, -1);
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            try
            {
                switch (ev.EventId)
                {
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.PropertyChange:
                        OnPropertyChange(ev);
                        break;
                    case MpvEventId.FileLoaded:
                        FileLoaded?.Invoke();
                        break;
                    case MpvEventId.EndFile:
                        var end = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                        if (end.Reason == MpvEndFileReason.Eof)
                            TrackEnded?.Invoke(false);
                        else if (end.Reason == MpvEndFileReason.Error)
                        {
                            Log?.Invoke("playback error: " + MpvNative.ErrorText(end.Error));
                            TrackEnded?.Invoke(true);
                        }
                        break;
                    case MpvEventId.LogMessage:
                        var msg = Marshal.PtrToStructure<MpvEventLogMessage>(ev.Data);
                        var text = $"[{Marshal.PtrToStringUTF8(msg.Prefix)}] {Marshal.PtrToStringUTF8(msg.Text)?.TrimEnd()}";
                        Debug.WriteLine(text);
                        Log?.Invoke(text);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Never let a subscriber exception kill the event thread.
                Debug.WriteLine("mpv event handler error: " + ex);
            }
        }
    }

    private void OnPropertyChange(MpvEvent ev)
    {
        var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
        bool has = prop.Format != MpvFormat.None && prop.Data != IntPtr.Zero;
        switch (ev.ReplyUserdata)
        {
            case ObsTimePos:
                PositionChanged?.Invoke(has ? ReadDouble(prop.Data) : 0);
                break;
            case ObsDuration:
                DurationChanged?.Invoke(has ? ReadDouble(prop.Data) : 0);
                break;
            case ObsPause:
                if (has) PauseChanged?.Invoke(Marshal.ReadInt32(prop.Data) != 0);
                break;
            case ObsVolume:
                if (has) VolumeChanged?.Invoke(ReadDouble(prop.Data));
                break;
            case ObsBuffering:
                BufferingChanged?.Invoke(has && Marshal.ReadInt32(prop.Data) != 0);
                break;
        }
    }

    private static double ReadDouble(IntPtr p) => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p));

    private void Opt(string name, string value)
    {
        int err = MpvNative.SetOptionString(_ctx, name, value);
        if (err < 0) Debug.WriteLine($"mpv option {name}={value} failed: {MpvNative.ErrorText(err)}");
    }

    private static void Check(int err, string what)
    {
        if (err < 0) throw new InvalidOperationException($"{what} failed: {MpvNative.ErrorText(err)}");
    }

    /// <summary>mpv "%len%value" quoting so commas/equals in paths survive key=value list parsing.</summary>
    private static string Quote(string value) => $"%{Encoding.UTF8.GetByteCount(value)}%{value}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MpvNative.Wakeup(_ctx);
        _eventThread.Join(TimeSpan.FromSeconds(2));
        MpvNative.TerminateDestroy(_ctx);
    }
}
