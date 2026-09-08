using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxAgent.Core.Plugins.Abi;

/// <summary>
/// The JSON <c>cxagent_plugin_describe</c> returns — see Abi/README.md, "describe". Field-for-field
/// <see cref="PluginManifest"/> plus the redundant <see cref="AbiVersion"/> the wire format alone
/// needs (Abi/README.md explains why the version is checked twice).
/// </summary>
public sealed record AbiManifest(
    // "pluginContract", THE SAME NAME A MANAGED SIDECAR USES. One contract covers both loaders, so
    // it is spelled once; a second name for the same number is a second thing to keep in step.
    [property: JsonPropertyName("pluginContract")] int AbiVersion,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("spawns")] bool Spawns,
    [property: JsonPropertyName("tools")] IReadOnlyList<AbiToolManifest> Tools,
    // ABSENT MEANS FALSE — a library built before contract 3 (or one that just never asks) has no
    // "client" key in its describe() JSON at all, and PluginManifest.Client's own default is false
    // for exactly that reason: silence reads as "did not ask," never as "host too old to tell."
    [property: JsonPropertyName("client")] bool Client = false,
    // ABSENT MEANS NONE — the same shape of addition as Client, above: a library built before
    // commands existed has no "commands" key in its describe() JSON at all, and
    // PluginManifest.DeclaredCommands' own default (null, read back as an empty list through
    // PluginManifest.Commands) already means exactly that for a managed plugin's manifest.
    [property: JsonPropertyName("commands")] IReadOnlyList<AbiCommandManifest>? Commands = null);

/// <summary>One tool entry inside <see cref="AbiManifest"/> — mirrors <see cref="PluginToolManifest"/>.</summary>
public sealed record AbiToolManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema,
    // A JsonElement, NOT A BOOL, because "gated" is three-state: true, false, or "dynamic".
    // Parsed rather than typed so an ABI plugin and a managed sidecar are held to exactly the same
    // rule — including refusing an unknown string by name instead of falling back to "never ask".
    [property: JsonPropertyName("gated")] JsonElement Gated,
    [property: JsonPropertyName("alwaysAskable")] bool? AlwaysAskable = null);

/// <summary>One command entry inside <see cref="AbiManifest"/> — mirrors <see cref="PluginCommandManifest"/>.</summary>
public sealed record AbiCommandManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("arguments")] IReadOnlyList<AbiCommandArgument>? Arguments = null);

/// <summary>One argument entry inside <see cref="AbiCommandManifest"/> — mirrors <see cref="PluginCommandArgument"/>.</summary>
public sealed record AbiCommandArgument(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// The JSON <c>cxagent_plugin_start</c> receives — see Abi/README.md, "context". Deliberately
/// smaller than <see cref="IPluginContext"/>: no transcript, no logger channel, no lifetime token,
/// no child-process registration inbound field — see the README for where each of those instead
/// lives (host-owned stderr, host-side abandonment, an outbound message).
/// </summary>
public sealed record AbiPluginContext(
    [property: JsonPropertyName("workingDirectory")] string WorkingDirectory,
    [property: JsonPropertyName("settings")] JsonElement Settings,
    // THE HOST'S OWN CONTRACT, so a native plugin can refuse a host too old to refuse it first —
    // see IPluginContext.HostContract. Absent means a host predating the field, which is exactly
    // the case worth refusing, so a reader treats a missing value as 0 rather than as "current".
    [property: JsonPropertyName("hostContract")] int HostContract = 0);

/// <summary>
/// The JSON <c>cxagent_plugin_invoke</c> receives — see Abi/README.md, "call". <see cref="Arguments"/>
/// is always a JSON object, never null and never omitted; an argument-less tool receives <c>{}</c>.
/// </summary>
public sealed record AbiInvokeCall(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("arguments")] JsonElement Arguments);

/// <summary>
/// The JSON <c>cxagent_plugin_command</c> returns — see Abi/README.md, "command". Field-for-field
/// <see cref="CommandResult"/>, with <see cref="Status"/> spelled as the lowercase wire form of
/// <see cref="PluginCommandOutcome"/> rather than the enum's own name, matching how <c>gated</c>
/// already crosses the wire as a string a native author writes by hand rather than a numeric enum
/// value they would have to look up.
/// </summary>
public sealed record AbiCommandResult(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// The JSON result of one call to a <see cref="Models.JobResult"/>-shaped operation — the payload
/// carried inside <see cref="AbiResultEnvelope.Result"/> on a successful <c>invoke</c>. Field-for-field
/// <see cref="Models.JobResult"/>, with <see cref="Models.JobResult.Duration"/> carried as
/// <see cref="DurationMs"/> — see Abi/README.md, "the result envelope", for why milliseconds.
/// </summary>
public sealed record AbiJobResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("permissionDenied")] bool PermissionDenied,
    [property: JsonPropertyName("decidedBy")] string? DecidedBy,
    [property: JsonPropertyName("output")] JsonElement Output,
    [property: JsonPropertyName("logFile")] string? LogFile,
    [property: JsonPropertyName("durationMs")] long DurationMs);

/// <summary>
/// The one envelope shape shared by <c>start</c>, <c>invoke</c>, and <c>stop</c> — see Abi/README.md,
/// "The result envelope": one shape for all three rather than three near-duplicates.
///
/// <para><see cref="Ok"/> AND <see cref="Result"/>.<see cref="AbiJobResult.Success"/> ARE NOT THE
/// SAME BIT. <see cref="Ok"/> false means the CALL ITSELF failed to produce a result at all — a
/// malformed argument, an ABI-level fault. <c>Ok:true, Result.Success:false</c> means the call
/// completed and the tool failed on its own terms, exactly the distinction
/// <see cref="IPlugin.Invoke"/>'s managed contract already draws by returning a
/// <see cref="Models.JobResult"/> with <c>Success:false</c> rather than throwing.</para>
/// </summary>
public sealed record AbiResultEnvelope(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] AbiJobResult? Result,
    [property: JsonPropertyName("error")] string? Error);
