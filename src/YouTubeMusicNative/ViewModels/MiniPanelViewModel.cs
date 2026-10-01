using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.ViewModels;

public enum MiniTab { Queue, Search, Library }

/// <summary>
/// The mini player's pull-down drawer: up next, search and library in a compact layout. Reuses the main
/// window's view models (queue, search, library), so state is shared with the full player.
/// </summary>
public sealed partial class MiniPanelViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    public MiniPanelViewModel(MainViewModel main, SettingsStore store)
    {
        Main = main;
        _store = store;
        _isExpanded = store.Settings.MiniExpanded;
        _tab = Enum.TryParse<MiniTab>(store.Settings.MiniTab, out var tab) ? tab : MiniTab.Queue;
        if (_tab == MiniTab.Library) _ = Main.Library.EnsureLoadedAsync();
    }

    public MainViewModel Main { get; }

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabName), nameof(IsQueueTab), nameof(IsSearchTab), nameof(IsLibraryTab))]
    private MiniTab _tab;

    /// <summary>A playlist opened from the Library tab (shown in place of the playlist list).</summary>
    [ObservableProperty] private PlaylistViewModel? _openPlaylist;

    public string TabName => Tab.ToString();
    public bool IsQueueTab => Tab == MiniTab.Queue;
    public bool IsSearchTab => Tab == MiniTab.Search;
    public bool IsLibraryTab => Tab == MiniTab.Library;

    partial void OnIsExpandedChanged(bool value) => _store.Settings.MiniExpanded = value;

    partial void OnTabChanged(MiniTab value) => _store.Settings.MiniTab = value.ToString();

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SelectTab(string? name)
    {
        if (!Enum.TryParse<MiniTab>(name, out var tab)) return;
        // Clicking Library again while inside a playlist goes back to the list.
        if (tab == MiniTab.Library && Tab == MiniTab.Library) OpenPlaylist = null;
        Tab = tab;
        IsExpanded = true;
        if (tab == MiniTab.Library) _ = Main.Library.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void ShowPlaylist(PlaylistInfo? info)
    {
        if (info is null) return;
        var page = Main.CreatePlaylistPage(info);
        OpenPlaylist = page;
        _ = page.LoadAsync();
    }

    [RelayCommand]
    private void ClosePlaylist() => OpenPlaylist = null;
}
