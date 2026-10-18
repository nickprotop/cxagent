using System.Text.Json;

namespace CxAgent.Core.Llm.Providers;

/// <summary>
/// Local Ollama endpoint. A genuine preset of OpenAiCompatibleProvider (Ollama serves the OpenAI
/// wire at /v1): localhost baseUrl default, keyless. Named so it appears as a first-class "local"
/// entry without the user hand-configuring baseUrl. This reuses the real driver — it is not a facade.
/// </summary>
internal sealed class OllamaProvider : OpenAiCompatibleProvider
{
    private readonly HttpClient _client;
    private readonly string _tagsUrl;

    public OllamaProvider(string providerId, string displayName, string model,
        string? baseUrl = null, HttpClient? client = null, RetryPolicy? retryPolicy = null)
        : base(new OpenAiProviderOptions
        {
            ProviderId = providerId,
            DisplayName = displayName,
            Model = model,
            BaseUrl = baseUrl ?? "http://localhost:11434/v1",
            Client = client,
            Retry = retryPolicy,

            // NO KEY AND NO CACHE CONTROL. Ollama is local: it needs no credential, and filling its
            // own memory costs nothing, so a breakpoint would be a field it ignores.
        })
    {
        var effectiveBaseUrl = (baseUrl ?? "http://localhost:11434/v1").TrimEnd('/');
        // Ollama's native API (tags, pull, etc.) lives outside the OpenAI-compatible /v1 prefix.
        if (effectiveBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            effectiveBaseUrl = effectiveBaseUrl[..^"/v1".Length];
        _tagsUrl = $"{effectiveBaseUrl}/api/tags";
        _client = client ?? Shared;
    }

    private OllamaProvider(OllamaProvider other, string? level) : base(other, level)
    {
        _client = other._client;
        _tagsUrl = other._tagsUrl;
    }

    /// <inheritdoc/>
    public override ILlmProvider WithThinking(string? level) => new OllamaProvider(this, level);

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        [.. (await ListModelDetailsAsync(ct)).Models.Select(m => m.Id)];

    /// <summary>Ollama's own <c>/api/tags</c>, which names the models and says nothing about their windows.</summary>
    public override async Task<ModelCatalogResult> ListModelDetailsAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetAsync(_tagsUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return new ModelCatalogResult([], $"the endpoint answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return new ModelCatalogResult([]);
            return new ModelCatalogResult([.. models.EnumerateArray()
                .Select(e => e.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => new ModelInfo(s!))]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ModelCatalogResult([], ex.Message); }
    }
}
