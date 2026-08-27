namespace CxAgent.Core.Llm;

/// <summary>Which driver talks to an endpoint. The typed form of config.json's <c>kind</c>.</summary>
public enum ProviderKind
{
    /// <summary>Anything speaking the OpenAI chat-completions shape — llama.cpp, vLLM, OpenRouter,
    /// LM Studio, OpenAI itself.</summary>
    OpenAiCompatible,

    /// <summary>Anthropic's own API. Takes no base URL.</summary>
    Anthropic,

    /// <summary>Ollama's native API, which is not the OpenAI shape.</summary>
    Ollama,
}

/// <summary>
/// One model a host can talk to — config.json's <c>providers</c> entry, in code.
/// </summary>
/// <param name="Kind">
/// Which driver. POSITIONAL AND REQUIRED, deliberately: it was tempting to infer it from the shape
/// of a base URL, and that is a guess dressed as convenience. A host naming its own models knows
/// which protocol they speak, and a wrong inference fails at the first request rather than here.
/// </param>
/// <param name="Model">The model id the endpoint expects — <c>qwen3.6-…gguf</c>, <c>claude-sonnet-4-5</c>.</param>
public sealed record ModelConfig(ProviderKind Kind, string Model)
{
    /// <summary>Where the endpoint lives. Null for <see cref="ProviderKind.Anthropic"/>, which has
    /// one address, and required for everything else.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The key, or null for a local server that checks none.</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// How much this model can hold, in tokens.
    ///
    /// <para>NOT DECORATION. The compression threshold derives from it, so a wrong number compacts
    /// far too early or not until the provider refuses the request. Null means unknown, and
    /// compaction falls back to a fixed threshold.</para>
    /// </summary>
    public int? ContextWindow { get; init; }

    /// <summary>How many sub-agents may call THIS endpoint at once. Null is unlimited — cxagent
    /// cannot discover what an endpoint tolerates, so it does not guess.</summary>
    public int? MaxConcurrentAgents { get; init; }

    /// <summary>Extra headers, for endpoints that want attribution or routing hints.</summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>Ask this endpoint to cache the system prompt. Opt-in because cache writes are billed
    /// above normal input — see ProviderInstanceConfig for the numbers.</summary>
    public bool CacheControl { get; init; }
}

/// <summary>
/// One MCP server — config.json's <c>mcp</c> entry, in code.
/// </summary>
public sealed record McpConfig
{
    /// <summary>The command line, as it would be typed. <c>new("npx", "-y", "@upstash/context7-mcp")</c>
    /// reads as the thing it launches.</summary>
    public McpConfig(params string[] command) => Command = command;

    public IReadOnlyList<string> Command { get; }

    public bool Enabled { get; init; } = true;

    public int? TimeoutMs { get; init; }

    public Dictionary<string, string> Environment { get; init; } = [];

    public string? WorkingDirectory { get; init; }
}

/// <summary>
/// A whole cxagent configuration, written in code.
///
/// <para>WHY THIS EXISTS. <see cref="ResolvedConfig"/> is what everything downstream consumes, and it
/// is shaped for that job: a provider, a catalog, an instance name, a window, agent types, MCP
/// servers, budgets and the errors from reading a file — twelve members across three lifetimes.
/// Authoring one by hand means stating the same fact several times: the provider appears both alone
/// and inside the catalog, its name appears as InstanceName and as a dictionary key, its window
/// appears twice. A caller can make those disagree and nothing stops them.</para>
///
/// <para>SO THIS IS THE FRONT DOOR, mirroring config.json rather than the runtime. Each fact appears
/// once, <see cref="Resolve"/> produces the ResolvedConfig everything already reads, and the ~40 call
/// sites downstream do not move.</para>
///
/// <para>IT IS NOT A REPLACEMENT for reading config.json — that path is unchanged. It is for the host
/// that already knows its models from its own settings and has no reason to write them into a file
/// for cxagent to read back.</para>
/// </summary>
public sealed record AgentConfig
{
    /// <summary>Every model this process can talk to, by the name a user types at <c>/model</c>.</summary>
    public Dictionary<string, ModelConfig> Models { get; init; } = [];

    /// <summary>Which of <see cref="Models"/> a session starts on. Null takes the single entry when
    /// there is exactly one, and resolves to nothing when there are several.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// Which model reviews writes in <c>/mode edits auto</c>.
    ///
    /// <para>NULL MEANS AUTO IS NOT OFFERED — not listed, not cyclable, not parseable. A mode that
    /// promises review while nothing reviews is worse than no mode at all.</para>
    /// </summary>
    public string? Classifier { get; init; }

    /// <summary>Turns one request may take before it is stopped. Null is unbounded.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Compact when the context passes this many tokens. Null derives it from the model's
    /// own window, which is the better answer when the window is known.</summary>
    public int? CompressAbove { get; init; }

    /// <summary>
    /// Sub-agent types, added to the shipped five rather than replacing them.
    ///
    /// <para>A SHIPPED NAME OVERRIDES ONE FIELD: <c>["planner"] = new() { MaxTurns = 40 }</c> keeps
    /// the shipped briefing and changes the budget. A new name brings its own briefing, because a
    /// type with nothing to tell its children is not a type.</para>
    /// </summary>
    public Dictionary<string, AgentTypeConfig> Agents { get; init; } = [];

    /// <summary>MCP servers every session in this process shares.</summary>
    public Dictionary<string, McpConfig> Mcp { get; init; } = [];

    /// <summary>
    /// Plugins this process is configured with, by name — the same vocabulary <c>config.json</c>'s
    /// <c>plugins</c> uses, so an embedder passing config in code gets the same validation (config-time
    /// collision checking included) as one editing the file. See <see cref="Llm.PluginConfig"/>.
    /// </summary>
    public Dictionary<string, PluginConfig> Plugins { get; init; } = [];

