using System.Net.Http;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using System.Windows;
using YouTubeMusicNative.Services;
using YouTubeMusicNative.Views;

namespace YouTubeMusicNative.ViewModels;

public sealed partial class LoginViewModel(InnerTubeClient api, SettingsStore store, PlaybackService playback, Action loggedInChanged)
    : ObservableObject
{
    // CookieSource values: "gecko|<browser name>|<profiles folder>" when the session is read from a Firefox-family
    // browser (re-read at every start), "window" after the built-in sign-in window. "firefox" is the older form.
    private const string LegacyFirefoxSource = "firefox";
    private const string WindowSource = "window";

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
    [ObservableProperty] private string? _waitingBrowserName;
    [ObservableProperty] private bool _showManual;

    /// <summary>Name, handle and photo of the signed-in account (null until loaded / when signed out).</summary>
    [ObservableProperty] private AccountInfo? _account;

    public bool IsLoggedIn => api.IsLoggedIn;

    /// <summary>The ways to sign in on this PC, the one that suits the default browser first.</summary>
    [ObservableProperty] private IReadOnlyList<SignInOption> _signInOptions = [];

    /// <summary>Rebuilds the sign-in choices (browsers can be installed or the default changed while the app runs).</summary>
    public void RefreshSignInOptions()
    {
        var current = DefaultBrowser.Current();
        var options = new List<SignInOption>();
        foreach (var browser in DefaultBrowser.InstalledGecko())
        {
            bool isDefault = browser.Name == current.Name;
            options.Add(new SignInOption(SignInKind.Browser, $"Sign in with {browser.Name}",
                $"Opens Google sign-in in {browser.Name}; the app picks the session up by itself (instantly if you're " +
                $"already signed in there) and stays linked to it.",
                "\uE774", isDefault ? "Your default browser" : null, browser));
        }
        options.Add(new SignInOption(SignInKind.Window, "Sign in with Google",
            current.CanShareSession
                ? "Signs in through a small Google window instead of your browser."
                : $"Signs in through a small Google window. Works with any browser, including {current.Name}, whose saved sign-ins other apps can't read.",
            "\uE77B", current.CanShareSession ? null : "Recommended", null));
        options.Add(new SignInOption(SignInKind.Cookie, "Paste a cookie or import cookies.txt",
            "For advanced users: copy the cookie header from your browser's developer tools.",
            "\uE8C8", null, null));
        // The default browser (or the Google window when the default can't share its session) goes first.
        SignInOptions = options.OrderBy(o => o.Badge is null ? 1 : 0).ToList();
    }

    /// <summary>Signed in from a browser whose session is re-read at every start ("Linked to Firefox").</summary>
    public bool IsLinkedToBrowser => api.IsLoggedIn && LinkedBrowser() is not null;

    public string? LinkedBrowserName => LinkedBrowser()?.Name;

    private string SignInWindowData => Path.Combine(store.Directory, "SignIn");

    /// <summary>
    /// Restores the session on startup. A Firefox-linked login is re-read from Firefox each time,
    /// so Google's periodic cookie rotation never signs the app out.
    /// </summary>
    public void TryRestore()
    {
        RefreshSignInOptions();
        if (LinkedBrowser() is { } linked && GeckoCookies.ReadYouTubeCookieHeader(linked.GeckoProfilesDir!) is { } fresh
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

    /// <summary>"Sign in" from elsewhere in the app: uses the first (best) option for this PC.</summary>
    [RelayCommand]
    private async Task SignInWithBrowserAsync()
    {
        RefreshSignInOptions();
        await SignInWithAsync(SignInOptions.First());
    }

    [RelayCommand]
    private async Task SignInWithAsync(SignInOption? option)
    {
        switch (option?.Kind)
        {
            case SignInKind.Browser:
                // The default browser opens a plain link; any other one is started by name.
                bool isDefault = DefaultBrowser.Current().Name == option.Browser!.Name;
                await SignInThroughGeckoAsync(option.Browser, launchExe: isDefault ? null : option.Browser.Exe);
                break;
            case SignInKind.Window:
                SignInWithWindow();
                break;
            case SignInKind.Cookie:
                ShowManual = !ShowManual;
                break;
        }
    }

    /// <summary>Google sign-in in a small window (WebView2), for browsers whose cookies can't be read.</summary>
    private void SignInWithWindow()
    {
        _browserWait?.Cancel();
        if (!GoogleSignInWindow.IsAvailable)
        {
            ShowError("The sign-in window needs the Microsoft Edge WebView2 runtime, which isn't installed. " +
                      "Sign in with a copied cookie instead (below).");
            ShowManual = true;
            return;
        }

        IsError = false;
        Message = null;
        bool? signedIn;
        var window = new GoogleSignInWindow(SignInUrl, SignInWindowData);
        try
        {
            if (Application.Current.MainWindow is { IsVisible: true } owner) window.Owner = owner;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            signedIn = window.ShowDialog();
        }
        catch (Exception ex)
        {
            // Commands swallow exceptions, so say what happened instead of silently doing nothing.
            AppLog.Write("sign-in window: " + ex);
            ShowError("The sign-in window couldn't open: " + ex.Message);
            return;
        }

        if (signedIn == true && Auth.Parse(window.CookieHeader ?? "") is { } auth)
            Complete(auth, WindowSource, "Signed in. Your library is now available.");
        else if (window.Error is { } error)
        {
            ShowError(error + " Sign in with a copied cookie instead (below).");
            ShowManual = true;
        }
    }

    private async Task SignInThroughGeckoAsync(BrowserInfo browser, string? launchExe)
    {
        if (await TryCompleteFromGeckoAsync(browser))
            return;

        try
        {
            // The default browser for a plain URL; a named exe when it has to be a specific browser.
            Process.Start(launchExe is null
                ? new ProcessStartInfo(SignInUrl) { UseShellExecute = true }
                : new ProcessStartInfo(launchExe, SignInUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Process.Start(new ProcessStartInfo(SignInUrl) { UseShellExecute = true });
        }

        _browserWait?.Cancel();
        _browserWait = new CancellationTokenSource(PollTimeout);
        var ct = _browserWait.Token;
        WaitingBrowserName = browser.Name;
        IsWaitingForBrowser = true;
        IsError = false;
        Message = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);
                if (await TryCompleteFromGeckoAsync(browser)) return;
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
            ShowError($"Didn't see a YouTube sign-in in {browser.Name}. Try again, or use a copied cookie below.");
    }

    [RelayCommand]
    private void CancelBrowserSignIn()
    {
        _browserWait?.Cancel();
        Message = "Sign-in cancelled.";
        IsError = false;
    }

    private async Task<bool> TryCompleteFromGeckoAsync(BrowserInfo browser)
    {
        var dir = browser.GeckoProfilesDir!;
        var header = await Task.Run(() => GeckoCookies.ReadYouTubeCookieHeader(dir));
        if (header is null || Auth.Parse(header) is not { } auth) return false;
        Complete(auth, $"gecko|{browser.Name}|{dir}", $"Signed in with your {browser.Name} session.");
        return true;
    }

    /// <summary>The Firefox-family browser the saved session is linked to, if any.</summary>
    private BrowserInfo? LinkedBrowser()
    {
        var source = store.Settings.CookieSource;
        if (source == LegacyFirefoxSource) return new BrowserInfo("Firefox", DefaultBrowser.FirefoxProfiles);
        if (source?.Split('|') is ["gecko", var name, var dir]) return new BrowserInfo(name, dir);
        return null;
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
        if (store.Settings.CookieSource == WindowSource) GoogleSignInWindow.ClearSession(SignInWindowData);
        store.Settings.CookieSource = null;
        RefreshSignInOptions();
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
        OnPropertyChanged(nameof(IsLinkedToBrowser));
        OnPropertyChanged(nameof(LinkedBrowserName));
    }
}

public enum SignInKind { Browser, Window, Cookie }

/// <summary>One way to sign in, as listed on the Account page.</summary>
/// <param name="Badge">"Your default browser" / "Recommended", or null.</param>
public sealed record SignInOption(SignInKind Kind, string Title, string Detail, string Glyph, string? Badge, BrowserInfo? Browser);
