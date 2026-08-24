using CxAgent.Core.Storage;

namespace CxAgent.Core.Llm;

/// <summary>
/// Everything config.json resolved to at startup — or the errors to show instead.
///
/// <para>NAMED FOR WHAT IT HOLDS. It was ProviderResolution, which described a third of it: the
/// provider, its instance name and window. The rest is the whole configuration — every configured
/// instance, the agent types, the MCP servers, the orchestrator's budgets, the classifier — carried
/// under a name that said "provider" and read as one.</para>
///
/// <para>THREE LIFETIMES ARE STILL TANGLED HERE, and the name only stops the misreading rather than
/// fixing it:</para>
/// <list type="bullet">
///   <item><b>The catalog</b> — Providers, AgentTypes, McpServers, Orchestrator,
///   MaxConcurrentAgents. Read once and never rebound; see AppBootstrap on why config is not applied
///   in place.</item>
///   <item><b>This session's model</b> — Provider, InstanceName, ContextWindow, DisplayName. Changes
///   on every <c>/model</c>, which is why Session.SwitchModel takes a whole one of these and uses
///   four fields.</item>
///   <item><b>The read itself</b> — Errors and Warnings, true of the load and of nothing after.</item>
/// </list>
///
/// <para>WHY THAT MATTERS, and it is not tidiness: when one record means both "the catalog" and "the
/// current model", a method that updates one has no way to say which. SwapProvider moved the agent
/// and the host and not the sub-agent spawner, and every child kept talking to the model the session
/// started on — a missed line rather than a compile error, because nothing named the set. Splitting
/// this into a catalog and an active model would make that unexpressible; it is ~40 call sites and
/// wants its own change.</para>
/// </summary>/// <summary>
/// What config.json resolved to: the catalog, the model to start on, and what went wrong reading it.
///
/// <para>THREE LIFETIMES, NOW NAMED. This was twelve members in one record — the catalog that never
/// changes, the model <c>/model</c> replaces, and the errors from the read — so a method updating one
/// had no way to say which. That is not a stylistic complaint: SwapProvider moved the agent's
/// provider and the host's runtime and not the sub-agent factory's captured default, and every child
/// kept talking to the model the session started on while the switch notice promised otherwise. A
/// missed line, because nothing named the set.</para>
///
/// <para>THIS TYPE SURVIVES AS THE PAIRING, because resolving produces all three at once and a
/// caller reading a config file wants them together. What changed is that a caller CHANGING one now
/// says which: <see cref="ActiveModel"/> travels alone through <c>SwitchModel</c>, and
/// <see cref="ProviderCatalog"/> cannot travel with it.</para>
/// </summary>
/// <param name="Model">The model to start on, or null when nothing usable resolved.</param>
/// <param name="Catalog">Everything else config said — fixed for this process.</param>
/// <param name="Errors">Why resolution failed. Empty on the happy path.</param>
/// <param name="Warnings">Non-fatal complaints, said once so a skipped server is not mistaken for a
/// slow one.</param>
public sealed record ResolvedConfig(
    ActiveModel? Model,
    ProviderCatalog Catalog,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>Whether anything usable resolved. Errors says why not.</summary>
    public bool HasProvider => Model is not null;

    /// <summary>Never null, so a caller need not check before enumerating.</summary>
    public IReadOnlyList<string> Notices => Warnings ?? [];

    // ---- the members callers read, forwarded ----------------------------------------------------
    //
    // NOT A FACADE HIDING THE OLD SHAPE. Reading a value through the pairing is fine and always was;
    // what caused the bug was WRITING one — a method that took the whole record and updated some of
    // it. Those callers now take an ActiveModel or a ProviderCatalog and physically cannot carry the
    // other along, which is the change that matters. These stay because `config.Provider` reads
    // better than `config.Model?.Provider` at two dozen call sites that only ever read.

    public ILlmProvider? Provider => Model?.Provider;
    public string? DisplayName => Model?.DisplayName;
    public string? InstanceName => Model?.InstanceName;
    public int? ContextWindow => Model?.ContextWindow;

    public ProviderRegistry? Providers => Catalog.Instances;
    public IReadOnlyDictionary<string, AgentTypeConfig> AgentTypes => Catalog.Types;
    public IReadOnlyDictionary<string, McpServerConfig> McpServers => Catalog.Servers;
    public OrchestratorSettings? Orchestrator => Catalog.Orchestrator;
    public IReadOnlyDictionary<string, PluginConfig> Plugins => Catalog.Plugins;
    public IReadOnlyList<string> PluginPaths => Catalog.PluginPaths;

    /// <summary>S1 as the USER wrote it, from <c>llmAgent.tools</c>. Composed ahead of the
    /// embedder's <c>SharedServices.ToolSelection</c> — one level, two authors.</summary>
    public Jobs.ToolSelection? Tools => Catalog.Tools;
    public int? MaxConcurrentAgents => Catalog.MaxConcurrentAgents;
    public string? ClassifierInstance => Catalog.ClassifierInstance;

    /// <summary>The theme name from config, or null for cxagent's own. Resolved at startup, where
    /// an unknown name falls back rather than failing — the registry does not exist here.</summary>
    public string? Theme => Catalog.Theme;

    /// <summary>
    /// The same configuration over a different model — what <c>/model</c> produces.
    ///
    /// <para>NAMED RATHER THAN LEFT TO <c>with</c>. Changing the model by reaching into the record and
    /// setting one field — <c>with { ContextWindow = … }</c> — reads as a small edit, and is the shape
    /// that lets a swap update some of the model and not the rest. Saying "the catalog is the same,
    /// the model is this" makes the boundary the type's rather than the caller's.</para>
    /// </summary>
    public ResolvedConfig WithModel(ActiveModel model) => this with { Model = model };

    /// <summary>The same model over a different catalog — for a caller assembling one in pieces.</summary>
    public ResolvedConfig WithCatalog(ProviderCatalog catalog) => this with { Catalog = catalog };

    /// <summary>The same configuration, with the model's window corrected. The one field a caller
    /// legitimately adjusts alone: it is config-only, so a probe or a test may know it late.</summary>
    public ResolvedConfig WithContextWindow(int? window) =>
        Model is null ? this : this with { Model = Model with { ContextWindow = window } };

    /// <summary>Nothing resolved, and here is why.</summary>
    public static ResolvedConfig Failed(IReadOnlyList<string> errors) =>
        new(null, ProviderCatalog.Empty, errors);

    /// <summary>A session over one provider and nothing else configured.</summary>
    public static ResolvedConfig ForTesting(ILlmProvider provider, string instanceName = "test") =>
        new(new ActiveModel(provider, instanceName, provider.DisplayName),
            ProviderCatalog.Empty, []);
}

