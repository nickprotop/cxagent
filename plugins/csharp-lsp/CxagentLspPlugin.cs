using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Lsp;

/// <summary>
/// One managed plugin, one language server, three tools — see PLUGINS.md, "What a plugin is": the
/// plugin IS the executor its tools share, holding the one LspClient connection all three dispatch
/// through.
///
/// <para>THE SERVER COMMAND AND ITS ARGUMENTS COME FROM SETTINGS, NEVER HARDCODED. csharp-ls and
/// OmniSharp are configured in KIND, not just in command line — csharp-ls learns its workspace from
/// the LSP initialize handshake alone, while OmniSharp additionally wants `-lsp` to speak LSP at all
/// and can be told its root with `-s`. Reading `server` and `args` verbatim from
/// <see cref="IPluginContext.Settings"/> is what lets one binary serve both without an if-branch
/// naming either server: {"server":"csharp-ls","args":[]} and
/// {"server":"/opt/omnisharp/OmniSharp","args":["-lsp"]} are both just "start this command with
/// these arguments" to this class.</para>
/// </summary>
public sealed class CxagentLspPlugin : IPlugin
{
    private IPluginContext _context = null!;
    private LspClient? _client;
    private string _workingDirectory = "";

    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _workingDirectory = context.WorkingDirectory;

        // THE MANIFEST RETURNED HERE MUST MATCH csharp-lsp.plugin.json BYTE FOR BYTE IN SHAPE — see
        // IPlugin.Load's own doc. Duplicating the schema by hand risks exactly the drift that check
        // exists to catch, so this reads and parses the sidecar rather than restating it — the same
        // file ships beside the DLL either way, and there is only one JSON to keep truthful.
        // BESIDE THIS ASSEMBLY, NOT BESIDE THE HOST EXECUTABLE. AppContext.BaseDirectory is the
        // running process's own directory — the app's output folder, never this plugin's. The two
        // coincide only when a plugin sits in that same folder, which is what the unit tests in this
        // repo do and production never does: PluginDiscovery searches .cxagent/plugins and the global
        // config folder, so a sidecar path built from the host's directory looks in the wrong place
        // for every real load.
        //
        // NOT UNIT-TESTABLE FROM THE TEST HOST, which is why the constraint is written here. Proving
        // it needs the plugin loaded from a directory that is not the test host's own, and
        // ManagedPluginLoader uses Assembly.LoadFrom with no AssemblyLoadContext of its own (see its
        // type doc) — a second copy of an already-loaded identity resolves back to the resident
        // instance, so an in-process test keeps the test host's directory whichever way this line is
        // written. It is verified by loading the plugin from /tmp/cxgpu/.cxagent/plugins in a
        // separate process instead.
        var here = Path.GetDirectoryName(typeof(CxagentLspPlugin).Assembly.Location)!;
        var sidecarPath = Path.Combine(here, "csharp-lsp.plugin.json");
        var parsed = PluginManifest.Parse(File.ReadAllText(sidecarPath));
        if (!parsed.IsSuccess || parsed.Manifest is null)
            throw new InvalidOperationException(
                $"csharp-lsp's own sidecar failed to parse: {string.Join("; ", parsed.Errors)}");