    /// <summary>
    /// Where a plugin's <see cref="PluginConfig.File"/> is searched for, in order — a SIBLING of
    /// <see cref="Plugins"/>, matching <c>config.json</c>'s <c>pluginPaths</c> for the same reason
    /// that key is not nested inside <c>plugins</c> there: a settings member living among name-keyed
    /// entries collides with a plugin of that name.
    /// </summary>
    public List<string> PluginPaths { get; init; } = [];

    /// <summary>
    /// Builds what the runtime consumes.
    ///
    /// <para>THE ERRORS COME BACK AS A VALUE, exactly as ConfigResolver's do — a model named as
    /// default that is not in <see cref="Models"/> is a mistake worth reporting rather than throwing
    /// at, because the caller is usually assembling this from its own settings and wants to say what
    /// went wrong.</para>
    /// </summary>
    public ResolvedConfig Resolve(HttpClient? client = null)
    {
        var errors = new List<string>();

        if (Models.Count == 0) errors.Add("no models were configured.");

        var defaultName = DefaultModel ?? (Models.Count == 1 ? Models.Keys.First() : null);

        if (DefaultModel is { } named && !Models.ContainsKey(named))
            errors.Add($"defaultModel '{named}' is not among the configured models.");
        else if (defaultName is null && Models.Count > 1)
            errors.Add("several models are configured but none was named as the default.");

        if (Classifier is { } classifier && !Models.ContainsKey(classifier))
            errors.Add($"classifier '{classifier}' is not among the configured models.");

        // SAME CHECK config.json GETS — see ProviderConfigLoader.ValidatePluginCollisions. An
        // embedder's PluginPaths entries are searched directly, with no config directory to fall
        // back to for a relative one: code-configured plugins have no such anchor.
        ProviderConfigLoader.ValidatePluginCollisions(Plugins, config => FindPluginSidecar(config), errors);

        if (errors.Count > 0) return ResolvedConfig.Failed(errors);

        var instances = Models.ToDictionary(
            m => m.Key,
            m => ProviderFor(m.Key, m.Value, client),
            StringComparer.Ordinal);

        var windows = Models.ToDictionary(m => m.Key, m => m.Value.ContextWindow, StringComparer.Ordinal);
        var chosen = Models[defaultName!];

        return new ResolvedConfig(
            new ActiveModel(
                instances[defaultName!],
                defaultName,
                $"{KindName(chosen.Kind)} {chosen.Model}",
                chosen.ContextWindow),
            new ProviderCatalog(
                Instances: ProviderRegistry.FromProviders(instances, defaultName, windows),
                AgentTypes: new Dictionary<string, AgentTypeConfig>(Agents, StringComparer.Ordinal),
                McpServers: Mcp.ToDictionary(
                    s => s.Key,
                    s => new McpServerConfig(s.Value.Command, s.Value.Enabled, s.Value.TimeoutMs,
                        s.Value.Environment.Count > 0 ? s.Value.Environment : null,
                        s.Value.WorkingDirectory),
                    StringComparer.Ordinal),
                Orchestrator: new OrchestratorSettings(MaxTurns, CompressAbove),
                MaxConcurrentAgents: chosen.MaxConcurrentAgents,
                ClassifierInstance: Classifier)
            { PluginPaths = PluginPaths },
            [],
            Entries: new PluginEntries(Plugins));
    }

    /// <summary>
    /// Finds one plugin's sidecar manifest by searching <see cref="PluginPaths"/> directly, in
    /// order — the code-config counterpart of <c>ProviderConfigLoader.FindPluginSidecar</c>. A
    /// relative entry here resolves against the CURRENT DIRECTORY rather than a config directory:
    /// code-configured plugins have no config.json beside them to anchor a relative path against.
    /// </summary>
    private string? FindPluginSidecar(PluginConfig config)
    {
        foreach (var raw in PluginPaths)
        {
            var assemblyPath = Path.Combine(raw, config.File);
            if (!File.Exists(assemblyPath)) continue;

            var sidecarPath = Path.ChangeExtension(assemblyPath, null) + ".plugin.json";
            if (File.Exists(sidecarPath)) return sidecarPath;
        }

        return null;
    }

    // THE SAME CONSTRUCTION ProviderRegistry.Construct performs for a config.json entry, reached from
    // a typed kind rather than a string. Kept here rather than shared because that one takes a
    // ProviderInstanceConfig — the JSON shape — and building one of those just to unwrap it would be
    // a detour through the file format this type exists to avoid.
    private static ILlmProvider ProviderFor(string name, ModelConfig model, HttpClient? client)
    {
        var display = $"{KindName(model.Kind)} {model.Model}";

        return model.Kind switch
        {
            ProviderKind.Anthropic => new Providers.AnthropicProvider(
                name, display, model.Model, model.ApiKey ?? "", client: client),

            ProviderKind.Ollama => new Providers.OllamaProvider(
                name, display, model.Model, model.BaseUrl, client: client),

            _ => new Providers.OpenAiCompatibleProvider(new Providers.OpenAiProviderOptions
            {
                ProviderId = name,
                DisplayName = display,
                Model = model.Model,
                BaseUrl = model.BaseUrl ?? "",
                ApiKey = model.ApiKey,
                ExtraHeaders = model.Headers.Count > 0 ? model.Headers : null,
                Client = client,
                CacheControl = model.CacheControl,
            }),
        };
    }

    /// <summary>The config.json spelling, so a display name reads the same however it was built.</summary>
    private static string KindName(ProviderKind kind) => kind switch
    {
        ProviderKind.Anthropic => "anthropic",
        ProviderKind.Ollama => "ollama",
        _ => "openai-compatible",
    };
}
