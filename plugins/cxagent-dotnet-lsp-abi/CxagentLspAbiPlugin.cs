using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CxAgent.Plugins.LspAbi;

// ---- the JSON shapes crossing cxagent_plugin.h's boundary --------------------------------------
//
// REIMPLEMENTED HERE, NOT REFERENCED FROM CxAgent.Core.Plugins.Abi.AbiCodec/AbiContract — a
// NativeAOT-published library resolves everything it needs at ILC compile time, and CxAgent.Core is
// a managed assembly built for the HOST process, never loaded into this one (see
// cxagent-dotnet-lsp-abi.csproj's own comment on why there is no ProjectReference to it at all).
// Field names below are kept byte-for-byte identical to AbiContract.cs's records — this file is the
// PROOF that the JSON shape, not a shared C# type, is the actual contract cxagent_plugin.h promises;
// see PLUGINS.md, "The boundary is JSON."

public sealed record AbiToolManifestWire(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema,
    [property: JsonPropertyName("gated")] bool Gated);

public sealed record AbiManifestWire(
    [property: JsonPropertyName("abiVersion")] int AbiVersion,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("spawns")] bool Spawns,
    [property: JsonPropertyName("tools")] IReadOnlyList<AbiToolManifestWire> Tools);

public sealed record AbiPluginContextWire(
    [property: JsonPropertyName("workingDirectory")] string WorkingDirectory,
    [property: JsonPropertyName("settings")] JsonElement Settings);

/// <summary>
/// <see cref="Output"/> IS A <see cref="JsonNode"/>, WHERE AbiCodec.cs's managed AbiJobResult HOLDS
/// A <see cref="JsonElement"/> BACKED BY <c>Dictionary&lt;string, object?&gt;</c> ONE LEVEL UP
/// (<c>JobResult.Output</c>). That is not a stylistic choice — it is forced the same way the named
/// records in LspProtocolJson.cs are: <c>JobResult.Output</c> is deliberately open
/// (<c>Dictionary&lt;string, object?&gt;</c>, PLUGINS.md/Abi/README.md, "the result envelope" — "the
/// same escape hatch JobParameters/JobResult already use managed-side"), and a source-generated
/// <see cref="JsonSerializerContext"/> cannot describe a polymorphic <c>object?</c> value ahead of
/// time — attempting to serialize <c>Dictionary&lt;string, object?&gt;</c> under NativeAOT throws
/// <c>NotSupportedException: JsonTypeInfo metadata ... was not provided</c> the first time a value
/// in it is anything but a primitive the resolver happens to also know about (a nested
/// <c>List&lt;Dictionary&lt;string, object?&gt;&gt;</c>, exactly what <c>LocationsResult</c> below
/// builds, reproduces this). <see cref="JsonNode"/> sidesteps it because <c>System.Text.Json</c>
/// ships a built-in, non-reflective converter for the DOM types (<see cref="JsonObject"/>/
/// <see cref="JsonArray"/>/<see cref="JsonValue"/>) themselves — a plugin author on this side of the
/// boundary builds output as a JSON tree explicitly rather than handing the serializer an untyped
/// bag and trusting reflection to figure it out, which is exactly the trust NativeAOT withdraws.
/// </summary>
public sealed record AbiJobResultWire(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("permissionDenied")] bool PermissionDenied,
    [property: JsonPropertyName("decidedBy")] string? DecidedBy,
    [property: JsonPropertyName("output")] JsonNode? Output,
    [property: JsonPropertyName("logFile")] string? LogFile,
    [property: JsonPropertyName("durationMs")] long DurationMs);

public sealed record AbiResultEnvelopeWire(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] AbiJobResultWire? Result,
    [property: JsonPropertyName("error")] string? Error);

[JsonSerializable(typeof(AbiManifestWire))]
[JsonSerializable(typeof(AbiPluginContextWire))]
[JsonSerializable(typeof(AbiResultEnvelopeWire))]
internal partial class AbiWireJson : JsonSerializerContext
{
}

