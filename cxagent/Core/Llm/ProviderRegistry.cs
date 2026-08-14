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

    /// <summary>Per-instance context windows, empty for a registry built without config (tests, the
    /// mock path) — where "unknown" is the honest answer anyway.</summary>
    private Dictionary<string, int?> _windows = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Each instance's configured context window, for the instances that declared one.
    ///
    /// <para>WHY THIS IS NOT ON <see cref="ILlmProvider"/>: it is a config-only number, and adding it
    /// to the interface would ripple into every vendor driver and test double for a value only the
    /// config lookup has (the argument is spelled out at AgentHost's own _contextWindow). So it lives
    /// here, symmetric with <see cref="InstanceModels"/>, which exists for the same kind of question.</para>
    ///
    /// <para>WHY IT IS NEEDED AT ALL: a sub-agent type may name a different instance, and a window
    /// belongs to the MODEL, not the session. A child given provider A with provider B's window sees
    /// IsUnderPressure as permanently false — AgentContext returns false for a MISSING window, never
    /// for a wrong one — so it never compacts and dies on a provider overflow instead. Silent, in the
    /// dangerous direction.</para>
    ///
    /// <para>ABSENT MEANS UNKNOWN, and unknown is legal: compaction falls back to a fixed threshold.
    /// A guessed window that is too large is worse than none.</para>
    /// </summary>
    public IReadOnlyDictionary<string, int?> InstanceWindows => _windows;

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
    /// <param name="windows">
    /// Per-instance context windows, when the caller knows them. Optional because the mock path and
    /// most tests do not — and "unknown" is the honest answer there, not a guess.
    /// </param>
    public static ProviderRegistry FromProviders(
        IReadOnlyDictionary<string, ILlmProvider> providers, string? defaultName,
        IReadOnlyDictionary<string, int?>? windows = null) =>
        new(new Dictionary<string, ILlmProvider>(providers), defaultName)
        {
            _windows = windows is null
                ? new Dictionary<string, int?>(StringComparer.Ordinal)
                : new Dictionary<string, int?>(windows, StringComparer.Ordinal),
        };

    public static ProviderRegistry Build(ProviderSettings settings, HttpClient? client = null)
    {
        var built = new Dictionary<string, ILlmProvider>();
        // THE WINDOWS COME ALONG NOW. This method already reads every instance's config and used to
        // keep only the provider it constructed from it — so the one place that HAS the windows was
        // the one place that dropped them.
        var windows = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var (name, cfg) in settings.Providers)
        {
            built[name] = Construct(name, cfg, client);
            windows[name] = cfg.ContextWindow;
        }
        return new ProviderRegistry(built, settings.DefaultProvider) { _windows = windows };
    }

    private static ILlmProvider Construct(string name, ProviderInstanceConfig cfg, HttpClient? client)
    {
        var display = $"{cfg.Kind} {cfg.Model}";
        return cfg.Kind switch
        {
            "anthropic" => new AnthropicProvider(
                name, display, cfg.Model, cfg.ApiKey ?? "", client: client),
            "openai-compatible" => new OpenAiCompatibleProvider(new OpenAiProviderOptions
            {
                ProviderId = name,
                DisplayName = display,
                Model = cfg.Model,
                BaseUrl = cfg.BaseUrl ?? "",
                ApiKey = cfg.ApiKey,
                ExtraHeaders = cfg.ExtraHeaders,
                Client = client,
                CacheControl = cfg.CacheControl,
            }),
            "ollama" => new OllamaProvider(
                name, display, cfg.Model, cfg.BaseUrl, client: client),
            _ => throw new InvalidOperationException(
                $"provider '{name}': unknown kind '{cfg.Kind}' reached the factory (should have failed validation).")
        };
    }
}
