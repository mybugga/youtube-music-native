using System.Text.Json;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.Tests;

public class SessionTests
{
    [Theory]
    [InlineData("3:32", 212)]
    [InlineData("1:02:03", 3723)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("live", 0)]
    public void Parses_listed_durations(string? text, double seconds) =>
        Assert.Equal(seconds, PlaybackService.ParseDuration(text));

    [Fact]
    public void Session_round_trips_through_json()
    {
        var session = new SessionState
        {
            Queue = [new Track("abc", "Song", "Artist", "Album", "2:53", "https://x/y.jpg"), new Track("def", "Two", "B", null, null, null)],
            Index = 1,
            Position = 42.5,
            Shuffle = true,
            Repeat = RepeatMode.One,
        };
        var back = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(session))!;
        Assert.Equal(session.Queue, back.Queue);
        Assert.Equal(1, back.Index);
        Assert.Equal(42.5, back.Position);
        Assert.True(back.Shuffle);
        Assert.Equal(RepeatMode.One, back.Repeat);
    }

    [Fact]
    public void Restored_queue_keeps_its_order_and_selection()
    {
        var q = new QueueService();
        var tracks = Enumerable.Range(0, 5).Select(i => new Track($"v{i}", $"t{i}", "a", null, null, null)).ToList();
        q.Restore(tracks, 3, shuffle: true, RepeatMode.All);
        Assert.Equal(tracks, q.Items);
        Assert.Equal("v3", q.Current!.VideoId);
        Assert.True(q.Shuffle);
        Assert.Equal(RepeatMode.All, q.Repeat);
    }
}