        return Task.FromResult(parsed.Manifest);
    }

    public async Task Start(CancellationToken ct)
    {
        var (server, args) = ReadServerSettings(_context.Settings);

        var (client, processId) = await LspClient.StartAsync(server, args, _workingDirectory, ct)
            .ConfigureAwait(false);
        _client = client;

        // REGISTERED THE MOMENT THE PROCESS EXISTS, before any request is sent. A failed initialize
        // handshake still leaves a live process that needs reaping — registering only after success
        // would leak exactly the crash case RegisterChildProcess exists for.
        _context.RegisterChildProcess(processId);
    }

    /// <summary>
    /// Reads <c>settings.server</c> (required) and <c>settings.args</c> (optional, defaulting to
    /// none) — the whole of what this plugin needs to know to start EITHER server, and nothing more,
    /// because anything more would be this plugin guessing at a server's identity from its path
    /// rather than being told.
    /// </summary>
    private static (string Server, IReadOnlyList<string> Args) ReadServerSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("server", out var serverEl) ||
            serverEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "csharp-lsp requires a 'server' string in its settings — the language server command to run.");
        }

        var server = serverEl.GetString()!;
        var args = new List<string>();
        if (settings.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
            foreach (var a in argsEl.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String)
                    args.Add(a.GetString()!);

        return (server, args);
    }

    public async Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct)
    {
        // AN UNKNOWN NAME IS CHECKED BEFORE THE "IS THE SERVER RUNNING" CHECK BELOW — see
        // IPlugin.Invoke's own doc: toolName is always one this plugin's own manifest declared, so
        // reaching an unrecognised one is this plugin's bug regardless of Start/Stop state, not a
        // startup-ordering mistake a caller could otherwise confuse it with.
        if (toolName is not ("lsp_definition" or "lsp_references" or "lsp_diagnostics"))
            throw new InvalidOperationException($"csharp-lsp has no tool named '{toolName}'.");

        if (_client is null)
            return new JobResult { Success = false, ErrorMessage = "language server is not running." };

        // A CALL'S OWN TOKEN GOES TO THE REQUEST, NOT THE SERVER'S LIFETIME. The server outlives
        // every individual call (IPluginContext.Lifetime's own doc), but one slow lookup should be
        // cancellable without tearing down the connection every other call still needs.
        try
        {
            return toolName switch
            {
                "lsp_definition" => await HandleDefinitionAsync(call, ct).ConfigureAwait(false),
                "lsp_references" => await HandleReferencesAsync(call, ct).ConfigureAwait(false),
                _ => HandleDiagnostics(call),
            };
        }
        catch (LspErrorException ex)
        {
            return new JobResult { Success = false, ErrorMessage = $"language server error: {ex.Message}" };
        }
    }

    private async Task<JobResult> HandleDefinitionAsync(JobParameters call, CancellationToken ct)
    {
        var (path, position) = OpenAndResolvePosition(call);
        var locations = await _client!.DefinitionAsync(path, position, ct).ConfigureAwait(false);
        return LocationsResult(locations);
    }

    private async Task<JobResult> HandleReferencesAsync(JobParameters call, CancellationToken ct)
    {
        var (path, position) = OpenAndResolvePosition(call);
        var locations = await _client!.ReferencesAsync(path, position, ct).ConfigureAwait(false);
        return LocationsResult(locations);
    }

    private JobResult HandleDiagnostics(JobParameters call)
    {
        var path = ResolvePath(call.Get<string>("file"));
        _client!.EnsureOpen(path);
        var diagnostics = _client.Diagnostics(path);

        return new JobResult
        {
            Success = true,
            Output =
            {
                // See LocationsResult's own doc: Agent renders Output["content"] and nothing else,
                // so a diagnostics result without it reaches the model blank.
                ["content"] = diagnostics.Count == 0
                    ? "No diagnostics for that file."
                    : string.Join("\n", diagnostics.Select(d =>
                        $"{d.Line + 1}:{d.Character + 1} {d.Severity}: {d.Message}")),
                ["diagnostics"] = diagnostics.Select(d => new Dictionary<string, object?>
                {
                    // +1: THE SERVER'S 0-BASED POSITION BECOMES THE 1-BASED ONE THE TOOL SCHEMA
                    // PROMISES — see csharp-lsp.plugin.json's own description. Every position this
                    // plugin hands back to the model crosses this same conversion exactly once.
                    ["line"] = d.Line + 1,
                    ["character"] = d.Character + 1,
                    ["severity"] = d.Severity,
                    ["message"] = d.Message,
                }).ToList(),
            },
        };
    }

    /// <summary>Resolves the file, opens it with the server, and converts the 1-based tool position
    /// to the server's 0-based one — the one place this conversion happens for the two position-taking
    /// tools, so lsp_definition and lsp_references cannot drift apart on it.</summary>
    private (string Path, LspPosition Position) OpenAndResolvePosition(JobParameters call)
    {
        var path = ResolvePath(call.Get<string>("file"));
        _client!.EnsureOpen(path);

        var line = call.Get<int>("line");
        var character = call.Get<int>("character");
        return (path, new LspPosition(line - 1, character - 1));
    }

    private string ResolvePath(string file) =>
        Path.IsPathRooted(file) ? file : Path.Combine(_workingDirectory, file);

    /// <summary>
    /// A tool result carrying BOTH a <c>content</c> string and the structured <c>locations</c>.
    ///
    /// <para><c>content</c> IS WHAT THE MODEL ACTUALLY READS — <c>Agent</c> renders a tool result by
    /// taking <c>Output["content"]</c> and nothing else, the convention every built-in follows
    /// (FileJobExecutor writes it for read, grep and glob alike). A result carrying only structured
    /// keys reaches the model as an EMPTY STRING: it does not see "no locations", it sees nothing at
    /// all, and answers by inventing a reason the lookup failed. Observed live — the model reported
    /// the language server was not indexed while the server was running and answering correctly.</para>
    ///
    /// <para>The structured key stays beside it: it costs nothing, it is what a non-model consumer
    /// of JobResult would want, and dropping it to satisfy the renderer would throw away the typed
    /// answer to keep the printed one.</para>
    /// </summary>
    private JobResult LocationsResult(IReadOnlyList<LspLocation> locations)
    {
        var rendered = locations
            .Select(l => $"{UriToPath(l.UriOrPath)}:{l.Start.Line + 1}:{l.Start.Character + 1}")
            .ToList();

        return new JobResult
        {
            Success = true,
            Output =
            {
                // NAMED AS A MISS RATHER THAN LEFT BLANK. "No definition found" is an answer the
                // model can act on; an empty string is one it has to guess about.
                ["content"] = rendered.Count == 0
                    ? "No definition found at that position."
                    : string.Join("\n", rendered),
                ["locations"] = locations.Select(l => new Dictionary<string, object?>
                {
                    ["file"] = UriToPath(l.UriOrPath),
                    ["line"] = l.Start.Line + 1,
                    ["character"] = l.Start.Character + 1,
                }).ToList(),
            },
        };
    }

    private static string UriToPath(string uri) =>
        uri.StartsWith("file://", StringComparison.Ordinal) ? new Uri(uri).LocalPath : uri;

    public async Task Stop(CancellationToken ct)
    {
        if (_client is null) return;

        await _client.ShutdownAsync(ct).ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
    }
}
