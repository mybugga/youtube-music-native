using System.IO;
using System.Text.Json;
using YouTubeMusicNative.Api;

namespace YouTubeMusicNative.Services;

/// <summary>A playlist kept on this PC only (works without signing in).</summary>
public sealed class LocalPlaylist
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Created { get; set; }
    public List<Track> Tracks { get; set; } = [];
}

/// <summary>
/// Playlists and liked songs stored on this PC, in library.json next to the settings. Used whether or not the
/// user is signed in; they show in Your Library with a "Local" tag.
/// </summary>
public sealed class LocalLibrary
{
    /// <summary>Browse id prefix of local playlists; the local liked songs use <see cref="LikedId"/>.</summary>
    public const string Prefix = "local:";
    public const string LikedId = "local:liked";

    private sealed class Data
    {
        public List<LocalPlaylist> Playlists { get; set; } = [];
        public List<Track> Liked { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _path;
    private Data _data = new();

    public LocalLibrary(string directory)
    {
        _path = Path.Combine(directory, "library.json");
        try
        {
            if (File.Exists(_path)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Write("library.json unreadable: " + ex.Message);
        }
    }

    public static bool IsLocalId(string? id) => id?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Raised after any change (playlists, songs or likes).</summary>
    public event Action? Changed;

    public IReadOnlyList<LocalPlaylist> Playlists => _data.Playlists;
    public IReadOnlyList<Track> Liked => _data.Liked;

    public bool IsLiked(string videoId) => _data.Liked.Any(t => t.VideoId == videoId);

    /// <summary>Likes or unlikes a song; returns whether it's liked now.</summary>
    public bool ToggleLike(Track track)
    {
        var existing = _data.Liked.FindIndex(t => t.VideoId == track.VideoId);
        if (existing >= 0) _data.Liked.RemoveAt(existing);
        else _data.Liked.Insert(0, Clean(track));
        Save();
        return existing < 0;
    }

    public LocalPlaylist Create(string title, string description, Track? firstSong) =>
        Create(title, description, firstSong is null ? Array.Empty<Track>() : new[] { firstSong });

    /// <summary>A new playlist with these songs (a copied or imported one); repeated songs are kept once.</summary>
    public LocalPlaylist Create(string title, string description, IEnumerable<Track> songs)
    {
        var playlist = new LocalPlaylist
        {
            Id = Prefix + Guid.NewGuid().ToString("N")[..12],
            Title = title,
            Description = description,
            Created = DateTime.UtcNow,
            Tracks = songs.DistinctBy(t => t.VideoId).Select(Clean).ToList(),
        };
        _data.Playlists.Insert(0, playlist);
        Save();
        return playlist;
    }

    /// <summary>The title, or "title (2)", "title (3)"… if a local playlist already has it.</summary>
    public string UniqueTitle(string title)
    {
        var taken = _data.Playlists.Select(p => p.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(title)) return title;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{title} ({n})")) return $"{title} ({n})";
    }

    public LocalPlaylist? Find(string id) => _data.Playlists.FirstOrDefault(p => p.Id == id);

    public void Delete(string id)
    {
        if (_data.Playlists.RemoveAll(p => p.Id == id) > 0) Save();
    }

    /// <summary>Adds a song; false if it was already there.</summary>
    public bool Add(string playlistId, Track track)
    {
        if (Find(playlistId) is not { } playlist || playlist.Tracks.Any(t => t.VideoId == track.VideoId)) return false;
        playlist.Tracks.Add(Clean(track));
        Save();
        return true;
    }

    public void Remove(string playlistId, string videoId)
    {
        if (playlistId == LikedId)
        {
            if (_data.Liked.RemoveAll(t => t.VideoId == videoId) > 0) Save();
            return;
        }
        if (Find(playlistId) is { } playlist && playlist.Tracks.RemoveAll(t => t.VideoId == videoId) > 0) Save();
    }

    /// <summary>Songs of a local playlist (or the local liked songs).</summary>
    public IReadOnlyList<Track> TracksOf(string id) =>
        id == LikedId ? _data.Liked : Find(id)?.Tracks ?? [];

    /// <summary>The song itself, without where it was opened from (that's per page, not stored).</summary>
    private static Track Clean(Track t) => t with { SetVideoId = null, SourcePlaylistId = null, SourcePlaylistTitle = null, Liked = null };

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
        catch (IOException ex)
        {
            AppLog.Write("couldn't save library.json: " + ex.Message);
        }
        Changed?.Invoke();
    }
}