/// <summary>
/// The five <c>extern "C"</c> exports cxagent_plugin.h declares — the ABI counterpart to
/// CxagentLspPlugin.cs's <c>IPlugin</c> implementation. Everything this class does maps 1:1 onto
/// that file's own five members (Load/Start/Invoke/Stop, plus the manifest each one used to read
/// from Load alone), split across <c>describe</c>+<c>start</c> the way Abi/README.md's own "describe"
/// section explains: a native plugin has no working directory or settings until <c>start</c>, so its
/// manifest cannot depend on either.
///
/// <para>ONE PROCESS, ONE PLUGIN INSTANCE — matching cxagent.PluginHost's own "one host process per
/// plugin instance." State that would be instance fields on a managed <c>IPlugin</c> is static
/// fields here instead, because <c>[UnmanagedCallersOnly]</c> methods cannot be instance methods:
/// there is no managed object for the host process to hold a reference to across calls, only five
/// bare function pointers resolved by symbol name (cxagent.PluginHost/NativePlugin.cs). A second
/// plugin instance in the same OS process never happens — cxagent-plugin-host loads exactly one
/// library and exits when it is done — so static state here carries no more risk than an instance
/// field would.</para>
/// </summary>
public static class CxagentLspAbiPlugin
{
    private static string _workingDirectory = "";
    private static LspClient? _client;

    // ---- cxagent_plugin_abi_version ------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "cxagent_plugin_abi_version")]
    public static int AbiVersion() => 1;

    // ---- cxagent_plugin_describe ----------------------------------------------------------------

    /// <summary>
    /// Returns the manifest — see cxagent_plugin.h's own doc: "the same shape IPlugin.Load returns
    /// managed-side." UNLIKE CxagentLspPlugin.Load, this reads no sidecar file at runtime: a native
    /// plugin has no assembly-location concept to resolve one against (see cxagent_plugin.h,
    /// OWNERSHIP — everything crossing this boundary is JSON, not a file path), so the manifest
    /// content is baked into this method directly. <see cref="AbiPluginLoader"/>'s own mismatch check
    /// (PluginManifestMatch.Mismatch, run against cxagent-dotnet-lsp-abi.plugin.json) is what keeps
    /// this hardcoded copy honest — the same discipline the managed plugin's runtime sidecar read
    /// enforces by construction; this class enforces the same invariant by being kept in sync with
    /// the sidecar file next to it, which the test suite's own describe-matches-sidecar test proves.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "cxagent_plugin_describe")]
    public static nint Describe()
    {
        var manifest = new AbiManifestWire(
            AbiVersion: 1,
            Name: "cxagent-dotnet-lsp-abi",
            Version: "1.0.0",
            Instructions: "These tools talk to a running C# language server rooted at the session's "
                + "working directory, and answer only for C# source files. Positions are 1-based "
                + "(line 1 is the first line, character 1 is the first column of that line), matching "
                + "how a human reads a file — the plugin converts to the server's own convention "
                + "internally. lsp_definition and lsp_references need a file path (relative to the "
                + "working directory or absolute) plus a line and character landing inside the "
                + "symbol. lsp_diagnostics takes only a file path and reports whatever the server "
                + "currently has computed for it; call lsp_definition or lsp_references first if "
                + "diagnostics come back empty right after a file is opened, since the server needs "
                + "a moment to analyse it.",
            Spawns: true,
            Tools:
            [
                new AbiToolManifestWire("lsp_definition",
                    "Finds where the symbol at a file position is DECLARED. Crosses project "
                    + "boundaries: a reference in a test project resolves to a declaration in the "
                    + "project under test, if the server has indexed both.",
                    PositionToolSchema("reference"), false),
                new AbiToolManifestWire("lsp_references",
                    "Finds every place the symbol at a file position is USED, across every project "
                    + "the server has indexed.",
                    PositionToolSchema("symbol"), false),
                new AbiToolManifestWire("lsp_diagnostics",
                    "Reports the server's current errors and warnings for a file — whatever it has "
                    + "already computed, not a fresh compile on demand.",
                    FileOnlyToolSchema(), false),
            ]);

        return WriteResult(JsonSerializer.Serialize(manifest, AbiWireJson.Default.AbiManifestWire));
    }

