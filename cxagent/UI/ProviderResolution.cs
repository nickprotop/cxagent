using CxAgent.Core.Llm;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>Outcome of resolving a provider at startup: either a live provider, or the errors to show.</summary>
/// <param name="Orchestrator">
/// The orchestrator token budgets from config, threaded through so AppBootstrap can bound the live
/// AgentHost. Defaulted to null (= unbounded) so --mock and the no-provider paths need not supply it.
/// Without this the budgets parse and round-trip but never reach the runner, leaving the cap unit-tested
/// and unenforced — which is exactly what shipped before this parameter existed.
/// </param>
/// <param name="Providers">
/// The role→provider resolver <c>llm_agent</c> dispatches through, built from the SAME settings load
/// as <paramref name="Provider"/>. Carried here because the default provider alone is not enough to
/// run a role-bearing job: the plugin needs the whole catalog plus the role bindings. Null on the
/// no-provider paths, where there is nothing to dispatch to anyway.
/// </param>
/// <param name="MaxConcurrentAgents">
/// The resolved instance's <c>maxConcurrentAgents</c> — how many sub-agents may call THIS endpoint
/// at once. Null (the default, and the common case) means unlimited: cxagent cannot discover what an
/// endpoint tolerates, so it does not guess. Threaded here for the same reason ContextWindow is —
/// config-only, resolved once at startup rather than re-read per spawn.
/// </param>
/// <param name="ContextWindow">
/// The default provider instance's ProviderInstanceConfig.ContextWindow (P11 Task 1), threaded
/// through so AppBootstrap can hand AgentHost the real number to derive its compression threshold
/// from (P11 Task 2: OrchestratorSettings.EffectiveCompressThreshold). ILlmProvider itself carries no
/// such property — it is config-only — so this is resolved here, once, from the same settings load as
/// Provider, rather than re-read from config at every compression check. Null on the --mock and
/// no-provider paths, and whenever the user hasn't set contextWindow for the resolved instance.
/// </param>
public sealed record ProviderResolution(
    ILlmProvider? Provider,
    string? DisplayName,
    IReadOnlyList<string> Errors,
    OrchestratorSettings? Orchestrator = null,
    ProviderRegistry? Providers = null,
    int? ContextWindow = null,
    int? MaxConcurrentAgents = null)
{
    public bool HasProvider => Provider is not null;

    /// <summary>
    /// Which <c>providers</c> entry is in use, or null when it cannot be named (the mock).
    ///
    /// <para>DISTINCT FROM <see cref="DisplayName"/>, which is the driver's own label — "Anthropic",
    /// "Mock". This is the key a user typed in config and the one <c>/model</c> switches by, and
    /// nothing carried it before: the resolution knew the window it had resolved but not which
    /// instance produced it.</para>
    /// </summary>
    public string? InstanceName { get; init; }

    /// <summary>Configured MCP servers, empty when none are — carried here because this is the one
    /// place config.json is read, and the servers have to be started from what it said.</summary>
    /// <summary>Configured sub-agent types, empty when none are. Carried for the same reason
    /// McpServers is: AppBootstrap composes from this record, and a setting that stops here is a
    /// setting the session never gets.</summary>
    public IReadOnlyDictionary<string, AgentTypeConfig> AgentTypes { get; init; } =
        new Dictionary<string, AgentTypeConfig>();

    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; } =
        new Dictionary<string, McpServerConfig>();

    /// <summary>Non-fatal config complaints, for the transcript to show. A skipped server the user
    /// never hears about is indistinguishable from one that is merely slow.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Resolves the startup provider from config (or --mock). Never throws: a missing/invalid config
