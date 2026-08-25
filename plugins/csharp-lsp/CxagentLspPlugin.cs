using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Lsp;

/// <summary>
/// One managed plugin, one language server, three tools — see the plugin design, "What a plugin is": the
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
    /// <summary>The language server used when settings name none — see
    /// <see cref="ReadServerSettings"/>.</summary>
    private const string DefaultServer = "csharp-ls";

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

        // SAY WHICH SERVER, WHEN NOBODY CHOSE IT. A session quietly driving a server the user did
        // not name is the confusing case: the tools work or fail for reasons that live in a
        // default they never saw.
        if (ReferenceEquals(server, DefaultServer))
            _context.Logger.Log($"csharp-lsp: no 'server' in settings, using '{DefaultServer}'.");

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
            // A DEFAULT, SO THE PLUGIN WORKS WITH NO SETTINGS AT ALL. `/plugin load` carries no
            // settings — there is nowhere for them to come from when config does not name the
            // plugin — so a plugin that REQUIRED one could only ever be tried by editing config
            // first. csharp-ls is the default because it is pure LSP over stdio and needs no flags:
            // OmniSharp speaks its own protocol unless given -lsp, so defaulting to it would fail
            // in a way that looks like the plugin being broken.
            //
            // NOT SILENT. A default that differs from what the user meant is worth one line in the
            // transcript, and the log reaches it — a session that mysteriously drives the wrong
            // server is worse than one that says which it picked.
            return (DefaultServer, []);
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
        if (toolName is not ("csharp_definition" or "csharp_references" or "csharp_diagnostics"))
            throw new InvalidOperationException($"csharp-lsp has no tool named '{toolName}'.");

        // THE FILE IS CHECKED BEFORE THE SERVER IS. Whether this tool serves a .go file does not
        // depend on a server being up, and answering "not running" to a call that would be refused
        // anyway sends the caller to fix the wrong thing.
        try
        {
            ResolveFileArgument(call, toolName);
        }
        catch (ToolRefusal refusal)
        {
            return new JobResult { Success = false, ErrorMessage = refusal.Message };
        }
        catch (KeyNotFoundException)
        {
            // A MISSING ARGUMENT IS A FAILED CALL, NOT A CRASH. The schema marks file required, so
            // reaching here means the model omitted it — and it can fix that if told, where an
            // unhandled exception just reports that the tool broke.
            return new JobResult
            {
                Success = false,
                ErrorMessage = $"{toolName} needs a 'file' argument — the path to a C# or Razor file.",
            };
        }

        if (_client is null)
            return new JobResult { Success = false, ErrorMessage = "language server is not running." };

        // A CALL'S OWN TOKEN GOES TO THE REQUEST, NOT THE SERVER'S LIFETIME. The server outlives
        // every individual call (IPluginContext.Lifetime's own doc), but one slow lookup should be
        // cancellable without tearing down the connection every other call still needs.
        try
        {
            return toolName switch
            {
                "csharp_definition" => await HandleDefinitionAsync(call, toolName, ct).ConfigureAwait(false),
                "csharp_references" => await HandleReferencesAsync(call, toolName, ct).ConfigureAwait(false),
                _ => HandleDiagnostics(call, toolName),
            };
        }
        catch (ToolRefusal refusal)
        {
            // A REFUSAL IS A FAILED CALL, NOT A CRASH. The model is told what this tool serves and
            // what to do instead, which is the difference between "wrong tool for this file" and
            // "this capability is broken" — and it cannot tell those apart from an empty result.
            return new JobResult { Success = false, ErrorMessage = refusal.Message };
        }
        catch (LspErrorException ex)
        {
            return new JobResult { Success = false, ErrorMessage = $"language server error: {ex.Message}" };
        }
    }

    private async Task<JobResult> HandleDefinitionAsync(JobParameters call, string toolName, CancellationToken ct)
    {
        var (path, position) = OpenAndResolvePosition(call, toolName);
        var locations = await _client!.DefinitionAsync(path, position, ct).ConfigureAwait(false);
        return LocationsResult(locations);
    }

    private async Task<JobResult> HandleReferencesAsync(JobParameters call, string toolName, CancellationToken ct)
    {
        var (path, position) = OpenAndResolvePosition(call, toolName);
        var locations = await _client!.ReferencesAsync(path, position, ct).ConfigureAwait(false);
        return LocationsResult(locations);
    }

    private JobResult HandleDiagnostics(JobParameters call, string toolName)
    {
        var path = ResolvePath(call.Get<string>("file"), toolName);
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
    /// tools, so csharp_definition and csharp_references cannot drift apart on it.</summary>
    private (string Path, LspPosition Position) OpenAndResolvePosition(JobParameters call, string toolName)
    {
        var path = ResolvePath(call.Get<string>("file"), toolName);
        _client!.EnsureOpen(path);

        var line = call.Get<int>("line");
        var character = call.Get<int>("character");
        return (path, new LspPosition(line - 1, character - 1));
    }

    /// <summary>What these tools answer for. Anything else is refused — see <see cref="ResolvePath"/>.</summary>
    private static readonly string[] ServedExtensions = [".cs", ".csx", ".razor", ".cshtml"];

    /// <summary>
    /// The absolute path for a tool's <c>file</c> argument, or a refusal.
    ///
    /// <para>REFUSING BEATS ANSWERING EMPTILY. A language server handed a Go file returns no
    /// locations, and an empty result reads to the model as "nothing found here" — so it explains
    /// the silence rather than trying a tool that could answer. Naming the extension turns that into
    /// something it can act on.</para>
    ///
    /// <para>THE MISSING-FILE CASE IS CHECKED HERE TOO, because the alternative is an exception out
    /// of <c>File.ReadAllText</c> inside the client, which surfaces as a crashed tool rather than a
    /// failed call.</para>
    /// </summary>
    /// <summary>The <c>file</c> argument resolved and checked — see <see cref="ResolvePath"/>. Used
    /// by <see cref="Invoke"/> to refuse before the server is consulted, and by each handler to get
    /// the path it works with.</summary>
    private string ResolveFileArgument(JobParameters call, string toolName) =>
        ResolvePath(call.Get<string>("file"), toolName);

    private string ResolvePath(string file, string toolName)
    {
        var path = Path.IsPathRooted(file) ? file : Path.Combine(_workingDirectory, file);

        var extension = Path.GetExtension(path);
        if (!ServedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ToolRefusal(
                $"{toolName} works on C# and Razor files ({string.Join(", ", ServedExtensions)}). "
              + $"'{file}' is not one of those — use a different tool for this file, or read it directly.");

        if (!File.Exists(path))
            throw new ToolRefusal(
                $"no file at '{path}' — a relative path is resolved against the working directory.");

        return path;
    }

    /// <summary>A refusal the caller turns into a failed <see cref="JobResult"/> — see
    /// <see cref="Invoke"/>. An exception rather than a return value because
    /// <see cref="ResolvePath"/> is called from three places that each want the same handling.</summary>
    private sealed class ToolRefusal(string message) : Exception(message);

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
