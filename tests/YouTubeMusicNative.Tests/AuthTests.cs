using YouTubeMusicNative.Api;

namespace YouTubeMusicNative.Tests;

public class AuthTests
{
    [Fact]
    public void Parses_cookie_header_and_builds_sapisidhash()
    {
        var auth = Auth.Parse("cookie: VISITOR_INFO1_LIVE=v; SAPISID=abcSAPISID; HSID=h=with=equals");

        Assert.NotNull(auth);
        Assert.Equal("h=with=equals", auth.Cookies["HSID"]);
        Assert.Equal(
            "SAPISIDHASH 1700000000_958be2d45afc2262a8777d73de623e5ba13eed75",
            auth.AuthorizationHeader(DateTimeOffset.FromUnixTimeSeconds(1700000000)));
    }

    [Fact]
    public void Parses_netscape_file_including_httponly_lines()
    {
        var text = "# Netscape HTTP Cookie File\n" +
                   ".youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\tabc\n" +
                   "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tsid\n" +
                   ".google.com\tTRUE\t/\tTRUE\t0\tNID\tignored\n";

        var auth = Auth.Parse(text);

        Assert.NotNull(auth);
        Assert.Equal("sid", auth.Cookies["SID"]);
        Assert.False(auth.Cookies.ContainsKey("NID"));
    }

    [Fact]
    public void Netscape_export_round_trips()
    {
        var auth = Auth.Parse("SAPISID=abc; SID=xyz")!;
        var again = Auth.Parse(auth.ToNetscape());

        Assert.NotNull(again);
        Assert.Equal(auth.CookieHeader, again.CookieHeader);
    }

    [Fact]
    public void Returns_null_when_not_signed_in() =>
        Assert.Null(Auth.Parse("VISITOR_INFO1_LIVE=v; PREF=f6=40000000"));

    [Fact]
    public void Reads_session_index_and_brand_channel_from_page_config()
    {
        var html = """<script>ytcfg.set({"LOGGED_IN":true,"SESSION_INDEX":"1","DELEGATED_SESSION_ID":"1026694529"});</script>""";
        var session = InnerTubeClient.ParseSession(html);
        Assert.Equal("1", session.AuthUser);
        Assert.Equal("1026694529", session.PageId);

        var plain = InnerTubeClient.ParseSession("""{"LOGGED_IN":false}""");
        Assert.Equal("0", plain.AuthUser);
        Assert.Null(plain.PageId);
    }
}
