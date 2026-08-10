using System.Net;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Mcp;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// MCP over Streamable HTTP, driven against a REAL loopback listener.
///
/// <para>Same reasoning as <see cref="McpClientTests"/> using a real subprocess: the risk here is the
/// wire — a handshake, a session header that must be echoed, and a response that is EITHER a JSON
/// object OR an SSE stream at the server's choice. A mocked HttpClient would model our assumptions
/// about those and pass while the framing was wrong.</para>
/// </summary>
public class McpHttpClientTests : IDisposable
{
    private readonly List<FakeServer> _servers = [];

    public void Dispose()
    {
        foreach (var s in _servers) s.Dispose();
    }

    /// <summary>
    /// A scripted MCP server on loopback. Records every request so the headers a client is REQUIRED
    /// to send can be asserted on directly rather than inferred from a result.
    /// </summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string Url { get; }
        public List<(string Method, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        /// <summary>Answers as SSE rather than a single JSON object — both are legal, per request.</summary>
        public bool UseSse { get; init; }

        /// <summary>Assigns a session id at initialize, as a stateful server does.</summary>
        public string? SessionId { get; init; }

        /// <summary>Answers 404 to non-initialize calls until this many have been seen — the shape of
        /// a session the server dropped.</summary>
        public int Expire404Count { get; set; }

        /// <summary>Never answers, so the client must time out on its own.</summary>
        public bool Hang { get; init; }

        /// <summary>Answers 500 to everything after the handshake.</summary>
        public bool FailCalls { get; init; }

        /// <summary>What the server answers initialize with — a counter-offer when it differs.</summary>
        public string ProtocolVersion { get; init; } = "2025-06-18";

        public FakeServer()
        {
            // Port 0 is not available to HttpListener, so take a free one by probing.
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/mcp";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }

                try { await HandleAsync(ctx); }
                catch (Exception) { /* a closed listener mid-request is not a test failure */ }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var headers = ctx.Request.Headers.AllKeys
                .Where(k => k is not null)
                .ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);

            lock (Requests) Requests.Add((ctx.Request.HttpMethod, headers, body));

            if (Hang)
            {
                // Hold the request open. The client's own timeout is what must end this.
                try { await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token); } catch (Exception) { }
                return;
            }

            var isInitialize = body.Contains("\"initialize\"", StringComparison.Ordinal);
            var id = JsonDocument.Parse(body).RootElement.TryGetProperty("id", out var idEl)
                ? idEl.GetRawText() : "null";

            if (!isInitialize && Expire404Count > 0)
            {
                Expire404Count--;
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            if (!isInitialize && FailCalls)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
                return;
            }

            var result = isInitialize
                // Placeholder substitution, not interpolation: the JSON is dense with braces
                // ({"tools":{}}) and every raw-interpolation form ends up fighting them.
                ? """{"protocolVersion":"@@PROTO@@","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1"},"instructions":"Be brief."}"""
                    .Replace("@@PROTO@@", ProtocolVersion)
                : body.Contains("tools/list", StringComparison.Ordinal)
                    ? """{"tools":[{"name":"go","description":"Goes.","inputSchema":{"type":"object"}}]}"""
                    : """{"content":[{"type":"text","text":"hello over http"}]}""";

