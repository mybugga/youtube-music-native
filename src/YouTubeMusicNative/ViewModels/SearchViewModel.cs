using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public sealed partial class SearchViewModel : TrackListViewModel
{
    private const int MaxRecent = 12;

    private readonly InnerTubeClient api;
    private readonly SettingsStore _store;
    private readonly Action<string> _runQuery;
    private ContinuationToken? _next;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowRecent), nameof(ShowIntro))]
    private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowRecent), nameof(ShowIntro))]
    private bool _hasSearched;

    /// <param name="runQuery">Puts a query in the search box and searches (used by recent searches).</param>
    public SearchViewModel(InnerTubeClient api, PlaybackService playback, SettingsStore store, Action<string> runQuery)
        : base(playback)
    {
        this.api = api;
        _store = store;
        _runQuery = runQuery;
        RecentSearches = new ObservableCollection<string>(store.Settings.RecentSearches.Take(MaxRecent));
        RecentSearches.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowRecent));
            OnPropertyChanged(nameof(ShowIntro));
        };
    }

    public ObservableCollection<string> RecentSearches { get; }

    /// <summary>Results for the current query; with an empty box the page shows recent searches instead.</summary>
    public bool ShowResults => HasSearched && Query.Trim().Length > 0;
    public bool ShowRecent => !ShowResults && RecentSearches.Count > 0;
    public bool ShowIntro => !ShowResults && RecentSearches.Count == 0;

    /// <summary>Remembers the current query (on Enter, or when a result is played).</summary>
    public void RememberQuery()
    {
        var q = Query.Trim();
        if (q.Length == 0) return;
        if (RecentSearches.FirstOrDefault(r => string.Equals(r, q, StringComparison.OrdinalIgnoreCase)) is { } existing)
            RecentSearches.Remove(existing);
        RecentSearches.Insert(0, q);
        while (RecentSearches.Count > MaxRecent) RecentSearches.RemoveAt(RecentSearches.Count - 1);
        SaveRecent();
    }

    [RelayCommand]
    private void UseRecent(string? query)
    {
        if (!string.IsNullOrWhiteSpace(query)) _runQuery(query);
    }

    [RelayCommand]
    private void RemoveRecent(string? query)
    {
        if (query is not null && RecentSearches.Remove(query)) SaveRecent();
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentSearches.Clear();
        SaveRecent();
    }

    private void SaveRecent()
    {
        _store.Settings.RecentSearches = [.. RecentSearches];
        _store.Save();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = Query.Trim();
        if (q.Length == 0) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = false; // a new search supersedes any in-flight one
        await RunAsync(async () =>
        {
            var page = await api.SearchSongsAsync(q, ct);
            Tracks.Clear();
            foreach (var t in page.Tracks) Tracks.Add(t);
            _next = page.Next;
            HasSearched = true;
        });
    }

    /// <summary>Called by the view when the list is scrolled near the bottom.</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_next is not { } token || IsLoading) return;
        var ct = _cts?.Token ?? default;
        await RunAsync(async () =>
        {
            var page = await api.ContinueSearchAsync(token, ct);
            var known = Tracks.Select(t => t.VideoId).ToHashSet();
            foreach (var t in page.Tracks.Where(t => known.Add(t.VideoId))) Tracks.Add(t);
            _next = page.Next;
        });
    }

    /// <summary>Like YouTube Music: playing a search result starts that song (radio follows if autoplay is on).</summary>
    protected override void Play(Track? track)
    {
        if (track is null) return;
        RememberQuery();
        Playback.PlayList([track], 0);
    }
}
