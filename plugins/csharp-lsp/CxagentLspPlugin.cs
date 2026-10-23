using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Lsp;

/// <summary>
/// One managed plugin, one language server, three tools. The plugin IS the executor its tools share,
/// holding the one LspClient connection all three dispatch through.
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
public sealed class CxagentLspPlugin : IPlugin, IPluginGateSource
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

        // THE PLUGIN'S HALF OF THE CONTRACT CHECK, and the only half that can catch this case. A
        // host that KNOWS contract 2 refuses anything else before this method is ever called — but a
        // host OLDER than 2 has never heard of it, reads this plugin's sidecar with its own rules,
        // and takes `"gated": "dynamic"` for whatever its boolean parser falls back to. That is
        // false: these three tools would offer themselves UNGATED, and a read outside the working
        // directory would happen with no prompt, on a host with nothing wrong with it except age.
        //
        // Throwing is the whole mechanism: IPlugin.Load's contract is that a throw fails the load
        // and names the reason, which is exactly the outcome wanted here.
        if (context.HostContract < RequiredContract)
            throw new NotSupportedException(
                $"csharp-lsp needs plugin contract {RequiredContract}; this cxagent speaks "
              + $"{context.HostContract}. Its tools decide per call whether to ask permission, and a "
              + "host that does not understand that would run them without asking at all.");

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
        var (path, at) = OpenAndResolvePosition(call, toolName);
        var locations = await _client!.DefinitionAsync(path, new LspPosition(at.Line, at.Character), ct).ConfigureAwait(false);
        return LocationsResult(locations, at.Note, Miss("definition", call, at));
    }

    private async Task<JobResult> HandleReferencesAsync(JobParameters call, string toolName, CancellationToken ct)
    {
        var (path, at) = OpenAndResolvePosition(call, toolName);
        var locations = await _client!.ReferencesAsync(path, new LspPosition(at.Line, at.Character), ct).ConfigureAwait(false);
        return LocationsResult(locations, at.Note, Miss("references", call, at));
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
                // THE TEXT ALONE — see LocationsResult's own doc.
                ["content"] = diagnostics.Count == 0
                    ? "No diagnostics for that file."
                    : string.Join("\n", diagnostics.Select(d =>
                        $"{d.Line + 1}:{d.Character + 1} {d.Severity}: {d.Message}")),
            },
        };
    }

    /// <summary>Resolves the file, opens it with the server, and converts the 1-based tool position
    /// to the server's 0-based one — the one place this conversion happens for the two position-taking
    /// tools, so csharp_definition and csharp_references cannot drift apart on it.</summary>
    private (string Path, ResolvedPosition At) OpenAndResolvePosition(JobParameters call, string toolName)
    {
        var path = ResolvePath(call.Get<string>("file"), toolName);

        // FOUND BEFORE THE SERVER IS ASKED: a position that cannot be resolved is refused with the reason, rather
        // than sent and answered with an empty result the model reads as "nothing here".
        var character = call.Get("character", 0);
        var at = SymbolPosition.Resolve(File.ReadAllLines(path), call.Get<int>("line"),
            character > 0 ? character : null, call.Get<string?>("symbol", null));
        if (at.Problem is { } problem) throw new ToolRefusal($"{toolName}: {problem}");

        _client!.EnsureOpen(path);
        return (path, at);
    }

    /// <summary>What an empty answer says — named for the tool that gave it, and for the symbol when one was named.</summary>
    private static string Miss(string what, JobParameters call, ResolvedPosition at) =>
        call.Get<string?>("symbol", null) is { Length: > 0 } symbol
            ? $"No {what} found for '{symbol}' at line {at.Line + 1}."
            : $"No {what} found at line {at.Line + 1}, character {at.Character + 1}.";

    /// <summary>What these tools answer for. Anything else is refused — see <see cref="ResolvePath"/>.</summary>
    /// <summary>
    /// Whether an already-normalised path is inside the working directory.
    ///
    /// <para>SYMLINKS ARE NOT RESOLVED, deliberately. <see cref="Path.GetFullPath"/> flattens ".."
    /// but does not follow links, so a symlink INSIDE the tree pointing outside it reads as
    /// contained here. Following links would need every segment resolved and would still race a
    /// link swapped between the check and the read; this gate is a prompt about where the model
    /// SAID to look, not a sandbox, and pretending otherwise would be the more dangerous claim.</para>
    /// </summary>
    private bool IsInsideWorkspace(string fullPath)
    {
        var root = Path.GetFullPath(_workingDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        // A CASE-SENSITIVE COMPARE ON UNIX, insensitive on Windows: a path differing only in case is
        // the same file there and a different one here, and getting this backwards would either
        // prompt for files already inside the tree or fail to prompt for files outside it.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullPath.StartsWith(root, comparison);
    }

    /// <summary>
    /// Asks before reading a file outside the working directory, and says nothing about one inside.
    ///
    /// <para>THIS IS WHY THE TOOLS ARE "dynamic" RATHER THAN GATED. Every call here is a read, and
    /// a prompt on each one would make the plugin unusable — a language server is consulted dozens
    /// of times per turn. What deserves a question is not the READING, it is the reading of
    /// something the user did not open this session to work on.</para>
    ///
    /// <para>A MALFORMED CALL ASKS RATHER THAN REFUSING HERE. An argument that is missing or not a
    /// path cannot be shown to be inside the workspace, and Invoke's own refusal will produce the
    /// better message a moment later — but only if the call is permitted to get that far.</para>
    /// </summary>
    public PluginGate? Gate(string toolName, JobParameters call)
    {
        var file = call.Get("file", "");
        if (string.IsNullOrWhiteSpace(file)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(_workingDirectory, file));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return IsInsideWorkspace(full)
            ? null
            : new PluginGate($"read '{full}', which is outside {_workingDirectory}");
    }

    /// <summary>
    /// The contract this plugin needs, matching <c>pluginContract</c> in its own sidecar. Stated as
    /// a constant rather than read back from the parsed manifest because the check runs BEFORE the
    /// sidecar is read — a host too old to be trusted with the manifest is too old to have its
    /// reading of that manifest believed.
    /// </summary>
    private const int RequiredContract = 2;

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
        // NORMALISED, because Path.Combine does not resolve ".." — it leaves the segments in the
        // string, so "../../elsewhere/Secrets.cs" stays literal and any containment test on the raw
        // value would be comparing a path that has not been walked yet.
        var path = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(_workingDirectory, file));

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
    /// A tool result as the model reads it: a note when the position was moved, then the locations — or the miss,
    /// named for the tool that had it.
    ///
    /// <para>EVERYTHING IN <c>content</c>, NOTHING BESIDE IT. The host renders every key of a result, so a
    /// structured copy of the same locations would reach the model twice; a miss is named rather than left blank,
    /// because an empty string reads as a broken tool and the model invents a reason for it.</para>
    /// </summary>
    private JobResult LocationsResult(IReadOnlyList<LspLocation> locations, string? note, string miss)
    {
        var rendered = locations
            .Select(l => $"{UriToPath(l.UriOrPath)}:{l.Start.Line + 1}:{l.Start.Character + 1}")
            .ToList();
        var answer = rendered.Count == 0 ? miss : string.Join("\n", rendered);

        return new JobResult
        {
            Success = true,
            Output = { ["content"] = note is null ? answer : note + "\n" + answer },
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
