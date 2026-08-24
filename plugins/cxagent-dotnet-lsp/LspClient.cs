using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CxAgent.Plugins.Lsp;

/// <summary>One LSP position, in the SERVER'S convention (0-based line and character) — never the
/// 1-based one the tool schema hands the plugin. Conversion happens once, at the tool boundary in
/// <see cref="CxagentLspPlugin"/>, so nothing past that point has to remember which convention it is
/// holding.</summary>
public sealed record LspPosition(int Line, int Character);

/// <summary>A location the server reported: a file URI plus the span within it.</summary>
public sealed record LspLocation(string UriOrPath, LspPosition Start, LspPosition End);

/// <summary>One diagnostic entry, already flattened to the fields a tool result needs.</summary>
public sealed record LspDiagnostic(int Line, int Character, string Severity, string Message);

/// <summary>
/// Speaks just enough LSP for definition, references and diagnostics: the initialize handshake,
/// didOpen, the three request methods, and the publishDiagnostics push notification.
///
/// <para>ONE SERVER PROCESS, ONE CLIENT, FOR THE PLUGIN'S WHOLE LIFETIME. A language server's value
/// here is its warm index across the workspace — csharp-ls and OmniSharp both take real seconds to
/// load a solution's projects, and starting fresh per tool call would pay that cost on every single
/// lsp_definition. See IPluginContext.Lifetime's own doc: this class is what actually lives on that
/// token.</para>
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly JsonRpcConnection _rpc;
    private readonly HashSet<string> _openDocuments = new();
    private readonly object _openLock = new();

    /// <summary>Diagnostics as last pushed by the server, keyed by the document URI they describe.
    /// <c>publishDiagnostics</c> is a REPLACEMENT, not a delta — the spec defines each push as the
    /// full current set for that file, so a fresh push simply overwrites rather than merges.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<LspDiagnostic>> _diagnostics = new();

    private LspClient(Process process, JsonRpcConnection rpc)
    {
        _process = process;
        _rpc = rpc;
    }

    /// <summary>
    /// Spawns <paramref name="serverCommand"/> and completes the LSP initialize handshake against
    /// <paramref name="workspaceRoot"/>.
    ///
    /// <para>THE PROCESS ID IS RETURNED ALONGSIDE THE CLIENT, not registered here. Registration needs
    /// <c>IPluginContext.RegisterChildProcess</c>, which this class has no reference to — keeping the
    /// dependency one-directional (plugin depends on client, not the reverse) is what lets this class
    /// be tested without a fake IPluginContext at all.</para>
    /// </summary>
    public static async Task<(LspClient Client, int ProcessId)> StartAsync(
        string command, IReadOnlyList<string> args, string workspaceRoot, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workspaceRoot,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start language server '{command}'.");

        // STDERR IS DRAINED, NOT READ. Neither server's diagnostic chatter is protocol; leaving the
        // pipe unread risks the OS-buffer deadlock RedirectStandardError otherwise invites once the
        // child writes enough of it.
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync().ConfigureAwait(false); }
            catch { /* process exit races the read; nothing to act on either way. */ }
        });

        // THE CLIENT MUST EXIST BEFORE THE CONNECTION IS BUILT, so the notification callback below
        // can close over it — but assigning it only after construction (default(LspClient!) here,
        // reassigned once real) would let a race between StartReading and the field write reach
        // OnNotification through a stale reference. A local mutable capture avoids that: the
        // closure reads `client` fresh each call, and StartReading only runs after `client` holds
        // its final value.
        LspClient? client = null;
        var rpc = new JsonRpcConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            (method, @params) => client!.OnNotification(method, @params));
        client = new LspClient(process, rpc);
        rpc.StartReading();

        await client.InitializeAsync(workspaceRoot, ct).ConfigureAwait(false);
        return (client, process.Id);
    }

    private void OnNotification(string method, JsonNode? @params)
    {
        if (method != "textDocument/publishDiagnostics" || @params is null) return;

        var uri = @params["uri"]?.GetValue<string>();
        if (uri is null) return;

        var items = new List<LspDiagnostic>();
        if (@params["diagnostics"] is JsonArray arr)
        {
            foreach (var d in arr)
            {
                if (d is null) continue;
                var range = d["range"]?["start"];
                var line = range?["line"]?.GetValue<int>() ?? 0;
                var character = range?["character"]?.GetValue<int>() ?? 0;
                var severity = SeverityName(d["severity"]?.GetValue<int?>());
                var message = d["message"]?.GetValue<string>() ?? "";
                items.Add(new LspDiagnostic(line, character, severity, message));
            }
        }
        _diagnostics[uri] = items;
    }

    private static string SeverityName(int? severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "information",
        4 => "hint",
        _ => "unknown",
    };

    /// <summary>
    /// The <c>initialize</c>/<c>initialized</c> handshake, rooting the server at
    /// <paramref name="workspaceRoot"/> via <c>rootUri</c> — the LSP-standard way a server learns its
    /// workspace. csharp-ls has no command-line workspace flag and learns its root ONLY this way;
    /// OmniSharp accepts <c>-s</c> too, but sending rootUri regardless costs nothing and keeps this
    /// method identical for both servers, which is the point — see PLUGINS.md and the task brief on
    /// reading settings rather than hardcoding per-server behaviour.
    /// </summary>
    private async Task InitializeAsync(string workspaceRoot, CancellationToken ct)
    {
        var rootUri = new Uri(workspaceRoot.TrimEnd('/') + "/").AbsoluteUri;
        var initParams = new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    synchronization = new { dynamicRegistration = false },
                    definition = new { dynamicRegistration = false },
                    references = new { dynamicRegistration = false },
                    publishDiagnostics = new { relatedInformation = false },
                },
            },
        };
        await _rpc.SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);
        _rpc.SendNotification("initialized", new { });
    }

    /// <summary>Opens a document with the server if not already open — definition/references/diagnostics
    /// all require <c>textDocument/didOpen</c> first; the server has no other way to learn a file's
    /// current content or start analysing it.</summary>
    public void EnsureOpen(string absolutePath)
    {
        var uri = new Uri(absolutePath).AbsoluteUri;
        lock (_openLock)
        {
            if (!_openDocuments.Add(uri)) return;
        }

        var text = File.ReadAllText(absolutePath);

        // THE ONE LANGUAGE-SPECIFIC LINE IN THIS PLUGIN, and the reason its name says dotnet. A
        // server decides how to parse a document from the languageId it is handed, so this constant
        // is what scopes the plugin to C# — everything else here (the framing, the handshake, the
        // position conversion, the server command itself) is protocol, not language. Pointing this
        // plugin at gopls or rust-analyzer would not fail loudly; the server would accept the
        // document, parse it as C#, and quietly return nothing useful. A general LSP plugin derives
        // this from the file extension instead; that is a different plugin, deliberately not this one.
        _rpc.SendNotification("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "csharp", version = 1, text },
        });
    }

    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(string absolutePath, LspPosition position, CancellationToken ct)
    {
        var result = await RequestLocationsAsync("textDocument/definition", absolutePath, position, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(string absolutePath, LspPosition position, CancellationToken ct)
    {
        var uri = new Uri(absolutePath).AbsoluteUri;
        var result = await _rpc.SendRequestAsync("textDocument/references", new
        {
            textDocument = new { uri },
            position = new { line = position.Line, character = position.Character },
            context = new { includeDeclaration = true },
        }, ct).ConfigureAwait(false);

        return ParseLocations(result);
    }

    private async Task<IReadOnlyList<LspLocation>> RequestLocationsAsync(
        string method, string absolutePath, LspPosition position, CancellationToken ct)
    {
        var uri = new Uri(absolutePath).AbsoluteUri;
        var result = await _rpc.SendRequestAsync(method, new
        {
            textDocument = new { uri },
            position = new { line = position.Line, character = position.Character },
        }, ct).ConfigureAwait(false);

        return ParseLocations(result);
    }

    /// <summary>
    /// <c>textDocument/definition</c> may answer with a single Location, a Location[], or a
    /// LocationLink[] — the LSP spec allows all three depending on server capability, and both servers
    /// here answer differently for the same request shape. Reading uri/range from EITHER a `location`
    /// wrapper (LocationLink) or the object itself (Location) is what lets one code path handle both
    /// without knowing in advance which one a given server sent.
    /// </summary>
    private static IReadOnlyList<LspLocation> ParseLocations(JsonNode? result)
    {
        // A `null` RESULT MEANS "no definition/references found" per the LSP spec, not an error —
        // JsonNode represents JSON null as a C# null here, same as an absent key, so both collapse
        // to the same empty answer.
        if (result is null) return [];

        var nodes = result is JsonArray arr ? arr.ToList() : [result];
        var locations = new List<LspLocation>();

        foreach (var node in nodes)
        {
            if (node is null) continue;
            // LocationLink carries the span under `targetUri`/`targetRange`; Location carries it
            // directly under `uri`/`range`. Trying targetUri first and falling back covers both.
            var uri = node["targetUri"]?.GetValue<string>() ?? node["uri"]?.GetValue<string>();
            var range = node["targetSelectionRange"] ?? node["targetRange"] ?? node["range"];
            if (uri is null || range is null) continue;

            var start = range["start"];
            var end = range["end"];
            locations.Add(new LspLocation(uri,
                new LspPosition(start?["line"]?.GetValue<int>() ?? 0, start?["character"]?.GetValue<int>() ?? 0),
                new LspPosition(end?["line"]?.GetValue<int>() ?? 0, end?["character"]?.GetValue<int>() ?? 0)));
        }

        return locations;
    }

    /// <summary>
    /// Whatever the server has already pushed for this file — NOT a fresh request, because
    /// <c>textDocument/diagnostics</c> pull support is optional in the spec and neither server here
    /// implements it; publishDiagnostics push is the only mechanism both share. An empty result right
    /// after <see cref="EnsureOpen"/> usually means the server has not finished analysing yet, which
    /// the tool's own description tells the model to expect.
    /// </summary>
    public IReadOnlyList<LspDiagnostic> Diagnostics(string absolutePath)
    {
        var uri = new Uri(absolutePath).AbsoluteUri;
        return _diagnostics.TryGetValue(uri, out var list) ? list : [];
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        try
        {
            await _rpc.SendRequestAsync("shutdown", null, ct).ConfigureAwait(false);
            _rpc.SendNotification("exit", null);
        }
        catch { /* the server may already be gone — Stop() kills the process either way. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _rpc.DisposeAsync().ConfigureAwait(false);

        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* already exiting. */ }
        }
        _process.Dispose();
    }
}
