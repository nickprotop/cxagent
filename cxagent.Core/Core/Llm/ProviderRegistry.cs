using CxAgent.Core.Llm.Providers;

namespace CxAgent.Core.Llm;

/// <summary>
/// Config-driven map: instance name -> constructed ILlmProvider. Built once from ProviderSettings;
/// the app-default (orchestrator) provider is resolved via Default. The wizard (P5) and llm_agent
/// (P3b) consume this. NOTE: distinct from the P3 JobRegistry (jobs), despite the similar name.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _providers;

    /// <summary>Per-instance context windows, empty for a registry built without config (tests, the
    /// mock path) — where "unknown" is the honest answer anyway.
    ///
    /// <para>CONFIGURED FIRST, THEN WHAT THE ENDPOINT SAID. An entry that declares
    /// <c>contextWindow</c> lands here at Build and never moves; one that does not is filled in by
    /// <see cref="WindowFor"/> the first time somebody needs it, from the probe. Mutable for that
    /// reason and no other — see <see cref="_endpoints"/> for why the answer is cached rather than
    /// asked again.</para></summary>
    private Dictionary<string, int?> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// What the probe needs to ask an instance its window, for the instances that did not declare
    /// one. Absent means there is nothing to ask — a mock, or an entry with no base URL.
    ///
    /// <para>KEPT RATHER THAN THE WHOLE CONFIG, because this is the only thing the registry has any
    /// business asking later, and holding the full <c>ProviderInstanceConfig</c> would put an API key
    /// on a long-lived object for no reason the registry can name.</para>
    /// </summary>
    private Dictionary<string, ProbeTarget> _endpoints = new(StringComparer.Ordinal);

    /// <summary>Where to ask, what to ask about, and what to authenticate with.</summary>
    private readonly record struct ProbeTarget(string? BaseUrl, string? Model, string? ApiKey);

    /// <summary>Guards the lazy fill. Two sessions switching at once would otherwise probe the same
    /// endpoint twice and race on the dictionary.</summary>
    private readonly object _windowLock = new();

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
    /// <see cref="MockLlmProvider"/> (or any test double) behind a <c>RoleResolver</c>, and the
    /// dispatch path could only ever be tested against a real HTTP driver.
    /// <paramref name="defaultName"/> may be null or name a missing instance, matching Build's
    /// tolerance of a config with no <c>defaultProvider</c>.
    /// </summary>
    /// <param name="windows">
    /// Per-instance context windows, when the caller knows them. Optional because the mock path and
    /// most tests do not — and "unknown" is the honest answer there, not a guess.
    /// </param>
    /// <param name="providers">The configured instances, keyed by the name config gave them.</param>
    /// <param name="defaultName">Which instance a session opens on, or null when none is set.</param>
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

        // AND WHERE TO ASK, for the ones that declared nothing. Probing all of them HERE would cost
        // three seconds per unconfigured endpoint at startup, for models the user may never select —
        // so the coordinates are kept and the question is asked on first use instead.
        var endpoints = new Dictionary<string, ProbeTarget>(StringComparer.Ordinal);

        foreach (var (name, cfg) in settings.Providers)
        {
            built[name] = Construct(name, cfg, client);
            windows[name] = cfg.ContextWindow;
            if (cfg.ContextWindow is null && !string.IsNullOrWhiteSpace(cfg.BaseUrl))
                endpoints[name] = new ProbeTarget(cfg.BaseUrl, cfg.Model, cfg.ApiKey);
        }

        return new ProviderRegistry(built, settings.DefaultProvider)
        {
            _windows = windows,
            _endpoints = endpoints,
        };
    }

    /// <summary>
    /// The model this registry knows by that name, or null when it knows no such instance.
    ///
    /// <para>HERE RATHER THAN ON <see cref="ProviderCatalog"/> BECAUSE THIS IS WHERE THE DATA IS.
    /// An <see cref="ActiveModel"/> is four facts — provider, instance name, display name, window —
    /// and this type holds all four. The catalog delegates, so there is exactly one definition of
    /// what a named model resolves to rather than two that can drift.</para>
    /// </summary>
    public ActiveModel? Use(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;
        if (!TryGet(instanceName, out var provider)) return null;

        // WindowFor, not InstanceWindows. An instance that declared no contextWindow has its window
        // ASKED OF THE ENDPOINT here, because a null one does not travel harmlessly:
        // AgentHost.SwapProvider reads `model.ContextWindow ?? _runtime.ContextWindow`, so a switch
        // carrying null silently keeps the window of the model being LEFT — compaction sized against
        // the wrong ceiling, which is the bug ContextWindowProbe exists to prevent.
        return new ActiveModel(provider, instanceName, provider.DisplayName, WindowFor(instanceName));
    }

    /// <summary>
    /// This instance's context window: what config declared, or what the endpoint says it serves.
    ///
    /// <para>THE REASON THIS IS NOT JUST A DICTIONARY LOOKUP. A window that is null travels badly:
    /// <c>AgentHost.SwapProvider</c> reads <c>model.ContextWindow ?? _runtime.ContextWindow</c>, so a
    /// switch to an instance with no window silently KEEPS THE MODEL BEING LEFT — the new model
    /// sized against the old one's ceiling, which is the compaction bug ContextWindowProbe was
    /// written to prevent, reintroduced at the switch instead of at startup.</para>
    ///
    /// <para>ASKED ONCE. The answer is written back, so a second switch to the same instance is a
    /// dictionary read. A probe that failed caches nothing and is retried — the endpoint may simply
    /// have been down, and null already means "unknown" everywhere downstream.</para>
    ///
    /// <para>SYNCHRONOUS, matching the two call sites in ConfigResolver that already block on this
    /// probe during startup. It is bounded at three seconds and every failure returns null.</para>
    /// </summary>
    public int? WindowFor(string instanceName)
    {
        lock (_windowLock)
        {
            if (_windows.TryGetValue(instanceName, out var known) && known is not null) return known;
            if (!_endpoints.TryGetValue(instanceName, out var target)) return null;

            var probed = ContextWindowProbe
                .TryGetAsync(target.BaseUrl, target.Model, target.ApiKey)
                .GetAwaiter().GetResult();

            // ONLY A REAL ANSWER IS REMEMBERED. Caching a null would turn one unreachable endpoint
            // into a permanently unknown window for the life of the process.
            if (probed is not null) _windows[instanceName] = probed;
            return probed;
        }
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
