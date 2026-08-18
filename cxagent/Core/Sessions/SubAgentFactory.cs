using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Sessions;

/// <summary>A child, its captured output, and the panel holding its rows.</summary>
/// <param name="Agent">The child itself. Its <c>Id</c> addresses its row and names its log directory.</param>
/// <param name="Sink">Everything it said, for inspection after the fact.</param>
/// <param name="Jobs">Every tool it called.</param>
/// <param name="TypeName">The resolved type — <c>general</c> unless config named another.</param>
/// <param name="ModelId">
/// The instance:model this child actually runs on, which is NOT always the session's: a type may
/// name its own provider. Carried here because the row is the only place a user learns a worker went
/// somewhere else, and by the time it finishes the provider is no longer reachable from the row.
/// Qualified by instance because that is what "somewhere else" means — two instances can serve the
/// same model, and a bare model name reports the interesting case as no change at all.
/// </param>
public sealed record SubAgent(Agent Agent, BufferedChatSink Sink, BufferedJobPanel Jobs,
    string TypeName, string? ModelId);

/// <summary>
/// Builds a sub-agent from a parent's own wiring.
///
/// <para>THE CHILD IS BUILT DIRECTLY AS AN <see cref="Agent"/>, NEVER THROUGH <see cref="AgentHost"/>,
/// and that single rule buys two things at once. <c>AgentHost</c> is the UI's side of ONE session: it
/// owns the session store, so a child built through it would write a row under its own id that
/// <c>OfferResumeAsync</c> then offers at next launch as a crashed session the user never ran; and it
/// subscribes the four agent events for its own status bar, which are exactly the events a child's
/// telemetry reporter needs.</para>
///
/// <para>EVERY WIRE HERE HAS A NAMED FAILURE IF OMITTED — see the comments. They are written out
/// because "the factory passes the parent's X" is the kind of line that reads as obvious and is
/// silently wrong when X is the one thing that had to differ.</para>
/// </summary>
public sealed class SubAgentFactory
{

    /// <summary>
    /// Derives a compaction threshold from a context window — the caller's own rule, injected rather
    /// than reimplemented.
    ///
    /// <para>The session's threshold is computed once in AppBootstrap from the ACTIVE provider's
    /// window (EffectiveCompressThreshold, which is 80% of it unless config states a figure). A type
    /// naming a different instance has a different window, so the derivation has to run again — and
    /// a second copy of "80%" here would desynchronise the moment either moved.</para>
    ///
    /// <para>Null for callers that never use per-type providers, which then keep the session's
    /// threshold unchanged.</para>
    /// </summary>

    /// <summary>
    /// Everything a child needs that comes from the SESSION rather than from the spawn call.
    ///
    /// <para>TWELVE PARAMETERS BECAME ONE, and the grouping is not cosmetic: these are exactly the
    /// values a child INHERITS. What a child gets differently — its own buffered sinks, its own log
    /// directory nested under its parent, a fresh context, a type's provider and window — is decided
    /// in <see cref="SubAgentFactory.Create"/> and is deliberately NOT here. A child is not a copy of
    /// its parent, and this type is only the half that is shared.</para>
    ///
    /// <para>IMMUTABLE, AND DERIVED RATHER THAN MUTATED. <c>init</c> everywhere, so a caller cannot
    /// hand two children the same instance and then change it under one of them. Where a child needs
    /// a different value — a type with its own provider — <see cref="With"/> produces a NEW record
    /// and the original is untouched. Sharing one mutable settings object between concurrent children
    /// is the class of bug that shows up as a child compacting against another child's window.</para>
    /// </summary>
    public sealed record SubAgentRuntime
    {
        /// <summary>The session's provider, unless a type names its own.</summary>
        public required ILlmProvider Provider { get; init; }

        /// <summary>Which `providers` entry that is, so a child's spend is attributed to the
        /// instance it actually used rather than merged with another entry serving the same
        /// model.</summary>
        public string? InstanceName { get; init; }

        /// <summary>The tools a child may call — the parent's registry, not a narrowed copy.</summary>
        public required PluginRegistry Plugins { get; init; }