public static class ConfigResolver
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
    public static ResolvedConfig? ResolveInstance(
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

            return new ResolvedConfig(
                new ActiveModel(provider, instanceName, provider.DisplayName, contextWindow),
                new ProviderCatalog(
                    Instances: registry,
                    AgentTypes: settings.AgentTypes,
                    McpServers: settings.McpServers,
                    Orchestrator: settings.Orchestrator,
                    MaxConcurrentAgents: cfg.MaxConcurrentAgents,
                    ClassifierInstance: settings.Classifier,
                    Theme: settings.Theme)
                { Tools = settings.Tools, Plugins = settings.Plugins, PluginPaths = settings.PluginPaths },
                [],
                settings.Warnings);
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
    /// "✗ Queue empty." in chat and left the job panel permanently empty (ToolsChanged runs only after
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

    public static ResolvedConfig Resolve(AppPaths paths, IReadOnlyDictionary<string, string> env, bool useMock)
    {
        if (useMock)
        {
            var mock = new MockLlmProvider();
            SeedDemoPlan(mock);
            // The demo path gets a registry over the mock too, so an llm_agent job in a --mock
            // session dispatches rather than failing to resolve an executor type that is registered.
            var mockRegistry = ProviderRegistry.FromProviders(
                new Dictionary<string, ILlmProvider> { ["mock"] = mock }, "mock");
            return new ResolvedConfig(
                new ActiveModel(mock, "mock", mock.DisplayName),
                new ProviderCatalog(Instances: mockRegistry),
                []);
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
            return new ResolvedConfig(
                new ActiveModel(provider, settings.DefaultProvider, provider.DisplayName, contextWindow),
                new ProviderCatalog(
                    Instances: registry,
                    AgentTypes: settings.AgentTypes,
                    McpServers: settings.McpServers,
                    Orchestrator: settings.Orchestrator,
                    MaxConcurrentAgents: cfg?.MaxConcurrentAgents,
                    ClassifierInstance: settings.Classifier,
                    Theme: settings.Theme)
                // llmAgent.tools (S1 in config). ResolveInstance carried it and THIS PATH DID NOT,
                // so the key parsed, validated, warned correctly about bad terms — and was then
                // dropped on every normal startup, taking effect only after a /model switch went
                // through the other method. Every config test was green: they proved the loader
                // read it, and nothing proved anything applied it.
                // PLUGINS AND PLUGINPATHS TRAVEL WITH Tools, for the identical reason: this path
                // runs on every normal startup, and a key carried only by ResolveInstance takes
                // effect only after a /model switch. A configured plugin that loads after switching
                // models but not on launch is the same defect wearing a different key's name.
                { Tools = settings.Tools, Plugins = settings.Plugins, PluginPaths = settings.PluginPaths },
                [],
                settings.Warnings);
        }
        catch (ProviderConfigException ex)
        {
            return new ResolvedConfig(null, ProviderCatalog.Empty, ex.Errors);
        }
        catch (InvalidOperationException ex)
        {
            return new ResolvedConfig(null, ProviderCatalog.Empty, new[] { ex.Message });
        }
    }
}
