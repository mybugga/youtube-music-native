using System.IO;
using System.Text.Json;
using YouTubeMusicNative.Api;

namespace YouTubeMusicNative.Services;

/// <summary>
/// A copy of the signed-in user's YouTube library (the playlist list and the songs of each playlist) kept on this PC,
/// so it stays browsable and playable after signing out or while offline. Shown with a "Cached" tag then.
/// Stored in cache.json; "Clear cached library" in Settings deletes it.
/// </summary>
public sealed class LibraryCache
{
    private sealed class Data
    {
        public List<PlaylistInfo> Playlists { get; set; } = [];
        public Dictionary<string, List<Track>> Tracks { get; set; } = [];
    }

    private readonly string _path;
    private Data _data = new();

    public LibraryCache(string directory)
    {
        _path = Path.Combine(directory, "cache.json");
        try
        {
            if (File.Exists(_path)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            AppLog.Write("cache.json unreadable: " + ex.Message);
        }
    }

    public IReadOnlyList<PlaylistInfo> Playlists => _data.Playlists;
    public bool IsEmpty => _data.Playlists.Count == 0;

    /// <summary>Raised after the cache is cleared.</summary>
    public event Action? Cleared;

    public bool HasTracks(string browseId) => _data.Tracks.ContainsKey(browseId);

    public IReadOnlyList<Track>? TracksOf(string browseId) => _data.Tracks.GetValueOrDefault(browseId);

    public void SetPlaylists(IEnumerable<PlaylistInfo> playlists)
    {
        _data.Playlists = playlists.Select(p => p with { Source = PlaylistSource.YouTube }).ToList();
        // Forget songs of playlists that are gone from the library.
        var keep = _data.Playlists.Select(p => p.BrowseId).ToHashSet();
        foreach (var gone in _data.Tracks.Keys.Where(k => !keep.Contains(k)).ToList()) _data.Tracks.Remove(gone);
        Save();
    }

    public void SetTracks(string browseId, IEnumerable<Track> tracks)
    {
        _data.Tracks[browseId] = tracks.Select(t => t with { SourcePlaylistId = null, SourcePlaylistTitle = null }).ToList();
        Save();
    }

    public void Clear()
    {
        _data = new();
        try { File.Delete(_path); } catch (IOException) { }
        Cleared?.Invoke();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_data));
        }
        catch (IOException ex)
        {
            AppLog.Write("couldn't save cache.json: " + ex.Message);
        }
    }
}
