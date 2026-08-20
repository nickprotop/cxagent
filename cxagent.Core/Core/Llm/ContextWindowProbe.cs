using System.Net.Http.Headers;
using System.Text.Json;

namespace CxAgent.Core.Llm;

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
public static class ContextWindowProbe
{
    /// <summary>Bounded so a slow or absent endpoint delays startup by at most this.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

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