        /// <summary>
        /// THE PARENT'S LEDGER, DELIBERATELY (D7). A child's spend is the session's spend: the figure
        /// a user reads covers the work, not the agent that happened to do it, and a child with its
        /// own ledger spends against nothing.
        /// </summary>
        public required TokenLedger Ledger { get; init; }

        /// <summary>The parent's log manager. <c>Create</c> nests each child beneath its parent.</summary>
        public LogFileManager? Logs { get; init; }

        /// <summary>
        /// THE PARENT'S CEILING, not a smaller number invented for children. A figure chosen here is
        /// the same mistake a tight session cap makes: it caps mid-work and returns a salvage summary
        /// that the caller reads as a finished answer. Zero means unbounded, and <see cref="Agent"/>
        /// translates it.
        /// </summary>
        public required int MaxTurns { get; init; }

        /// <summary>
        /// The caller's computed threshold — <c>EffectiveCompressThreshold(window) ??
        /// DefaultCompressThreshold</c> — never a literal. Two copies of that number desynchronise
        /// the moment either moves, and a child that never compresses dies on an overflow instead.
        /// </summary>
        public int? CompressAbove { get; init; }

        /// <summary>
        /// The child's own <see cref="AgentContext"/> is constructed with this. <c>Window</c> is
        /// get-only, so it can only go in AT CONSTRUCTION — omit it and occupancy reads zero,
        /// <c>IsUnderPressure</c> is permanently false, and the child never compacts however long it
        /// runs.
        /// </summary>
        public int? ContextWindow { get; init; }

        /// <summary>Recomputes the threshold when a type brings its own window. See <c>Create</c>.</summary>
        public Func<int?, int?>? ThresholdFor { get; init; }

        /// <summary>cxagent's own config folder, so a user-level CXAGENT.md applies everywhere.</summary>
        public string? GlobalInstructionsDir { get; init; }

        /// <summary>
        /// THE PARENT'S TOOLSET, decided (D21). A child that cannot reach the docs server is crippled
        /// for the obvious use case — "go and find out how X works" is exactly what a child is for.
        /// </summary>
        public Core.Mcp.McpToolset? Mcp { get; init; }

        /// <summary>
        /// A CHILD WORKS WHERE ITS PARENT DOES. Inherited rather than read from the process, so a
        /// session's children stay in that session's folder even when another session runs elsewhere.
        /// </summary>
        public string? WorkingDir { get; init; }

        /// <summary>How many children may call the endpoint at once. Null or 0 is unlimited.</summary>
        public int? MaxConcurrentAgents { get; init; }

        /// <summary>
        /// The same runtime with a type's provider and window substituted — a NEW record, never a
        /// mutation of this one.
        ///
        /// <para>BOTH TOGETHER OR NEITHER. Pairing provider A with the session's window is the exact
        /// failure the window field documents: the child measures pressure against a ceiling its
        /// model does not have. The threshold follows the window for the same reason.</para>
        /// </summary>
        public SubAgentRuntime With(ILlmProvider provider, int? contextWindow, string? instanceName)
            => this with
            {
                Provider = provider,
                ContextWindow = contextWindow,
                CompressAbove = ThresholdFor?.Invoke(contextWindow) ?? CompressAbove,

                // THE NAME MOVES WITH THE PROVIDER, for the same reason the window does: a child on
                // another entry attributed under the parent's name is spend recorded against a model
                // it never called.
                InstanceName = instanceName,
            };
    }

    // NOT readonly: /model swaps the session's provider in place, and a child with no provider of
    // its own inherits from here. Left captured, every sub-agent kept talking to the model the
    // session started on — confirmed in the usage archive, where every explore run after a switch
    // still recorded the old instance, and contradicted by the switch notice's own promise that
    // "sub-agents use this too unless their type names another provider".
    private SubAgentRuntime _runtime;

