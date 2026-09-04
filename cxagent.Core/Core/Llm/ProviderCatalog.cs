namespace CxAgent.Core.Llm;

/// <summary>
/// What this process was configured with, and never changes.
///
/// <para>ONE OF THREE LIFETIMES <see cref="ResolvedConfig"/> HELD TOGETHER. That record carried the
/// catalog, the model a session is currently using, and the errors from reading a file — twelve
/// members answering "when is this true?" three different ways. A method updating one had no way to
/// say which, and that is not a stylistic complaint: SwapProvider moved the agent's provider and the
/// host's runtime and not the sub-agent factory's captured default, so every child kept talking to
/// the model the session started on while the switch notice promised otherwise. A missed line,
/// because nothing named the set.</para>
///
/// <para>FIXED FOR THE PROCESS. Config is resolved once and never rebound — see AppBootstrap on why
/// setup restarts rather than reconfiguring in place — so everything here is read-only for a session's
/// whole life. What a session CHANGES is <see cref="ActiveModel"/>, and the two being separate types
/// is what makes "changed the model" and "changed the configuration" different statements.</para>
/// </summary>
/// <param name="Instances">
/// Every configured model, by the name a user types at <c>/model</c>. Null on the paths where nothing
/// resolved, where there is nothing to dispatch to anyway.
/// </param>
/// <param name="AgentTypes">Sub-agent types from config, merged with the shipped ones by
/// <c>AgentTypeCatalog</c> rather than replacing them.</param>
/// <param name="McpServers">MCP server definitions, loaded once at startup.</param>
/// <param name="Orchestrator">Turn and compaction budgets bounding every loop in this process.</param>
/// <param name="MaxConcurrentAgents">
/// How many sub-agents may call the default endpoint at once. Null is unlimited — cxagent cannot
/// discover what an endpoint tolerates, so it does not guess.
/// </param>
/// <param name="ClassifierInstance">
/// Which instance reviews writes in <c>/mode edits auto</c>.
///
/// <para>NULL MEANS AUTO IS NOT OFFERED — not listed, not cyclable, not parseable. A mode that
/// promises review while nothing reviews is worse than no mode at all.</para>
/// </param>
/// <param name="Theme">
/// The theme name from configuration, or null for cxagent's own. Not validated here — which themes
/// exist is a question only the window system can answer, and it does not exist when config is read.
/// </param>
public sealed record ProviderCatalog(
    ProviderRegistry? Instances = null,
    IReadOnlyDictionary<string, AgentTypeConfig>? AgentTypes = null,
    IReadOnlyDictionary<string, McpServerConfig>? McpServers = null,
    OrchestratorSettings? Orchestrator = null,
    int? MaxConcurrentAgents = null,
    string? ClassifierInstance = null,
    string? Theme = null)
{
    /// <summary>
    /// How long each of the classifier's two stages may take, from config's
    /// <c>classifierTimeoutSeconds</c>. Null leaves the classifier's own default.
    ///
    /// <para>AN INIT PROPERTY, NOT A CONSTRUCTOR PARAMETER, following PluginPaths below: the
    /// positional list is already at seven and this is a setting almost nobody passes.</para>
    /// </summary>
    public int? ClassifierTimeoutSeconds { get; init; }

    /// <summary>Where a plugin's <c>file</c> is searched for, in order — matching config.json's
    /// <c>pluginPaths</c>. Search paths are resolved once and stay fixed for the process, unlike the
    /// plugin entries themselves — see <see cref="PluginEntries"/> for why those moved out.</summary>
    public IReadOnlyList<string> PluginPaths { get; init; } = [];

    /// <summary>
    /// S1 as the user wrote it, from <c>llmAgent.tools</c>. Null when config said nothing.
    ///
    /// <para>A NAMED MEMBER: this record is already at six positional parameters, and a seventh is
    /// where AV1561 says to stop and ask whether the group wants a name. It does not — these are
    /// genuinely separate facts about one config file.</para>
    /// </summary>
    public Jobs.ToolSelection? Tools { get; init; }

    /// <summary>Never null, so a caller enumerating types need not check first.</summary>
    public IReadOnlyDictionary<string, AgentTypeConfig> Types =>
        AgentTypes ?? new Dictionary<string, AgentTypeConfig>();

    /// <summary>Never null, for the same reason as <see cref="Types"/>.</summary>
    public IReadOnlyDictionary<string, McpServerConfig> Servers =>
        McpServers ?? new Dictionary<string, McpServerConfig>();

    /// <summary>
    /// The model this catalog knows by that name, or null when it knows no such instance.
    ///
    /// <para>DERIVATION IS THE NORMAL PATH, and this is the method that makes it one. An
    /// <see cref="ActiveModel"/> is four facts about a catalog entry — provider, name, label,
    /// window — and every one of them is already here; building it by hand meant a caller
    /// re-deriving what the catalog could state. Four sites did, each with its own copy of the
    /// window logic, which is the accretion this codebase has a rule about.</para>
    ///
    /// <para>FROM MEMORY, NEVER FROM DISK. <c>/model</c> reached this answer through
    /// <c>ConfigResolver.ResolveInstance</c>, which re-read config.json, re-validated it, rebuilt
    /// the whole registry and re-probed the window — to obtain something this process already held,
    /// and then discarded every part of it but the model. Worse than wasteful: the catalog is
    /// documented as fixed for the process (setup restarts rather than reconfiguring in place), so a
    /// config edited since startup gave <c>/model</c> a different answer than the rest of the
    /// session, silently and with no restart.</para>
    ///
    /// <para>A MISS IS A MISS. This does not fall back to reading config — two answer sources behind
    /// one method is precisely the ambiguity that separating this type from
    /// <see cref="ActiveModel"/> was meant to end. An unknown name returns null and the caller says
    /// "no such instance"; re-reading config stays an explicit act.</para>
    /// </summary>
    /// <remarks>Delegates to <see cref="ProviderRegistry.Use"/>, which is where the data lives —
    /// one definition of what a named model resolves to, not two that can drift.</remarks>
    public ActiveModel? Use(string? instanceName) => Instances?.Use(instanceName);

    /// <summary>A catalog with nothing in it — the no-provider paths, and a starting point for a
    /// caller filling one in.</summary>
    public static ProviderCatalog Empty { get; } = new();
}
