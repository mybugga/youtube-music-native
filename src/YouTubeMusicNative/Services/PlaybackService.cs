using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Player;

namespace YouTubeMusicNative.Services;

/// <summary>
/// Owns the player and the queue: starts tracks, auto-advances, and tops the queue up with radio
/// tracks when autoplay is on. All public members are UI-thread only; mpv events are marshalled in.
/// </summary>
public sealed partial class PlaybackService : ObservableObject, IDisposable
{
    private const int RadioThreshold = 2;
    private const int MaxConsecutiveErrors = 3;

    private readonly MpvPlayer _mpv;
    private readonly InnerTubeClient _api;
    private readonly SettingsStore _store;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _statusTimer;

    private double _lastPostedPosition = -1;
    private bool _radioLoading;
    private int _consecutiveErrors;
    private string? _lastYtdlError;
    private volatile bool _stopped; // queue finished, gave up after errors, or a restored session not started yet; mpv is idle
    private double? _resumeAt; // a restored track is shown but not loaded yet; Play starts it here
    private string? _retriedVideoId; // a track that already got its one automatic retry
    private (Track Track, double Position)? _waitingForNetwork; // failed because we went offline; resumes on reconnect
    private readonly DispatcherTimer _sessionTimer;

    private sealed record PendingSeek(double Target, long Deadline);
    private volatile PendingSeek? _pendingSeek; // written on the UI thread, read on the mpv event thread

    public QueueService Queue { get; } = new();

    [ObservableProperty] private Track? _nowPlaying;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private bool _isPaused = true;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _autoplay;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Dark tint taken from the current track's art; drives the now-playing backgrounds.</summary>
    [ObservableProperty] private System.Windows.Media.Color _artColor = Services.ArtColor.Fallback;

    partial void OnNowPlayingChanged(Track? value) => _ = UpdateArtColorAsync(value);

    partial void OnIsPausedChanged(bool value)
    {
        if (value) SaveSession();
    }

    private async Task UpdateArtColorAsync(Track? track)
    {
        var color = await Services.ArtColor.GetAsync(track?.ThumbnailUrl);
        if (ReferenceEquals(track, NowPlaying)) ArtColor = color; // ignore results for tracks already skipped
    }

    /// <summary>Raised after a new track starts loading, and after a user seek (for the media overlay timeline).</summary>
    public event Action? TimelineReset;

    public PlaybackService(InnerTubeClient api, SettingsStore store)
    {
        _api = api;
        _store = store;
        _ui = Dispatcher.CurrentDispatcher;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Tick += (_, _) => { StatusMessage = null; _statusTimer.Stop(); };
        // Snapshot the session now and then while playing, so even a crash or power loss resumes close by.
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _sessionTimer.Tick += (_, _) => { if (!IsPaused) SaveSession(); };
        _sessionTimer.Start();

        _mpv = new MpvPlayer(UpdateService.YtdlpPath(store));
        RefreshYtdlOptions();

        _mpv.PositionChanged += pos =>
        {
            // Nothing loaded yet (a restored session waiting for Play): mpv's idle 0 would wipe the saved position.
            if (_stopped) return;
            // A network seek takes a moment; until mpv gets near the target it keeps reporting the old
            // position, which would make the seek bar snap back. Drop those (with a timeout as a safety net).
            if (_pendingSeek is { } seek)
            {
                if (Math.Abs(pos - seek.Target) > 1.5 && Environment.TickCount64 < seek.Deadline) return;
                _pendingSeek = null;
                WriteLog($"seek landed at {pos:F1}s (target {seek.Target:F1}s)");
            }
            // time-pos fires per audio packet; only wake the UI twice a second.
            if (Math.Abs(pos - _lastPostedPosition) < 0.5) return;
            _lastPostedPosition = pos;
            _ui.BeginInvoke(() =>
            {
                if (_stopped) return;
                // Posted before a seek that has since been requested? Then it's stale.
                if (_pendingSeek is { } s && Math.Abs(pos - s.Target) > 1.5) return;
                Position = pos;
            });
        };
        _mpv.DurationChanged += d => _ui.BeginInvoke(() =>
        {
            if (!_stopped) Duration = d; // same: keep the listed length while idle
        });
        _mpv.PauseChanged += p => _ui.BeginInvoke(() => IsPaused = p);
        _mpv.BufferingChanged += b => _ui.BeginInvoke(() => IsBuffering = b);
        _mpv.FileLoaded += () => _ui.BeginInvoke(() =>
        {
            IsBuffering = false;
            _retriedVideoId = null;
            _consecutiveErrors = 0;
            TimelineReset?.Invoke();
        });
        _mpv.TrackEnded += error => _ui.BeginInvoke(() => OnTrackEnded(error));
        // A cover that failed to download is asked for again (see ThumbnailConverter).
        ThumbnailConverter.RetryRequested += () =>
        {
            OnPropertyChanged(nameof(NowPlaying));
            System.Windows.Data.CollectionViewSource.GetDefaultView(Queue.Items).Refresh();
        };
        Connectivity.Instance.Reconnected += () =>
        {
            // Art requested while offline failed: make everything showing the current song and the queue ask again.
            OnPropertyChanged(nameof(NowPlaying));
            _ = UpdateArtColorAsync(NowPlaying);
            System.Windows.Data.CollectionViewSource.GetDefaultView(Queue.Items).Refresh();

            if (_waitingForNetwork is not { } wait || !ReferenceEquals(wait.Track, NowPlaying)) return;
            _waitingForNetwork = null;
            Start(wait.Track, wait.Position);
        };
        // mpv/yt-dlp warnings go to a small per-session log for troubleshooting playback failures.
        AppLog.Open(store.Directory);
        _mpv.Log += line =>
        {
            if (line.Contains("ytdl_hook") || line.Contains("ERROR")) _lastYtdlError = line;
            WriteLog(line);
        };

        _volume = store.Settings.Volume;
        _autoplay = store.Settings.Autoplay;
        _mpv.SetVolume(_volume);
    }