    /// <summary>
    /// Points future children at a different default model — see <see cref="Agent.SwapProvider"/>.
    ///
    /// <para>FUTURE ONLY. A child already running keeps the provider it was built with: it holds its
    /// own conversation with that model, and moving it mid-flight would send half a dialogue to one
    /// endpoint and half to another.</para>
    ///
    /// <para>A TYPE THAT NAMES ITS OWN PROVIDER IS UNAFFECTED, which is the whole point of naming
    /// one — the override is applied per child at spawn time, on top of whatever this holds.</para>
    /// </summary>
    public void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName) =>
        _runtime = _runtime.With(provider, contextWindow, instanceName);

    public SubAgentFactory(SubAgentRuntime runtime)
    {
        _runtime = runtime;

        // 0 AND NULL BOTH MEAN UNLIMITED, matching maxTurns. A semaphore of 0 would deadlock every
        // spawn forever, which is the one reading of "zero" nobody wants.
        ConcurrencySlot = runtime.MaxConcurrentAgents is > 0
            ? new SemaphoreSlim(runtime.MaxConcurrentAgents.Value)
            : null;
    }

    /// <summary>
    /// Builds one child. Its context is fresh and its own — <see cref="Agent"/> creates one when none
    /// is passed, which is the self-containment guarantee, so a child can never append to its
    /// caller's conversation.
    /// </summary>
    /// <param name="briefing">
    /// HOW to work — the config type's standing instructions, and the highest-authority text in the
    /// child's prompt (see <c>Agent.RenderBriefing</c>: "where it disagrees with anything above,
    /// follow this").
    ///
    /// <para>EMPTY UNTIL STEP 2, deliberately. There are no configured types yet, so there is nothing
    /// human-written to put here — and letting the parent fill it instead would rank a
    /// model-generated instruction above the config that does not exist yet, which is exactly the
    /// escalation D9's precedence rule exists to prevent. A parent with something to say uses
    /// <paramref name="callerContext"/>.</para>
    /// </param>
    /// <param name="callerContext">
    /// WHAT TO KNOW — situational facts the parent has and the child cannot discover: "the build is
    /// broken in IndentShift.cs, ignore it", "the regex approach was already tried". Renders below the
    /// briefing and claims no authority.
    /// </param>
    /// <param name="label">
    /// A few words naming this child FOR THE USER — the status row, and the "asked for by:" line on
    /// its permission prompts. Never sent to the model.
    /// </param>
    /// <param name="type">
    /// The resolved type, or null for a plain child on the parent's wiring.
    ///
    /// <para>EVERYTHING A TYPE DECIDES IS RESOLVED TOGETHER HERE — provider, window, threshold and
    /// turn cap. They cannot be split: a child given one provider and another's window sees
    /// IsUnderPressure as permanently false (AgentContext returns false for a MISSING window, never
    /// for a wrong one), never compacts, and dies on a provider overflow instead.</para>
    /// </param>
    /// <summary>
    /// The session's ledger, for a caller that must decide whether a child should start at all.
    ///
    /// <para>Exposed rather than duplicated: it is the SAME instance every child records into, so a
    /// budget check here and the spend it is checking cannot disagree.</para>
    /// </summary>
    public TokenLedger Ledger => _runtime.Ledger;

    /// <summary>
    /// Bounds how many children run at once, or null for unbounded — the default.
    ///
    /// <para>UNCAPPED UNLESS CONFIGURED. A cap picked without evidence throttles every user to guard
    /// against a problem none of them may have, and cxagent cannot discover what its endpoint
    /// tolerates. The barrier remains the real invariant: no child outlives its turn, however many
    /// there are.</para>
    ///
    /// <para>Held here rather than in the spawner because a session has one, whatever spawns come
    /// and go.</para>
    /// </summary>
    public SemaphoreSlim? ConcurrencySlot { get; }

    /// <summary>Where children resolve relative paths — the session's directory. Exposed so the
    /// spawner can check whether a planner wrote the file it was told to write, without the parent
    /// having to trust a line of the child's own text.</summary>
    public string? WorkingDir => _runtime.WorkingDir;

    /// <param name="parentAgentId">
    /// The spawning agent's id, so the child's logs nest beneath it. Null keeps the flat layout —
    /// only the tests that predate nesting pass nothing.
    /// </param>
    public SubAgent Create(string? briefing = null, string? callerContext = null, string? label = null,
        AgentType? type = null, string? parentAgentId = null)
    {
        var sink = new BufferedChatSink();
        var jobs = new BufferedJobPanel();

        // A TYPE'S PROVIDER BRINGS ITS OWN WINDOW, or neither is used — and the threshold follows
        // the window, since it is derived from it. Falling back per-field would pair provider A with
        // the session's window, which is the exact failure the runtime's own doc describes.
        //
        // A NEW RECORD PER CHILD, never a mutation of the shared one: two children spawned in one
        // turn must not be able to see each other's provider.
        var runtime = type?.Provider is not null
            ? _runtime.With(type.Provider, type.ContextWindow, type.InstanceName)
            : _runtime;

        var provider = runtime.Provider;
        var window = runtime.ContextWindow;
        var compressAbove = runtime.CompressAbove;

        // NULL INHERITS, 0 IS UNBOUNDED — and Agent already translates 0, so this passes it through
        // rather than re-implementing the rule in a second place.
        var maxTurns = type?.MaxTurns ?? _runtime.MaxTurns;

        var agent = new Agent(
            provider,
            _runtime.Plugins,
            _runtime.Ledger,
            // BOTH BUFFERED, and both are required. A buffered chat sink with the parent's job panel
            // still leaks a row per tool call into the parent's transcript.
            sink,
            jobs,
            // ITS OWN LOG DIRECTORY, keyed by the child's id and NESTED UNDER THE PARENT'S. The only
            // surface on which a finished child is inspectable after the fact — and while it sat at
            // the top level it was indistinguishable from a session, so `ls -t` showed a child above
            // the parent that spawned it and the newest directory was often not the newest session.
            parentAgentId is null ? _runtime.Logs : _runtime.Logs?.Under(parentAgentId),
            maxTurns,
            compressAbove: compressAbove,
            // A FRESH CONTEXT WITH THE WINDOW SET. Not the parent's: two agents sharing one context
            // is the failure the whole design exists to prevent.
            context: new AgentContext(window),
            workingDir: _runtime.WorkingDir,
            instanceName: runtime.InstanceName,
            globalInstructionsDir: _runtime.GlobalInstructionsDir,
            mcp: _runtime.Mcp,
            // THE TYPE'S BRIEFING WINS over the parameter: the parameter is the caller saying what
            // this child is for, and a type is a human in config saying how that work is done (D9).
            briefing: string.IsNullOrWhiteSpace(type?.Briefing) ? briefing : type!.Briefing,
            callerContext: callerContext,
            label: label,

            // THE CHILD'S OWN SYSTEM PROMPT (D24). Without this it would be handed the session
            // prompt unchanged — told about /clear and /compress it cannot run, for a user it does
            // not have, and never told that its final message is the entire answer.
            isSubAgent: true);

        // NOTE WHAT IS NOT PASSED, because both absences are load-bearing:
        //
        //   * NO SESSION STORE — Agent has no such parameter at all. Only AgentHost persists, which
        //     is precisely why a child must not be built through one.
        //
        //   * NO SPAWNER — a child constructed without one structurally CANNOT nest. That is what
        //     makes "no sub-agents of sub-agents" true rather than aspirational: it is not a rule the
        //     child is asked to follow, it is a tool it was never given.
        // instance:model, THE SAME LABEL THE LEDGER KEYS BY. A bare model name is ambiguous — two
        // instances can serve one model with different endpoints and windows — and this row is
        // written to the same history database the session rows are, so the two must agree or a
        // stat that groups them reads one model as two.
        return new SubAgent(agent, sink, jobs,
            TypeName: type?.Name ?? AgentTypeCatalog.DefaultTypeName,
            ModelId: runtime.InstanceName is { Length: > 0 } instance
                ? $"{instance}:{provider.ModelId}"
                : provider.ModelId);
    }
}
