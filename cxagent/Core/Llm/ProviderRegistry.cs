using CxAgent.Core.Llm.Providers;

namespace CxAgent.Core.Llm;

/// <summary>
/// Config-driven map: instance name -> constructed ILlmProvider. Built once from ProviderSettings;
/// the app-default (orchestrator) provider is resolved via Default. The wizard (P5) and llm_agent
/// (P3b) consume this. NOTE: distinct from the P3 PluginRegistry (jobs), despite the similar name.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _providers;
    private readonly string? _defaultName;

    private ProviderRegistry(Dictionary<string, ILlmProvider> providers, string? defaultName)
    {
        _providers = providers;
        _defaultName = defaultName;
    }

    public IReadOnlyCollection<string> InstanceNames => _providers.Keys;

    /// <summary>Each configured instance with the model it currently serves, for the model_hint
    /// param's description. Lived on RoleResolver until roles were removed; it was never about
    /// roles, only about which instances exist.</summary>
    public IReadOnlyDictionary<string, string> InstanceModels =>
        _providers.ToDictionary(kv => kv.Key, kv => kv.Value.ModelId, StringComparer.Ordinal);

    public bool TryGet(string instanceName, out ILlmProvider provider)
        => _providers.TryGetValue(instanceName, out provider!);

    /// <summary>
    /// Non-throwing counterpart to <see cref="Default"/>, for callers that must degrade rather than
    /// fail when no default is configured. <c>defaultProvider</c> is optional in config.json (a file
    /// with providers but no such key validates cleanly), so "no default" is a reachable state, not
    /// a programming error.
    /// </summary>
    public bool TryGetDefault(out ILlmProvider provider)
    {
        if (_defaultName is not null) return _providers.TryGetValue(_defaultName, out provider!);
        provider = null!;
        return false;
    }

    public ILlmProvider Default =>
        _defaultName is not null && _providers.TryGetValue(_defaultName, out var p)
            ? p
            : throw new InvalidOperationException(
                "No default provider configured. Set 'defaultProvider' in config.json to a configured instance.");

    /// <summary>
    /// A registry over ALREADY-CONSTRUCTED providers, bypassing the config-driven factory. Exists
    /// because <see cref="Build"/> can only produce the concrete vendor drivers named in
    /// <see cref="ProviderInstanceConfig.Kind"/> — so there is otherwise no way to put a
    /// <see cref="MockLlmProvider"/> (or any test double) behind a <see cref="RoleResolver"/>, and the
    /// dispatch path could only ever be tested against a real HTTP driver.
    /// <paramref name="defaultName"/> may be null or name a missing instance, matching Build's
    /// tolerance of a config with no <c>defaultProvider</c>.
    /// </summary>
    public static ProviderRegistry FromProviders(
        IReadOnlyDictionary<string, ILlmProvider> providers, string? defaultName) =>
        new(new Dictionary<string, ILlmProvider>(providers), defaultName);

    public static ProviderRegistry Build(ProviderSettings settings, HttpClient? client = null)
    {
        var built = new Dictionary<string, ILlmProvider>();
        foreach (var (name, cfg) in settings.Providers)
            built[name] = Construct(name, cfg, client);
        return new ProviderRegistry(built, settings.DefaultProvider);
    }

    private static ILlmProvider Construct(string name, ProviderInstanceConfig cfg, HttpClient? client)
    {
        var display = $"{cfg.Kind} {cfg.Model}";
        return cfg.Kind switch
        {
            "anthropic" => new AnthropicProvider(
                name, display, cfg.Model, cfg.ApiKey ?? "", client: client),
            "openai-compatible" => new OpenAiCompatibleProvider(
                name, display, cfg.Model, cfg.BaseUrl ?? "", cfg.ApiKey, cfg.ExtraHeaders, client: client),
            "ollama" => new OllamaProvider(
                name, display, cfg.Model, cfg.BaseUrl, client: client),
            _ => throw new InvalidOperationException(
                $"provider '{name}': unknown kind '{cfg.Kind}' reached the factory (should have failed validation).")
        };
    }
}
