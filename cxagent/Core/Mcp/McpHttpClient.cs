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
/// <para>AUTHORIZATION IS NOT IMPLEMENTED HERE, deliberately. The spec's OAuth flow is triggered by a
/// 401 carrying <c>WWW-Authenticate</c>, and it is a whole stack — RFC 9728 discovery, RFC 8414
/// metadata, OAuth 2.1 with PKCE and RFC 8707 resource indicators, a callback listener. A static
/// <c>headers</c> block covers every server that takes an API key, which is most of them, and is why
/// this ships useful without any of that.</para>
/// </summary>
public sealed class McpHttpClient : IMcpServer, IAsyncDisposable
{
    /// <summary>
    /// The revision we ask for.
    ///
    /// <para>NOT <c>2024-11-05</c>, which is the oldest and pairs with the deprecated HTTP+SSE
    /// transport. A server that disagrees counter-offers its own version and we accept it, so asking
    /// high costs nothing and asking low would pin us to a transport this class does not speak.</para>
    /// </summary>
    private const string PreferredProtocolVersion = "2025-06-18";

    private readonly string _name;
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly HttpClient _http;

    private string? _sessionId;
    private string _protocolVersion = PreferredProtocolVersion;
    private int _nextId;

    public string Name => _name;
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
        // A fresh session: forget any id from a previous one, or the server will reject it.
        _sessionId = null;

        var result = await SendAsync("initialize", new
        {
            protocolVersion = PreferredProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "cxagent", version = "1" },
        }, ct, isInitialize: true);

        if (result is null)
        {
            Error ??= "no response to initialize";
            return false;
        }

        // THE SERVER'S COUNTER-OFFER WINS. Legacy negotiation is "the server answers with a version
        // it supports"; ignoring that and carrying on at ours is how a client ends up sending a
        // dialect the other end never agreed to.
        if (result.Value.TryGetProperty("protocolVersion", out var version)
            && version.ValueKind == JsonValueKind.String
            && version.GetString() is { Length: > 0 } negotiated)
            _protocolVersion = negotiated;

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

            if (!response.IsSuccessStatusCode)
            {
                Error = $"HTTP {(int)response.StatusCode}";
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