    /// <summary>Re-applies yt-dlp options (call after login/logout).</summary>
    public void RefreshYtdlOptions()
    {
        var cookies = _api.IsLoggedIn ? _store.YtdlCookiesPath : null;
        _mpv.SetYtdlOptions(cookies, FindJsRuntime());
    }

    public void PlayList(IReadOnlyList<Track> tracks, int index) => Start(Queue.ReplaceAndPlay(tracks, index));

    public void PlayQueueIndex(int index) => Start(Queue.JumpTo(index));

    public void Next() => Start(Queue.MoveNext(auto: false));

    public void Previous()
    {
        if (Position > 3) Seek(0);
        else Start(Queue.MovePrevious());
    }

    public void TogglePause()
    {
        if (NowPlaying is null || _stopped)
            Start(Queue.Current, _resumeAt ?? 0);
        else
            _mpv.TogglePause();
    }

    public void Play()
    {
        if (NowPlaying is null || _stopped) Start(Queue.Current, _resumeAt ?? 0);
        else _mpv.SetPaused(false);
    }

    public void Pause() => _mpv.SetPaused(true);

    public void Seek(double seconds)
    {
        if (NowPlaying is null) return;
        if (_stopped)
        {
            // Restored but not started yet: move the resume point; Play starts from there.
            if (_resumeAt is not null)
            {
                _resumeAt = Math.Clamp(seconds, 0, Math.Max(0, Duration - 1));
                Position = _resumeAt.Value;
            }
            return;
        }
        if (Duration > 0) seconds = Math.Min(seconds, Duration - 0.5);
        seconds = Math.Max(0, seconds);
        _pendingSeek = new PendingSeek(seconds, Environment.TickCount64 + 4000);
        WriteLog($"seek {Position:F1}s -> {seconds:F1}s (duration {Duration:F1}s)");
        _mpv.Seek(seconds);
        Position = seconds;
        _lastPostedPosition = seconds;
        TimelineReset?.Invoke();
    }

    private static void WriteLog(string line) => AppLog.Write(line);

    /// <summary>Skip forward/back by <paramref name="delta"/> seconds (arrow keys).</summary>
    public void SeekRelative(double delta) => Seek(Position + delta);

