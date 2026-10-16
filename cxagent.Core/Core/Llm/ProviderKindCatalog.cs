namespace CxAgent.Core.Llm;

/// <summary>
/// The presets the Configuration dialog's gallery offers. Every kind the loader knows has one, and no
/// preset names a kind it does not (tests assert both), so adding a driver cannot leave it unreachable
/// from the dialog.
/// </summary>
public static class ProviderKindCatalog
{
    /// <summary>
    /// A pre-filled starting point for a provider instance. A preset is NOT a kind — it selects an
    /// existing kind and pre-fills baseUrl/headers/model hint, so adding an aggregator here must never
    /// add a kind.
    /// </summary>
    public record ProviderPreset(
        string Id,
        string DisplayName,
        string Kind,
        string? BaseUrl,
        string ModelHint,
        IReadOnlyDictionary<string, string>? ExtraHeaders,
        string Blurb = "");

    /// <summary>
    /// The cards a new provider starts from, in the order the gallery shows them: the hosted services
    /// people name first, then the local servers, then the general case. OpenAI and llama.cpp have cards
    /// of their own although both are openai-compatible, because cxagent treats each specially — OpenAI
    /// cannot be asked which models reason, and llama.cpp's thinking switch comes from its chat template —
    /// and a card is where that is said.
    /// </summary>
    public static IReadOnlyList<ProviderPreset> Presets { get; } = new[]
    {
        new ProviderPreset("anthropic", "Anthropic", "anthropic",
            null, "claude-sonnet-5-5", null, "Claude, direct"),
        new ProviderPreset("openrouter", "OpenRouter", "openai-compatible",
            "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-5-5",
            // OPENROUTER'S ATTRIBUTION HEADER, carried by the preset so an instance made from it sends it.
            new Dictionary<string, string> { ["X-Title"] = "cxagent" },
            "hundreds of models, one key"),
        new ProviderPreset("openai", "OpenAI", "openai-compatible",
            "https://api.openai.com/v1", "gpt-5", null, "GPT and the o-series"),
        new ProviderPreset("ollama", "Ollama", "ollama",
            "http://localhost:11434", "llama3.1", null, "local models"),
        new ProviderPreset("llamacpp", "llama.cpp", "openai-compatible",
            "http://localhost:8080/v1", "", null, "llama-server"),
        new ProviderPreset("openai-compatible", "OpenAI-compatible", "openai-compatible",
            null, "", null, "any /v1 endpoint"),
    };

    public static ProviderPreset PresetFor(string id) =>
        Presets.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "unknown provider preset");
}
