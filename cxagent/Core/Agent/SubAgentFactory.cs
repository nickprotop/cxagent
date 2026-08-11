using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Agent;

/// <summary>A child, its captured output, and the panel holding its rows.</summary>
/// <param name="Agent">The child itself. Its <c>Id</c> addresses its row and names its log directory.</param>
/// <param name="Sink">Everything it said, for inspection after the fact.</param>
/// <param name="Jobs">Every tool it called.</param>
public sealed record SubAgent(Agent Agent, BufferedChatSink Sink, BufferedJobPanel Jobs);

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
    private readonly ILlmProvider _provider;
    private readonly PluginRegistry _plugins;
    private readonly TokenLedger _ledger;
    private readonly LogFileManager? _logs;
    private readonly int _maxTurns;
    private readonly int? _compressAbove;
    private readonly int? _contextWindow;

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
    private readonly Func<int?, int?>? _thresholdFor;
    private readonly string? _globalInstructionsDir;
    private readonly Core.Mcp.McpToolset? _mcp;

    /// <param name="ledger">
    /// THE PARENT'S, DELIBERATELY (D7). A child's spend is the session's spend: the budget the user
    /// set covers the work, not the agent that happened to do it, and a child with its own ledger
    /// spends against nothing and never trips the breach warning.
    ///
    /// <para>GIVEN rather than inherited by construction order, which is the whole point of hoisting
    /// ledger creation to the composition root. When ledgers become per-model, the caller resolves
    /// one by model and hands it here; this signature does not change.</para>
    /// </param>
    /// <param name="maxTurns">
    /// THE PARENT'S CEILING, not a smaller number invented here. A figure chosen for children is the
    /// same mistake as the old <c>MaxWorkerTurns: 10</c>: it caps mid-work and returns a salvage
    /// summary that the caller reads as a finished answer. Zero means unbounded, and
    /// <see cref="Agent"/> translates it.
    /// </param>
    /// <param name="compressAbove">
    /// Must be the caller's computed threshold — <c>EffectiveCompressThreshold(window) ??
    /// DefaultCompressThreshold</c> — never a literal. Two copies of that number desynchronise the
    /// moment either moves, and a child that never compresses dies on a context overflow instead.
    /// </param>
    /// <param name="contextWindow">
    /// The child's own <see cref="AgentContext"/> is constructed with this. <c>Window</c> is
    /// get-only, so it can only go in AT CONSTRUCTION — omit it and occupancy reads zero,
    /// <c>IsUnderPressure</c> is permanently false, and the child never compacts however long it
    /// runs.
    /// </param>
    /// <param name="mcp">
    /// THE PARENT'S TOOLSET, decided (D21). A child that cannot reach the docs server is crippled for
    /// the obvious use case — "go and find out how X works" is exactly what a child is for. This is
    /// also the first shared mutable thing a child touches: <c>McpClient.WriteAsync</c> holds no lock
    /// on a shared stdio pipe, which is fine while one child runs at a time and is step 3's problem.
    /// </param>
    public SubAgentFactory(ILlmProvider provider, PluginRegistry plugins, TokenLedger ledger,
        LogFileManager? logs, int maxTurns, int? compressAbove, int? contextWindow,
        string? globalInstructionsDir, Core.Mcp.McpToolset? mcp,
        Func<int?, int?>? thresholdFor = null)
    {
        _provider = provider;
        _plugins = plugins;
        _ledger = ledger;
        _logs = logs;
        _maxTurns = maxTurns;
        _compressAbove = compressAbove;
        _contextWindow = contextWindow;
        _thresholdFor = thresholdFor;
        _globalInstructionsDir = globalInstructionsDir;
        _mcp = mcp;
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
    public SubAgent Create(string? briefing = null, string? callerContext = null, string? label = null,
        AgentType? type = null)
    {
        var sink = new BufferedChatSink();
        var jobs = new BufferedJobPanel();

        // A TYPE'S PROVIDER BRINGS ITS OWN WINDOW, or neither is used. Falling back per-field would
        // pair provider A with the session's window, which is the exact failure above.
        var provider = type?.Provider ?? _provider;
        var window = type?.Provider is not null ? type.ContextWindow : _contextWindow;

        // AND THE THRESHOLD FOLLOWS THE WINDOW. It is derived from the window (80% of it when config
        // states no explicit figure), so a window that moves and a threshold that does not is a child
        // compacting against the wrong ceiling.
        var compressAbove = type?.Provider is not null
            ? _thresholdFor?.Invoke(window) ?? _compressAbove
            : _compressAbove;

        // NULL INHERITS, 0 IS UNBOUNDED — and Agent already translates 0, so this passes it through
        // rather than re-implementing the rule in a second place.
        var maxTurns = type?.MaxTurns ?? _maxTurns;

        var agent = new Agent(
            provider,
            _plugins,
            _ledger,
            // BOTH BUFFERED, and both are required. A buffered chat sink with the parent's job panel
            // still leaks a row per tool call into the parent's transcript.
            sink,
            jobs,
            // ITS OWN LOG DIRECTORY, keyed by the child's id. The only surface on which a finished
            // child is inspectable after the fact.
            _logs,
            maxTurns,
            compressAbove: compressAbove,
            // A FRESH CONTEXT WITH THE WINDOW SET. Not the parent's: two agents sharing one context
            // is the failure the whole design exists to prevent.
            context: new AgentContext(window),
            globalInstructionsDir: _globalInstructionsDir,
            mcp: _mcp,
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
        return new SubAgent(agent, sink, jobs);
    }
}
