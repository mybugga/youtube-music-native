using YouTubeMusicNative.Api;
using YouTubeMusicNative.Services;

namespace YouTubeMusicNative.Tests;

public class QueueTests
{
    private static Track T(string id) => new(id, id, "artist", null, null, null);
    private static readonly Track[] Five = [T("a"), T("b"), T("c"), T("d"), T("e")];

    [Fact]
    public void Advances_and_stops_at_end_without_repeat()
    {
        var q = new QueueService();
        q.ReplaceAndPlay(Five, 3);

        Assert.Equal("e", q.MoveNext(auto: true)?.VideoId);
        Assert.Null(q.MoveNext(auto: true));
        Assert.Equal("e", q.Current?.VideoId);
    }

    [Fact]
    public void Repeat_all_wraps_and_repeat_one_stays_on_auto_only()
    {
        var q = new QueueService();
        q.ReplaceAndPlay(Five, 4);
        q.Repeat = RepeatMode.All;
        Assert.Equal("a", q.MoveNext(auto: true)?.VideoId);

        q.Repeat = RepeatMode.One;
        Assert.Equal("a", q.MoveNext(auto: true)?.VideoId);
        Assert.Equal("b", q.MoveNext(auto: false)?.VideoId); // pressing Next still skips
    }

    [Fact]
    public void Shuffle_keeps_history_and_current_in_place()
    {
        var q = new QueueService();
        q.ReplaceAndPlay(Five, 1);
        q.Shuffle = true;

        Assert.Equal(["a", "b"], q.Items.Take(2).Select(t => t.VideoId));
        Assert.Equal(["c", "d", "e"], q.Items.Skip(2).Select(t => t.VideoId).Order());
        Assert.Equal("b", q.Current?.VideoId);
    }

    [Fact]
    public void Remove_before_current_keeps_current_track()
    {
        var q = new QueueService();
        q.ReplaceAndPlay(Five, 2);
        q.RemoveAt(0);
        q.RemoveAt(q.CurrentIndex); // ignored: can't remove the playing track

        Assert.Equal("c", q.Current?.VideoId);
        Assert.Equal(4, q.Items.Count);
    }

    [Fact]
    public void Add_next_inserts_after_current()
    {
        var q = new QueueService();
        q.ReplaceAndPlay(Five, 0);
        q.AddNext(T("x"));

        Assert.Equal("x", q.MoveNext(auto: false)?.VideoId);
    }
}
