using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CxAgent.Core.Mcp;

/// <summary>
/// One MCP server reached over HTTP — the Streamable HTTP transport.
///
/// <para>Same protocol as <see cref="McpClient"/>, none of the plumbing. There is no child process
/// and no pipe: every JSON-RPC message is its own HTTP POST to a single endpoint, and the reply comes
/// back as EITHER one JSON object OR an SSE stream, at the server's choice per request. The client
/// MUST support both — there is no negotiating out of it.</para>
///
/// <para>THE SAME CONTRACT AS STDIO: nothing here throws at the caller. A refused connection, a 500,
/// a DNS failure or a hang produces a false or an error string, and the session carries on without
/// those tools.</para>
///
/// <para>THE DEPRECATED 2024-11-05 HTTP+SSE TRANSPORT IS NOT IMPLEMENTED, and that is a decision
/// rather than an omission. It is not a variation on this one: replies arrive on a long-lived GET
/// stream that must be held open for the session, correlated to requests by id, with the POST
/// endpoint discovered from an <c>endpoint</c> event — a reader loop closer to the stdio client than
/// to anything here. Measured before deciding: of 50 servers in the MCP registry, 42 remotes are
/// <c>streamable-http</c> and 1 is <c>sse</c>, and context7 — which served the old endpoint until
/// recently — now answers 404 on it. A whole second transport for that is not worth the surface it
/// would add. What IS owed is a diagnosable failure, which <see cref="SendAsync"/> gives.</para>
///
/// <para>AUTHORIZATION IS NOT PERFORMED HERE, only DETECTED. A 401 sets <see cref="NeedsAuth"/> and
/// records where the server said its metadata lives; the flow itself lives in
/// <see cref="Auth.OAuthFlow"/> and is driven by <c>/mcp login</c>. NO BROWSER OPENS FROM A TURN:
/// this code runs while the agent is working, possibly while the user is away, and an agent that
/// silently opens a browser mid-task is hostile. Logging in is something they type.</para>
///
/// <para>A static <c>headers</c> block remains the simpler path and covers every server that takes an
/// API key, which is most of them — the OAuth flow is only reachable by servers that actually
/// answer 401.</para>
/// </summary>
public sealed class McpHttpClient : IMcpConnection
{
    private readonly string _name;
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly HttpClient _http;

    private string? _sessionId;
    private string _protocolVersion = McpProtocol.Latest;
    private int _nextId;

    public string Name => _name;

    /// <summary>The version we asked for — the latest we support, per the spec's SHOULD.</summary>
    public string RequestedProtocolVersion => McpProtocol.Latest;

    /// <summary>What the handshake settled on, or null before it has run.</summary>
    public string? NegotiatedProtocolVersion { get; private set; }

    /// <summary>True when the server answered 401 — it needs a login, not a fix.</summary>
    public bool NeedsAuth { get; private set; }

    /// <summary>Where the 401 said its authorization metadata lives, when it said.</summary>
    public string? AuthMetadataUrl { get; private set; }

    /// <summary>
    /// The bearer token to send, re-read before EVERY request rather than captured once.
    ///
    /// <para>A delegate because a login happens after this client is built, and a refresh replaces
    /// the token mid-session — a value captured at construction would be the one state that is
    /// never current.</para>
    /// </summary>
    public Func<string?>? AccessToken { get; set; }
    public string? Instructions { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<McpToolDef> Tools { get; private set; } = [];

    public McpHttpClient(string name, string url, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null, HttpMessageHandler? handler = null)
    {
        _name = name;
        _url = url;
        _headers = headers;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Handshakes with the server. False means it is not usable, with <see cref="Error"/>
    /// saying why.</summary>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        // A fresh session: forget any id from a previous one, or the server will reject it. The
        // auth state resets too — a retry after logging in must not report the previous 401.
        _sessionId = null;
        NeedsAuth = false;

        var result = await SendAsync("initialize", new
        {
            protocolVersion = McpProtocol.Latest,
            capabilities = new { },
            clientInfo = new { name = "cxagent", version = "1" },
        }, ct, isInitialize: true);

        if (result is null)
        {
            Error ??= "no response to initialize";
            return false;
        }

        // THE SERVER'S COUNTER-OFFER SETTLES IT, and one we cannot speak is a disconnect rather than
        // something to paper over — the same rule and the same supported list as stdio, so a server
        // can never be reachable one way and refused the other.
        var offered = result.Value.TryGetProperty("protocolVersion", out var version)
                   && version.ValueKind == JsonValueKind.String ? version.GetString() : null;

        var (negotiated, versionError) = McpProtocol.Negotiate(offered);
        if (versionError is not null)
        {
            Error = versionError;
            return false;
        }
        _protocolVersion = negotiated!;
        NegotiatedProtocolVersion = negotiated;

        if (result.Value.TryGetProperty("instructions", out var instr)
            && instr.ValueKind == JsonValueKind.String)
        {
            var text = instr.GetString();
            Instructions = string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        }

        // A notification: no id, and no reply to wait for.
        await PostAsync(JsonSerializer.Serialize(
            new { jsonrpc = "2.0", method = "notifications/initialized" }), ct);

        return true;
    }

    public async Task<IReadOnlyList<McpToolDef>> ListToolsAsync(CancellationToken ct)
    {
        var result = await SendAsync("tools/list", new { }, ct);
        if (result is null || !result.Value.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            // A failed refresh clears the cache rather than leaving a dead server's tools on offer.
            Tools = [];
            return [];
        }

        var list = new List<McpToolDef>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var description = tool.TryGetProperty("description", out var d)
                           && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";

            var schema = tool.TryGetProperty("inputSchema", out var s)
                ? s.Clone()
                : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

            list.Add(new McpToolDef(name!, description, schema));
        }

        Tools = list;
        return list;
    }

