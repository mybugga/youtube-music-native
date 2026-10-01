using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public sealed partial class HomeViewModel(
    InnerTubeClient api, PlaybackService playback, Action<PlaylistInfo> openPlaylist, Action openNowPlaying)
    : ObservableObject
{
    /// <summary>Anonymous feeds start with very few shelves; fetch this many before stopping to wait for scrolling.</summary>
    private const int MinSections = 6;

    private ContinuationToken? _next;
    private bool _loaded;

    public ObservableCollection<HomeSection> Sections { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good evening",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    public async Task EnsureLoadedAsync()
    {
        OnPropertyChanged(nameof(Greeting));
        if (!_loaded) await RefreshAsync();
    }

    /// <summary>Forces a reload next time (e.g. after signing in, the feed becomes personal).</summary>
    public void Reset()
    {
        _loaded = false;
        Sections.Clear();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            var page = await api.GetHomeAsync();
            Sections.Clear();
            Append(page);
            while (Sections.Count < MinSections && _next is { } token)
                Append(await api.ContinueHomeAsync(token));
            _loaded = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || _next is not { } token) return;
        IsLoading = true;
        try
        {
            Append(await api.ContinueHomeAsync(token));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Append(HomePage page)
    {
        foreach (var s in page.Sections) Sections.Add(s);
        _next = page.Next;
    }

    /// <summary>Card click: songs start playing and show the Now Playing screen; everything else opens its page.</summary>
    [RelayCommand]
    private void Open(MediaItem? item)
    {
        if (item is null) return;
        if (item.Kind == ItemKind.Song)
        {
            playback.PlayList([item.ToTrack()], 0);
            openNowPlaying();
        }
        else openPlaylist(item.ToPlaylist());
    }

    /// <summary>Card play button: plays a song, or the whole playlist/album/artist's top songs without navigating.</summary>
    [RelayCommand]
    private async Task PlayAsync(MediaItem? item)
    {
        if (item is null) return;
        if (item.Kind == ItemKind.Song)
        {
            playback.PlayList([item.ToTrack()], 0);
            return;
        }
        try
        {
            var page = await api.GetPlaylistAsync(item.BrowseId!);
            var tracks = page.Tracks.Select(t => t.ThumbnailUrl is null ? t with { ThumbnailUrl = item.ThumbnailUrl } : t).ToList();
            if (tracks.Count > 0) playback.PlayList(tracks, 0);
            else playback.ShowStatus($"Nothing playable in \"{item.Title}\"");
        }
        catch (Exception ex)
        {
            playback.ShowStatus("Couldn't load: " + ex.Message);
        }
    }
}
