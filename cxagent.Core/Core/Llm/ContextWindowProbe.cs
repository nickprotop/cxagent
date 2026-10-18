using System.Net.Http.Headers;
using System.Text.Json;

namespace CxAgent.Core.Llm;

/// <summary>
/// Where to ask for a context window, what to ask about, and what to authenticate with.
///
/// <para>NAMED, NOT FOUR STRINGS. Kind, base URL, model and key are all <c>string?</c>, so passed
/// loose a transposed pair compiles and probes the wrong thing — a key sent as a model id, or a
/// URL.</para>
/// </summary>
/// <param name="Kind">The instance's <c>kind</c>; <c>anthropic</c> is asked its Models API,
/// every other kind takes the llama.cpp path.</param>
/// <param name="BaseUrl">The configured base URL, or null for the kind's default.</param>
/// <param name="Model">The model id to ask about.</param>
/// <param name="ApiKey">The instance's key, or null.</param>
internal readonly record struct ProbeTarget(string? Kind, string? BaseUrl, string? Model, string? ApiKey);

/// <summary>
/// Asks an OpenAI-compatible endpoint how large the served context window actually is.
///
/// <para>WHY ASK RATHER THAN BE TOLD. The window was configuration only — an optional
/// <c>contextWindow</c> field nobody sets — and everything downstream degrades quietly without it:
/// the compression trigger falls back to a fixed 40,000 tokens, and the session panel prints a bare
/// token count with no percentage because it has nothing to divide by. Measured against a local
/// server serving 212,992, that fallback fires at 19% used — discarding context the model holds
/// comfortably. A wrong number is not a safer default than no number; it is a different bug.</para>
///
/// <para>N_CTX, NOT N_CTX_TRAIN. llama.cpp reports both: what it is SERVING and what the model was
/// trained at (212,992 and 262,144 on the machine this was written against). The served figure is
/// the ceiling requests are actually rejected at — sizing against the trained one would put the
/// trigger past the real limit, which is the failure this exists to prevent.</para>
///
/// <para>NON-STANDARD, and treated as such. <c>meta.n_ctx</c> is llama.cpp's extension; OpenAI's own
/// API returns nothing of the kind. Every failure — no server, a hosted endpoint, an unparseable
/// shape — returns null and lets the caller fall back to config. Startup must not depend on it.</para>
/// </summary>
internal static class ContextWindowProbe
{
    /// <summary>Bounded so a slow or absent endpoint delays startup by at most this.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private const string AnthropicDefaultBaseUrl = "https://api.anthropic.com/v1";

    /// <summary>
    /// The context window for the instance <paramref name="target"/> describes, or null when it
    /// cannot be determined.
    ///
    /// <para>ANTHROPIC SAYS SO OUTRIGHT. Its Models API returns <c>max_input_tokens</c> for a model
    /// id, so an <c>anthropic</c> instance is asked there — with its own auth header, and against
    /// the default URL when none is configured, which is the normal case. The llama.cpp path needs a
    /// baseUrl and would return at once, leaving a 1M-token model compacted at the 40,000-token
    /// fallback. Every other kind takes the llama.cpp path.</para>
    /// </summary>
    public static Task<int?> TryGetAsync(ProbeTarget target, CancellationToken ct = default) =>
        string.Equals(target.Kind, "anthropic", StringComparison.Ordinal)
            ? TryGetAnthropicAsync(target, ct)
            : TryGetAsync(target.BaseUrl, target.Model, target.ApiKey, ct);

    private static async Task<int?> TryGetAnthropicAsync(ProbeTarget target, CancellationToken ct)
    {
        var (_, baseUrl, model, apiKey) = target;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var root = (string.IsNullOrWhiteSpace(baseUrl) ? AnthropicDefaultBaseUrl : baseUrl).TrimEnd('/');
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{root}/models/{Uri.EscapeDataString(model)}");
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

            using var resp = await http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseAnthropicContextWindow(await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (Exception)
        {
            // Same contract as the llama.cpp path: unknown, never a startup failure.
            return null;
        }
    }

    /// <summary>Pulls <c>max_input_tokens</c> out of an Anthropic <c>/v1/models/{id}</c> response.</summary>
    public static int? ParseAnthropicContextWindow(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("max_input_tokens", out var m)
                   && m.ValueKind == JsonValueKind.Number
                   && m.TryGetInt32(out var window) && window > 0
                ? window
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The served context window for <paramref name="model"/>, or null when it cannot be determined.
    /// </summary>
    /// <param name="baseUrl">The provider's base URL, e.g. <c>http://localhost:8771/v1</c>.</param>
    /// <param name="model">The model id to match; the first entry is used when it is not found.</param>
    /// <param name="apiKey">Sent as a bearer token when present.</param>
    /// <param name="ct">Cancels the probe; it is bounded at three seconds regardless.</param>
    public static async Task<int?> TryGetAsync(
        string? baseUrl, string? model, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var json = await http.GetStringAsync($"{baseUrl.TrimEnd('/')}/models", cts.Token);
            return ParseContextWindow(json, model);
        }
        catch (Exception)
        {
            // Any failure is "we do not know" — the caller has config and a default to fall back on,
            // and a probe that can break startup is worse than one that returns nothing.
            return null;
        }
    }

    /// <summary>
    /// Pulls <c>data[].meta.n_ctx</c> out of a <c>/v1/models</c> response.
    /// </summary>
    /// <remarks>
    /// Separated from the HTTP call so the parsing — the part with edge cases — is testable against
    /// captured payloads without a server.
    /// </remarks>
    public static int? ParseContextWindow(string json, string? model)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return null;

            // PREFER THE NAMED MODEL. An endpoint may serve several, and their windows differ — using
            // the first would report one model's ceiling while requests go to another.
            int? firstSeen = null;
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("meta", out var meta)
                    || meta.ValueKind != JsonValueKind.Object) continue;
                if (!meta.TryGetProperty("n_ctx", out var ctx)
                    || ctx.ValueKind != JsonValueKind.Number
                    || !ctx.TryGetInt32(out var window)
                    || window <= 0) continue;

                var id = entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(model)
                    && string.Equals(id, model, StringComparison.OrdinalIgnoreCase))
                    return window;

                firstSeen ??= window;
            }

            // A single-model server that names its model differently than the config does is the
            // common local case, so one unambiguous entry is better than nothing.
            return firstSeen;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