            var payload = $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""";

            if (isInitialize && SessionId is not null)
                ctx.Response.Headers["Mcp-Session-Id"] = SessionId;

            byte[] bytes;
            if (UseSse)
            {
                ctx.Response.ContentType = "text/event-stream";
                // Deliberately noisy: a comment, an event: line and an id: line the client MUST
                // ignore, plus the data the client needs.
                bytes = Encoding.UTF8.GetBytes($": keep-alive\nevent: message\nid: 7\ndata: {payload}\n\n");
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                bytes = Encoding.UTF8.GetBytes(payload);
            }

            ctx.Response.StatusCode = 200;
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

    private FakeServer NewServer(FakeServer server)
    {
        _servers.Add(server);
        return server;
    }

    private static JsonElement NoArgs => JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>The simple path most servers take: one JSON object back.</summary>
    [Fact]
    public async Task CallToolAsync_WithAJsonResponse_ReturnsTheText()
    {
        var server = NewServer(new FakeServer());
        await using var client = new McpHttpClient("remote", server.Url);

        Assert.True(await client.StartAsync(CancellationToken.None));
        var result = await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        Assert.Contains("hello over http", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND the SSE path, which the client MUST also support — the server picks per request. Same
    /// call, different content type, IDENTICAL result to the caller: that equivalence is the test.
    /// </summary>
    [Fact]
    public async Task CallToolAsync_WithAnSseResponse_ReturnsTheSameText()
    {
        var server = NewServer(new FakeServer { UseSse = true });
        await using var client = new McpHttpClient("remote", server.Url);

        Assert.True(await client.StartAsync(CancellationToken.None));
        var result = await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        Assert.Contains("hello over http", result, StringComparison.Ordinal);
    }

    /// <summary>Both content types MUST be advertised, or a server is entitled to refuse.</summary>
    [Fact]
    public async Task EveryRequest_AcceptsBothJsonAndEventStream()
    {
        var server = NewServer(new FakeServer());
        await using var client = new McpHttpClient("remote", server.Url);
        await client.StartAsync(CancellationToken.None);

        var accept = server.Requests[0].Headers["Accept"];
        Assert.Contains("application/json", accept, StringComparison.Ordinal);
        Assert.Contains("text/event-stream", accept, StringComparison.Ordinal);
    }

    /// <summary>
    /// The session id from initialize is echoed on EVERY subsequent request. A server that assigned
    /// one and then stops seeing it answers 400 to everything after the handshake.
    /// </summary>
    [Fact]
    public async Task AfterInitialize_TheSessionIdIsSentOnEveryRequest()
    {
        var server = NewServer(new FakeServer { SessionId = "abc-123" });
        await using var client = new McpHttpClient("remote", server.Url);
        await client.StartAsync(CancellationToken.None);

        await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        // Every request AFTER the handshake carries it. The handshake itself cannot: the id does not
        // exist until the server answers.
        var after = server.Requests.Skip(1).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, r => Assert.Equal("abc-123", r.Headers.GetValueOrDefault("Mcp-Session-Id")));
    }

    /// <summary>The negotiated protocol version rides on every post-handshake request.</summary>
    [Fact]
    public async Task AfterInitialize_TheProtocolVersionHeaderIsSent()
    {
        var server = NewServer(new FakeServer());
        await using var client = new McpHttpClient("remote", server.Url);
        await client.StartAsync(CancellationToken.None);

        await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        Assert.All(server.Requests.Skip(1),
            r => Assert.False(string.IsNullOrEmpty(r.Headers.GetValueOrDefault("MCP-Protocol-Version"))));
    }

    /// <summary>
    /// HTTP 404 means the server dropped the session. The spec REQUIRES re-initialising rather than
    /// failing the call: "MUST start a new session by sending a new InitializeRequest".
    /// </summary>
    [Fact]
    public async Task WhenTheSessionExpires_ItReinitialisesAndRetriesOnce()
    {
        var server = NewServer(new FakeServer { SessionId = "s1" });
        await using var client = new McpHttpClient("remote", server.Url);
        await client.StartAsync(CancellationToken.None);

        server.Expire404Count = 1;                 // the next call 404s, then the server is healthy
        var result = await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        Assert.Contains("hello over http", result, StringComparison.Ordinal);

        // It re-handshook rather than merely retrying the same dead session.
        Assert.True(server.Requests.Count(r => r.Body.Contains("\"initialize\"", StringComparison.Ordinal)) >= 2,
            "a 404 must trigger a new InitializeRequest");
    }

    /// <summary>...ONCE. A server that 404s the re-initialised session too must not loop for ever.</summary>
    [Fact]
    public async Task WhenReinitialisationAlsoFails_ItGivesUpRatherThanLooping()
    {
        var server = NewServer(new FakeServer { SessionId = "s1" });
        await using var client = new McpHttpClient("remote", server.Url);
        await client.StartAsync(CancellationToken.None);

        server.Expire404Count = 50;                // every call 404s, for ever
        var result = await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);

        // Bounded: one handshake, one call, one re-handshake, one retry. Not fifty.
        Assert.True(server.Requests.Count <= 6, $"gave up after {server.Requests.Count} requests");
    }

    /// <summary>Configured headers reach the server on every request, handshake included — this is
    /// how an API key gets to a remote server without any OAuth at all.</summary>
    [Fact]
    public async Task ConfiguredHeaders_AreSentOnEveryRequest()
    {
        var server = NewServer(new FakeServer());
        await using var client = new McpHttpClient("remote", server.Url,
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token" });

        await client.StartAsync(CancellationToken.None);
        await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        Assert.NotEmpty(server.Requests);
        Assert.All(server.Requests,
            r => Assert.Equal("Bearer secret-token", r.Headers.GetValueOrDefault("Authorization")));
    }

    /// <summary>A server that never answers fails its call rather than hanging the turn loop.</summary>
    [Fact]
    public async Task WhenTheServerHangs_TheCallTimesOut()
    {
        var server = NewServer(new FakeServer { Hang = true });
        await using var client = new McpHttpClient("remote", server.Url,
            timeout: TimeSpan.FromMilliseconds(400));

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.NotNull(client.Error);
    }

    /// <summary>500s are results, not exceptions — Plan 4's contract, unchanged by the transport.</summary>
    [Fact]
    public async Task WhenTheServerErrors_ItReturnsAnErrorRatherThanThrowing()
    {
        var server = NewServer(new FakeServer { FailCalls = true });
        await using var client = new McpHttpClient("remote", server.Url);
        await client.StartAsync(CancellationToken.None);

        var result = await client.CallToolAsync("go", NoArgs, CancellationToken.None);

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A refused connection is a dead server, not a crash.</summary>
    [Fact]
    public async Task StartAsync_WhenNothingIsListening_ReturnsFalse()
    {
        await using var client = new McpHttpClient("ghost", "http://127.0.0.1:1/mcp",
            timeout: TimeSpan.FromSeconds(2));

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.NotNull(client.Error);
    }

    /// <summary>Tools and instructions arrive the same way they do over stdio — the transport is the
    /// only thing that differs, which is what lets one toolset serve both.</summary>
    [Fact]
    public async Task ListToolsAsync_AndInstructions_WorkOverHttp()
    {
        var server = NewServer(new FakeServer());
        await using var client = new McpHttpClient("remote", server.Url);
        await client.StartAsync(CancellationToken.None);

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal("go", Assert.Single(tools).Name);
        Assert.Equal("Be brief.", client.Instructions);
    }
    /// <summary>Both transports negotiate from ONE supported list, so a server can never be reachable
    /// over stdio and refused over HTTP for a reason the user cannot see.</summary>
    [Fact]
    public async Task StartAsync_AcceptsACounterOfferWeSupport()
    {
        var server = NewServer(new FakeServer { ProtocolVersion = "2024-11-05" });
        await using var client = new McpHttpClient("remote", server.Url);

        Assert.True(await client.StartAsync(CancellationToken.None));
        Assert.Equal("2024-11-05", client.NegotiatedProtocolVersion);

        // And the header carries the NEGOTIATED version, not the one we asked for.
        await client.CallToolAsync("go", NoArgs, CancellationToken.None);
        Assert.Equal("2024-11-05",
            server.Requests.Last().Headers.GetValueOrDefault("MCP-Protocol-Version"));
    }

    /// <summary>A version we cannot speak disconnects, naming both sides.</summary>
    [Fact]
    public async Task StartAsync_RejectsACounterOfferWeCannotSupport()
    {
        var server = NewServer(new FakeServer { ProtocolVersion = "2099-01-01" });
        await using var client = new McpHttpClient("remote", server.Url);

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.Contains("2099-01-01", client.Error!, StringComparison.Ordinal);
    }
}