    /// <summary>
    /// <paramref name="lineNoun"/> is the ONLY difference between lsp_definition's and
    /// lsp_references's schema — "reference" vs "symbol" in the line field's description, matching
    /// cxagent-dotnet-lsp-abi.plugin.json (and cxagent-dotnet-lsp.plugin.json before it) word for
    /// word. AbiPluginLoader.Load's own mismatch check (PluginManifestMatch.Mismatch) compares this
    /// method's output against that sidecar byte for byte, so a schema that quietly drifted from it
    /// — even in wording no tool call actually depends on — fails the load rather than the plugin
    /// silently describing itself as something the sidecar the user approved does not say.
    /// </summary>
    private static JsonElement PositionToolSchema(string lineNoun) => JsonDocument.Parse($$"""
        {
          "type": "object",
          "properties": {
            "file": { "type": "string", "description": "Path to the file, relative to the working directory or absolute." },
            "line": { "type": "integer", "description": "1-based line number of the {{lineNoun}}." },
            "character": { "type": "integer", "description": "1-based column, landing inside the symbol's name." }
          },
          "required": ["file", "line", "character"]
        }
        """).RootElement.Clone();

    private static JsonElement FileOnlyToolSchema() => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "file": { "type": "string", "description": "Path to the file, relative to the working directory or absolute." }
          },
          "required": ["file"]
        }
        """).RootElement.Clone();

    // ---- cxagent_plugin_start -------------------------------------------------------------------

    /// <summary>
    /// See CxagentLspPlugin.Start's own doc for the settings-reading contract this mirrors exactly:
    /// <c>settings.server</c> required, <c>settings.args</c> optional. THE ONE STRUCTURAL DIFFERENCE
    /// FROM THE MANAGED PLUGIN: the spawned language server's pid has nowhere to register — see
    /// "THE UNCLOSED GAP" below. Everything else (reading server/args verbatim rather than branching
    /// on server identity, starting the client, remembering the working directory for path
    /// resolution in Invoke) is identical.
    ///
    /// <para>THE UNCLOSED GAP: cxagent_plugin.h's own README (Abi/README.md, "context") states
    /// <c>RegisterChildProcess</c> "is called the same way, but as an OUTBOUND message rather than an
    /// inbound context field" — a promise this rewrite could not keep. HostProtocol.cs's wire
    /// vocabulary is strictly request/reply: <c>AbiHostProcess.Send</c> writes one request line then
    /// reads exactly one reply line, assuming the very next line on the host's stdout answers this
    /// call. There is no line shape for a message the PLUGIN originates unprompted, and adding one
    /// means the host's stdout would carry two kinds of line a reader has to tell apart while a
    /// request is still in flight — a real protocol change (a notification frame, and a client loop
    /// that reads it without blocking a pending reply), not a fix scoped to this plugin. So the
    /// language server's pid, unlike the host process's own pid (registered by
    /// <c>AbiPluginLoader.Load</c>, which the host process CAN reach directly, itself being a
    /// managed-side reference), is unregistered: a crash before <c>Stop</c> runs leaks it. This is
    /// reported here rather than silently worked around, per the task's own instruction to report a
    /// leak loudly rather than absorb it quietly.</para>
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "cxagent_plugin_start")]
    public static nint Start(nint contextJsonPtr)
    {
        try
        {
            var contextJson = ReadUtf8(contextJsonPtr);
            var context = JsonSerializer.Deserialize(contextJson, AbiWireJson.Default.AbiPluginContextWire)
                ?? throw new InvalidOperationException("start received no context.");

            _workingDirectory = context.WorkingDirectory;
            var (server, args) = ReadServerSettings(context.Settings);

            var (client, _processId) = LspClient.StartAsync(server, args, _workingDirectory, CancellationToken.None)
                .GetAwaiter().GetResult();
            _client = client;

            // _processId IS DELIBERATELY UNUSED PAST THIS POINT — see this method's own doc, "THE
            // UNCLOSED GAP." Recorded here as a named discard rather than `_`, so this line reads as
            // a decision rather than an oversight.

            return WriteOkEnvelope();
        }
        catch (Exception ex)
        {
            // NO EXCEPTION MAY CROSS cxagent_plugin.h's BOUNDARY — every catch site in this class
            // exists for that one reason, mirroring cxagent.Tests/AbiFixtures/fixture_plugin.c's own
            // FIXTURE_CRASH/FIXTURE_MALFORMED discipline on the C side: a plugin author's job is to
            // turn a failure into envelope data, never let it unwind past extern "C".
            return WriteFailEnvelope($"cxagent-dotnet-lsp-abi failed to start: {ex.Message}");
        }
    }

    private static (string Server, IReadOnlyList<string> Args) ReadServerSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("server", out var serverEl) ||
            serverEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "cxagent-dotnet-lsp-abi requires a 'server' string in its settings — the language server command to run.");
        }

        var server = serverEl.GetString()!;
        var args = new List<string>();
        if (settings.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
            foreach (var a in argsEl.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String)
                    args.Add(a.GetString()!);

        return (server, args);
    }

    // ---- cxagent_plugin_invoke ------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "cxagent_plugin_invoke")]
    public static nint Invoke(nint toolNamePtr, nint callJsonPtr)
    {
        try
        {
            var toolName = ReadUtf8(toolNamePtr);
            var callJson = ReadUtf8(callJsonPtr);

            // AN UNKNOWN NAME IS CHECKED BEFORE THE "IS THE SERVER RUNNING" CHECK BELOW — see
            // CxagentLspPlugin.Invoke's identical ordering and its own reasoning: toolName is always
            // one this plugin's own manifest declared, so reaching an unrecognised one here is this
            // plugin's bug regardless of Start/Stop state. Unlike the managed contract (which throws
            // for this case — IPlugin.Invoke's own doc), the ABI has no exception channel to throw
            // into; it reports the same "this is a bug" distinction as ok:false instead, which
            // AbiCodec.ToInvokeResult already surfaces as a call-level failure rather than a
            // JobResult.Success:false the caller could mistake for an ordinary tool failure.
            if (toolName is not ("lsp_definition" or "lsp_references" or "lsp_diagnostics"))
                return WriteFailEnvelope($"cxagent-dotnet-lsp-abi has no tool named '{toolName}'.");

            if (_client is null)
                return WriteInvokeResult(false, "language server is not running.", null);

            // call_json IS THE ARGUMENTS OBJECT DIRECTLY, never wrapped — cxagent_plugin.h's own
            // signature: "cxagent_plugin_invoke(tool_name, call_json)" with call_json documented as
            // "the tool's arguments object." The {"toolName":...,"arguments":{...}} envelope was the
            // shape the HOST<->cxagent-plugin-host wire used one level up (HostProtocol.cs);
            // Program.cs's own RunInvokeCall unwraps it before calling this export, so nothing here
            // ever sees that outer wrapper. JsonDocument.Parse(...).RootElement, NOT
            // JsonSerializer.Deserialize&lt;JsonElement&gt;(...) — the latter routes through the
            // generic serializer and warns IL2026/IL3050 even though JsonElement itself needs no
            // type metadata; JsonDocument's own parser is the metadata-free path to the same value.
            var arguments = JsonDocument.Parse(callJson).RootElement;

            return toolName switch
            {
                "lsp_definition" => HandleDefinition(arguments),
                "lsp_references" => HandleReferences(arguments),
                _ => HandleDiagnostics(arguments),
            };
        }
        catch (LspErrorException ex)
        {
            return WriteInvokeResult(false, $"language server error: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return WriteFailEnvelope($"cxagent-dotnet-lsp-abi invoke failed: {ex.Message}");
        }
    }

    private static nint HandleDefinition(JsonElement call)
    {
        var (path, position) = OpenAndResolvePosition(call);
        var locations = _client!.DefinitionAsync(path, position, CancellationToken.None).GetAwaiter().GetResult();
        return LocationsResult(locations);
    }

    private static nint HandleReferences(JsonElement call)
    {
        var (path, position) = OpenAndResolvePosition(call);
        var locations = _client!.ReferencesAsync(path, position, CancellationToken.None).GetAwaiter().GetResult();
        return LocationsResult(locations);
    }

    private static nint HandleDiagnostics(JsonElement call)
    {
        var path = ResolvePath(call.GetProperty("file").GetString()!);
        _client!.EnsureOpen(path);
        var diagnostics = _client.Diagnostics(path);

        // +1: THE SERVER'S 0-BASED POSITION BECOMES THE 1-BASED ONE THE TOOL SCHEMA PROMISES — see
        // cxagent-dotnet-lsp-abi.plugin.json's own description, and CxagentLspPlugin's identical
        // conversion. Every position this plugin hands back to the model crosses this same
        // conversion exactly once, on either side of the boundary. BUILT AS A JsonArray/JsonObject
        // TREE, NOT A Dictionary<string, object?> — see AbiJobResultWire's own doc for why.
        //
        // new JsonArray(JsonNode?[]), NOT repeated .Add(JsonObject) — JsonArray.Add<T> is a generic
        // method flagged IL2026/IL3050 regardless of T, even for a T that is itself already a
        // JsonNode and needs no reflection at runtime (verified by hand: it runs correctly under
        // NativeAOT despite the warning). The array constructor overload taking JsonNode?[] carries
        // no such attribute, so building the array in one call rather than incrementally is what
        // keeps the warning honest rather than suppressed.
        var items = diagnostics.Select(d => (JsonNode?)new JsonObject
        {
            ["line"] = d.Line + 1,
            ["character"] = d.Character + 1,
            ["severity"] = d.Severity,
            ["message"] = d.Message,
        }).ToArray();

        var output = new JsonObject { ["diagnostics"] = new JsonArray(items) };
        return WriteInvokeResult(true, null, output);
    }

    private static (string Path, LspPosition Position) OpenAndResolvePosition(JsonElement call)
    {
        var path = ResolvePath(call.GetProperty("file").GetString()!);
        _client!.EnsureOpen(path);

        var line = call.GetProperty("line").GetInt32();
        var character = call.GetProperty("character").GetInt32();
        return (path, new LspPosition(line - 1, character - 1));
    }

    private static string ResolvePath(string file) =>
        Path.IsPathRooted(file) ? file : Path.Combine(_workingDirectory, file);

    private static nint LocationsResult(IReadOnlyList<LspLocation> locations)
    {
        var items = locations.Select(l => (JsonNode?)new JsonObject
        {
            ["file"] = UriToPath(l.UriOrPath),
            ["line"] = l.Start.Line + 1,
            ["character"] = l.Start.Character + 1,
        }).ToArray();

        var output = new JsonObject { ["locations"] = new JsonArray(items) };
        return WriteInvokeResult(true, null, output);
    }

    private static string UriToPath(string uri) =>
        uri.StartsWith("file://", StringComparison.Ordinal) ? new Uri(uri).LocalPath : uri;

    // ---- cxagent_plugin_stop --------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "cxagent_plugin_stop")]
    public static nint Stop()
    {
        try
        {
            if (_client is not null)
            {
                _client.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _client = null;
            }
            return WriteOkEnvelope();
        }
        catch (Exception ex)
        {
            return WriteFailEnvelope($"cxagent-dotnet-lsp-abi failed to stop: {ex.Message}");
        }
    }

    // ---- cxagent_plugin_free --------------------------------------------------------------------

    /// <summary>
    /// Releases a string this library returned — see cxagent_plugin.h, OWNERSHIP: "the side that
    /// allocated a string frees it, using its own allocator." Every string this class hands back was
    /// allocated by <see cref="WriteResult"/> via <see cref="Marshal.StringToCoTaskMemUTF8"/>, so
    /// <see cref="Marshal.FreeCoTaskMem"/> is the matching release — the managed-runtime equivalent
    /// of the fixture's own <c>free(ptr)</c> on a <c>malloc</c>'d buffer.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "cxagent_plugin_free")]
    public static void Free(nint ptr)
    {
        if (ptr != nint.Zero) Marshal.FreeCoTaskMem(ptr);
    }

    // ---- envelope helpers ------------------------------------------------------------------------

    private static nint WriteOkEnvelope() =>
        WriteResult(JsonSerializer.Serialize(new AbiResultEnvelopeWire(true, null, null), AbiWireJson.Default.AbiResultEnvelopeWire));

    private static nint WriteFailEnvelope(string error) =>
        WriteResult(JsonSerializer.Serialize(new AbiResultEnvelopeWire(false, null, error), AbiWireJson.Default.AbiResultEnvelopeWire));

    private static nint WriteInvokeResult(bool success, string? errorMessage, JsonNode? output)
    {
        var result = new AbiJobResultWire(success, 0, errorMessage, false, null, output, null, 0);
        var envelope = new AbiResultEnvelopeWire(true, result, null);
        return WriteResult(JsonSerializer.Serialize(envelope, AbiWireJson.Default.AbiResultEnvelopeWire));
    }

    private static nint WriteResult(string json) => Marshal.StringToCoTaskMemUTF8(json);

    private static string ReadUtf8(nint ptr) => Marshal.PtrToStringUTF8(ptr) ?? "";
}
