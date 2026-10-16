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

    /// <summary>
    /// A refusal that says nothing about the model: a server still loading it (llama.cpp answers 503
    /// until its weights are in), a rate limit, an overloaded API. Asked again rather than kept —
    /// kept, it would leave the session without levels until a restart. A 404 is different: the
    /// route does not exist, and asking again would only ask again.
    /// </summary>
    internal static bool IsTemporary(System.Net.HttpStatusCode status) =>
        (int)status is 408 or 429 or >= 500;

    private static readonly string[] EffortOrder = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>OpenRouter's effort names in offering order; it also knows "minimal".</summary>
    private static readonly string[] OpenRouterEffortOrder = ["minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// What OpenRouter's <c>GET /api/v1/models</c> says <paramref name="model"/> takes. A
    /// <c>reasoning</c> object marks a model that reasons: its <c>supported_efforts</c> are the
    /// levels, <c>off</c> leads unless the model is <c>mandatory</c>, and with no efforts listed a
    /// model that may stop reasoning offers <c>off</c> and <c>on</c>. Null when the body is not the list.
    /// </summary>
    public static ThinkingOptions? ParseOpenRouterModels(string json, string? model)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var m in data.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var id) || id.GetString() != model) continue;
                if (!m.TryGetProperty("reasoning", out var r) || r.ValueKind != JsonValueKind.Object)
                    return ThinkingOptions.None("this model does not reason");

                var mandatory = r.TryGetProperty("mandatory", out var mand) && mand.ValueKind == JsonValueKind.True;
                var efforts = r.TryGetProperty("supported_efforts", out var se) && se.ValueKind == JsonValueKind.Array
                    ? se.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                    : [];
                var levels = new List<string>();
                if (!mandatory) levels.Add(ThinkingLevels.Off);
                if (efforts.Count > 0) levels.AddRange(OpenRouterEffortOrder.Where(efforts.Contains));
                else if (!mandatory) levels.Add(ThinkingLevels.On);
                return levels.Count > 0 ? new ThinkingOptions(levels)
                    : ThinkingOptions.None("this model always reasons, at a level OpenRouter does not let you choose");
            }
            return ThinkingOptions.None("OpenRouter does not list this model");
        }
        catch (JsonException) { return null; }
    }

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
///
/// <para>A TIMEOUT WAITS BEFORE IT IS ASKED AGAIN. A request with a level waits on this answer, so a
/// gateway that never replies on its probe route would add the whole probe timeout to every request.
/// For <see cref="AfterATimeout"/> the answer is "not said" at once — the request goes without level
/// fields, as it would after the timeout anyway. A quick failure (refused, 503 while a model loads)
/// costs nothing to repeat and is asked again every time.</para>
/// </summary>
/// <param name="clock">The time; the system clock unless a test sets one.</param>
public sealed class CapabilityCache(TimeProvider? clock = null)
{
    /// <summary>How long a timed-out probe is left before it is asked again.</summary>
    public static readonly TimeSpan AfterATimeout = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ThinkingOptions? _known;
    private DateTimeOffset _askAgainAt = DateTimeOffset.MinValue;

    /// <summary>The cached answer, or null while none has been given.</summary>
    public ThinkingOptions? Known => Volatile.Read(ref _known);

    /// <summary>The cached answer, probing first if there is none.</summary>
    /// <param name="probe">Asks the endpoint; null means no answer (failure or timeout), which is not cached.</param>
    /// <param name="ct">Cancels the wait and the probe.</param>
    public async Task<ThinkingOptions> GetAsync(
        Func<CancellationToken, Task<ThinkingOptions?>> probe, CancellationToken ct)
    {
        if (Known is { } k) return k;

        // OFF THE CALLER'S CONTEXT throughout. A probe begun on a UI thread would otherwise resume
        // there while holding the gate, and any wait on that thread for the gate would never end.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Known is { } again) return again;
            if (_clock.GetUtcNow() < _askAgainAt) return Unsaid;
            ThinkingOptions? answer;
            try { answer = await probe(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                // The probe's own bound fired, not the caller's: the endpoint hung.
                _askAgainAt = _clock.GetUtcNow() + AfterATimeout;
                answer = null;
            }
            catch (Exception) { answer = null; }
            if (answer is not null) Volatile.Write(ref _known, answer);
            return answer ?? Unsaid;
        }
        finally { _gate.Release(); }
    }

    private static readonly ThinkingOptions Unsaid = ThinkingOptions.None("the endpoint did not say what it accepts");
}
