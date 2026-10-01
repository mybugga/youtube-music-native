using System.Net.Http;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public sealed partial class LoginViewModel(InnerTubeClient api, SettingsStore store, PlaybackService playback, Action loggedInChanged)
    : ObservableObject
{
    private const string FirefoxSource = "firefox";

    // YouTube's own sign-in link: Google sign-in, then www.youtube.com/signin sets the youtube.com
    // session cookies, then lands on YouTube Music. "passive" skips the form if already signed in to Google.
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26hl%3Den%26next%3D" +
        "https%253A%252F%252Fmusic.youtube.com%252F";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(10);

    private CancellationTokenSource? _browserWait;

    [ObservableProperty] private string _cookieText = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isWaitingForBrowser;
    [ObservableProperty] private bool _showManual;

    /// <summary>Name, handle and photo of the signed-in account (null until loaded / when signed out).</summary>
    [ObservableProperty] private AccountInfo? _account;

    public bool IsLoggedIn => api.IsLoggedIn;
    public bool IsFirefoxAvailable => FirefoxCookies.IsAvailable;
    public bool IsLinkedToFirefox => api.IsLoggedIn && store.Settings.CookieSource == FirefoxSource;

    /// <summary>
    /// Restores the session on startup. A Firefox-linked login is re-read from Firefox each time,
    /// so Google's periodic cookie rotation never signs the app out.
    /// </summary>
    public void TryRestore()
    {
        if (store.Settings.CookieSource == FirefoxSource && FirefoxCookies.ReadYouTubeCookieHeader() is { } fresh
            && Auth.Parse(fresh) is { } current)
        {
            api.Auth = current;
            store.SaveCookies(current.CookieHeader, current.ToNetscape());
            return;
        }

        if (store.LoadCookies() is { } saved && Auth.Parse(saved) is { } auth)
        {
            api.Auth = auth;
            // Rewrite yt-dlp's jar in case it was deleted.
            if (!File.Exists(store.YtdlCookiesPath)) store.SaveCookies(auth.CookieHeader, auth.ToNetscape());
        }
    }

    /// <summary>
    /// Opens Google sign-in in Firefox, then watches Firefox's cookie store and completes the login as
    /// soon as the YouTube session appears. If Firefox is already signed in, this finishes immediately.
    /// </summary>
    [RelayCommand]
    private async Task SignInWithBrowserAsync()
    {
        if (!FirefoxCookies.IsAvailable)
        {
            ShowError("Firefox wasn't found. Sign in with a copied cookie instead (below).");
            ShowManual = true;
            return;
        }

        if (await TryCompleteFromFirefoxAsync())
            return;

        try
        {
            // Launch Firefox explicitly (not just the default browser) because that's where we read cookies from.
            Process.Start(new ProcessStartInfo("firefox.exe", SignInUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Process.Start(new ProcessStartInfo(SignInUrl) { UseShellExecute = true });
        }

        _browserWait?.Cancel();
        _browserWait = new CancellationTokenSource(PollTimeout);
        var ct = _browserWait.Token;
        IsWaitingForBrowser = true;
        IsError = false;
        Message = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);
                if (await TryCompleteFromFirefoxAsync()) return;
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled or timed out
        }
        finally
        {
            IsWaitingForBrowser = false;
        }

        if (!api.IsLoggedIn && _browserWait?.IsCancellationRequested == true && Message is null)
            ShowError("Didn't see a YouTube sign-in in Firefox. Try again, or use a copied cookie below.");
    }

    [RelayCommand]
    private void CancelBrowserSignIn()
    {
        _browserWait?.Cancel();
        Message = "Sign-in cancelled.";
        IsError = false;
    }

    private async Task<bool> TryCompleteFromFirefoxAsync()
    {
        var header = await Task.Run(FirefoxCookies.ReadYouTubeCookieHeader);
        if (header is null || Auth.Parse(header) is not { } auth) return false;
        Complete(auth, FirefoxSource, "Signed in with your Firefox session.");
        return true;
    }

    [RelayCommand]
    private void Login()
    {
        var auth = Auth.Parse(CookieText);
        if (auth is null)
        {
            ShowError("No SAPISID cookie found. Copy the full Cookie header from a music.youtube.com request while signed in.");
            return;
        }
        CookieText = "";
        Complete(auth, source: null, "Signed in. Your library is now available.");
    }

    [RelayCommand]
    private void ImportFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import cookies.txt",
            Filter = "Cookie files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        CookieText = File.ReadAllText(dlg.FileName);
        Login();
    }

    [RelayCommand]
    private void ToggleManual() => ShowManual = !ShowManual;

    /// <summary>Fetches the account name/photo for the title bar and this page.</summary>
    public async Task LoadAccountAsync()
    {
        if (!api.IsLoggedIn)
        {
            Account = null;
            return;
        }
        try
        {
            Account = await api.GetAccountAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Account = null; // the generic "signed in" card still shows
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _browserWait?.Cancel();
        Account = null;
        api.Auth = null;
        store.ClearCookies();
        store.Settings.CookieSource = null;
        store.Save();
        playback.RefreshYtdlOptions();
        IsError = false;
        Message = "Signed out of YouTube Music Native. (Your browser stays signed in.)";
        RaiseLoginState();
        loggedInChanged();
    }

    private void Complete(Auth auth, string? source, string message)
    {
        _browserWait?.Cancel();
        api.Auth = auth;
        store.SaveCookies(auth.CookieHeader, auth.ToNetscape());
        store.Settings.CookieSource = source;
        store.Save();
        playback.RefreshYtdlOptions();
        IsError = false;
        Message = message;
        RaiseLoginState();
        loggedInChanged();
    }

    private void ShowError(string message)
    {
        IsError = true;
        Message = message;
    }

    private void RaiseLoginState()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(IsLinkedToFirefox));
    }
}
