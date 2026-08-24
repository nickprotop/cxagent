using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CxAgent.Plugins.LspAbi;

/// <summary>
/// LSP's own JSON-RPC framing over a pair of streams: each message is preceded by
/// <c>Content-Length: N\r\n\r\n</c> and no other header this plugin needs to read. Not the general
/// JSON-RPC-over-HTTP shape — LSP fixed this exact header set in its spec, so there is nothing here
/// to make configurable.
///
/// <para>IDENTICAL TO THE MANAGED PLUGIN'S JsonRpcConnection IN EVERY WAY BUT ONE: <see
/// cref="SendRequestAsync{TParams}"/> and <see cref="SendNotification{TParams}"/> are generic over
/// the params type and take a <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>
/// instead of serializing a bare <c>object?</c> with <c>JsonSerializer.SerializeToNode(@params)</c>.
/// That one change is forced by NativeAOT, not by anything about the ABI boundary itself — reflection-
/// based serialization of an arbitrary <c>object</c> throws at runtime once trimming/AOT strips the
/// reflection metadata it needs (see the plugin host's own README for the exact exception), so every
/// payload this connection writes needs a source-generated <see
/// cref="System.Text.Json.Serialization.JsonSerializerContext"/> entry — see LspProtocolJson.cs. The
/// managed plugin has no such constraint: it runs inside the host process's ordinary JIT, where
/// reflection-based serialization of an anonymous object works with no annotation at all.</para>
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _writeStream;
    private readonly Stream _readStream;
    private readonly object _writeLock = new();
    private int _nextId;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly object _pendingLock = new();
    private readonly Action<string, JsonNode?>? _onNotification;
    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCts;

    public JsonRpcConnection(Stream writeStream, Stream readStream, Action<string, JsonNode?>? onNotification = null)
    {
        _writeStream = writeStream;
        _readStream = readStream;
        _onNotification = onNotification;
    }

    /// <summary>
    /// Starts the background read loop. Separate from the constructor so a caller can wire
    /// <c>onNotification</c> (diagnostics arrive this way, unsolicited) before anything can race it.
    /// </summary>
    public void StartReading()
    {
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    /// <summary>Sends a request and awaits its matched response — correlated by numeric id, which is
    /// this connection's own counter rather than anything the caller supplies, since nothing here
    /// needs ids to mean anything beyond "which pending call is this."</summary>
    public async Task<JsonNode?> SendRequestAsync<TParams>(string method, TParams? @params,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams>? typeInfo, CancellationToken ct)
    {
        int id;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            id = ++_nextId;
            _pending[id] = tcs;
        }

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params is null || typeInfo is null ? null : JsonSerializer.SerializeToNode(@params, typeInfo),
        };
        WriteMessage(envelope);

        // THE PUMP TASK OWNS COMPLETING tcs; a call cancelled here just abandons the pending entry
        // rather than removing it, because the server may still send a matching response and the
        // pump must not fault trying to complete a TCS nobody is waiting on. Dictionary entries for
        // a long-dead session are bounded by the session's own lifetime — this plugin's process, not
        // an unbounded accumulation across a long-running one.
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>A request with no params — <c>shutdown</c>, and nothing else this plugin sends.</summary>
    public Task<JsonNode?> SendRequestAsync(string method, CancellationToken ct) =>
        SendRequestAsync<object?>(method, null, null, ct);

    /// <summary>Sends a notification — no id, no response expected. Used for <c>initialized</c> and
    /// <c>textDocument/didOpen</c>, where the LSP spec defines no reply.</summary>
    public void SendNotification<TParams>(string method, TParams? @params,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams>? typeInfo)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params is null || typeInfo is null ? null : JsonSerializer.SerializeToNode(@params, typeInfo),
        };
        WriteMessage(envelope);
    }

    /// <summary>A notification with no params — <c>exit</c>, and nothing else this plugin sends.</summary>
    public void SendNotification(string method) => SendNotification<object?>(method, null, null);

    private void WriteMessage(JsonNode envelope)
    {
        var json = envelope.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";

        // ONE LOCK FOR THE WHOLE FRAME. Interleaving two writers' header-then-body would let one
        // request's bytes land inside another's declared length, and the server would read garbage
        // past the boundary with no way to resynchronise short of dropping the connection.
        lock (_writeLock)
        {
            var headerBytes = Encoding.ASCII.GetBytes(header);
            _writeStream.Write(headerBytes, 0, headerBytes.Length);
            _writeStream.Write(bytes, 0, bytes.Length);
            _writeStream.Flush();
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var node = await ReadMessageAsync(ct).ConfigureAwait(false);
                if (node is null) break; // stream closed — the server process exited.

                if (node["id"] is JsonNode idNode && node["method"] is null)
                {
                    // A RESPONSE: has an id, no method. Errors surface as a fault on the awaiting
                    // call rather than a silently-null result, so a caller sees WHY a definition
                    // lookup came back empty instead of guessing between "no result" and "broke."
                    var id = idNode.GetValue<int>();
                    TaskCompletionSource<JsonNode?>? tcs;
                    lock (_pendingLock)
                    {
                        _pending.Remove(id, out tcs);
                    }
                    if (tcs is null) continue;

                    if (node["error"] is JsonNode error)
                        tcs.TrySetException(new LspErrorException(error.ToJsonString()));
                    else
                        tcs.TrySetResult(node["result"]);
                }
                else if (node["method"] is JsonNode methodNode)
                {
                    // A REQUEST OR NOTIFICATION FROM THE SERVER. This plugin answers no server-initiated
                    // requests (no window/showMessageRequest, no workspace/configuration) — the three
                    // tools in scope never need the server to ask anything back — so only notifications
                    // are forwarded; a server request with an id is simply left unanswered, which every
                    // server here tolerates for requests it never receives an answer to relying on.
                    if (node["id"] is null)
                        _onNotification?.Invoke(methodNode.GetValue<string>(), node["params"]);
                }
            }
        }
        catch (OperationCanceledException) { /* Stop() cancelled the pump — not a fault. */ }
        finally
        {
            // A CLOSED PIPE LEAVES NO RESPONSE COMING. Every still-pending call would otherwise hang
            // until its own caller's cancellation token fires, which for Lifetime-scoped calls could
            // be "never" — so a dead connection cancels them itself rather than leaking awaiters.
            TaskCompletionSource<JsonNode?>[] abandoned;
            lock (_pendingLock)
            {
                abandoned = _pending.Values.ToArray();
                _pending.Clear();
            }
            foreach (var tcs in abandoned)
                tcs.TrySetException(new LspErrorException("connection closed before a response arrived."));
        }
    }

    private async Task<JsonNode?> ReadMessageAsync(CancellationToken ct)
    {
        int? contentLength = null;
        var lineBytes = new List<byte>();

        while (true)
        {
            var b = _readStream.ReadByte();
            if (b < 0) return null; // EOF: the server process closed its stdout.

            if (b == '\n')
            {
                var line = Encoding.ASCII.GetString(lineBytes.ToArray()).TrimEnd('\r');
                lineBytes.Clear();

                if (line.Length == 0) break; // blank line ends the header block.

                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var name = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(value);
                }
            }
            else
            {
                lineBytes.Add((byte)b);
            }
        }

        if (contentLength is not int len)
            throw new LspErrorException("message had no Content-Length header.");

        var buffer = new byte[len];
        var offset = 0;
        while (offset < len)
        {
            var read = await _readStream.ReadAsync(buffer.AsMemory(offset, len - offset), ct).ConfigureAwait(false);
            if (read == 0) return null; // EOF mid-body: same "server exited" case as an EOF on the header.
            offset += read;
        }

        return JsonNode.Parse(buffer);
    }

    public async ValueTask DisposeAsync()
    {
        _pumpCts?.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch { /* pump's own exceptions are already surfaced to pending callers. */ }
        }
        _pumpCts?.Dispose();
    }
}

/// <summary>An LSP <c>error</c> response, or a transport failure (closed pipe, missing header) that
/// prevented one from ever being read — both are "this call did not get a usable answer," which is
/// what every catch site here actually needs to know.</summary>
public sealed class LspErrorException(string message) : Exception(message);
