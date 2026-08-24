using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CxAgent.Core.Mcp.Auth;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Turning a 401 into the two documents that say where to authorize.
///
/// <para>Discovery only — no browser, no callback, no tokens — which is exactly why it is worth
/// testing on its own: four steps that can each fail, and a failure that does not say WHICH step is
/// a failure nobody can act on.</para>
/// </summary>
// Binds an HttpListener — see HttpListenerCollection for why every such class must join.
[Collection("http-listeners")]
public class ProtectedResourceTests : IDisposable
{
    private readonly List<FakeHost> _hosts = [];
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
        _http.Dispose();
    }

    /// <summary>Serves canned documents on loopback, so the fetch path is real HTTP.</summary>
    private sealed class FakeHost : IDisposable
    {
        // NOT READONLY: TestPorts.BindLoopback may REPLACE this listener — a failed Start can leave
        // a registration in the process-global endpoint map that Prefixes.Clear does not unwind, and
        // the only clean recovery is a fresh instance. See TestPorts.
        private HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, (int Status, string Body)> _routes;

        public string Origin { get; }

        public FakeHost(Dictionary<string, (int Status, string Body)> routes)
        {
            _routes = routes;
            // THE SHARED RETRYING BINDER, not a port picked and released. Asking the OS for a free
            // port, stopping the probe listener and then binding HttpListener leaves a window in
            // which anything can take that port — including another test class doing the same dance
            // in parallel, which is precisely what happens in a full-suite run. Observed as
            // "Address already in use" that moved between classes from run to run.
            var prefix = TestPorts.BindLoopback(ref _listener);
            Origin = prefix.TrimEnd('/');
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
                    var path = ctx.Request.Url?.AbsolutePath ?? "";
                    if (_routes.TryGetValue(path, out var route))
                    {
                        var bytes = Encoding.UTF8.GetBytes(route.Body);
                        ctx.Response.StatusCode = route.Status;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                    else ctx.Response.StatusCode = 404;

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

    private FakeHost Host(Dictionary<string, (int, string)> routes)
    {
        var h = new FakeHost(routes);
        _hosts.Add(h);
        return h;
    }

    // ---- the header ------------------------------------------------------------------------

    /// <summary>
    /// The metadata URL is READ FROM THE HEADER, not derived from the server's own URL.
    ///
    /// <para>The well-known path could be guessed, but RFC 9728 §5.1 has the server SAY where its
    /// document lives, and one that keeps it elsewhere is entitled to be believed. The fixture is the
    /// real header context7 returns.</para>
    /// </summary>
    [Fact]
    public void MetadataUrlFrom_ReadsTheResourceMetadataParameter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate",
            "Bearer resource_metadata=\"https://mcp.context7.com/.well-known/oauth-protected-resource\"");

        Assert.Equal("https://mcp.context7.com/.well-known/oauth-protected-resource",
            ProtectedResource.MetadataUrlFrom(response.Headers));
    }

    /// <summary>Other auth-params alongside it are ignored rather than confusing the parse.</summary>
    [Fact]
    public void MetadataUrlFrom_IgnoresOtherParameters()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate",
            "Bearer realm=\"mcp\", error=\"invalid_token\", resource_metadata=\"https://x/meta\"");

        Assert.Equal("https://x/meta", ProtectedResource.MetadataUrlFrom(response.Headers));
    }

    /// <summary>A 401 with no pointer is not a crash — some servers just refuse.</summary>
    [Fact]
    public void MetadataUrlFrom_WithNoPointer_IsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=\"mcp\"");

        Assert.Null(ProtectedResource.MetadataUrlFrom(response.Headers));
    }

    // ---- the protected-resource document ---------------------------------------------------

    /// <summary>The real shape, taken from context7's live document.</summary>
    [Fact]
    public async Task FetchResourceAsync_ReadsTheResourceAndItsAuthorizationServers()
    {
        var host = Host(new()
        {
            ["/.well-known/oauth-protected-resource"] = (200, """
                {"resource":"https://mcp.example.com",
                 "authorization_servers":["https://auth.example.com"],
                 "bearer_methods_supported":["header"]}
                """),
        });

        var (metadata, error) = await ProtectedResource.FetchResourceAsync(
            _http, host.Origin + "/.well-known/oauth-protected-resource", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("https://mcp.example.com", metadata!.Resource);
        Assert.Equal(["https://auth.example.com"], metadata.AuthorizationServers);
    }

    /// <summary>
    /// SEVERAL AUTHORIZATION SERVERS ARE KEPT IN ORDER. RFC 9728 §7.6 leaves the choice to the
    /// client; keeping the list means the caller can say WHICH it picked rather than the choice being
    /// invisible.
    /// </summary>
    [Fact]
    public async Task FetchResourceAsync_KeepsEveryAuthorizationServerInOrder()
    {
        var host = Host(new()
        {
            ["/.well-known/oauth-protected-resource"] = (200, """
                {"resource":"https://mcp.example.com",
                 "authorization_servers":["https://first.example","https://second.example"]}
                """),
        });

        var (metadata, _) = await ProtectedResource.FetchResourceAsync(
            _http, host.Origin + "/.well-known/oauth-protected-resource", CancellationToken.None);

        Assert.Equal(["https://first.example", "https://second.example"], metadata!.AuthorizationServers);
    }

    /// <summary>A document naming no authorization server cannot be used, and says so — rather than
    /// failing later with an empty URL.</summary>
    [Fact]
    public async Task FetchResourceAsync_WithNoAuthorizationServer_IsAClearError()
    {
        var host = Host(new()
        {
            ["/.well-known/oauth-protected-resource"] = (200, """{"resource":"https://mcp.example.com"}"""),
        });

        var (metadata, error) = await ProtectedResource.FetchResourceAsync(
            _http, host.Origin + "/.well-known/oauth-protected-resource", CancellationToken.None);

        Assert.Null(metadata);
        Assert.Contains("authorization server", error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A missing document is an error naming the step, not an exception.</summary>
    [Fact]
    public async Task FetchResourceAsync_WhenTheDocumentIsMissing_SaysWhichStepFailed()
    {
        var host = Host(new());

        var (metadata, error) = await ProtectedResource.FetchResourceAsync(
            _http, host.Origin + "/.well-known/oauth-protected-resource", CancellationToken.None);

        Assert.Null(metadata);
        Assert.Contains("protected-resource metadata", error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A body that is not JSON is the same: an error, never a throw.</summary>
    [Fact]
    public async Task FetchResourceAsync_WhenTheBodyIsNotJson_IsAnErrorNotACrash()
    {
        var host = Host(new() { ["/.well-known/oauth-protected-resource"] = (200, "<html>nope</html>") });

        var (metadata, error) = await ProtectedResource.FetchResourceAsync(
            _http, host.Origin + "/.well-known/oauth-protected-resource", CancellationToken.None);

        Assert.Null(metadata);
        Assert.NotNull(error);
    }

    // ---- the authorization server document --------------------------------------------------

    /// <summary>The endpoints the flow needs, from the shape context7 actually serves.</summary>
    [Fact]
    public async Task FetchAuthorizationServerAsync_ReadsTheEndpoints()
    {
        var host = Host(new()
        {
            ["/.well-known/oauth-authorization-server"] = (200, """
                {"issuer":"https://auth.example",
                 "authorization_endpoint":"https://auth.example/api/oauth/authorize",
                 "token_endpoint":"https://auth.example/api/oauth/token",
                 "registration_endpoint":"https://auth.example/api/oauth/register",
                 "code_challenge_methods_supported":["S256"]}
                """),
        });

        var (metadata, error) = await ProtectedResource.FetchAuthorizationServerAsync(
            _http, host.Origin, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("https://auth.example/api/oauth/authorize", metadata!.AuthorizationEndpoint);
        Assert.Equal("https://auth.example/api/oauth/token", metadata.TokenEndpoint);
        Assert.Equal("https://auth.example/api/oauth/register", metadata.RegistrationEndpoint);
        Assert.True(metadata.SupportsS256);
    }

    /// <summary>
    /// THE WELL-KNOWN PATH GOES AFTER THE ORIGIN, BEFORE THE ISSUER'S PATH (RFC 8414 §3.1).
    ///
    /// <para><c>https://host/tenant</c> is <c>https://host/.well-known/…/tenant</c>, not the naive
    /// concatenation. Getting this wrong works for every issuer without a path and fails for every
    /// multi-tenant one — a bug that would look like "some servers just don't work".</para>
    /// </summary>
    [Fact]
    public async Task FetchAuthorizationServerAsync_InsertsTheWellKnownPathAfterTheOrigin()
    {
        var host = Host(new()
        {
            ["/.well-known/oauth-authorization-server/tenant-a"] = (200, """
                {"issuer":"https://auth.example/tenant-a",
                 "authorization_endpoint":"https://auth.example/a/authorize",
                 "token_endpoint":"https://auth.example/a/token"}
                """),
        });

        var (metadata, error) = await ProtectedResource.FetchAuthorizationServerAsync(
            _http, host.Origin + "/tenant-a", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("https://auth.example/a/token", metadata!.TokenEndpoint);
    }

    /// <summary>A document missing an endpoint the flow needs is rejected here, where the reason is
    /// obvious, rather than at the moment a null URL is used.</summary>
    [Fact]
    public async Task FetchAuthorizationServerAsync_MissingAnEndpoint_IsAClearError()
    {
        var host = Host(new()
        {
            ["/.well-known/oauth-authorization-server"] = (200, """{"issuer":"https://auth.example"}"""),
        });

        var (metadata, error) = await ProtectedResource.FetchAuthorizationServerAsync(
            _http, host.Origin, CancellationToken.None);

        Assert.Null(metadata);
        Assert.Contains("token_endpoint", error!, StringComparison.Ordinal);
    }

    /// <summary>An unreachable authorization server names the URL it tried, so the failure is
    /// diagnosable without a packet capture.</summary>
    [Fact]
    public async Task FetchAuthorizationServerAsync_WhenUnreachable_NamesTheUrlItTried()
    {
        var (metadata, error) = await ProtectedResource.FetchAuthorizationServerAsync(
            _http, "http://127.0.0.1:1", CancellationToken.None);

        Assert.Null(metadata);
        Assert.Contains(".well-known/oauth-authorization-server", error!, StringComparison.Ordinal);
    }

    /// <summary>Nonsense in, error out — never an exception from a parse.</summary>
    [Fact]
    public async Task FetchAuthorizationServerAsync_WithAnUnusableIssuer_IsAnError()
    {
        var (metadata, error) = await ProtectedResource.FetchAuthorizationServerAsync(
            _http, "not a url", CancellationToken.None);

        Assert.Null(metadata);
        Assert.NotNull(error);
    }
}
