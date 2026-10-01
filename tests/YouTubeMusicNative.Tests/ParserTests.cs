using System.IO;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Api.Parsers;

namespace YouTubeMusicNative.Tests;

/// <summary>Parsers against real InnerTube responses captured into fixtures/.</summary>
public class ParserTests(ITestOutputHelper output)
{
    private static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;

    private void Dump(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks.Take(8))
            output.WriteLine($"{t.VideoId} | {t.Title} | {t.Artists} | {t.Album} | {t.Duration} | {t.ThumbnailUrl?[..Math.Min(50, t.ThumbnailUrl.Length)]}");
    }

    [Fact]
    public void Search_songs_parses_tracks_with_metadata()
    {
        var page = PlaylistParser.ParsePlaylistPage(Load("search_songs.json"));
        Dump(page.Tracks);

        Assert.True(page.Tracks.Count >= 10, $"only {page.Tracks.Count} tracks");
        Assert.All(page.Tracks, t =>
        {
            Assert.Equal(11, t.VideoId.Length);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Artists));
            Assert.NotNull(t.ThumbnailUrl);
        });
        Assert.True(page.Tracks.Count(t => t.Duration is not null) >= page.Tracks.Count * 0.8);
        Assert.Contains(page.Tracks, t => t.Artists.Contains("Daft Punk"));
        Assert.NotNull(page.Next);
    }

    [Fact]
    public void Radio_parses_panel_starting_with_seed()
    {
        var tracks = TrackParser.ParsePanelItems(Load("next_radio.json"));
        Dump(tracks);

        Assert.True(tracks.Count >= 20, $"only {tracks.Count} tracks");
        Assert.Equal("5NV6Rdv1a3I", tracks[0].VideoId);
        Assert.All(tracks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Artists));
        });
        Assert.Equal(tracks.Count, tracks.Select(t => t.VideoId).Distinct().Count());
    }

    [Fact]
    public void Playlist_page_parses_tracks()
    {
        var page = PlaylistParser.ParsePlaylistPage(Load("browse_playlist.json"));
        Dump(page.Tracks);
        output.WriteLine($"count={page.Tracks.Count} next={(page.Next is null ? "none" : page.Next.InBody ? "body" : "query")}");

        Assert.True(page.Tracks.Count >= 10, $"only {page.Tracks.Count} tracks");
        Assert.All(page.Tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Title)));
        Assert.True(page.Tracks.Count(t => t.Duration is not null) >= page.Tracks.Count * 0.8);
    }

    [Theory]
    [InlineData("browse_home.json")]
    [InlineData("browse_home_cont.json")]
    public void Home_parses_sections_with_openable_items(string fixture)
    {
        var page = HomeParser.Parse(Load(fixture));
        foreach (var s in page.Sections)
            output.WriteLine($"{s.Title} ({s.Strapline}) — {s.Items.Count}: {s.Items[0].Kind} {s.Items[0].Title} | {s.Items[0].Subtitle}");

        Assert.True(page.Sections.Count >= 2);
        Assert.All(page.Sections.SelectMany(s => s.Items), i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Title));
            Assert.NotNull(i.ThumbnailUrl);
            Assert.True(i.Kind == ItemKind.Song ? i.VideoId is not null : i.BrowseId is not null);
        });
        Assert.NotNull(page.Next);
    }

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/abc=w60-h60-l90-rj", 120, "https://lh3.googleusercontent.com/abc=w120-h120-l90-rj")]
    [InlineData("https://i.ytimg.com/vi/5NV6Rdv1a3I/hqdefault.jpg?sqp=abc&rs=def", 120, "https://i.ytimg.com/vi/5NV6Rdv1a3I/mqdefault.jpg")]
    [InlineData("https://example.com/a.jpg", 120, "https://example.com/a.jpg")]
    public void ResizeThumb_picks_small_variants(string url, int size, string expected) =>
        Assert.Equal(expected, JsonNav.ResizeThumb(url, size));
}