    public void ShowStatus(string message)
    {
        StatusMessage = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    partial void OnVolumeChanged(double value)
    {
        _mpv.SetVolume(value);
        _store.Settings.Volume = value;
    }

    partial void OnAutoplayChanged(bool value)
    {
        _store.Settings.Autoplay = value;
        if (value) _ = ExtendWithRadioAsync();
    }

    private void Start(Track? track, double startAt = 0)
    {
        if (track is null) return;
        _waitingForNetwork = null;
        _stopped = false;
        _resumeAt = null;
        _pendingSeek = null;
        IsPaused = false; // PlayVideo unpauses; mpv confirms via its pause property
        NowPlaying = track;
        Position = startAt;
        // Show the listed length straight away; mpv replaces it with the exact one once the stream opens.
        Duration = ParseDuration(track.Duration);
        _lastPostedPosition = -1;
        IsBuffering = true;
        _lastYtdlError = null;
        _mpv.PlayVideo(track.VideoId, startAt);
        TimelineReset?.Invoke();
        _ = ExtendWithRadioAsync();
        SaveSession();
        _ = WaitIfOfflineAsync(track, startAt);
    }

    /// <summary>
    /// Checks the connection as a song starts, so no internet shows up right away (offline screen, spinner on the
    /// song) instead of after yt-dlp's long timeout. The song then starts by itself once the connection is back.
    /// </summary>
    private async Task WaitIfOfflineAsync(Track track, double startAt)
    {
        if (await Connectivity.Instance.CheckAsync() || !ReferenceEquals(track, NowPlaying)) return;
        _waitingForNetwork = (track, startAt);
        IsBuffering = true;
        ShowStatus("No internet connection. Playback continues when you're back online.");
    }

    /// <summary>"3:32" / "1:02:03" to seconds; 0 when unknown.</summary>
    internal static double ParseDuration(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!int.TryParse(part, out var n)) return 0;
            total = total * 60 + n;
        }
        return total;
    }

    /// <summary>Saves the queue, current track and position for the next launch.</summary>
    public void SaveSession()
    {
        if (Queue.Items.Count == 0 || Queue.CurrentIndex < 0)
        {
            _store.SaveSession(null);
            return;
        }
        // Finished the queue: next launch shows the track from the start.
        double position = _resumeAt ?? (_stopped ? 0 : Position);
        _store.SaveSession(new SessionState
        {
            Queue = [.. Queue.Items],
            Index = Queue.CurrentIndex,
            Position = Math.Round(position, 1),
            Shuffle = Queue.Shuffle,
            Repeat = Queue.Repeat,
            WasPlaying = !_stopped && !IsPaused,
        });
    }

    /// <summary>
    /// Shows the last session's track, paused at its saved position, without loading anything:
    /// no network or audio work happens until the user presses Play.
    /// </summary>
    /// <returns>True if music was playing when the session was saved.</returns>
    public bool RestoreSession()
    {
        if (_store.LoadSession() is not { Queue.Count: > 0 } session) return false;
        Queue.Restore(session.Queue, session.Index, session.Shuffle, session.Repeat);
        if (Queue.Current is not { } track) return false;

        _stopped = true;
        // Pause mpv too: it starts unpaused, and its first pause report would otherwise flip the button to "playing".
        _mpv.SetPaused(true);
        IsPaused = true;
        NowPlaying = track;
        Duration = ParseDuration(track.Duration);
        // A track saved in its last seconds would end immediately; start it over instead.
        _resumeAt = Duration > 0 && session.Position > Duration - 5 ? 0 : session.Position;
        Position = _resumeAt.Value;
        TimelineReset?.Invoke();
        return session.WasPlaying;
    }

    private async void OnTrackEnded(bool error)
    {
        // Lost the connection: don't burn the retry or skip through the queue; wait and carry on from here.
        if (error && NowPlaying is { } current && !await Connectivity.Instance.CheckAsync())
        {
            _waitingForNetwork = (current, Position > 1 ? Position : 0);
            IsBuffering = true;
            ShowStatus("No internet connection. Playback continues when you're back online.");
            return;
        }

        // Stream URLs sometimes answer 403 once and work on a fresh resolve; retry before skipping the song.
        if (error && NowPlaying is { } failed && failed.VideoId != _retriedVideoId)
        {
            _retriedVideoId = failed.VideoId;
            WriteLog($"retrying {failed.VideoId} after a playback error");
            Start(failed, Position > 1 ? Position : 0);
            return;
        }

        if (error)
        {
            var title = NowPlaying?.Title ?? "track";
            ShowStatus($"Couldn't play \"{title}\". {_lastYtdlError}".Trim());
            if (++_consecutiveErrors >= MaxConsecutiveErrors)
            {
                _consecutiveErrors = 0;
                IsBuffering = false;
                StopAtEnd();
                return; // likely a network/yt-dlp problem; don't skip through the whole queue
            }
        }

        var next = Queue.MoveNext(auto: !error);
        if (next is null && Autoplay)
        {
            await ExtendWithRadioAsync();
            next = Queue.MoveNext(auto: false);
        }

        if (next is null)
        {
            StopAtEnd();
            return;
        }
        Start(next);
    }

    /// <summary>
    /// Nothing left to play. Pause mpv itself (rather than just flipping IsPaused) so the pause
    /// state reported by mpv stays the single source of truth; the next Play restarts the track.
    /// </summary>
    private void StopAtEnd()
    {
        _stopped = true;
        _mpv.SetPaused(true);
        Position = 0;
    }

    /// <summary>When autoplay is on and the queue is nearly empty, append radio tracks seeded from the last queued song.</summary>
    private async Task ExtendWithRadioAsync()
    {
        if (!Autoplay || _radioLoading || Queue.Items.Count == 0 || Queue.RemainingAfterCurrent > RadioThreshold)
            return;

        _radioLoading = true;
        try
        {
            var seed = Queue.Items[^1];
            var radio = await _api.GetRadioAsync(seed.VideoId);
            var known = Queue.Items.Select(t => t.VideoId).ToHashSet();
            Queue.AddRange(radio.Where(t => known.Add(t.VideoId)));
        }
        catch (Exception ex)
        {
            ShowStatus("Autoplay failed: " + ex.Message);
        }
        finally
        {
            _radioLoading = false;
        }
    }

    /// <summary>
    /// yt-dlp needs a JS runtime for YouTube. Prefer the Deno shipped next to the app; otherwise yt-dlp finds a
    /// Deno on PATH by itself, and Node has to be named explicitly.
    /// </summary>
    private static string? FindJsRuntime()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "deno.exe");
        if (File.Exists(bundled)) return "deno:" + bundled;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        bool OnPath(string exe) => path.Split(Path.PathSeparator)
            .Any(dir => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir.Trim(), exe)));

        if (OnPath("deno.exe")) return null;
        if (OnPath("node.exe")) return "node";
        return null;
    }

    public void Dispose()
    {
        _sessionTimer.Stop();
        _mpv.Dispose();
    }
}
