using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using YouTubeMusicNative.Api;

namespace YouTubeMusicNative.Services;

/// <summary>
/// A playlist saved to a .json file (Export / Import): title, description and the songs, nothing about the account.
/// </summary>
public sealed class PlaylistFile
{
    private const string FormatName = "youtube-music-native-playlist";
    private const int CurrentVersion = 1;

    public string Format { get; set; } = "";
    public int Version { get; set; } = CurrentVersion;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Exported { get; set; }
    public List<Track> Tracks { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(string path, string title, string description, IEnumerable<Track> tracks)
    {
        var file = new PlaylistFile
        {
            Format = FormatName,
            Title = title,
            Description = description,
            Exported = DateTime.UtcNow,
            // Just the songs: where they were opened from and like status are this PC's business.
            Tracks = tracks.Where(t => !string.IsNullOrEmpty(t.VideoId)).DistinctBy(t => t.VideoId)
                .Select(t => t with { SetVideoId = null, SourcePlaylistId = null, SourcePlaylistTitle = null, Liked = null })
                .ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
    }

    /// <summary>Reads an exported playlist; throws <see cref="InvalidDataException"/> with a readable message if it isn't one.</summary>
    public static PlaylistFile Read(string path)
    {
        PlaylistFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PlaylistFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is null || file.Format != FormatName)
            throw new InvalidDataException("That file isn't a playlist exported from YouTube Music Native.");
        if (file.Version > CurrentVersion)
            throw new InvalidDataException("That playlist was exported by a newer version of the app. Update to import it.");
        file.Tracks = file.Tracks.Where(t => !string.IsNullOrWhiteSpace(t.VideoId) && t.Title is not null)
            .DistinctBy(t => t.VideoId).Select(t => t with { Artists = t.Artists ?? "" }).ToList();
        if (string.IsNullOrWhiteSpace(file.Title)) file.Title = Path.GetFileNameWithoutExtension(path);
        file.Description ??= "";
        return file;
    }

    /// <summary>A file name made from the playlist title.</summary>
    public static string FileNameFor(string title)
    {
        var name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return (name.Length == 0 ? "playlist" : name) + ".json";
    }
}
