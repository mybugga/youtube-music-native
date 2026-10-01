using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public sealed partial class PlaylistViewModel : TrackListViewModel
{
    private readonly InnerTubeClient _api;
    private ContinuationToken? _next;

    public PlaylistInfo Info { get; }

    [ObservableProperty] private bool _hasMore;

    /// <summary>The user's own playlist: songs can be removed and the playlist deleted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveTracks), nameof(CanDelete))]
    private bool _isEditable;

    public bool CanRemoveTracks => IsEditable && !Info.IsBuiltIn;
    public bool CanDelete => IsEditable && !Info.IsBuiltIn;
    public bool IsLikedSongs => Info.PlaylistId == "LM";
    [ObservableProperty] private Color _headerColor = ArtColor.Fallback;

    public string TrackCountText => Tracks.Count == 0 ? "" : HasMore ? $"{Tracks.Count}+ songs" : $"{Tracks.Count} songs";

    public PlaylistViewModel(InnerTubeClient api, PlaybackService playback, PlaylistInfo info) : base(playback)
    {
        _api = api;
        Info = info;
        Tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TrackCountText));
    }

    public async Task LoadAsync()
    {
        _ = LoadHeaderColorAsync();
        await RunAsync(async () =>
        {
            var page = await _api.GetPlaylistAsync(Info.BrowseId);
            IsEditable = page.IsEditable || Info.IsOwned;
            Tracks.Clear();
            AddTracks(page.Tracks);
            _next = page.Next;
            HasMore = _next != null;
        });
    }

    private async Task LoadHeaderColorAsync() => HeaderColor = await ArtColor.GetAsync(Info.ThumbnailUrl);

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_next is not { } token || IsLoading) return;
        await RunAsync(async () =>
        {
            var page = await _api.ContinuePlaylistAsync(token);
            AddTracks(page.Tracks);
            _next = page.Next;
            HasMore = _next != null;
        });
    }

    /// <summary>Album rows carry no art of their own; give them the album cover.</summary>
    private void AddTracks(IReadOnlyList<Track> tracks)
    {
        LibraryActions.Instance?.Observe(tracks);
        foreach (var t in tracks)
            Tracks.Add(t.ThumbnailUrl is null ? t with { ThumbnailUrl = Info.ThumbnailUrl, Album = t.Album ?? Info.Title } : t);
    }

    partial void OnHasMoreChanged(bool value) => OnPropertyChanged(nameof(TrackCountText));

    [RelayCommand]
    private async Task RemoveFromPlaylistAsync(Track? track)
    {
        if (track is null || !CanRemoveTracks) return;
        int index = Tracks.IndexOf(track);
        if (index >= 0) Tracks.RemoveAt(index); // optimistic
        try
        {
            await _api.RemoveFromPlaylistAsync(Info.PlaylistId, track);
            Playback.ShowStatus($"Removed from {Info.Title}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (index >= 0) Tracks.Insert(Math.Min(index, Tracks.Count), track);
            Playback.ShowStatus("Couldn't remove the song: " + ex.Message);
        }
    }

    /// <summary>Keeps the Liked Music page in step with hearts clicked anywhere in the app.</summary>
    public void OnLikeChanged(Track track, bool liked)
    {
        if (!IsLikedSongs) return;
        var existing = Tracks.FirstOrDefault(t => t.VideoId == track.VideoId);
        if (liked && existing is null) Tracks.Insert(0, track);
        else if (!liked && existing is not null) Tracks.Remove(existing);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count == 0) return;
        Playback.Queue.Shuffle = false;
        Playback.PlayList(Tracks.ToList(), 0);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        if (Tracks.Count == 0) return;
        var list = Tracks.ToArray();
        Random.Shared.Shuffle(list);
        Playback.Queue.Shuffle = true;
        Playback.PlayList(list, 0);
    }
}
