using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YouTubeMusicNative.Api.Parsers;

namespace YouTubeMusicNative.Api;

/// <summary>Minimal client for the YouTube Music web (WEB_REMIX) InnerTube API.</summary>
public sealed class InnerTubeClient : IDisposable
{
    private const string BaseUrl = "https://music.youtube.com/youtubei/v1/";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    // ytmusicapi's search filter params: "EgWKAQ" + type + "AWoMEA4QChADEAQQCRAF". II = songs.
    private const string SongsFilterParams = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    private readonly HttpClient _http;
    private readonly string _clientVersion = "1." + DateTime.UtcNow.ToString("yyyyMMdd") + ".01.00";

    // Anonymous session id handed out by the first response; continuations are only valid within it.
    private string? _visitorId;

    public Auth? Auth
    {
        get => _auth;
        set
        {
            _auth = value;
            // A new account means a new session: drop the account slot/channel and the anonymous visitor id.
            _session = null;
            _visitorId = null;
        }
    }
    private Auth? _auth;
    private Task<SessionInfo>? _session;

    /// <summary>
    /// Which signed-in Google account (X-Goog-AuthUser) and which channel/brand account (X-Goog-PageId) the
    /// browser session is using. Without them, a browser signed in to several accounts, or using a brand
    /// channel, gets a different (often empty) library than the one shown on music.youtube.com.
    /// </summary>
    internal sealed record SessionInfo(string AuthUser, string? PageId);
    public bool IsLoggedIn => Auth != null;

    public InnerTubeClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false, // cookies are sent explicitly from Auth
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<TrackPage> SearchSongsAsync(string query, CancellationToken ct = default)
    {
        var body = NewBody();
        body["query"] = query;
        body["params"] = SongsFilterParams;
        var json = await PostAsync("search", body, null, ct);
        return PlaylistParser.ParsePlaylistPage(json);
    }

    public async Task<TrackPage> ContinueSearchAsync(ContinuationToken token, CancellationToken ct = default)
    {
        var json = await PostContinuationAsync("search", token, ct);
        return PlaylistParser.ParseContinuation(json);
    }

    /// <summary>The top card of an unfiltered search (an artist, album or playlist), or null.</summary>
    public async Task<MediaItem?> SearchTopResultAsync(string query, CancellationToken ct = default)
    {
        var body = NewBody();
        body["query"] = query;
        var json = await PostAsync("search", body, null, ct);
        return ArtistParser.ParseTopResult(json);
    }

