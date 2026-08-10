using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CxAgent.Core.Mcp;

/// <summary>One tool a server offers, exactly as the server described it.</summary>
/// <param name="Description">
/// The server author's own prose. Carried verbatim: it is the only thing telling the model what a
/// tool is FOR, and a schema cannot say it.
/// </param>
public sealed record McpToolDef(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// One MCP server, spoken to over its stdin/stdout as a child process.
///
/// <para>Newline-framed JSON-RPC 2.0, and only the three methods that make tools work:
/// <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>, plus the <c>notifications/initialized</c>
/// the handshake requires. No resources, no prompts, no sampling — those are surface area with no
/// caller here.</para>
///
/// <para>NOTHING HERE THROWS AT THE CALLER. A server that will not start, dies mid-session, or never
/// answers produces a false or an error string; the session carries on without those tools. This is
/// third-party code on the end of a pipe, and it must not be able to take the app down — the same
/// contract <see cref="Storage.LogFileManager"/> and <see cref="Storage.SqliteSessionStore"/> hold.</para>
/// </summary>
public sealed class McpClient : IMcpConnection
{
    /// <summary>
    /// The tools from the last <see cref="ListToolsAsync"/>, so <see cref="IMcpServer"/> can expose
    /// them without an await.
    ///
    /// <para>Cached rather than fetched on demand because the tool list is read while BUILDING a
    /// request — a synchronous path, on every prompt. Re-asking the server there would put a pipe
    /// round-trip in front of every turn, and a server that had gone slow would stall the session
    /// rather than merely lose its tools.</para>
    /// </summary>
    public IReadOnlyList<McpToolDef> Tools { get; private set; } = [];

    private readonly string _name;
    private readonly string[] _command;
    private readonly TimeSpan _timeout;

    private Process? _process;
    private Task? _reader;
    private Task? _stderrReader;
    private int _nextId;

    /// <summary>
    /// Calls waiting on a reply, by id.
    ///
    /// <para>Keyed rather than a single slot because a reply can arrive for a call that has already
    /// timed out — see the reader, which discards those rather than handing them to whoever happens
    /// to be waiting next. Answering call N with call N-1's result is the kind of bug that surfaces
    /// as a mysterious wrong answer hours later.</para>
    /// </summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    /// <summary>The server's own usage prose from <c>initialize</c>, or null if it sent none.</summary>
    public string? Instructions { get; private set; }

    /// <summary>The configured name, which prefixes every tool this server offers.</summary>
    public string Name => _name;

    /// <summary>Why the server is not usable, or null while it is. Shown to the user verbatim: a
    /// server that failed with "npx: command not found" is one they can fix in a second.</summary>
    public string? Error { get; private set; }

    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly string? _workingDirectory;

    /// <param name="environment">
    /// Variables for the child, merged OVER what it inherits. This is the spec's prescribed
    /// credential channel for stdio: <i>"Implementations using an STDIO transport SHOULD NOT follow
    /// this specification, and instead retrieve credentials from the environment."</i>
    /// </param>
    /// <param name="workingDirectory">Where to start it, or null to inherit ours.</param>
    public McpClient(string name, string[] command, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        _name = name;
        _command = command;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _environment = environment;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Spawns the server and completes the handshake. False means it is not usable, with
    /// <see cref="Error"/> saying why.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        if (_command.Length == 0)
        {
            Error = "no command configured";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _command[0],
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in _command.Skip(1)) psi.ArgumentList.Add(arg);

            // MERGED OVER THE INHERITED ENVIRONMENT, not replacing it. ProcessStartInfo seeds
            // psi.Environment from ours, so assigning only adds and overrides — which is what a
            // server needs: wiping it would take PATH, HOME and any proxy variable with it, and most
            // servers would simply fail to launch.
            if (_environment is not null)
                foreach (var (key, value) in _environment)
                    psi.Environment[key] = value;

            // Servers that take a path argument resolve it against their cwd, so one started from
            // wherever cxagent happened to launch reads a different tree than the user meant.
            if (!string.IsNullOrWhiteSpace(_workingDirectory))
                psi.WorkingDirectory = _workingDirectory;

            _process = Process.Start(psi);
            if (_process is null)
            {
                Error = "could not start";
                return false;
            }

            // ONE READER FOR STDOUT. Two would interleave partial lines off a single stream, which
            // corrupts a frame rather than merely reordering it.
            _reader = Task.Run(ReadLoopAsync);

            // AND STDERR IS DRAINED, never parsed. A chatty server whose stderr pipe fills blocks on
            // write and deadlocks — with nothing on screen to explain why it stopped answering.
            _stderrReader = Task.Run(async () =>
            {
                try { await _process.StandardError.ReadToEndAsync(); }
                catch (Exception) { /* the process is gone; nothing to drain */ }
            });

            var init = await SendAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "cxagent", version = "1" },
            }, ct);

            if (init is null)
            {
                Error ??= "no response to initialize";
                return false;
            }

            if (init.Value.TryGetProperty("instructions", out var instr)
                && instr.ValueKind == JsonValueKind.String)
            {
                var text = instr.GetString();
                Instructions = string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
            }

            // A NOTIFICATION, so no id and no reply to wait for. Servers are entitled to refuse work
            // until they have seen it.
            await WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, ct);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>The tools this server offers, or an empty list if it cannot say.</summary>
    public async Task<IReadOnlyList<McpToolDef>> ListToolsAsync(CancellationToken ct)
    {
        var result = await SendAsync("tools/list", new { }, ct);
        if (result is null || !result.Value.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            // A FAILED REFRESH CLEARS THE CACHE rather than leaving the last good answer in place.
            // Keeping it would offer the model tools of a server that has stopped answering, and
            // every call would fail one turn later — worse than the tools being gone, because the
            // model would keep choosing them.
            Tools = [];
            return [];
        }

        var list = new List<McpToolDef>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var description = tool.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";

            // THE SCHEMA WHOLE, not rebuilt from its property names — per-parameter descriptions live
            // inside it, and they are half of what tells the model how to call the thing.
            var schema = tool.TryGetProperty("inputSchema", out var s)
                ? s.Clone()
                : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

            list.Add(new McpToolDef(name!, description, schema));
        }

        Tools = list;
        return list;
    }

    /// <summary>
    /// Calls a tool and returns what it produced, as text for the model to read.
    ///
    /// <para>AN ERROR IS A RESULT, NOT AN EXCEPTION. <c>isError</c> comes back as its own text so the
    /// model can correct itself; throwing would end the turn over something it could have fixed.</para>
    /// </summary>
    public async Task<string> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var result = await SendAsync("tools/call", new { name, arguments }, ct);
        if (result is null)
            return Error is null
                ? $"error: '{name}' timed out after {_timeout.TotalSeconds:N0}s"
                : $"error calling '{name}': {Error}";

        var text = TextOf(result.Value);

        // STRUCTURED-ONLY RESULTS still have to say something. A server that answers entirely in
        // structuredContent would otherwise come back blank.
        if (string.IsNullOrWhiteSpace(text)
            && result.Value.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            text = structured.GetRawText();

        var isError = result.Value.TryGetProperty("isError", out var e)
                   && e.ValueKind == JsonValueKind.True;

        return isError ? $"error: {text}" : text;
    }

    /// <summary>The text parts of a tool result, joined. Non-text content is named rather than
    /// dropped silently — an image the model cannot see is still something it should know arrived.</summary>
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

    /// <summary>Sends a request and waits for its reply, or null on timeout, death or malformed JSON.</summary>
    private async Task<JsonElement?> SendAsync(string method, object parameters, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
        {
            Error ??= "the server is not running";
            return null;
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);

            // A TIMED-OUT CALL ABANDONS ITS ID; the server is left alone. Killing it would take every
            // other tool on that server down over one slow call, and a late reply costs nothing — the
            // reader drops replies nobody is waiting for.
            await using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        await _process!.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    /// <summary>Reads replies until the pipe closes, matching each to the call waiting on its id.</summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (_process is not null && !_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.Number) continue;

                    // A reply for an id nobody is waiting on is DISCARDED, not handed to the next
                    // caller — that is how a timed-out call's late answer becomes another call's
                    // wrong one.
                    if (!_pending.TryRemove(idElement.GetInt32(), out var waiting)) continue;

                    if (root.TryGetProperty("error", out var error))
                    {
                        Error = error.TryGetProperty("message", out var m) ? m.GetString() : "server error";
                        waiting.TrySetCanceled();
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        waiting.TrySetResult(result.Clone());
                    }
                    else
                    {
                        waiting.TrySetCanceled();
                    }
                }
                catch (JsonException)
                {
                    // A server writing something that is not a JSON frame — a stray log line on
                    // stdout — is not a reason to stop reading the ones that are.
                }
            }
        }
        catch (Exception)
        {
            // The pipe closed under us. Whoever is waiting will time out and say so.
        }
        finally
        {
            // Nothing more will arrive; release anyone still waiting rather than letting them sit
            // out their full timeout for an answer that can never come.
            foreach (var waiting in _pending.Values) waiting.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                // Closing stdin is how an MCP server is asked to stop; killing is the fallback for
                // one that does not take the hint. An orphaned child outlives the app and holds
                // whatever it had open.
                try { _process.StandardInput.Close(); } catch (Exception) { }
                if (!_process.WaitForExit(1_000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { /* best effort */ }

        try
        {
            if (_reader is not null) await _reader.WaitAsync(TimeSpan.FromSeconds(1));
            if (_stderrReader is not null) await _stderrReader.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception) { /* the readers are ending anyway */ }

        _process?.Dispose();
        _process = null;
    }
}
