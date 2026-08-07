namespace CxAgent.Core.Llm;

/// <summary>Static, user-facing metadata for one provider kind. Drives the wizard's kind list.</summary>
public record ProviderKindInfo(
    string Kind,
    string DisplayName,
    bool RequiresApiKey,
    bool RequiresBaseUrl,
    string ModelHint);

/// <summary>
/// The provider kinds the wizard can configure. Kept in lockstep with
/// ProviderConfigLoader.KnownKinds (a test asserts the two sets are equal) so adding a driver
/// surfaces it in the wizard automatically rather than silently omitting it.
/// </summary>
public static class ProviderKindCatalog
{
    public static IReadOnlyList<ProviderKindInfo> All { get; } = new[]
    {
        new ProviderKindInfo("anthropic", "Anthropic (Claude)",
            RequiresApiKey: true,  RequiresBaseUrl: false, ModelHint: "claude-sonnet-4-5"),
        // RequiresApiKey: true is NOT arbitrary — it mirrors ProviderConfigLoader's validation rule
        // (ProviderConfig.cs): an apiKey is required for every KnownKind except those in KeylessKinds,
        // and KeylessKinds is {"ollama"} only. Setting this false would make the wizard skip the key
        // prompt and then write a config the loader rejects at next startup.
        new ProviderKindInfo("openai-compatible", "OpenAI-compatible endpoint",
            RequiresApiKey: true,  RequiresBaseUrl: true,  ModelHint: "gpt-4o-mini"),
        new ProviderKindInfo("ollama", "Ollama (local)",
            RequiresApiKey: false, RequiresBaseUrl: true,  ModelHint: "llama3.1"),
    };

    public static ProviderKindInfo For(string kind) =>
        All.FirstOrDefault(k => k.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown provider kind");

    /// <summary>
    /// A pre-filled starting point for a provider instance. A preset is NOT a kind — it selects an
    /// existing kind and pre-fills baseUrl/headers/model hint. ProviderKindCatalog.All must stay
    /// set-equal to ProviderConfigLoader.KnownKinds (a test asserts it), so adding an aggregator here
    /// must never add a kind.
    /// </summary>
    public record ProviderPreset(
        string Id,
        string DisplayName,
        string Kind,
        string? BaseUrl,
        string ModelHint,
        IReadOnlyDictionary<string, string>? ExtraHeaders);

    public static IReadOnlyList<ProviderPreset> Presets { get; } = new[]
    {
        new ProviderPreset("anthropic", "Anthropic (Claude)", "anthropic",
            null, "claude-sonnet-4-5", null),
        new ProviderPreset("openrouter", "OpenRouter (aggregator)", "openai-compatible",
            "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4-5",
            // OpenRouter's attribution headers. Task 1 made these survive a write; before that fix
            // they were parsed and silently dropped on save.
            new Dictionary<string, string> { ["X-Title"] = "cxagent" }),
        new ProviderPreset("openai-compatible", "OpenAI-compatible endpoint", "openai-compatible",
            null, "gpt-4o-mini", null),
        new ProviderPreset("ollama", "Ollama (local)", "ollama",
            "http://localhost:11434", "llama3.1", null),
    };

    public static ProviderPreset PresetFor(string id) =>
        Presets.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "unknown provider preset");
}
