using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxAgent.Core.Plugins.Abi;

/// <summary>
/// The ABI version this build understands — see <c>cxagent_plugin.h</c>, "ABI HANDSHAKE": checked
/// with EXACT EQUALITY against what a native library reports, never a floor. A future v2 host may
/// accept <c>{1, 2}</c> explicitly, with code that knows both shapes; nothing here does that yet.
/// </summary>
public static class AbiContract
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// The JSON <c>cxagent_plugin_describe</c> returns — see Abi/README.md, "describe". Field-for-field
/// <see cref="PluginManifest"/> plus the redundant <see cref="AbiVersion"/> the wire format alone
/// needs (Abi/README.md explains why the version is checked twice).
/// </summary>
public sealed record AbiManifest(
    [property: JsonPropertyName("abiVersion")] int AbiVersion,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("spawns")] bool Spawns,
    [property: JsonPropertyName("tools")] IReadOnlyList<AbiToolManifest> Tools);

/// <summary>One tool entry inside <see cref="AbiManifest"/> — mirrors <see cref="PluginToolManifest"/>.</summary>
public sealed record AbiToolManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema,
    [property: JsonPropertyName("gated")] bool Gated);

/// <summary>
/// The JSON <c>cxagent_plugin_start</c> receives — see Abi/README.md, "context". Deliberately
/// smaller than <see cref="IPluginContext"/>: no transcript, no logger channel, no lifetime token,
/// no child-process registration inbound field — see the README for where each of those instead
/// lives (host-owned stderr, host-side abandonment, an outbound message).
/// </summary>
public sealed record AbiPluginContext(
    [property: JsonPropertyName("workingDirectory")] string WorkingDirectory,
    [property: JsonPropertyName("settings")] JsonElement Settings);

/// <summary>
/// The JSON <c>cxagent_plugin_invoke</c> receives — see Abi/README.md, "call". <see cref="Arguments"/>
/// is always a JSON object, never null and never omitted; an argument-less tool receives <c>{}</c>.
/// </summary>
public sealed record AbiInvokeCall(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("arguments")] JsonElement Arguments);

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