    /// <summary>Calls a tool. An error is a RESULT, not an exception — the model can correct itself.</summary>
    public async Task<string> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var result = await SendAsync("tools/call", new { name, arguments }, ct);
        if (result is null)
            return $"error calling '{name}': {Error ?? "no response"}";

        var text = TextOf(result.Value);

        if (string.IsNullOrWhiteSpace(text)
            && result.Value.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            text = structured.GetRawText();

        var isError = result.Value.TryGetProperty("isError", out var e)
                   && e.ValueKind == JsonValueKind.True;

        return isError ? $"error: {text}" : text;
    }

    private static string TextOf(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "text" && item.TryGetProperty("text", out var text))
                parts.Add(text.GetString() ?? "");
            else if (type is not null)
                parts.Add($"[{type} content]");
        }
        return string.Join("\n", parts);
    }

    /// <summary>
    /// One request, one reply.
    ///
    /// <para>A 404 means the server dropped the session, and the spec REQUIRES starting a new one
    /// rather than failing the call. That retry happens ONCE: a server that 404s the re-initialised
    /// session too is broken, and looping on it would hang the turn instead of reporting it.</para>
    /// </summary>
    private async Task<JsonElement?> SendAsync(string method, object parameters, CancellationToken ct,
        bool isInitialize = false, bool alreadyRetried = false)
    {
        var id = Interlocked.Increment(ref _nextId);
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });

        HttpResponseMessage response;
        try
        {
            response = await PostAsync(body, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Error = $"timed out after {_http.Timeout.TotalSeconds:N0}s";
            return null;
        }
        catch (Exception ex)
        {
            // A refused connection, a DNS failure, a TLS problem. All of them are "this server is not
            // usable", never an exception the caller has to handle.
            Error = ex.Message;
            return null;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound && !isInitialize && !alreadyRetried)
            {
                // The session is gone. Re-handshake and try the call once more.
                if (!await StartAsync(ct)) return null;
                return await SendAsync(method, parameters, ct, alreadyRetried: true);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // A 401 IS NOT AN ORDINARY FAILURE. It is the server saying "authorize first", and
                // the header tells us where — captured so /mcp login can act on it without the user
                // having to find it. NO BROWSER OPENS HERE: this runs inside a turn, possibly while
                // the user is away, and an agent that silently opens a browser mid-task is hostile.
                // Login is a thing they type.
                NeedsAuth = true;
                AuthMetadataUrl = Auth.ProtectedResource.MetadataUrlFrom(response.Headers);
                Error = "not authorized — run /mcp login " + _name;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // A HANDSHAKE REFUSED WITH 405/404 IS THE SIGNATURE OF AN SSE-ONLY SERVER: on the
                // deprecated 2024-11-05 transport the connect endpoint takes GET, not POST, so our
                // POST is rejected as the wrong method or an unknown path.
                //
                // We do not implement that transport (see the class note), so the one thing owed to
                // someone pointing us at such a server is a message naming the cause. "HTTP 405"
                // sends them to debug their network; naming the transport sends them to the server's
                // docs for a Streamable HTTP url. Other statuses are NOT blamed on it — a 500 is a
                // broken server, and this advice would send them the wrong way.
                Error = isInitialize && response.StatusCode is HttpStatusCode.MethodNotAllowed
                                                            or HttpStatusCode.NotFound
                    ? $"HTTP {(int)response.StatusCode} on the MCP endpoint. This server may only "
                      + "speak the deprecated HTTP+SSE transport, which cxagent does not support — "
                      + "check its documentation for a Streamable HTTP url."
                    : $"HTTP {(int)response.StatusCode}";
                return null;
            }

            if (isInitialize
                && response.Headers.TryGetValues("Mcp-Session-Id", out var ids)
                && ids.FirstOrDefault() is { Length: > 0 } assigned)
                _sessionId = assigned;

            var payload = await ReadPayloadAsync(response, ct);
            if (payload is null)
            {
                Error = "the server sent no JSON-RPC message";
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var error))
                {
                    Error = error.TryGetProperty("message", out var m) ? m.GetString() : "server error";
                    return null;
                }

                return root.TryGetProperty("result", out var result) ? result.Clone() : null;
            }
            catch (JsonException ex)
            {
                Error = $"unreadable response: {ex.Message}";
                return null;
            }
        }
    }

    private Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        // BOTH content types, always. The server chooses which to answer with, and one that sees only
        // application/json is entitled to refuse a request whose reply it wanted to stream.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (_sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);

        // The OAuth token, when we have one. In the HEADER, never a query string: the spec forbids
        // it, and query strings land in server logs, proxy logs and browser history.
        if (AccessToken?.Invoke() is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        // Configured headers LAST, so a user who needs to override one of ours can.
        if (_headers is not null)
            foreach (var (key, value) in _headers)
                request.Headers.TryAddWithoutValidation(key, value);

        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// The JSON-RPC message, whichever way the server chose to send it.
    ///
    /// <para>SSE PARSING IS <c>data:</c> LINES ONLY. <c>event:</c>, <c>id:</c> and comment lines are
    /// ignored, and consecutive <c>data:</c> lines concatenate per the SSE grammar. This is not a
    /// general SSE client and should not become one: we need the first complete message off the
    /// stream, and then we are done with it.</para>
    /// </summary>
    private static async Task<string?> ReadPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        if (!contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                // A blank line ends the event. If it carried data, that is our message.
                if (data.Length > 0) return data.ToString();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
            // Everything else — event:, id:, retry:, ": comment" — is not ours to interpret.
        }

        return data.Length > 0 ? data.ToString() : null;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
