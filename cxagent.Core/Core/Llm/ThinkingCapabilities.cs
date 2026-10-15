using System.Text.Json;

namespace CxAgent.Core.Llm;

/// <summary>
/// Asking a model what thinking it accepts. Every answer comes from the endpoint; nothing here
/// lists model names, because a list in code is wrong on the day a model ships.
/// </summary>
public static class ThinkingCapabilities
{
    /// <summary>Bounded like the window probe: an absent endpoint costs at most this, once.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static readonly string[] EffortOrder = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The offered levels from Anthropic's <c>GET /v1/models/{id}</c>, or null when the response
    /// carries no capabilities (unknown is not "none": the request then sends nothing).
    /// <c>off</c> leads only where <c>thinking.types.disabled</c> is accepted — Opus 5.5 and Sonnet
    /// 5.5 refuse it with a 400, so offering it there would offer a broken request.
    /// </summary>
    /// <param name="json">The response body.</param>
    public static ThinkingOptions? ParseAnthropic(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("capabilities", out var caps)
                || caps.ValueKind != JsonValueKind.Object) return null;

            static bool Supported(JsonElement parent, string name) =>
                parent.ValueKind == JsonValueKind.Object
                && parent.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.Object
                && c.TryGetProperty("supported", out var s) && s.ValueKind == JsonValueKind.True;

            var types = caps.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.Object
                && th.TryGetProperty("types", out var ty) ? ty : default;
            if (!Supported(types, "adaptive"))
                return ThinkingOptions.None("this model does not accept adaptive thinking");

            var effort = caps.TryGetProperty("effort", out var ef) ? ef : default;
            var levels = new List<string>();
            if (Supported(types, "disabled")) levels.Add(ThinkingLevels.Off);
            levels.AddRange(EffortOrder.Where(l => Supported(effort, l)));
            return levels.Count > 0 ? new ThinkingOptions(levels)
                : ThinkingOptions.None("this model offers no thinking levels");
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether a llama.cpp server's chat template takes <c>enable_thinking</c> — the one per-request
    /// switch it honours (measured: effort and budget fields are ignored per request).
    /// </summary>
    /// <param name="json">The <c>/props</c> response body.</param>
    public static ThinkingOptions? ParseLlamaProps(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chat_template", out var t) || t.ValueKind != JsonValueKind.String)
                return null;
            return t.GetString()!.Contains("enable_thinking", StringComparison.Ordinal)
                ? new ThinkingOptions([ThinkingLevels.Off, ThinkingLevels.On])
                : ThinkingOptions.None("this server's chat template has no thinking switch");
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// One probe answer, shared by a provider and every clone made by <c>WithThinking</c>.
///
/// <para>A DEFINITIVE ANSWER IS KEPT, EVEN "NONE". The endpoint replied — an error status, or a
/// body without capabilities — so asking again gets the same reply, and a gateway with no
/// <c>/models</c> route would otherwise pay an extra request on every turn. Only NO answer at all
/// (the probe threw or timed out) is asked again: that is one unreachable moment, and caching it
/// would make it a session with no levels.</para>
/// </summary>
public sealed class CapabilityCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ThinkingOptions? _known;

    /// <summary>The cached answer, or null while none has been given.</summary>
    public ThinkingOptions? Known => Volatile.Read(ref _known);

    /// <summary>The cached answer, probing first if there is none.</summary>
    /// <param name="probe">Asks the endpoint; null means no answer (failure or timeout), which is not cached.</param>
    /// <param name="ct">Cancels the wait and the probe.</param>
    public async Task<ThinkingOptions> GetAsync(
        Func<CancellationToken, Task<ThinkingOptions?>> probe, CancellationToken ct)
    {
        if (Known is { } k) return k;
        await _gate.WaitAsync(ct);
        try
        {
            if (Known is { } again) return again;
            ThinkingOptions? answer;
            try { answer = await probe(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { answer = null; }
            if (answer is not null) Volatile.Write(ref _known, answer);
            return answer ?? ThinkingOptions.None("the endpoint did not say what it accepts");
        }
        finally { _gate.Release(); }
    }
}
