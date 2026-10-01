using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

/// <summary>The shelves under an artist's top songs (the list's last "item", so they scroll with the page).</summary>
public sealed record ArtistShelves(ArtistViewModel Artist);

/// <summary>
/// An artist's page, like YouTube Music's: banner header with Shuffle / Mix, top songs (with "Show all"), then
/// albums, singles, videos, playlists and similar artists.
/// </summary>
public sealed partial class ArtistViewModel : TrackListViewModel
{
    private readonly InnerTubeClient _api;
    private readonly Action<PlaylistInfo> _openPlaylist;
    private ArtistPage? _page;

    public string BrowseId { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _audience;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _bannerUrl;
    [ObservableProperty] private Color _headerColor = ArtColor.Fallback;
    [ObservableProperty] private bool _hasShowAll;

    public ObservableCollection<HomeSection> Sections { get; } = [];

    /// <param name="openPlaylist">Opens albums / playlists (and other artists: the shell routes "UC…" ids to an artist page).</param>
    public ArtistViewModel(InnerTubeClient api, PlaybackService playback, string browseId, string? name,
        Action<PlaylistInfo> openPlaylist) : base(playback)
    {
        _api = api;
        _openPlaylist = openPlaylist;
        BrowseId = browseId;
        _name = name ?? "";
        ListItems.Add(new ArtistShelves(this));
    }

    public async Task LoadAsync() => await RunAsync(async () =>
    {
        var page = await _api.GetArtistAsync(BrowseId);
        _page = page;
        Name = page.Name;
        Audience = page.Audience;
        Description = page.Description;
        BannerUrl = page.BannerUrl;
        HasShowAll = page.TopSongsBrowseId is not null;
        Tracks.Clear();
        LibraryActions.Instance?.Observe(page.TopSongs);
        foreach (var t in page.TopSongs) Tracks.Add(t);
        Sections.Clear();
        foreach (var s in page.Sections) Sections.Add(s);
        HeaderColor = await ArtColor.GetAsync(page.ThumbnailUrl ?? page.TopSongs.FirstOrDefault()?.ThumbnailUrl);
    });

    [RelayCommand]
    private async Task ShuffleAsync() => await PlayWatchAsync(_page?.Shuffle, shuffleTopSongs: true);

    [RelayCommand]
    private async Task MixAsync() => await PlayWatchAsync(_page?.Mix, shuffleTopSongs: false);

    /// <summary>Plays what the button points at; without one (or offline), falls back to the top songs.</summary>
    private async Task PlayWatchAsync(WatchTarget? target, bool shuffleTopSongs)
    {
        List<Track> tracks = [];
        if (target is not null)
        {
            try
            {
                tracks = await _api.GetWatchPlaylistAsync(target);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Playback.ShowStatus("Couldn't load that: " + ex.Message);
            }
        }
        if (tracks.Count == 0)
        {
            tracks = Tracks.ToList();
            if (shuffleTopSongs) Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tracks));
        }
        if (tracks.Count == 0) return;
        Playback.Queue.Shuffle = false;
        Playback.PlayList(tracks, 0);
    }

    /// <summary>"Show all" next to Top songs: the artist's full songs playlist.</summary>
    [RelayCommand]
    private void ShowAll()
    {
        if (_page?.TopSongsBrowseId is { } id)
            _openPlaylist(new PlaylistInfo(id, $"{Name}: Songs", Name, _page.ThumbnailUrl));
    }

    /// <summary>Card click: songs and videos play; albums, playlists and artists open.</summary>
    [RelayCommand]
    private void Open(MediaItem? item)
    {
        if (item is null) return;
        if (item.Kind == ItemKind.Song) Playback.PlayList([item.ToTrack()], 0);
        else _openPlaylist(item.ToPlaylist());
    }

    /// <summary>The shared media card's play button (see the "Card" template).</summary>
    public IAsyncRelayCommand<MediaItem> CardPlayCommand => PlayItemCommand;

    /// <summary>Card play button: plays a song, or the album / playlist without opening it.</summary>
    [RelayCommand]
    private async Task PlayItemAsync(MediaItem? item)
    {
        if (item is null) return;
        if (item.Kind == ItemKind.Song || item.BrowseId is null)
        {
            Open(item);
            return;
        }
        try
        {
            List<Track> tracks;
            if (item.Kind == ItemKind.Artist) tracks = [.. (await _api.GetArtistAsync(item.BrowseId)).TopSongs];
            else tracks = [.. (await _api.GetPlaylistAsync(item.BrowseId)).Tracks];
            tracks = tracks.Select(t => t.ThumbnailUrl is null ? t with { ThumbnailUrl = item.ThumbnailUrl } : t).ToList();
            if (tracks.Count > 0) Playback.PlayList(tracks, 0);
            else Playback.ShowStatus($"Nothing playable in \"{item.Title}\"");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Playback.ShowStatus("Couldn't load that: " + ex.Message);
        }
    }
}
