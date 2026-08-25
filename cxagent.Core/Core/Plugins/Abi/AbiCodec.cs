using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins.Abi;

/// <summary>
/// What parsing one piece of the wire format produced — a value, or a reason it failed. Every
/// parse in this codec returns one of these rather than throwing: a native plugin's misbehaviour
/// (malformed JSON, a version mismatch, a missing required field) is DATA the host reports, never
/// an exception the host must catch at an `extern "C"` frontier that cannot safely propagate one
/// in the first place — see Abi/README.md, "no exception may cross this boundary."
/// </summary>
public readonly struct AbiParseResult<T>
{
    private readonly T? _value;

    public bool IsSuccess { get; }
    public string? Error { get; }
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException(
        $"AbiParseResult was a failure ({Error}); Value is not readable.");

    private AbiParseResult(bool isSuccess, T? value, string? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public static AbiParseResult<T> Success(T value) => new(true, value, null);
    public static AbiParseResult<T> Failure(string error) => new(false, default, error);
}

/// <summary>
/// Parses and writes the JSON that crosses the ABI boundary, and translates it to and from the
/// SAME types <see cref="ManagedPluginLoader"/> already produces — <see cref="PluginManifest"/> and
/// <see cref="JobResult"/> — so <see cref="PluginRegistry"/> holds an identical shape regardless of
/// which loader (managed or ABI) produced it. Nothing downstream of a load can tell which loader
/// ran; that is the whole point of writing one contract for both (the plugin design, "The v1 cut").
///
/// <para>NEVER USES <c>JsonSerializer.Deserialize&lt;T&gt;</c> DIRECTLY ON HOST INPUT WITHOUT A
/// TRY/CATCH — every entry point here is a boundary a malicious or merely broken native plugin can
/// hand malformed bytes across, and a JsonException escaping this class would be exactly the kind
/// of exception-crossing-a-process-boundary failure the ABI design forbids in the other direction.
/// Every method returns a result, never throws for bad input.</para>
/// </summary>
public static class AbiCodec
{
    private static readonly JsonSerializerOptions WriteOptions = new() { PropertyNamingPolicy = null };

    /// <summary>
    /// Bounds how much of a malformed payload an error message quotes — see Abi/README.md, "Every
    /// failure mode": "fails, quoting a bounded prefix of what was returned." An unbounded quote
    /// would let a misbehaving plugin blow up a log file with the exact bytes that broke it.
    /// </summary>
    private const int MalformedPreviewLength = 200;

    // ---- version handshake ----

    /// <summary>
    /// Checks a native library's reported ABI version against <see cref="PluginContract.Version"/>
    /// with EXACT equality — see cxagent_plugin.h, "ABI HANDSHAKE". A host meeting a version it does
    /// not understand refuses cleanly rather than guessing at an unfamiliar shape.
    /// </summary>
    public static AbiParseResult<int> CheckVersion(int reportedVersion)
    {
        return reportedVersion == PluginContract.Version
            ? AbiParseResult<int>.Success(reportedVersion)
            : AbiParseResult<int>.Failure(
                $"plugin reports ABI version {reportedVersion}, this host understands version "
                + $"{PluginContract.Version} only — refusing the load rather than guessing at "
                + "an unfamiliar shape.");
    }

    // ---- describe ----

    /// <summary>
    /// Parses <c>cxagent_plugin_describe</c>'s JSON into an <see cref="AbiManifest"/>, checking the
    /// in-body <c>abiVersion</c> against <see cref="PluginContract.Version"/> as well as the
    /// handshake function — see Abi/README.md, "describe": the two are deliberately redundant so a
    /// mismatch between them is caught as a manifest error rather than silently trusting whichever
    /// the host happened to read first.
    /// </summary>
    public static AbiParseResult<AbiManifest> ParseManifest(string json)
    {
        AbiManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AbiManifest>(json);
        }
        catch (JsonException ex)
        {
            return AbiParseResult<AbiManifest>.Failure(
                $"plugin_describe returned invalid JSON: {ex.Message} (payload: {Preview(json)})");
        }

        if (manifest is null)
            return AbiParseResult<AbiManifest>.Failure(
                "plugin_describe returned JSON null, not a manifest object.");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            return AbiParseResult<AbiManifest>.Failure("manifest is missing required field 'name'.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            return AbiParseResult<AbiManifest>.Failure("manifest is missing required field 'version'.");

        var versionCheck = CheckVersion(manifest.AbiVersion);
        if (!versionCheck.IsSuccess)
            return AbiParseResult<AbiManifest>.Failure(versionCheck.Error!);

        return AbiParseResult<AbiManifest>.Success(manifest);
    }

    /// <summary>
    /// Translates a validated <see cref="AbiManifest"/> into the SAME <see cref="PluginManifest"/>
    /// <see cref="ManagedPluginLoader"/> produces — see this class's own doc for why one shape must
    /// serve both loaders. <see cref="AbiManifest.AbiVersion"/> is deliberately dropped here: it is
    /// wire-format bookkeeping already spent by <see cref="ParseManifest"/>, and <see cref="PluginManifest"/>
    /// has no field for it because a managed plugin has no ABI version to report.
    /// </summary>
    public static PluginManifest ToPluginManifest(AbiManifest abi) => new(
        abi.Name, abi.Version, abi.Instructions, abi.Spawns,
        abi.Tools.Select(t => new PluginToolManifest(t.Name, t.Description, t.InputSchema,
            PluginGatingJson.Parse(t.Gated, t.Name, out _), t.AlwaysAskable ?? true)).ToList())
    {
        // CARRIED, NOT DROPPED. describe's own contract number is what the SIDECAR is compared
        // against downstream; leaving it null here would make every ABI plugin look like one whose
        // code declared nothing.
        Contract = abi.AbiVersion,
    };

    // ---- start context (host -> plugin) ----

    /// <summary>Writes the JSON <c>cxagent_plugin_start</c> receives — see Abi/README.md, "context".</summary>
    public static string WriteContext(string workingDirectory, JsonElement settings) =>
        JsonSerializer.Serialize(
            new AbiPluginContext(workingDirectory, settings, PluginContract.Version), WriteOptions);

    // ---- invoke call (host -> plugin) ----

    /// <summary>
    /// Writes the JSON <c>cxagent_plugin_invoke</c> receives — see Abi/README.md, "call".
    /// <paramref name="arguments"/> is never written as JSON null: an argument-less tool gets
    /// <c>{}</c>, so a plugin may parse the object unconditionally.
    /// </summary>
    public static string WriteInvokeCall(string toolName, JsonElement arguments)
    {
        var args = arguments.ValueKind == JsonValueKind.Undefined
            ? JsonDocument.Parse("{}").RootElement
            : arguments;
        return JsonSerializer.Serialize(new AbiInvokeCall(toolName, args), WriteOptions);
    }

    /// <summary>Converts a <see cref="JobParameters"/>'s values (an executor's own arguments) into the
    /// JSON object <see cref="WriteInvokeCall(string, JsonElement)"/> sends across — the inverse of
    /// how the managed side already reads <see cref="JobParameters"/> from a <see cref="JsonElement"/>
    /// after a round trip.</summary>
    public static string WriteInvokeCall(string toolName, JobParameters call)
    {
        var argsJson = JsonSerializer.Serialize(call.Values, WriteOptions);
        using var doc = JsonDocument.Parse(argsJson);
        return WriteInvokeCall(toolName, doc.RootElement.Clone());
    }

    // ---- result envelope (plugin -> host) ----

    /// <summary>
    /// Parses the JSON <c>start</c>/<c>invoke</c>/<c>stop</c> return into an
    /// <see cref="AbiResultEnvelope"/> — see Abi/README.md, "Every failure mode, and what the host
    /// does" for the exhaustive table this method implements. Every row of that table returns a
    /// failure result here rather than throwing; the caller (9b's host process) decides what a
    /// failure means for the tool call in flight.
    /// </summary>
    public static AbiParseResult<AbiResultEnvelope> ParseEnvelope(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return AbiParseResult<AbiResultEnvelope>.Failure(
                "plugin returned a NULL or empty result — a plugin must always return an envelope.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return AbiParseResult<AbiResultEnvelope>.Failure(
                $"plugin returned invalid JSON: {ex.Message} (payload: {Preview(json)})");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return AbiParseResult<AbiResultEnvelope>.Failure(
                    $"plugin's result envelope must be a JSON object, got {root.ValueKind} "
                    + $"(payload: {Preview(json)}).");

            if (!root.TryGetProperty("ok", out var okEl) ||
                okEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return AbiParseResult<AbiResultEnvelope>.Failure(
                    $"plugin's result envelope is missing required boolean field 'ok' "
                    + $"(payload: {Preview(json)}).");

            var ok = okEl.GetBoolean();

            if (!ok)
            {
                var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : null;
                return AbiParseResult<AbiResultEnvelope>.Success(
                    new AbiResultEnvelope(false, null, string.IsNullOrEmpty(error)
                        ? "plugin call failed (no error message given)."
                        : error));
            }

            AbiJobResult? result = null;
            if (root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    result = resultEl.Deserialize<AbiJobResult>();
                }
                catch (JsonException ex)
                {
                    return AbiParseResult<AbiResultEnvelope>.Failure(
                        $"plugin's result envelope has a malformed 'result': {ex.Message}");
                }
            }

            return AbiParseResult<AbiResultEnvelope>.Success(new AbiResultEnvelope(true, result, null));
        }
    }

    /// <summary>
    /// Confirms an envelope parsed from an <c>invoke</c> reply actually carries the
    /// <see cref="Models.JobResult"/> that call promises, and translates it — see Abi/README.md,
    /// "Every failure mode": "ok:true from invoke (missing result) — fails: invoke promises a
    /// JobResult and did not send one."
    /// </summary>
    public static AbiParseResult<JobResult> ToInvokeResult(AbiResultEnvelope envelope)
    {
        if (!envelope.Ok)
            return AbiParseResult<JobResult>.Failure(envelope.Error ?? "plugin call failed.");

        if (envelope.Result is null)
            return AbiParseResult<JobResult>.Failure(
                "plugin's invoke reply was ok:true but carried no 'result' — invoke always returns a JobResult.");

        var r = envelope.Result;
        var output = r.Output.ValueKind == JsonValueKind.Object
            ? r.Output.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value)
            : new Dictionary<string, object?>();

        return AbiParseResult<JobResult>.Success(new JobResult
        {
            Success = r.Success,
            ExitCode = r.ExitCode,
            ErrorMessage = r.ErrorMessage,
            PermissionDenied = r.PermissionDenied,
            DecidedBy = r.DecidedBy,
            Output = output,
            LogFile = r.LogFile,
            Duration = TimeSpan.FromMilliseconds(r.DurationMs),
        });
    }

    /// <summary>
    /// Confirms an envelope parsed from a <c>start</c>/<c>stop</c> reply is a bare success/failure
    /// with no <see cref="AbiResultEnvelope.Result"/> to read — those two calls answer
    /// <c>Task</c>, not <c>Task&lt;JobResult&gt;</c>, on the managed side.
    /// </summary>
    public static AbiParseResult<bool> ToVoidResult(AbiResultEnvelope envelope) => envelope.Ok
        ? AbiParseResult<bool>.Success(true)
        : AbiParseResult<bool>.Failure(envelope.Error ?? "plugin call failed.");

    private static string Preview(string json) =>
        json.Length <= MalformedPreviewLength ? json : json[..MalformedPreviewLength] + "…";
}
