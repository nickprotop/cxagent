using System.Net;
using System.Text;
using System.Web;
using CxAgent.Core.Mcp.Auth;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The whole login, end to end against a scripted authorization server: discovery, the browser
/// handoff, the callback, and the exchange.
///
/// <para>The browser is injected rather than opened, which is what makes this testable — and is the
/// same seam that lets a headless caller print the URL instead.</para>
/// </summary>
// Binds an HttpListener — see HttpListenerCollection for why every such class must join.
[Collection("http-listeners")]
public class McpLoginTests : IDisposable
{
    private readonly List<FakeAuthHost> _hosts = [];
    private readonly HttpClient _http = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxa-login-" + Guid.NewGuid().ToString("N"));

    public McpLoginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
        _http.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TokenStore Tokens() => new(new AppPaths(_dir));

    /// <summary>Serves the two metadata documents and a token endpoint, all on one loopback host.</summary>
    private sealed class FakeAuthHost : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string Origin { get; }
        public string MetadataUrl => Origin + "/.well-known/oauth-protected-resource";

        /// <summary>Set to fail the token exchange, so the unhappy path is exercised too.</summary>
        public bool RejectToken { get; init; }

        /// <summary>Omit the authorization server list, making discovery fail.</summary>
        public bool BrokenMetadata { get; init; }

        public FakeAuthHost()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Origin = $"http://127.0.0.1:{port}";
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

