using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Agents;

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
    string TypeName, string? ModelId)
{
    /// <summary>
    /// The child's observer: its two buffers, and any view of it — an agent tab — attached and detached as it is
    /// opened and closed. See <see cref="ChildFeed"/>.
    /// </summary>
    public required ChildFeed Feed { get; init; }

    /// <summary>The folder it works in, relative to its parent's — null when it works where the parent
    /// does. The form every surface shows and the envelope stores.</summary>
    public string? Root { get; init; }
}

/// <summary>
/// A child, paired with the parent row that started it — what <see cref="Agent.ChildSpawned"/>
/// carries.
/// </summary>
/// <param name="JobId">
/// The PARENT's job id: the key the row on screen is addressed by, from the moment it appears.
///
/// <para>NOT the child's agent id, which the child already carries and which a finished row reads
/// back out of the envelope instead. While the child is still running there is no envelope, so this
/// is the only key a live row can join on.</para>
/// </param>
/// <param name="Child">The child itself, live — its <c>Jobs</c> panel is mutated in place as it
/// works, so a holder of this can read what it is doing at any moment.</param>
public sealed record SpawnedChild(string JobId, SubAgent Child);

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
    /// in <see cref="SubAgentFactory.Create(ChildRequest)"/> and is deliberately NOT here. A child is not a copy of
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

        /// <summary>
        /// The session's tools, which a child inherits less what only the session may have — <see cref="Create(ChildRequest)"/>
        /// takes out the spawner and the model-facing commands, and composes the selection.
        ///
        /// <para>THE PARENT'S REGISTRY, not a narrowed copy, and THE PARENT'S MCP TOOLSET (D21): a child that cannot
        /// reach the docs server is crippled for the obvious use case — "go and find out how X works" is exactly
        /// what a child is for.</para>
        ///
        /// <para>INJECTED TOOLS ARE KEPT, unlike spawn and ask. Those are withheld because a child has no user to
        /// answer and no spawner to nest with; an injected tool usually answers to neither, since a child edits
        /// files exactly as its parent does. THE FILTERING IS <see cref="Agent"/>'S, NOT THIS RECORD'S: a tool that
        /// draws for a person declares <c>OfferToSubAgents => false</c> and the Agent constructor withholds it —
        /// beside <c>_askUser</c>, and for the same reason it is enforced there: the guarantee then holds for any
        /// path that builds a child, not only this one. So these are what a child COULD have; what it is offered is
        /// decided one layer down. The live <c>Dynamic</c> source passes through whole for the same reason, and the
        /// plugins' instructions with it, read from the same source on the same turn so a plugin's text arrives and
        /// departs with its tools.</para>
        ///
        /// <para>THE SELECTION IS THE SESSION'S (S1 composed with S2), NOT THE TURN'S. This record is built once
        /// when the session is wired, so a per-request S3 cannot live here — it rides the spawn call instead. A
        /// child gets Then(this, turn's, type's), composed at the moment it is created.</para>
        /// </summary>
        public required AgentTools Tools { get; init; }

        /// <summary>
        /// The session's guards, which a child inherits with its policy narrowed to its own folder and no way to ask
        /// the user — see <see cref="Create(ChildRequest)"/>.
        ///
        /// <para>THE POLICY, so a child's MCP calls can be judged. A child using an MCP tool is the common case — an
        /// explore agent calling a docs server — and without it the gate refuses the call for want of a policy,
        /// exactly as it would for the parent. See <see cref="AgentGuards.Policy"/>.</para>
        ///
        /// <para>THE CLASSIFIER THE PARENT SPECULATES WITH, so a sub-agent's tool calls warm the classifier's cache
        /// too rather than every delegated call paying its round trip synchronously at the gate — the exact cost the
        /// parent avoids by starting the call the moment it parses a tool call. Null wherever no classifier is
        /// reachable (headless runs, most tests, a fixed AllowAll/DenyAll gate): <see cref="Agent"/> treats a null
        /// classifier as "never speculate", the same graceful fallback the parent gets.</para>
        /// </summary>
        public AgentGuards Guards { get; init; } = new();

        /// <summary>
        /// Where a child's tools reach the CHILD, not its parent.
        ///
        /// <para>INHERITED BECAUSE A CHILD IS THE CASE THE PORT EXISTS FOR. A child inherits the whole
        /// plugin surface, and every plugin client is built once per session against the session's own
        /// agent — so a plugin tool a child calls woke the parent, with no error anywhere. The port
        /// resolves by the agent id a tool call carries, and each Agent stamps its own, so passing the
        /// same instance to every child is what makes each one reachable as itself.</para>
        /// </summary>
        public IAgentDelivery? Delivery { get; init; }

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
        /// A CHILD WORKS WHERE ITS PARENT DOES. Inherited rather than read from the process, so a
        /// session's children stay in that session's folder even when another session runs elsewhere.
        /// </summary>
        public string? WorkingDir { get; init; }

        /// <summary>How many children may call the endpoint at once. Null or 0 is unlimited.</summary>
        public int? MaxConcurrentAgents { get; init; }

        /// <summary>
        /// Whether a spawn may run in the background — returning a receipt at once and reporting to its
        /// parent when it ends. True unless the front end has one answer to give.
        ///
        /// <para>HEADLESS TURNS IT OFF (spec decision 8): a run that prints one answer and exits has
        /// nobody to deliver a later report to, so a spawn there must wait and put its work in that
        /// answer.</para>
        /// </summary>
        public bool AllowBackground { get; init; } = true;

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
    // its own inherits from here. Left captured, every sub-agent keeps talking to the model the
    // session started on — the usage archive then records the old instance for every explore run
    // after a switch, contradicting the switch notice's own promise that "sub-agents use this too
    // unless their type names another provider".
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
        _limit = runtime.MaxConcurrentAgents is > 0 ? runtime.MaxConcurrentAgents : null;
        ConcurrencySlot = _limit is { } limit ? new SemaphoreSlim(limit) : null;
    }

    /// <summary>The limit <see cref="ConcurrencySlot"/> was built for; null for unbounded.</summary>
    private int? _limit;

    /// <summary>
    /// Builds one child. Its context is fresh and its own — <see cref="Agent"/> creates one when none
    /// is passed, which is the self-containment guarantee, so a child can never append to its
    /// caller's conversation.
    /// </summary>    /// <summary>
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
    public SemaphoreSlim? ConcurrencySlot { get; private set; }

    /// <summary>
    /// A new limit for spawns from now on. A NEW SEMAPHORE, not a resized one: children already holding
    /// a slot release it into the semaphore they took it from, so the old count stays consistent.
    ///
    /// <para>ONLY WHEN THE LIMIT CHANGED. A new semaphore starts with every slot free while running
    /// children still hold slots in the old one, so swapping on a save that left the limit alone would
    /// let a second full batch start beside the first.</para>
    /// </summary>
    public void SwapLimit(int? maxConcurrentAgents)
    {
        var limit = maxConcurrentAgents is > 0 ? maxConcurrentAgents : null;
        if (limit == _limit) return;
        _limit = limit;
        ConcurrencySlot = limit is { } n ? new SemaphoreSlim(n) : null;
    }

    /// <summary>Where children resolve relative paths — the session's directory. Exposed so the
    /// spawner can check whether a planner wrote the file it was told to write, without the parent
    /// having to trust a line of the child's own text.</summary>
    public string? WorkingDir => _runtime.WorkingDir;

    /// <summary>The session's selection, for a caller that must predict what a child will have
    /// before creating one — see SubAgentSpawner.ChildCanWrite.</summary>
    internal Jobs.ToolSelection? SessionToolSelection => _runtime.Tools.Selection;

    /// <summary>The port a background child's report reaches its parent through; null where nothing
    /// can be told, which keeps every spawn in the foreground.</summary>
    internal IAgentDelivery? Delivery => _runtime.Delivery;

    /// <summary>See <see cref="SubAgentRuntime.AllowBackground"/>.</summary>
    internal bool AllowBackground => _runtime.AllowBackground;

    /// <summary>The session's policy, whose boundary a spawn's root is judged against.</summary>
    internal Permissions.PermissionPolicy? Policy => _runtime.Guards.Policy;


    /// <summary>
    /// What one child is built from.
    ///
    /// <para>A RECORD, NOT SIX OPTIONAL ARGUMENTS. They grew one per feature, three of them strings in a
    /// row, and a seventh would have been a fourth — two transposed strings compile and build the wrong
    /// child. Named members make that a build error.</para>
    /// </summary>
    public sealed record ChildRequest
    {
        /// <summary>The child's highest-authority text, or null to take its type's.</summary>
        public string? Briefing { get; init; }

        /// <summary>What the parent already knew and passed down.</summary>
        public string? CallerContext { get; init; }

        /// <summary>A short name for the child's row in the UI.</summary>
        public string? Label { get; init; }

        /// <summary>Which agent type to build, or null for the general one.</summary>
        public AgentType? Type { get; init; }

        /// <summary>The spawning agent's id, so the child's logs nest beneath it. Null keeps the flat
        /// layout — only the tests that predate nesting pass nothing.</summary>
        public string? ParentAgentId { get; init; }

        /// <summary>The parent's per-request selection, composed onto the session's for this child. Null
        /// when the parent is not running under one.</summary>
        public Jobs.ToolSelection? TurnTools { get; init; }

        /// <summary>The folder the child works in, when the spawn named one; null for the parent's own.</summary>
        public ChildRoot? Root { get; init; }
    }

    /// <summary>A plain child: the general type, no briefing, nothing passed down.</summary>
    public SubAgent Create() => Create(new ChildRequest());

    /// <summary>Builds the child <paramref name="request"/> describes.</summary>
    public SubAgent Create(ChildRequest request)
    {
        var briefing = request.Briefing;
        var callerContext = request.CallerContext;
        var label = request.Label;
        var type = request.Type;
        var parentAgentId = request.ParentAgentId;
        var turnTools = request.TurnTools;
        var sink = new BufferedChatSink();
        var jobs = new BufferedJobPanel();

        // ONE OBSERVER OVER BOTH BUFFERS, so a tab can attach to the child later and be replayed what it missed.
        // The buffers are told first and stay this child's record either way.
        var feed = new ChildFeed(sink, jobs);

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
        // THE ENTRY IT RUNS ON, as `name:model` — computed before the child is built, because the child's
        // permission prompt names it too (see the comment at the SubAgent below for why name:model).
        var entry = runtime.InstanceName is { Length: > 0 } instance
            ? $"{instance}:{provider.ModelId}"
            : provider.ModelId;
        var window = runtime.ContextWindow;
        var compressAbove = runtime.CompressAbove;

        var agent = new Agent(new AgentSetup
        {
            Model = new AgentModel(provider, _runtime.Ledger) { InstanceName = runtime.InstanceName },

            // BOTH OBSERVERS ARE THE FEED, whose buffers are the child's own. A buffered chat sink with the parent's
            // job panel still leaks a row per tool call into the parent's transcript.
            Outputs = new AgentOutputs(feed, feed)
            {
                // ITS OWN LOG DIRECTORY, keyed by the child's id and NESTED UNDER THE PARENT'S. The only
                // surface on which a finished child is inspectable after the fact — and while it sat at
                // the top level it was indistinguishable from a session, so `ls -t` showed a child above
                // the parent that spawned it and the newest directory was often not the newest session.
                Logs = parentAgentId is null ? _runtime.Logs : _runtime.Logs?.Under(parentAgentId),

                // THE SAME PORT THE PARENT GETS, resolving by agent id — so a tool this child calls
                // reaches THIS child rather than the session's agent. See SubAgentRuntime.Delivery.
                Delivery = _runtime.Delivery,
            },

            Identity = new AgentIdentity
            {
                // ITS OWN FOLDER, when the spawn named one: where its tools start, and — through a policy
                // narrowed to it — where its permission boundary is drawn.
                WorkingDir = request.Root?.Absolute ?? _runtime.WorkingDir,

                // THE CHILD'S OWN SYSTEM PROMPT (D24). Without this it would be handed the session
                // prompt unchanged — told about /clear and /compress it cannot run, for a user it does
                // not have, and never told that its final message is the entire answer.
                IsSubAgent = true,
                Label = label,

                // parentAgentId IS THE SESSION'S ID, NEVER AN INTERMEDIATE PARENT'S: a child cannot
                // spawn (no spawner — see Tools below), so whatever calls Create is always the session
                // itself. Threaded through so TaskTools' kill rule ("the session may kill any job; a
                // sub-agent only its own") can tell this child apart from the session it belongs to.
                SessionId = parentAgentId,
                RunsOn = $"{type?.Name ?? AgentTypeCatalog.DefaultTypeName} on {entry}",
                Root = request.Root?.Relative,
            },

            // THE SESSION'S TOOLS, LESS WHAT ONLY THE SESSION MAY HAVE. The runtime's group is the session's own,
            // so what a child must not hold is taken out here, by name:
            //
            //   * NO SPAWNER — a child constructed without one structurally CANNOT nest. That is what makes "no
            //     sub-agents of sub-agents" true rather than aspirational: it is not a rule the child is asked to
            //     follow, it is a tool it was never given. Agent does not refuse one, so nesting stays a change to
            //     this line rather than to Agent.
            //   * NO MODEL-FACING COMMANDS — they name slash commands for a user, and a child has none.
            //
            // INJECTED TOOLS PASS IN FULL; the Agent constructor decides what a child is actually offered, dropping
            // any whose OfferToSubAgents is false — a tool that renders for a person would draw into a buffer
            // nothing displays, succeed, and be discarded.
            //
            // S1∘S2 FROM THE RUNTIME, S3 FROM THE CALL, S4 FROM THE TYPE — composed in that order, here, because
            // this is the only place all three are known. The type's terms go LAST so a `+` in it can reopen what a
            // narrower session closed; composing onto a resolved set instead would make S4 narrowing-only, silently.
            Tools = _runtime.Tools with
            {
                Spawner = null,
                ModelFacingCommands = null,
                Selection = Jobs.ToolSelection.Then(
                    Jobs.ToolSelection.Then(_runtime.Tools.Selection, turnTools), type?.Tools),
            },

            // THE SESSION'S GUARDS, with the policy narrowed to the child's own folder when it has one, and NO WAY
            // TO ASK THE USER: a child has nobody listening. The classifier passes straight through — Agent's own
            // null check is what makes a null one "never speculate" rather than a crash.
            Guards = _runtime.Guards with
            {
                Policy = request.Root is { } root ? _runtime.Guards.Policy?.NarrowedTo(root.Absolute) : _runtime.Guards.Policy,
                AskUser = null,
            },

            // NO StartedOn: a child's prompt is dated the day it is built.
            Brief = new AgentBrief
            {
                // THE TYPE'S BRIEFING WINS over the request's: the request is the caller saying what this child
                // is for, and a type is a human in config saying how that work is done (D9).
                Briefing = string.IsNullOrWhiteSpace(type?.Briefing) ? briefing : type!.Briefing,
                CallerContext = callerContext,
                GlobalInstructionsDir = _runtime.GlobalInstructionsDir,
            },

            // NULL INHERITS, 0 IS UNBOUNDED — Agent translates 0, so this passes it through rather than
            // re-implementing the rule in a second place.
            Limits = new AgentLimits(type?.MaxTurns ?? _runtime.MaxTurns) { CompressAbove = compressAbove },

            // A FRESH CONTEXT WITH THE WINDOW SET. Not the parent's: two agents sharing one context
            // is the failure the whole design exists to prevent.
            Conversation = new AgentContext(window),
        });

        // THE TRANSCRIPT'S TOOL ROWS ARE DRAWN FROM FINISHED CALLS, which reach no observer — so the feed hears them
        // here, for a tab that opens later.
        agent.ToolCallFinished += feed.CallFinished;

        // NO SESSION STORE — Agent has no such member at all. Only AgentHost persists, which is precisely
        // why a child must not be built through one.
        //
        // instance:model, THE SAME LABEL THE LEDGER KEYS BY. A bare model name is ambiguous — two
        // instances can serve one model with different endpoints and windows — and this row is
        // written to the same history database the session rows are, so the two must agree or a
        // stat that groups them reads one model as two.
        return new SubAgent(agent, sink, jobs,
            TypeName: type?.Name ?? AgentTypeCatalog.DefaultTypeName,
            ModelId: entry)
        {
            Feed = feed,
            Root = request.Root?.Relative,
        };
    }
}
