using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace YouTubeMusicNative.Views;

/// <summary>
/// Google sign-in in a small window, for people whose default browser keeps its cookies out of reach
/// (Chrome, Edge and other Chromium browsers encrypt and lock them). It uses the WebView2 engine that ships with
/// Windows, only while the window is open. As soon as YouTube Music has a session, the cookies are taken
/// and the window closes, so the browser engine is gone again.
/// </summary>
public sealed class GoogleSignInWindow : Window
{
    private readonly string _url;
    private readonly string _dataFolder;
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 18, 18) };
    private readonly TextBlock _status;
    private bool _done;

    /// <summary>The youtube.com Cookie header once signed in; null if the window was closed first.</summary>
    public string? CookieHeader { get; private set; }

    /// <summary>Why the window couldn't be used (no WebView2 runtime, …), if that's what happened.</summary>
    public string? Error { get; private set; }

    /// <summary>The exception behind <see cref="Error"/>, for the log.</summary>
    public Exception? Failure { get; private set; }

    public GoogleSignInWindow(string url, string dataFolder)
    {
        _url = url;
        _dataFolder = dataFolder;
        Title = "Sign in to YouTube Music";
        Width = 480;
        Height = 700;
        MinWidth = 380;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));

        _status = new TextBlock
        {
            Text = "Loading Google sign-in…",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xA7, 0xA7)),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var root = new Grid();
        root.Children.Add(_status);
        // The WebView2 has to stay visible to finish initializing; its dark background covers the status text
        // until the page paints. On failure it's collapsed so the message shows.
        root.Children.Add(_web);
        Content = root;

        SourceInitialized += (_, _) => MainWindow.ApplyWindowFrame(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _web.Dispose(); // ends the WebView2 processes
    }

    /// <summary>The Edge WebView2 runtime is installed (it is on Windows 11 and on up-to-date Windows 10).</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString() is not null; }
            catch (WebView2RuntimeNotFoundException) { return false; }
        }
    }

    /// <summary>Forgets the window's Google session (on sign-out), so the next sign-in starts fresh.</summary>
    public static void ClearSession(string dataFolder)
    {
        try { if (Directory.Exists(dataFolder)) Directory.Delete(dataFolder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use; harmless */ }
    }

    private async Task StartAsync()
    {
        try
        {
            // A window closed moments ago can still hold the profile folder ("resource in use"): retry briefly.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, _dataFolder);
                    await _web.EnsureCoreWebView2Async(env);
                    break;
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x800700AA) && attempt < 10)
                {
                    await Task.Delay(500);
                }
            }
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException
                                       or ArgumentException or UnauthorizedAccessException or DllNotFoundException)
        {
            Failure = ex;
            Services.AppLog.Write("sign-in window: " + ex);
            Error = ex is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 runtime isn't installed."
                : "The sign-in window couldn't start (" + ex.Message + ").";
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Google opens some steps (help, account chooser) as pop-ups: keep everything in this window.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && !_done)
            {
                _web.Visibility = Visibility.Collapsed;
                _status.Text = $"Couldn't reach Google ({e.WebErrorStatus}).\nCheck your connection or firewall, then close and try again.";
                _status.TextAlignment = TextAlignment.Center;
                return;
            }
            await TryFinishAsync();
        };
        core.Navigate(_url);
    }

    /// <summary>Once the sign-in has landed on YouTube Music, take its cookies and close.</summary>
    private async Task TryFinishAsync()
    {
        if (_done || _web.CoreWebView2 is not { } core) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)) return;

        var cookies = await core.CookieManager.GetCookiesAsync("https://music.youtube.com");
        var jar = new Dictionary<string, string>();
        // Prefer the .youtube.com domain cookies over host-only duplicates.
        foreach (var c in cookies.OrderBy(c => c.Domain == ".youtube.com" ? 0 : 1))
            jar.TryAdd(c.Name, c.Value);
        if (!jar.ContainsKey("SAPISID") && !jar.ContainsKey("__Secure-3PAPISID")) return;

        _done = true;
        CookieHeader = string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
        _status.Text = "Signed in.";
        _web.Visibility = Visibility.Collapsed;
        DialogResult = true;
    }
}