/// yields a no-provider ProviderResolution carrying actionable error lines for the UI to render.
/// </summary>
public static class ProviderResolver
{
    /// <summary>
    /// The same resolution, against a NAMED instance rather than <c>defaultProvider</c>.
    ///
    /// <para>For <c>/model</c>. Everything that makes a session — the registry, the orchestrator
    /// settings, MCP servers, agent types — is identical; only which entry of <c>providers</c>
    /// answers, and therefore which model and which context window, differs.</para>
    ///
    /// <para>Null when the name is not configured or the config cannot be read. The caller says so;
    /// this returns nothing rather than falling back to the default, because silently continuing on
    /// the model the user was trying to leave is the worst available outcome.</para>
    /// </summary>
    public static ProviderResolution? ResolveInstance(
        AppPaths paths, IReadOnlyDictionary<string, string> env, string instanceName)
    {
        try
        {
            var settings = ProviderConfigLoader.LoadAndValidate(paths, env);
            var registry = ProviderRegistry.Build(settings);

            if (!registry.TryGet(instanceName, out var provider)) return null;
            if (!settings.Providers.TryGetValue(instanceName, out var cfg)) return null;

            int? contextWindow = cfg.ContextWindow
                ?? ContextWindowProbe.TryGetAsync(cfg.BaseUrl, cfg.Model, cfg.ApiKey)
                    .GetAwaiter().GetResult();

            return new ProviderResolution(provider, provider.DisplayName, Array.Empty<string>(),
                settings.Orchestrator, registry, contextWindow, cfg.MaxConcurrentAgents)
            {
                InstanceName = instanceName,
                McpServers = settings.McpServers,
                AgentTypes = settings.AgentTypes,
                Warnings = settings.Warnings,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Seeds the --mock provider with a canned create_plan so `--mock` is a WORKING demo path.
    /// MockLlmProvider is queue-driven (ChatAsync = Queue.Dequeue()), so an unseeded instance throws
    /// InvalidOperationException("Queue empty.") on the first goal — which surfaced as a red
    /// "✗ Queue empty." in chat and left the job panel permanently empty (SetJobs runs only after
    /// PlanCompiler.BuildDag, which needs a plan).
    ///
    /// The plan is deliberately dependency-shaped (two parallel roots → a dependent join) so the
    /// panel demonstrates several blocks moving through Queued → Running → Succeeded, and uses only
    /// side-effect-free built-ins (`wait`, and `shell` with echo) so running the demo can't touch
    /// the user's machine. Enqueued repeatedly so multiple goals can be submitted in one session.
    /// </summary>
    private static void SeedDemoPlan(MockLlmProvider mock)
    {
        const int Runs = 20;   // plenty for an interactive session; each submission dequeues one
        for (int i = 0; i < Runs; i++)
        {
            mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
            {
                summary = "Demo plan (--mock): two parallel checks, then a dependent build and report.",
                jobs = new object[]
                {
                    new { id = "fetch",  name = "Fetch sources",   type = "shell",
                          @params = new { command = "echo fetched 3 repositories" } },
                    new { id = "lint",   name = "Lint config",     type = "shell",
                          @params = new { command = "echo config OK" } },
                    new { id = "build",  name = "Build artifact",  type = "wait",
                          @params = new { seconds = 2 }, depends_on = new[] { "fetch", "lint" } },
                    new { id = "report", name = "Publish report",  type = "shell",
                          @params = new { command = "echo report published" }, depends_on = new[] { "build" } },
                }
            }));
        }
    }

    public static ProviderResolution Resolve(AppPaths paths, IReadOnlyDictionary<string, string> env, bool useMock)
    {
        if (useMock)
        {
            var mock = new MockLlmProvider();
            SeedDemoPlan(mock);
            // The demo path gets a registry over the mock too, so an llm_agent job in a --mock
            // session dispatches rather than failing to resolve a plugin type that is registered.
            var mockRegistry = ProviderRegistry.FromProviders(
                new Dictionary<string, ILlmProvider> { ["mock"] = mock }, "mock");
            return new ProviderResolution(mock, mock.DisplayName, Array.Empty<string>(),
                Providers: mockRegistry);
        }

        try
        {
            var settings = ProviderConfigLoader.LoadAndValidate(paths, env);
            var registry = ProviderRegistry.Build(settings);
            var provider = registry.Default;   // throws InvalidOperationException if defaultProvider unset/absent
            // Carry the orchestrator budgets through: AppBootstrap needs them to construct a BOUNDED
            // AgentHost. Dropping them here is what left the cap unenforced in production.
            // settings.DefaultProvider names the SAME instance `registry.Default` just resolved
            // (ProviderRegistry.Build validates this pairing), so looking its config back up by that
            // name gets the window for the provider actually in use, not some other configured one.
            var cfg = settings.DefaultProvider is { } dp && settings.Providers.TryGetValue(dp, out var c)
                ? c : null;

            // CONFIGURED FIRST, PROBED SECOND. An explicit contextWindow is the user telling us
            // something about their setup — a shared endpoint, a deliberately smaller budget — and a
            // number read off the server must not override that. The probe only fills the silence,
            // which is the common case: the field is optional and almost never set.
            //
            // Synchronous by design. Resolve() is called during startup before the UI exists, the
            // probe is bounded at three seconds, and every failure returns null — so the worst case
            // is the behaviour we had before it existed.
            int? contextWindow = cfg?.ContextWindow
                ?? ContextWindowProbe.TryGetAsync(cfg?.BaseUrl, cfg?.Model, cfg?.ApiKey)
                    .GetAwaiter().GetResult();
            return new ProviderResolution(provider, provider.DisplayName, Array.Empty<string>(),
                settings.Orchestrator, registry, contextWindow, cfg?.MaxConcurrentAgents)
            {
                InstanceName = settings.DefaultProvider,
                McpServers = settings.McpServers,
                AgentTypes = settings.AgentTypes,
                Warnings = settings.Warnings,
            };
        }
        catch (ProviderConfigException ex)
        {
            return new ProviderResolution(null, null, ex.Errors);
        }
        catch (InvalidOperationException ex)
        {
            return new ProviderResolution(null, null, new[] { ex.Message });
        }
    }
}