    public async Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default)
    {
        var json = await BrowseAsync(browseId, ct);
        return ArtistParser.Parse(json, browseId);
    }

    /// <summary>The songs a Shuffle / Mix button plays (a watch playlist).</summary>
    public async Task<List<Track>> GetWatchPlaylistAsync(WatchTarget target, CancellationToken ct = default)
    {
        var body = NewBody();
        if (target.VideoId is { } videoId) body["videoId"] = videoId;
        if (target.PlaylistId is { } playlistId) body["playlistId"] = playlistId;
        if (target.Params is { } p) body["params"] = p;
        body["isAudioOnly"] = true;
        body["enablePersistentPlaylistPanel"] = true;
        var json = await PostAsync("next", body, null, ct);
        return TrackParser.ParsePanelItems(json);
    }

    /// <summary>Radio ("up next") tracks seeded from a song. The first entry is usually the seed itself.</summary>
    public async Task<List<Track>> GetRadioAsync(string videoId, CancellationToken ct = default)
    {
        var body = NewBody();
        body["videoId"] = videoId;
        body["playlistId"] = "RDAMVM" + videoId;
        body["isAudioOnly"] = true;
        body["enablePersistentPlaylistPanel"] = true;
        body["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL";
        body["watchEndpointMusicSupportedConfigs"] = new JsonObject
        {
            ["watchEndpointMusicConfig"] = new JsonObject
            {
                ["hasPersistentPlaylistPanel"] = true,
                ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV",
            },
        };
        var json = await PostAsync("next", body, null, ct);
        return TrackParser.ParsePanelItems(json);
    }

    public async Task<HomePage> GetHomeAsync(CancellationToken ct = default)
    {
        var json = await BrowseAsync("FEmusic_home", ct);
        return HomeParser.Parse(json);
    }

    public async Task<HomePage> ContinueHomeAsync(ContinuationToken token, CancellationToken ct = default)
    {
        var json = await PostContinuationAsync("browse", token, ct);
        return HomeParser.Parse(json);
    }

    public async Task<List<PlaylistInfo>> GetLibraryPlaylistsAsync(CancellationToken ct = default)
    {
        var json = await BrowseAsync("FEmusic_liked_playlists", ct);
        return PlaylistParser.ParseGrid(json);
    }

    /// <summary>Tracks of a playlist ("VL..." browse id). Liked songs = "VLLM".</summary>
    // ---- account & library editing ----------------------------------------------------------

    public async Task<AccountInfo?> GetAccountAsync(CancellationToken ct = default) =>
        AccountParser.ParseAccount(await PostAsync("account/account_menu", NewBody(), null, ct));

    /// <summary>
    /// The user's editable playlists. The endpoint wants a song to check against; any valid id works
    /// when only the list itself is needed.
    /// </summary>
    public async Task<List<PlaylistOption>> GetEditablePlaylistsAsync(string? videoId = null, CancellationToken ct = default)
    {
        var body = NewBody();
        body["videoIds"] = new JsonArray(videoId ?? "dQw4w9WgXcQ");
        return AccountParser.ParseAddToPlaylist(await PostAsync("playlist/get_add_to_playlist", body, null, ct));
    }

    /// <summary>Creates a playlist (optionally with songs) and returns its id.</summary>
    public async Task<string> CreatePlaylistAsync(string title, string description, PlaylistPrivacy privacy,
        IEnumerable<string>? videoIds = null, CancellationToken ct = default)
    {
        var body = NewBody();
        body["title"] = title;
        body["description"] = description;
        body["privacyStatus"] = privacy.ToString().ToUpperInvariant();
        if (videoIds?.ToList() is { Count: > 0 } ids)
            body["videoIds"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray());
        var json = await PostAsync("playlist/create", body, null, ct);
        return json.Str("playlistId") ?? throw new HttpRequestException("YouTube Music didn't return the new playlist.");
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var body = NewBody();
        body["playlistId"] = playlistId;
        await PostAsync("playlist/delete", body, null, ct);
    }

    /// <summary>Adds a song; returns false if it was already in the playlist.</summary>
    public async Task<bool> AddToPlaylistAsync(string playlistId, string videoId, CancellationToken ct = default)
    {
        var body = NewBody();
        body["playlistId"] = playlistId;
        body["actions"] = new JsonArray(new JsonObject
        {
            ["action"] = "ACTION_ADD_VIDEO",
            ["addedVideoId"] = videoId,
            ["dedupeOption"] = "DEDUPE_OPTION_CHECK",
        });
        var json = await PostAsync("browse/edit_playlist", body, null, ct);
        var status = json.Str("status");
        if (status == "STATUS_SUCCEEDED") return true;
        // A duplicate is refused (STATUS_FAILED) with an "already in the playlist" toast and an "Add anyway" button.
        if (json.FindFirst("addToToastAction") is not null) return false;
        throw new HttpRequestException($"YouTube Music couldn't add the song ({status ?? "no status"}).");
    }

    public Task RemoveFromPlaylistAsync(string playlistId, Track track, CancellationToken ct = default) =>
        EditPlaylistAsync(playlistId, track.SetVideoId is { } setId
            ? new JsonObject { ["action"] = "ACTION_REMOVE_VIDEO", ["setVideoId"] = setId, ["removedVideoId"] = track.VideoId }
            : new JsonObject { ["action"] = "ACTION_REMOVE_VIDEO_BY_VIDEO_ID", ["removedVideoId"] = track.VideoId }, ct);

    private async Task<JsonNode?> EditPlaylistAsync(string playlistId, JsonObject action, CancellationToken ct)
    {
        var body = NewBody();
        body["playlistId"] = playlistId;
        body["actions"] = new JsonArray(action);
        var json = await PostAsync("browse/edit_playlist", body, null, ct);
        if (json.Str("status") is { } status && status != "STATUS_SUCCEEDED")
            throw new HttpRequestException($"YouTube Music couldn't update the playlist ({status}).");
        return json;
    }

    /// <summary>Like / remove like for a song.</summary>
    public async Task RateSongAsync(string videoId, bool like, CancellationToken ct = default)
    {
        var body = NewBody();
        body["target"] = new JsonObject { ["videoId"] = videoId };
        await PostAsync(like ? "like/like" : "like/removelike", body, null, ct);
    }

    /// <summary>Saves a playlist to / removes it from the library (for playlists the user doesn't own).</summary>
    public async Task SetPlaylistSavedAsync(string playlistId, bool saved, CancellationToken ct = default)
    {
        var body = NewBody();
        body["target"] = new JsonObject { ["playlistId"] = playlistId };
        await PostAsync(saved ? "like/like" : "like/removelike", body, null, ct);
    }

    /// <summary>Video ids of every liked song (follows continuations, up to <paramref name="maxPages"/>).</summary>
    public async Task<HashSet<string>> GetLikedVideoIdsAsync(int maxPages = 40, CancellationToken ct = default)
    {
        var ids = new HashSet<string>();
        var page = await GetPlaylistAsync("VLLM", ct);
        for (int i = 0; ; i++)
        {
            foreach (var t in page.Tracks) ids.Add(t.VideoId);
            if (page.Next is not { } next || i + 1 >= maxPages) break;
            page = await ContinuePlaylistAsync(next, ct);
        }
        return ids;
    }

    public async Task<TrackPage> GetPlaylistAsync(string browseId, CancellationToken ct = default)
    {
        var json = await BrowseAsync(browseId, ct);
        return PlaylistParser.ParsePlaylistPage(json);
    }

    public async Task<TrackPage> ContinuePlaylistAsync(ContinuationToken token, CancellationToken ct = default)
    {
        var json = await PostContinuationAsync("browse", token, ct);
        return PlaylistParser.ParseContinuation(json);
    }

    private Task<JsonNode?> BrowseAsync(string browseId, CancellationToken ct)
    {
        var body = NewBody();
        body["browseId"] = browseId;
        return PostAsync("browse", body, null, ct);
    }

    private Task<JsonNode?> PostContinuationAsync(string endpoint, ContinuationToken token, CancellationToken ct)
    {
        var body = NewBody();
        if (token.InBody)
            return PostAsync(endpoint, body.With("continuation", token.Token), null, ct);

        var t = Uri.EscapeDataString(token.Token);
        return PostAsync(endpoint, body, $"&ctoken={t}&continuation={t}&type=next", ct);
    }

    private JsonObject NewBody() => new()
    {
        ["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = "WEB_REMIX",
                ["clientVersion"] = _clientVersion,
                ["hl"] = "en",
                ["gl"] = "US",
            },
            ["user"] = new JsonObject(),
        },
    };

    /// <summary>Reads the session selection from music.youtube.com's page config, as the web app does.</summary>
    private async Task<SessionInfo> ResolveSessionAsync(Auth auth)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Auth.Origin + "/");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Cookie", auth.CookieHeader);
            using var resp = await _http.SendAsync(req);
            return ParseSession(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ReferenceEquals(_auth, auth)) _session = null; // retry on the next request
            return new SessionInfo("0", null);
        }
    }

    internal static SessionInfo ParseSession(string html)
    {
        var index = Regex.Match(html, @"""SESSION_INDEX"":""?(\d+)");
        var page = Regex.Match(html, @"""DELEGATED_SESSION_ID"":""(\d+)""");
        return new SessionInfo(index.Success ? index.Groups[1].Value : "0", page.Success ? page.Groups[1].Value : null);
    }

    private async Task<JsonNode?> PostAsync(string endpoint, JsonObject body, string? extraQuery, CancellationToken ct)
    {
        var auth = _auth;
        SessionInfo? session = null;
        if (auth is not null)
        {
            session = await (_session ??= ResolveSessionAsync(auth));
            if (session.PageId is { } pageId)
                body["context"]!["user"]!["onBehalfOfUser"] = pageId;
        }

        // YouTube occasionally answers a valid request with a 5xx; one retry clears nearly all of them.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SendAsync(endpoint, body, extraQuery, auth, session, ct);
            }
            catch (ServerErrorException) when (attempt == 0)
            {
                await Task.Delay(500, ct);
            }
        }
    }

    private sealed class ServerErrorException(string message) : HttpRequestException(message);

    private async Task<JsonNode?> SendAsync(string endpoint, JsonObject body, string? extraQuery, Auth? auth,
        SessionInfo? session, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{endpoint}?prettyPrint=false{extraQuery}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Origin", Auth.Origin);
        req.Headers.TryAddWithoutValidation("X-Origin", Auth.Origin);
        req.Headers.TryAddWithoutValidation("Referer", Auth.Origin + "/");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        if (_visitorId is not null)
            req.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", _visitorId);

        if (auth is not null && session is not null)
        {
            req.Headers.TryAddWithoutValidation("Cookie", auth.CookieHeader);
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationHeader());
            req.Headers.TryAddWithoutValidation("X-Goog-AuthUser", session.AuthUser);
            if (session.PageId is not null)
                req.Headers.TryAddWithoutValidation("X-Goog-PageId", session.PageId);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // No response at all (no connection, DNS failure, timeout): let the app check whether it's offline.
            Services.Connectivity.Instance.ReportFailure();
            throw;
        }
        using var _ = resp;
        Services.Connectivity.Instance.ReportSuccess();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var json = await JsonNode.ParseAsync(stream, cancellationToken: ct);
        if (!resp.IsSuccessStatusCode)
        {
            var message = json.Str("error.message") ?? resp.ReasonPhrase;
            message = $"YouTube Music {endpoint} failed ({(int)resp.StatusCode}): {message}";
            throw (int)resp.StatusCode >= 500 ? new ServerErrorException(message) : new HttpRequestException(message);
        }
        _visitorId ??= json.Str("responseContext.visitorData");
        return json;
    }

    public void Dispose() => _http.Dispose();
}

internal static class JsonObjectExtensions
{
    public static JsonObject With(this JsonObject obj, string key, JsonNode? value)
    {
        obj[key] = value;
        return obj;
    }
}