                try { await HandleAsync(ctx); }
                catch (Exception) { /* a closed listener mid-request is not a failure */ }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var (status, body) = path switch
            {
                "/.well-known/oauth-protected-resource" when BrokenMetadata =>
                    (200, """{"resource":"https://mcp.example"}"""),

                "/.well-known/oauth-protected-resource" =>
                    (200, $$"""{"resource":"https://mcp.example","authorization_servers":["{{Origin}}"]}"""),

                "/.well-known/oauth-authorization-server" =>
                    (200, $$"""{"issuer":"{{Origin}}","authorization_endpoint":"{{Origin}}/authorize","token_endpoint":"{{Origin}}/token","code_challenge_methods_supported":["S256"]}"""),

                "/token" when RejectToken =>
                    (400, """{"error":"invalid_grant","error_description":"code expired"}"""),

                "/token" =>
                    (200, """{"access_token":"SECRET-TOKEN-VALUE","refresh_token":"SECRET-REFRESH","expires_in":3600}"""),

                _ => (404, "{}"),
            };

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }

    private FakeAuthHost Host(FakeAuthHost h)
    {
        _hosts.Add(h);
        return h;
    }

    /// <summary>Stands in for the browser: reads the URL, and calls back as the real one would.</summary>
    private static Action<string> BrowserThatApproves(HttpClient http, string code = "the-code") =>
        url =>
        {
            var query = HttpUtility.ParseQueryString(new Uri(url).Query);
            var redirect = query["redirect_uri"]!;
            var state = query["state"]!;
            _ = http.GetAsync($"{redirect}?code={code}&state={Uri.EscapeDataString(state)}");
        };

    [Fact]
    public async Task RunAsync_HappyPath_StoresTokensAndReportsSuccess()
    {
        var host = Host(new FakeAuthHost());

        var result = await McpLogin.RunAsync(
            _http, Tokens(), "remote", host.MetadataUrl, "client-1", null,
            BrowserThatApproves(_http), TimeSpan.FromSeconds(10));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("SECRET-TOKEN-VALUE", Tokens().Get("remote")!.AccessToken);
    }

    /// <summary>
    /// NO TOKEN APPEARS IN ANY MESSAGE. Everything this returns lands in a transcript that gets
    /// scrolled through, screenshotted and pasted into issues — a token there is a token disclosed,
    /// and no amount of care elsewhere undoes it.
    /// </summary>
    [Fact]
    public async Task RunAsync_NeverPutsATokenInItsMessage()
    {
        var host = Host(new FakeAuthHost());

        var result = await McpLogin.RunAsync(
            _http, Tokens(), "remote", host.MetadataUrl, "client-1", null,
            BrowserThatApproves(_http), TimeSpan.FromSeconds(10));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("SECRET-TOKEN-VALUE", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-REFRESH", result.Message, StringComparison.Ordinal);
    }

    /// <summary>The message names the authorization server that was used — RFC 9728 leaves the choice
    /// to the client, and a choice nobody can see is one nobody can question.</summary>
    [Fact]
    public async Task RunAsync_NamesTheAuthorizationServerItUsed()
    {
        var host = Host(new FakeAuthHost());

        var result = await McpLogin.RunAsync(
            _http, Tokens(), "remote", host.MetadataUrl, "client-1", null,
            BrowserThatApproves(_http), TimeSpan.FromSeconds(10));

        Assert.Contains(host.Origin, result.Message, StringComparison.Ordinal);
    }

    /// <summary>A browser that never comes back times out — and does NOT store anything.</summary>
    [Fact]
    public async Task RunAsync_WhenTheBrowserNeverReturns_TimesOutWithoutStoringAnything()
    {
        var host = Host(new FakeAuthHost());

        var result = await McpLogin.RunAsync(
            _http, Tokens(), "remote", host.MetadataUrl, "client-1", null,
            _ => { /* the user closed the tab */ }, TimeSpan.FromMilliseconds(400));

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Tokens().Get("remote"));
    }

    /// <summary>A redirect whose state does not match is refused, and nothing is stored — the
    /// stale-tab case, which must not attach someone else's authorization to this session.</summary>
    [Fact]
    public async Task RunAsync_WithAMismatchedState_FailsAndStoresNothing()
    {
        var host = Host(new FakeAuthHost());

        var result = await McpLogin.RunAsync(
            _http, Tokens(), "remote", host.MetadataUrl, "client-1", null,
            url =>
            {
                var redirect = HttpUtility.ParseQueryString(new Uri(url).Query)["redirect_uri"]!;
                _ = _http.GetAsync($"{redirect}?code=abc&state=NOT-OURS");
            },
            TimeSpan.FromSeconds(10));

        Assert.False(result.Succeeded);
        Assert.Contains("state", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Tokens().Get("remote"));
    }

    /// <summary>A rejected exchange carries the server's own OAuth error, and stores nothing.</summary>
    [Fact]
    public async Task RunAsync_WhenTheExchangeIsRejected_ReportsTheServersReason()
    {
        var host = Host(new FakeAuthHost { RejectToken = true });

        var result = await McpLogin.RunAsync(
            _http, Tokens(), "remote", host.MetadataUrl, "client-1", null,
            BrowserThatApproves(_http), TimeSpan.FromSeconds(10));

        Assert.False(result.Succeeded);
        Assert.Contains("invalid_grant", result.Message, StringComparison.Ordinal);
        Assert.Null(Tokens().Get("remote"));
    }

    /// <summary>Discovery that fails names the server and the reason, rather than opening a browser
    /// at a URL that cannot work.</summary>
    [Fact]
    public async Task RunAsync_WhenDiscoveryFails_DoesNotOpenABrowser()
    {
        var host = Host(new FakeAuthHost { BrokenMetadata = true });
        var opened = false;

        var result = await McpLogin.RunAsync(
            _http, Tokens(), "remote", host.MetadataUrl, "client-1", null,
            _ => opened = true, TimeSpan.FromSeconds(10));

        Assert.False(result.Succeeded);
        Assert.False(opened, "a browser must not open when there is nowhere valid to send it");
        Assert.Contains("remote", result.Message, StringComparison.Ordinal);
    }

    // ---- token validity ------------------------------------------------------------------------

    /// <summary>A live token is used as-is: no refresh round trip for a token that works.</summary>
    [Fact]
    public async Task ValidTokenAsync_ReturnsALiveTokenWithoutRefreshing()
    {
        var host = Host(new FakeAuthHost());
        var store = Tokens();
        store.Save("remote", new OAuthTokens("still-good", "rt", DateTimeOffset.UtcNow.AddHours(1)));

        var token = await McpLogin.ValidTokenAsync(
            _http, store, "remote", host.MetadataUrl, "c", null, CancellationToken.None);

        Assert.Equal("still-good", token);
    }

    /// <summary>An expired token is refreshed, and the NEW one is stored — OAuth 2.1 rotates refresh
    /// tokens for public clients, so keeping the old one would work exactly once.</summary>
    [Fact]
    public async Task ValidTokenAsync_RefreshesAnExpiredToken_AndStoresTheRotatedOne()
    {
        var host = Host(new FakeAuthHost());
        var store = Tokens();
        store.Save("remote", new OAuthTokens("expired", "rt-old", DateTimeOffset.UtcNow.AddSeconds(-10)));

        var token = await McpLogin.ValidTokenAsync(
            _http, store, "remote", host.MetadataUrl, "c", null, CancellationToken.None);

        Assert.Equal("SECRET-TOKEN-VALUE", token);
        Assert.Equal("SECRET-REFRESH", store.Get("remote")!.RefreshToken);
    }

    /// <summary>
    /// A FAILED REFRESH FORGETS THE TOKENS. The refresh token is dead — revoked, expired, or for a
    /// client that no longer exists — and keeping it would make every later call pay a doomed round
    /// trip before failing the same way. Forgetting turns it into "you are not logged in", which is
    /// what it is, and points at the fix.
    /// </summary>
    [Fact]
    public async Task ValidTokenAsync_WhenTheRefreshFails_ForgetsTheTokens()
    {
        var host = Host(new FakeAuthHost { RejectToken = true });
        var store = Tokens();
        store.Save("remote", new OAuthTokens("expired", "rt-dead", DateTimeOffset.UtcNow.AddSeconds(-10)));

        var token = await McpLogin.ValidTokenAsync(
            _http, store, "remote", host.MetadataUrl, "c", null, CancellationToken.None);

        Assert.Null(token);
        Assert.Null(store.Get("remote"));
    }

    /// <summary>Never logged in is null, not an error — the caller says "run /mcp login".</summary>
    [Fact]
    public async Task ValidTokenAsync_WithNoStoredToken_IsNull()
    {
        var host = Host(new FakeAuthHost());

        Assert.Null(await McpLogin.ValidTokenAsync(
            _http, Tokens(), "never", host.MetadataUrl, "c", null, CancellationToken.None));
    }
}
