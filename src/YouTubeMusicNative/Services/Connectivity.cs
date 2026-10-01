using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YouTubeMusicNative.Services;

/// <summary>
/// Knows whether YouTube is reachable. Failed requests, Windows network changes and every song start trigger a
/// quick check. While offline it re-checks every few seconds and raises <see cref="Reconnected"/> once the connection is back,
/// so pages that failed can load again by themselves.
/// </summary>
public sealed partial class Connectivity : ObservableObject
{
    public static Connectivity Instance { get; } = new();

    private const string ProbeUrl = "https://www.youtube.com/generate_204";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    // Short timeouts: a silently dropped connection should count as offline within seconds, not after a 30 s stall.
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
    })
    { Timeout = TimeSpan.FromSeconds(3) };
    private Dispatcher? _ui;
    private DispatcherTimer? _retry;
    private int _checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    private bool _isOnline = true;

    public bool IsOffline => !IsOnline;

    [ObservableProperty] private bool _isChecking;

    /// <summary>The connection came back after being offline (raised on the UI thread).</summary>
    public event Action? Reconnected;

    private Connectivity() { }

    /// <summary>Starts watching (call once on the UI thread at startup) and checks right away.</summary>
    public void Start()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _retry = new DispatcherTimer { Interval = RetryInterval };
        _retry.Tick += async (_, _) => await CheckAsync();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => _ui.BeginInvoke(async () => await CheckAsync());
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.BeginInvoke(async () => await CheckAsync());
        if (!NetworkInterface.GetIsNetworkAvailable()) SetOnline(false);
        _ = CheckAsync();
    }

    /// <summary>A request failed in a way that looks like no connection: confirm, and go offline if so.</summary>
    public void ReportFailure() => _ui?.BeginInvoke(async () => await CheckAsync());

    /// <summary>A request got through, so we're online (cheap; no probe).</summary>
    public void ReportSuccess()
    {
        if (!IsOnline) _ui?.BeginInvoke(() => SetOnline(true));
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync();

    /// <summary>Probes YouTube; returns whether it's reachable.</summary>
    public async Task<bool> CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return IsOnline;
        IsChecking = true;
        try
        {
            bool ok;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, ProbeUrl);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                ok = true; // any HTTP answer means the network works
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ok = false;
            }
            SetOnline(ok);
            return ok;
        }
        finally
        {
            IsChecking = false;
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private void SetOnline(bool online)
    {
        if (online) _retry?.Stop();
        else _retry?.Start();
        if (online == IsOnline) return;
        IsOnline = online;
        AppLog.Write(online ? "connection restored" : "offline");
        if (online) Reconnected?.Invoke();
    }
}
