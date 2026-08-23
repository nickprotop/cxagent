using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using CxAgent.Core.Mcp.Auth;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The authorization-code flow: the parts where getting it subtly wrong is a security bug rather
/// than a broken feature.
///
/// <para>The pure halves — building the URL, checking the redirect — are tested without a browser or
/// a server, because that is where PKCE, <c>state</c> and the RFC 8707 <c>resource</c> live. The
/// token exchange is driven against a real loopback listener, since a mocked HttpClient would model
/// our assumptions about the form encoding rather than the wire.</para>
/// </summary>
// Binds an HttpListener — see HttpListenerCollection for why every such class must join.
[Collection("http-listeners")]
public class OAuthFlowTests : IDisposable
{
    private readonly List<FakeTokenServer> _servers = [];
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        foreach (var s in _servers) s.Dispose();
        _http.Dispose();
    }

    private static AuthorizationServerMetadata Server(string tokenEndpoint = "https://auth.example/token") =>
        new("https://auth.example", "https://auth.example/authorize", tokenEndpoint, null, ["S256"]);

    private const string Resource = "https://mcp.example.com";

    /// <summary>Records the token request so the parameters a client MUST send can be asserted on
    /// directly rather than inferred from a result.</summary>
    private sealed class FakeTokenServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string Url { get; }
        public Dictionary<string, string> LastForm { get; private set; } = [];
        public int Status { get; init; } = 200;
        public string Body { get; init; } =
            """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""";

        public FakeTokenServer()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}/token";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }

                try
                {
                    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    var parsed = HttpUtility.ParseQueryString(body);
                    LastForm = parsed.AllKeys.Where(k => k is not null)
                        .ToDictionary(k => k!, k => parsed[k] ?? "");

                    var bytes = Encoding.UTF8.GetBytes(Body);
                    ctx.Response.StatusCode = Status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch (Exception) { /* a closed listener mid-request is not a failure */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }

    private FakeTokenServer NewServer(FakeTokenServer s)
    {
        _servers.Add(s);
        return s;
    }

    // ---- the authorization request -----------------------------------------------------------

    /// <summary>
    /// PKCE, WITH S256 AND A REAL CHALLENGE. OAuth 2.1 §7.5.2 makes it a MUST, and it is what stops
    /// an intercepted authorization code from being redeemable by whoever intercepted it.
    ///
    /// <para>The challenge is verified as the actual SHA-256 of the verifier, not merely present —
    /// a client that sent a random string as its challenge would pass a presence check and fail
    /// every real server.</para>
    /// </summary>
    [Fact]
    public void CreateRequest_UsesS256Pkce_WithAChallengeDerivedFromTheVerifier()
    {
        var request = OAuthFlow.CreateRequest(Server(), Resource, "client-1", "http://127.0.0.1:9/callback");
        var query = HttpUtility.ParseQueryString(new Uri(request.AuthorizationUrl).Query);

        Assert.Equal("S256", query["code_challenge_method"]);

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, query["code_challenge"]);
    }

    /// <summary>The verifier is not in the URL. Sending it alongside the challenge would defeat the
    /// entire mechanism — anyone who saw the request could redeem the code.</summary>
    [Fact]
    public void CreateRequest_DoesNotPutTheVerifierInTheUrl()
    {
        var request = OAuthFlow.CreateRequest(Server(), Resource, "client-1", "http://127.0.0.1:9/callback");

        Assert.DoesNotContain(request.CodeVerifier, request.AuthorizationUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RFC 8707 resource PARAMETER, which the spec requires "regardless of whether authorization
    /// servers support it". It binds the token to the MCP server it is for, so a malicious server
    /// cannot replay it against another.
    /// </summary>
    [Fact]
    public void CreateRequest_IncludesTheResourceParameter()
    {
        var request = OAuthFlow.CreateRequest(Server(), Resource, "client-1", "http://127.0.0.1:9/callback");
        var query = HttpUtility.ParseQueryString(new Uri(request.AuthorizationUrl).Query);

        Assert.Equal(Resource, query["resource"]);
    }

    /// <summary>Two attempts never share a verifier or a state — reuse would make one login's
    /// redirect valid for another's.</summary>
    [Fact]
    public void CreateRequest_IsFreshEveryTime()
    {
        var a = OAuthFlow.CreateRequest(Server(), Resource, "c", "http://127.0.0.1:9/callback");
        var b = OAuthFlow.CreateRequest(Server(), Resource, "c", "http://127.0.0.1:9/callback");

        Assert.NotEqual(a.CodeVerifier, b.CodeVerifier);
        Assert.NotEqual(a.State, b.State);
    }

    /// <summary>An endpoint that already carries a query keeps it — appending with '?' twice
    /// produces a URL the server rejects.</summary>
    [Fact]
    public void CreateRequest_AppendsToAnEndpointThatAlreadyHasAQuery()
    {
        var server = new AuthorizationServerMetadata("i", "https://auth.example/authorize?tenant=a",
            "https://auth.example/token", null, ["S256"]);

        var request = OAuthFlow.CreateRequest(server, Resource, "c", "http://127.0.0.1:9/callback");

        Assert.Contains("tenant=a", request.AuthorizationUrl, StringComparison.Ordinal);
        Assert.Contains("&code_challenge=", request.AuthorizationUrl, StringComparison.Ordinal);
    }

    // ---- the redirect ------------------------------------------------------------------------

    [Fact]
    public void ReadRedirect_ReturnsTheCode_WhenTheStateMatches()
    {
        var (code, error) = OAuthFlow.ReadRedirect("?code=abc123&state=xyz", "xyz");

        Assert.Null(error);
        Assert.Equal("abc123", code);
    }

    /// <summary>
    /// A STATE MISMATCH IS REFUSED BEFORE THE CODE IS TOUCHED. A redirect that does not echo the
    /// value we generated did not come from the request we made — it is a stale tab or someone
    /// else's, and redeeming its code would attach a stranger's authorization to this session.
    /// </summary>
    [Fact]
    public void ReadRedirect_WithAMismatchedState_IsRefused()
    {
        var (code, error) = OAuthFlow.ReadRedirect("?code=abc123&state=WRONG", "xyz");

        Assert.Null(code);
        Assert.Contains("state", error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And a redirect with NO state at all — the same refusal, not a pass by omission.</summary>
    [Fact]
    public void ReadRedirect_WithNoState_IsRefused()
    {
        var (code, error) = OAuthFlow.ReadRedirect("?code=abc123", "xyz");

        Assert.Null(code);
        Assert.NotNull(error);
    }

    /// <summary>The server's own refusal is repeated verbatim — "the user clicked Cancel" is worth
    /// saying, where "login failed" is not.</summary>
    [Fact]
    public void ReadRedirect_CarriesTheServersOwnError()
    {
        var (code, error) = OAuthFlow.ReadRedirect(
            "?error=access_denied&error_description=User%20refused&state=xyz", "xyz");

        Assert.Null(code);
        Assert.Contains("access_denied", error!, StringComparison.Ordinal);
        Assert.Contains("User refused", error!, StringComparison.Ordinal);
    }

    // ---- the token exchange ------------------------------------------------------------------

    /// <summary>
    /// The verifier AND the resource both travel in the token request. RFC 8707 requires the resource
    /// in both requests; sending it only in the first is a common way to end up with a token whose
    /// audience is unbound.
    /// </summary>
    [Fact]
    public async Task ExchangeAsync_SendsTheVerifierAndTheResource()
    {
        var token = NewServer(new FakeTokenServer());
        var request = OAuthFlow.CreateRequest(Server(token.Url), Resource, "c1", "http://127.0.0.1:9/callback");

        var (tokens, error) = await OAuthFlow.ExchangeAsync(
            _http, Server(token.Url), request, "the-code", Resource, "c1", null, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("at-1", tokens!.AccessToken);
        Assert.Equal(request.CodeVerifier, token.LastForm["code_verifier"]);
        Assert.Equal(Resource, token.LastForm["resource"]);
        Assert.Equal("authorization_code", token.LastForm["grant_type"]);
    }

    /// <summary>An expiry becomes an absolute time, so a token stored now is not treated as fresh
    /// tomorrow.</summary>
    [Fact]
    public async Task ExchangeAsync_TurnsExpiresInIntoAnAbsoluteTime()
    {
        var token = NewServer(new FakeTokenServer());
        var request = OAuthFlow.CreateRequest(Server(token.Url), Resource, "c1", "http://127.0.0.1:9/callback");

        var (tokens, _) = await OAuthFlow.ExchangeAsync(
            _http, Server(token.Url), request, "code", Resource, "c1", null, CancellationToken.None);

        Assert.NotNull(tokens!.ExpiresAt);
        Assert.InRange(tokens.ExpiresAt!.Value,
            DateTimeOffset.UtcNow.AddMinutes(55), DateTimeOffset.UtcNow.AddMinutes(65));
        Assert.False(tokens.NeedsRefresh);
    }

    /// <summary>A token already past its life needs refreshing — and the minute of slack means one
    /// about to expire counts too, rather than failing on the request the user is waiting for.</summary>
    [Fact]
    public void NeedsRefresh_IsTrueForAnExpiredOrNearlyExpiredToken()
    {
        Assert.True(new OAuthTokens("a", "r", DateTimeOffset.UtcNow.AddSeconds(-1)).NeedsRefresh);
        Assert.True(new OAuthTokens("a", "r", DateTimeOffset.UtcNow.AddSeconds(30)).NeedsRefresh);
        Assert.False(new OAuthTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1)).NeedsRefresh);

        // No expiry means nothing to pre-empt: refresh when it actually fails.
        Assert.False(new OAuthTokens("a", "r", null).NeedsRefresh);
    }

    /// <summary>The server's OAuth error is carried through. "invalid_grant" says the code was
    /// already used or expired; "HTTP 400" says nothing.</summary>
    [Fact]
    public async Task ExchangeAsync_CarriesTheServersOAuthError()
    {
        var token = NewServer(new FakeTokenServer
        {
            Status = 400,
            Body = """{"error":"invalid_grant","error_description":"code expired"}""",
        });
        var request = OAuthFlow.CreateRequest(Server(token.Url), Resource, "c1", "http://127.0.0.1:9/callback");

        var (tokens, error) = await OAuthFlow.ExchangeAsync(
            _http, Server(token.Url), request, "stale", Resource, "c1", null, CancellationToken.None);

        Assert.Null(tokens);
        Assert.Contains("invalid_grant", error!, StringComparison.Ordinal);
        Assert.Contains("code expired", error!, StringComparison.Ordinal);
    }

    /// <summary>An unreachable token endpoint is an error, never an exception out of a command.</summary>
    [Fact]
    public async Task ExchangeAsync_WhenUnreachable_IsAnErrorNotACrash()
    {
        var server = Server("http://127.0.0.1:1/token");
        var request = OAuthFlow.CreateRequest(server, Resource, "c1", "http://127.0.0.1:9/callback");

        var (tokens, error) = await OAuthFlow.ExchangeAsync(
            _http, server, request, "code", Resource, "c1", null, CancellationToken.None);

        Assert.Null(tokens);
        Assert.NotNull(error);
    }

    /// <summary>A refresh carries the resource too, for the same audience-binding reason.</summary>
    [Fact]
    public async Task RefreshAsync_SendsTheRefreshTokenAndTheResource()
    {
        var token = NewServer(new FakeTokenServer());

        var (tokens, error) = await OAuthFlow.RefreshAsync(
            _http, Server(token.Url), "rt-old", Resource, "c1", null, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("at-1", tokens!.AccessToken);
        Assert.Equal("refresh_token", token.LastForm["grant_type"]);
        Assert.Equal("rt-old", token.LastForm["refresh_token"]);
        Assert.Equal(Resource, token.LastForm["resource"]);
    }
}

/// <summary>Tokens on disk: where they live, who can read them, and what a corrupt file means.</summary>
public class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxa-tok-" + Guid.NewGuid().ToString("N"));

    public TokenStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private TokenStore Store() => new(new AppPaths(_dir));

    [Fact]
    public void SaveThenGet_RoundTrips()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        Store().Save("context7", new OAuthTokens("at", "rt", expires));

        var loaded = Store().Get("context7");

        Assert.Equal("at", loaded!.AccessToken);
        Assert.Equal("rt", loaded.RefreshToken);
        Assert.Equal(expires.ToUnixTimeSeconds(), loaded.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    /// <summary>
    /// NOT IN config.json, and 0600. Config is what users paste into issues and commit to dotfiles;
    /// a token obtained through a browser login was never typed by them and has no business in a file
    /// they treat as shareable.
    /// </summary>
    [Fact]
    public void Save_WritesItsOwnFile_OwnerReadableOnly()
    {
        Store().Save("srv", new OAuthTokens("at", null, null));

        var path = Path.Combine(_dir, "mcp-tokens.json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(_dir, "config.json")));

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Remove_ForgetsOneServer_AndLeavesTheOthers()
    {
        var store = Store();
        store.Save("a", new OAuthTokens("at-a", null, null));
        store.Save("b", new OAuthTokens("at-b", null, null));

        store.Remove("a");

        Assert.Null(Store().Get("a"));
        Assert.Equal("at-b", Store().Get("b")!.AccessToken);
    }

    /// <summary>A corrupt file reads as "not logged in" — recoverable by logging in again, where
    /// throwing would take a session down over a cache.</summary>
    [Fact]
    public void Get_WithACorruptFile_IsNotLoggedIn_RatherThanACrash()
    {
        File.WriteAllText(Path.Combine(_dir, "mcp-tokens.json"), "{ not json");

        Assert.Null(Store().Get("anything"));
    }

    [Fact]
    public void Get_WithNoFile_IsNull()
    {
        Assert.Null(Store().Get("never-saved"));
    }
}
