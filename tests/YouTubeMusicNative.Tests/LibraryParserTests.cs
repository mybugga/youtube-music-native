using System.Text.Json.Nodes;
using YouTubeMusicNative.Api;
using YouTubeMusicNative.Api.Parsers;

namespace YouTubeMusicNative.Tests;

public class LibraryParserTests
{
    [Fact]
    public void Parses_active_account_name_handle_and_bigger_photo()
    {
        var json = JsonNode.Parse("""
            {"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{
              "accountName":{"runs":[{"text":"Some Channel"}]},
              "channelHandle":{"runs":[{"text":"@somechannel"}]},
              "accountPhoto":{"thumbnails":[{"url":"https://yt3.ggpht.com/abc=s108-c-k-c0x00ffffff-no-rj","width":108,"height":108}]}
            }}}}}}]}
            """);
        var account = AccountParser.ParseAccount(json)!;
        Assert.Equal("Some Channel", account.Name);
        Assert.Equal("@somechannel", account.Handle);
        Assert.Equal("https://yt3.ggpht.com/abc=s240-c-k-c0x00ffffff-no-rj", account.PhotoUrl);
    }

    [Fact]
    public void No_account_header_means_no_account() =>
        Assert.Null(AccountParser.ParseAccount(JsonNode.Parse("""{"actions":[]}""")));

    [Fact]
    public void Parses_add_to_playlist_options()
    {
        var json = JsonNode.Parse("""
            {"contents":[{"addToPlaylistRenderer":{"playlists":[
              {"playlistAddToOptionRenderer":{"playlistId":"PLabc","title":{"runs":[{"text":"Road trip"}]},
                "shortBylineText":{"runs":[{"text":"12 songs"}]}}},
              {"playlistAddToOptionRenderer":{"playlistId":"LM","title":{"runs":[{"text":"Liked Music"}]}}}
            ]}}]}
            """);
        var options = AccountParser.ParseAddToPlaylist(json);
        Assert.Equal(2, options.Count);
        Assert.Equal(new PlaylistOption("PLabc", "Road trip", "12 songs", null), options[0]);
        Assert.Null(options[1].Subtitle);
    }

    [Fact]
    public void Playlist_rows_carry_set_video_id_and_like_status()
    {
        var json = JsonNode.Parse("""
            {"contents":{"musicPlaylistShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{
              "playlistItemData":{"videoId":"vid00000001","playlistSetVideoId":"SET123"},
              "flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song"}]}}}],
              "menu":{"menuRenderer":{"topLevelButtons":[{"likeButtonRenderer":{"likeStatus":"LIKE"}}]}}
            }}]}},
             "header":{"musicEditablePlaylistDetailHeaderRenderer":{}}}
            """);
        var page = PlaylistParser.ParsePlaylistPage(json);
        Assert.True(page.IsEditable);
        var track = Assert.Single(page.Tracks);
        Assert.Equal("SET123", track.SetVideoId);
        Assert.True(track.Liked);
    }

    [Fact]
    public void Rows_without_like_button_have_unknown_like_status()
    {
        var json = JsonNode.Parse("""
            {"contents":{"musicShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{
              "playlistItemData":{"videoId":"vid00000002"},
              "flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song"}]}}}]
            }}]}}}
            """);
        var page = PlaylistParser.ParsePlaylistPage(json);
        Assert.False(page.IsEditable);
        Assert.Null(Assert.Single(page.Tracks).Liked);
    }

    [Theory]
    [InlineData("VLLM", "LM", true, false)]
    [InlineData("VLPLmine", "PLmine", false, true)]
    public void Playlist_ids_and_built_ins(string browseId, string playlistId, bool builtIn, bool canDeleteWhenOwned)
    {
        var info = new PlaylistInfo(browseId, "t", "s", null) { IsOwned = true };
        Assert.Equal(playlistId, info.PlaylistId);
        Assert.Equal(builtIn, info.IsBuiltIn);
        Assert.Equal(canDeleteWhenOwned, info.CanDelete);
        Assert.False(info.CanRemoveFromLibrary);
    }
}
