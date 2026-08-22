using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;
using CxAgent.Core.Helpers;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Agents;

/// <summary>
/// The UI's side of one <see cref="Agent"/>: owns it for the session, feeds it what the user types,
/// and republishes what it reports as the events the status bar and panels bind to.
///
/// <para>Every sink call is the UI-update seam — marshalling is the sink's responsibility, not
/// this type's.</para>
/// </summary>
public sealed class AgentHost : IDisposable
{
    // NOT readonly: SwapProvider rebinds it with `with` so SpendLabel and anything else reading the
    // runtime describes the model actually in use. Rebuilding the host to change one field was what
    // /model does.
    private AgentRuntime _runtime;
    private readonly SessionStores _stores;
    private readonly ISessionObserver _sink;

    /// <summary>Mints turn ids for this host and the agent it drives — ONE counter for the pair, so
    /// two minters cannot number the same transcript. See <see cref="Agent.MintTurnId"/>.</summary>
    /// <summary>
    /// The session's turn-id minter, or null for a bare host.
    ///
    /// <para>IDS NUMBER A TRANSCRIPT, and the transcript is the session's — so the counter is too.
    /// This class held one and the agent held another, both minting into the same sink, which
    /// produced <c>1, 2, 1</c> for a single exchange.</para>
    /// </summary>
    private Func<ChatMessageId>? _mintTurnId;

    /// <summary>Takes the session's minter, and passes it to the agent. Called by
    /// <c>Session.ReplaceHost</c>.</summary>
    public void UseTurnIds(Func<ChatMessageId> mint)
    {
        _mintTurnId = mint;
        _agent.MintTurnId = mint;
    }
    private readonly IToolObserver _jobPanel;


    /// <summary>
    /// cxagent's own config directory, where a user-level CXAGENT.md may sit — or null when there is
    /// none to read.
    ///
    /// <para>OUR CONFIG FOLDER ONLY — whatever <c>AppPaths.ConfigDir</c> resolves to on this OS, not a
    /// hardcoded <c>~/.config</c>. Another product's user-level file, such as
    /// <c>~/.claude/CLAUDE.md</c>, is not read: honouring it would mean silently obeying instructions
    /// written for a different agent with different tools. A repo's CLAUDE.md is different — it
    /// describes the PROJECT, so it is read where the project is.</para>
    /// </summary>

    /// <summary>Connected MCP servers, passed straight to the agent. Null when none are configured.</summary>

    /// <summary>What this host's agent was created to do, or null for a plain session. Fixed here so
    /// an F5 re-wire rebuilds the agent with the SAME briefing rather than silently dropping it.</summary>

    /// <summary>
    /// The subprocesses behind <c>_runtime.Mcp</c>, held only so <see cref="Dispose"/> can end them.
    ///
    /// <para>Separate from the toolset because ownership and use are different concerns: the toolset
    /// is asked what tools exist and never asked to shut anything down, and the host is the thing
    /// with a lifetime to match.</para>
    /// </summary>
    private readonly IReadOnlyList<IAsyncDisposable> _mcpServers = [];

    /// <summary>
    /// The resume buffer, or null when this session is not persisted.
    ///
    /// <para>Written on the turn boundary rather than at exit — a crash is precisely when exit does
    /// not happen. Every call into it is best-effort inside the store itself, so a disk that is full
    /// or a database that is locked costs the ability to resume and nothing else.</para>
    /// </summary>

    /// <summary>
    /// Usage history — a DIFFERENT database from <c>_stores.Resume</c>, and optional for the same
    /// reason: a session that cannot record statistics is unaffected in every way that matters.
    /// </summary>

    /// <summary>Turns this session has completed, for the history row. Not derived from the message
    /// count, which compaction rewrites — a compacted session would appear to have run backwards.
    /// </summary>
    private int _turns;

    /// <summary>When this session began. `updated_at` alone cannot give a duration, and duration is
    /// the axis every "where did my week go" view wants.</summary>
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;


    /// <summary>
    /// The agent this runner drives, built once and kept.
    ///
    /// </summary>
    private readonly Agent _agent;


    // The whole settings record rather than the one field read today: the compaction threshold is
    // derived from it per-agent (see BuildAgent), and the turn ceiling reads MaxTurns.

    /// <summary>How this session's agent spawns children, or null when sub-agents are not wired.
    /// Forwarded to the agent in BuildAgent — a field it must be, since BuildAgent runs after the
    /// constructor body.</summary>

    /// <summary>The mode this session starts in — from the command line. Applied to the agent in
    /// BuildAgent, after which <see cref="Mode"/> is the live value.</summary>
    // BOTH AXES, so the edits axis is not dropped between the runtime and the agent — a session
    // started in always-ask must not build its agent with the accept-edits default.
    private readonly WorkingMode _mode;

    /// <summary>
    /// The folder this session runs in, recorded with every saved turn so resume can be scoped to it.
    ///
    /// <para>Null in tests and anywhere that does not persist — a session saved without one is never
    /// OFFERED for resume, which is the safe direction: a row that cannot say where it came from
    /// could have come from anywhere.</para>
    /// </summary>

    /// <summary>
    /// The active provider instance's context window in tokens (ProviderInstanceConfig.ContextWindow —
    /// P11 Task 1), threaded through from ResolvedConfig at construction time rather than read off
    /// <c>_runtime.Provider</c> itself: ILlmProvider exposes identity (ProviderId/ModelId) but not this
    /// config-only number, and adding it to the interface would ripple into every vendor driver and
    /// test double for a value only ConfigResolver's config lookup actually has. Null when the user
    /// hasn't set contextWindow for this instance — EffectiveCompressThreshold treats that as "unknown"
    /// and falls back to the fixed constant, per its own precedence.
    /// </summary>

    /// <summary>How this session asks the user a question, or null when nothing can ask.</summary>

    /// <summary>
    /// Whether this session works alone or may delegate. Forwarded straight to the agent, which reads
    /// it on the next prompt — no rebuild, and the conversation is untouched.
    /// </summary>
    public WorkingMode Mode
    {
        get => _agent.Mode;
        set => _agent.Mode = value;
    }

    /// <summary>Cumulative orchestrator token spend for this runner's goal(s), against the configured budget.</summary>
    public TokenLedger Ledger { get; }

    /// <summary>
    /// The agent's context, living across every prompt this runner drives.
    ///
    /// <para>It is also what gives <c>/compress</c> something to compress: the session transcript
    /// holds only prompts and answers, so compressing that reported "nothing to free" on a session
    /// measured at 58,000 tokens.</para>
    /// </summary>
    public AgentContext Context { get; }

    /// <summary>
    /// Raised every time Ledger.Record runs, carrying the new running total — the status-bar cost
    /// readout's live hook, kept here rather than as a general-purpose event on the ledger's own
    /// object model.
    /// </summary>
    public event EventHandler<int>? TokensUpdated;

    /// <summary>
    /// Raised with the last turn's INPUT tokens — how full the context actually is right now.
    ///
    /// <para>Distinct from <see cref="TokensUpdated"/>, which carries the cumulative session total.
    /// Only this figure answers "will the next turn fit": it is one turn's measurement rather than a
    /// sum, so it rises and FALLS, and in particular it falls after compression. Driving the status
    /// bar's context percentage off the cumulative total instead shows 107% of a window that is half
    /// empty, and cannot move at all when compression frees space.</para>
    /// </summary>
    public event EventHandler<int>? ContextUsedUpdated;

    /// <summary>
    /// Raised when a compression has actually shrunk the conversation.
    ///
    /// <para>The true new occupancy is not knowable until the next turn — it is only ever read from
    /// what a provider reports it received — so this says "the last reading no longer describes the
    /// conversation" rather than carrying a number. The alternative, holding the pre-compression
    /// figure on screen, is the visible bug: compress, and the gauge does not move.</para>
    /// </summary>
    public event EventHandler<(int Before, int After)>? ContextCompressed;

    /// <summary>A scaled occupancy figure after compaction — arithmetic, not a measurement.</summary>
    public event EventHandler<int>? ContextEstimatedUpdated;

    /// <summary>
    /// Republishes a turn's reported input tokens, if it is a real measurement.
    ///
    /// <para>A reported 0 is never a measurement: both wires fall back to 0 when a provider omits
    /// usage, and forwarding that would read as "the context shrank" to every gauge downstream. It is
    /// dropped rather than published, so the last REAL reading stands until another one arrives.</para>
    ///
    /// </summary>
    private void RecordInputTokens(int inputTokens)
    {
        if (inputTokens <= 0) return;
        ContextUsedUpdated?.Invoke(this, inputTokens);
    }

    /// <summary>
    /// One model turn finished; the payload is how many tool calls it made.
    ///
    /// <para>Added because the session panel's turn and tool-call counters had NO source — the
    /// method that incremented them was never called, so both read 0 for the life of a session no
    /// matter how much work it did. Token usage was not a substitute: a provider that reports no
    /// usage (a local llama.cpp build often does not) leaves that channel silent too.</para>
    /// </summary>
    public event EventHandler<int>? TurnCompleted;

    /// <summary>
    /// The agent's id, which is also the name of its log directory — what a user needs to find this
    /// session again.
    ///
    /// </summary>
    public string SessionId => _agent.Id;

    /// <summary>
    /// Has a turn been written to the resume store yet?
    ///
    /// <para>FOR THE EXIT HINT. Sessions are saved per turn, so one where nothing was said was never
    /// stored — telling the user how to resume it would hand them a command that answers "no session
    /// matches" and makes resume look broken the first time they try it.</para>
    ///
    /// <para>A restored session counts immediately: it was loaded FROM a stored row, so there is
    /// something to come back to even before this instance saves anything of its own.</para>
    /// </summary>
    public bool HasSavedTurn => _turns > 0 || _resumed;

    private readonly bool _resumed;

    /// <summary>
    /// What THIS agent has spent — the parent alone, excluding every sub-agent.
    ///
    /// <para>NOT <c>Ledger.TotalTokens</c>, which is the whole session: children record into the
    /// shared ledger deliberately, because a budget belongs to the conversation rather than to
    /// whichever agent did the work. That makes the ledger right for a budget and wrong for a status
    /// bar, where the figure sits beside an occupancy percentage that IS this agent's — a fan-out
    /// session showed a spend four times the parent's with nothing to say why.</para>
    ///
    /// <para>The session-wide view has a home: the panel's "Tokens by agent".</para>
    /// </summary>
    public (int Input, int Output) OwnSpend => _agent.Spend;

    /// <summary>
    /// The skills whose bodies are still in THIS agent's window — the parent's, not the session's.
    /// A child's are reported on the child's own row, and a child is gone by the next turn.
    /// </summary>
    public IReadOnlyList<string> LoadedSkills => _agent.LoadedSkills;

    /// <summary>
    /// The session agent's plan. A child keeps its own and it stays with the child: a worker's plan
    /// is scaffolding for one job, and pooling it into the session's would make the panel report a
    /// list nobody can act on after the child exits.
    /// </summary>
    public IReadOnlyList<TodoItem> Todos => _agent.Todos;

    /// <summary>
    /// Raised when the model rewrites its plan.
    ///
    /// <para>MID-TURN, which is why it exists at all: a plan written on the first call of a
    /// forty-turn run would otherwise not reach the panel until the whole turn ended, and "what is
    /// it doing right now" is exactly the question the panel is being asked in the meantime.</para>
    /// </summary>
    public event Action? TodosChanged
    {
        add => _agent.TodosChanged += value;
        remove => _agent.TodosChanged -= value;
    }

    /// <summary>
    /// Every finished tool call, this agent's and its children's alike.
    ///
    /// <para>A FORWARDING ACCESSOR ONTO THE AGENT, like <see cref="TodosChanged"/> above, rather
    /// than an event this host raises: the kernel is what knows a call finished, and a copy raised
    /// here would be a second thing that can fall out of step with the one history already
    /// subscribes to.</para>
    ///
    /// <para>A CHILD'S CALLS ARRIVE UNDER THE CHILD'S OWN AGENT ID. The parent forwards them
    /// unchanged (Agent.OnChildSpawned) so that a subscriber can attribute work to the agent that
    /// actually did it — which is what lets one subscription here serve every worker at once.</para>
    /// </summary>
    public event Action<ToolCallReport>? ToolCallFinished
    {
        add => _agent.ToolCallFinished += value;
        remove => _agent.ToolCallFinished -= value;
    }

    /// <summary>
    /// A child was built for a spawn row — the pairing of the row with the agent it started.
    ///
    /// <para>A FORWARDING ACCESSOR, like <see cref="ToolCallFinished"/> above and for its reason: the
    /// kernel is what builds the child, and a copy raised here would be a second thing that can fall
    /// out of step with the one a row is drawn from.</para>
    /// </summary>
    public event Action<SpawnedChild>? ChildSpawned
    {
        add => _agent.ChildSpawned += value;
        remove => _agent.ChildSpawned -= value;
    }

    /// <summary>
    /// Records that this session ended normally, so it is never offered for resume.
    ///
    /// <para>THE DISTINCTION THE WHOLE STORE TURNS ON. A row left unfinished means the process did
    /// not get to say goodbye — which is the only signal available that a session was interrupted
    /// rather than completed. Called from the composition root after the run loop returns; if the
    /// process dies before that, the row correctly stays unfinished.</para>
    /// </summary>
    public void MarkSessionFinished() => _stores.Resume?.MarkFinished(_agent.Id);

    /// <summary>
    /// <c>orchestrator.maxTurns</c> as the user set it, or null when they did not — what that
    /// absence MEANS is <see cref="CeilingFor"/>'s to decide, not the parser's.
    /// </summary>
    public int? ConfiguredMaxTurns { get; init; }

    /// <summary>Raises <see cref="TurnCompleted"/>. Called by the loop, which is the only thing that
    /// knows a turn boundary.</summary>
    internal void OnTurnCompleted(int toolCalls) => TurnCompleted?.Invoke(this, toolCalls);    /// <summary>
    /// Where a session's record goes. Both optional, and both are the composition root's to own.
    ///
    /// <para>SHARED ACROSS SESSIONS, DELIBERATELY. These are keyed by agent id, not by session, so
    /// two sessions in one process write to one database rather than fighting over two handles to
    /// it. If a session ever needs its own, the boundary is wrong.</para>
    /// </summary>
    public sealed record SessionStores
    {
        /// <summary>Every completed turn lands here, so a crash leaves something to resume from.</summary>
        public Storage.SqliteSessionStore? Resume { get; init; }

        /// <summary>The archive <c>/stats</c> reads — a separate database that outlives the session.</summary>
        public Storage.UsageHistoryStore? History { get; init; }

        /// <summary>Per-job logs, nested under the agent that produced them.</summary>
        public Storage.LogFileManager? Logs { get; init; }
    }

    /// <summary>
    /// Everything the host FORWARDS to the agent it builds.
    ///
    /// <para>NINE OF THE NINETEEN PARAMETERS WERE PASS-THROUGH — stored once, read once, handed to
    /// <see cref="BuildAgent"/>, never used by the host itself. They were on the constructor because
    /// <see cref="Agent"/> needs them and the host is the only thing that builds one.</para>
    ///
    /// <para>THE FAILURE THIS REMOVES is named in BuildAgent's own comment: omitting a forwarded
    /// argument "compiles perfectly and produces a session whose agent silently has no spawn tool."
    /// A record cannot be half-passed.</para>
    ///
    /// <para>Immutable, like <see cref="SubAgentFactory.SubAgentRuntime"/> and for the same reason:
    /// a host that could mutate what it forwards could change an agent's tools under it.</para>
    /// </summary>
    public sealed record AgentRuntime
    {
        public required ILlmProvider Provider { get; init; }
        public required JobRegistry Executors { get; init; }

        /// <summary>
        /// Which <c>providers</c> entry this is, for spend attribution and the UI's label.
        ///
        /// <para>Two entries can serve the SAME model on different endpoints with different windows,
        /// so <c>instance:model</c> is the smallest thing that identifies what was actually talked
        /// to. The driver cannot supply it — an instance name belongs to config, not to a driver.</para>
        /// </summary>
        public string? InstanceName { get; init; }

        /// <summary>Where the session works. Data, never the process's own directory.</summary>
        public string? WorkingDir { get; init; }

        /// <summary>cxagent's config folder, so a user-level CXAGENT.md applies wherever they work.</summary>
        public string? GlobalInstructionsDir { get; init; }

        /// <summary>The real window, so compaction derives its threshold from actual headroom.</summary>
        public int? ContextWindow { get; init; }

        /// <summary>Turn cap and compaction threshold. Null means nothing was configured.</summary>
        public OrchestratorSettings? Orchestrator { get; init; }

        public Core.Mcp.McpToolset? Mcp { get; init; }

        /// <summary>
        /// The session's permission policy, carried so MCP calls can be judged.
        ///
        /// <para>THE TOOLSET CANNOT HOLD IT. An McpToolset is manager-level — one per process,
        /// shared by every session — while a policy is per-session, so a field there would judge one
        /// session's call by another session's rules. It travels with the CALL instead, the same way
        /// Requester does.</para>
        ///
        /// <para>NULL IS REFUSED BY THE GATE, which is what shipped: nothing passed a policy, so
        /// every MCP call was denied with "this request carried no session policy".</para>
        /// </summary>
        public Permissions.PermissionPolicy? Policy { get; init; }

        /// <summary>
        /// TASK 11: the same classifier <see cref="Permissions.PermissionDecider"/> consults, so the
        /// agent can start warming its cache the moment a tool call is PARSED rather than waiting for
        /// <see cref="Permissions.PermissionGatedExecutor"/> to ask for a verdict it needs synchronously.
        ///
        /// <para>NULL WHEREVER THE GATE ISN'T A <see cref="Permissions.PermissionDecider"/> — headless
        /// runs and most tests use <see cref="Permissions.PermissionGate.AllowAll"/> or
        /// <see cref="Permissions.PermissionGate.DenyAll"/>, neither of which owns a classifier at
        /// all. Speculation is purely a latency optimisation, so "no classifier reachable" simply
        /// means the agent never speculates and every gated call pays its normal synchronous cost —
        /// exactly today's behaviour.</para>
        /// </summary>
        public Permissions.ActionClassifier? Classifier { get; init; }

        /// <summary>The servers themselves, held only so the session can dispose them.</summary>
        public IReadOnlyList<IAsyncDisposable>? McpServers { get; init; }

        /// <summary>
        /// Where a mid-turn correction comes from — see <see cref="Agent.TakePendingSteer"/>.
        ///
        /// <para>Null for a headless host and for every child. Steering is a conversation the user
        /// is having with THIS session, and a sub-agent spawned with a brief is not in it.</para>
        /// </summary>
        public Func<string?>? TakePendingSteer { get; init; }

        public string? Briefing { get; init; }
        public ISubAgentSpawner? Spawner { get; init; }

        /// <summary>
        /// How this session is set up to work — BOTH axes.
        ///
        /// <para>A WORKING MODE, NOT A BARE AgentMode. It was the latter, which silently dropped the
        /// edits axis on the way in: a session started in always-ask would build its agent with the
        /// accept-edits default, because only the delegation half survived the trip. The implicit
        /// widening means every existing caller passing an AgentMode still compiles and still means
        /// what it meant.</para>
        /// </summary>
        public WorkingMode Mode { get; init; } = AgentMode.FanOut;

        /// <summary>
        /// How the model asks the user. Null in every headless path — a host with no UI must not
        /// offer a tool whose whole behaviour is to wait for a person.
        /// </summary>
        public Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<QuestionAnswers>>? AskUser { get; init; }

        /// <summary>
        /// The embedder's own tools, already wrapped in <see cref="Jobs.GatedAgentTool"/> by
        /// SessionFactory. Empty in every path that injects nothing.
        ///
        /// <para>ALREADY GATED WHEN IT ARRIVES. This type does no wrapping of its own, because the
        /// session's policy is not visible here — doing it in two places is how one of them ends up
        /// being the copy that forgets.</para>
        /// </summary>
        public IReadOnlyList<Jobs.IAgentTool> AgentTools { get; init; } = [];

        /// <summary>The session's tool selection (S1 composed with S2), or null for no opinion.
        /// A per-request selection is passed to <see cref="RunAsync"/> and composed onto it.</summary>
        public Jobs.ToolSelection? ToolSelection { get; init; }
    }

    public AgentHost(AgentRuntime runtime, ISessionObserver sink, IToolObserver jobPanel,
        SessionStores? stores = null,
        SessionSnapshot? resume = null,
        TokenLedger? ledger = null)
    {
        _runtime = runtime;
        _stores = stores ?? new SessionStores();
        _sink = sink;
        _jobPanel = jobPanel;

        // MODE IS THE ONE FORWARDED VALUE THAT CHANGES MID-SESSION (/mode agent single), so it is a
        // field rather than read back off the immutable runtime.
        _mode = runtime.Mode;
        _mcpServers = runtime.McpServers ?? [];

        // GIVEN, OR MADE HERE. A ledger constructed inside this constructor can only ever be THE
        // SESSION'S ONE LEDGER — and that is exactly the assumption per-model attribution has to
        // break. Taking it as a parameter makes "which ledger does this agent get?" a question with
        // an answer, owned by the composition root, without disturbing the ~10 construction sites
        // (every test) that have no opinion: they pass nothing and get today's behaviour.
        //
        // RESTORED, OR FRESH, when we make it. A resumed session carries the spend it already
        // incurred — a ledger restarting at zero would report a long session as costing nothing —
        // and its context is rehydrated rather than replayed, so the next turn continues from what
        // the agent knew instead of re-reading what it had already read.
        //
        // A CALLER THAT PASSES ONE OWNS THE SEEDING TOO. `resume` is still read here for Context, so
        // a caller handing in a ledger for a resumed session must seed it from the same snapshot —
        // see WireRunner, where both come off one local for exactly that reason.
        Ledger = ledger ?? (resume is null
            ? new TokenLedger()
            : new TokenLedger(resume.InputTokens, resume.OutputTokens));

        Context = new AgentContext(runtime.ContextWindow);
        if (resume is not null) Context.Replace(resume.Context);
        // Loaded FROM a stored row, so there is already something to come back to — see HasSavedTurn.
        _resumed = resume is not null;
        // LAST: BuildAgent reads Ledger and Context, so both must already be assigned.
        _agent = BuildAgent();
    }

    /// <summary>
    /// Runs one turn on this host's agent. A DELEGATION — everything around it is the session's.
    ///
    /// <para>WHAT STAYS HERE IS THE AGENT, which is what a host is: the thing that owns the agent, its
    /// executors, its MCP binding and its ledger. Starting a turn, stopping it, saying what happened and
    /// numbering the rows are the session's, and now live there.</para>
    /// </summary>
    /// <param name="turnTools">This request's tool selection, composed onto the session's. Null is
    /// the normal case: a front end that narrows once per session passes nothing here.</param>
    /// <param name="prompt">What the user asked for.</param>
    /// <param name="ct">Cancels the goal mid-run.</param>
    public Task RunAsync(string prompt, CancellationToken ct,
        Jobs.ToolSelection? turnTools = null) => _agent.SendAsync(prompt, ct, turnTools);


    /// <summary>
    /// A backstop, not a budget. The user's configured value when they set one, otherwise
    /// <see cref="DefaultTurnCeiling"/>.
    ///
    /// <para>NO LOW DEFAULT: a low cap exists to bound a WORKER inside a fan-out — one job among
    /// many, where a runaway costs the whole plan. A session is not that. The user is watching it and
    /// can stop it, and a ceiling in the low hundreds just ends real work at a number that has
    /// nothing to do with the task.</para>
    ///
    /// <para>WHAT DOES NOT FOLLOW is <c>int.MaxValue</c>. "No arbitrary limit" and "no limit" are
    /// different claims, and the first does not license the second. Stuck detection is not a
    /// substitute either unless it can actually END a run rather than nudge — without a real ceiling
    /// the common case, an unconfigured session, has nothing bounding it at all.</para>
    /// </summary>
    /// <para>ZERO MEANS UNBOUNDED, an explicit opt-out. Read literally it would be a ceiling of zero
    /// turns — the agent stopping before its first call and doing nothing — which nobody configures
    /// on purpose, so the number is free to carry the meaning someone actually intends by it. It
    /// matches opencode's <c>agent.steps ?? Infinity</c>: a session nobody asked to bound is not
    /// bounded.</para>
    /// <summary>
    /// Turns one request may take before it is stopped — this agent's, and every child's.
    ///
    /// <para>ONE RESOLUTION, SHARED. Resolving it here for the session agent and again, by a
    /// separate expression, for sub-agents lets the two disagree: a configured <c>0</c> makes the
    /// parent unbounded while children fall back to the default. A static so the composition root can
    /// resolve it once, before the host exists, and hand the same number to the factory.</para>
    /// </summary>
    public int TurnCeiling => CeilingFor(ConfiguredMaxTurns);

    /// <summary>
    /// What a configured <c>orchestrator.maxTurns</c> means.
    ///
    /// <para>ZERO IS THE OPT-OUT, not a mistake — the same meaning an agent type's <c>maxTurns</c>
    /// carries, and the reason nobody configures zero by accident: it would mean an agent that stops
    /// before its first call. Null is "nobody said", which is what the default is for.</para>
    /// </summary>
    public static int CeilingFor(int? configured) => configured switch
    {
        null => DefaultTurnCeiling,
        0 => int.MaxValue,
        int turns => turns,
    };

    /// <summary>
    /// Turns a single request may take before it is stopped, absent configuration.
    ///
    /// <para>Chosen to be unreachable by ordinary work and still finite. A live three-prompt drive
    /// used four turns; a long agentic session on a real repo used sixty-six. Three hundred leaves
    /// room for work several times harder than anything measured, while still bounding a model that
    /// has stopped making progress in a way the stuck-detector cannot see — one that varies its
    /// calls slightly each time and so never repeats a signature.</para>
    ///
    /// <para>Set <c>orchestrator.maxTurns</c> to <c>0</c> for no cap at all.</para>
    /// </summary>
    public const int DefaultTurnCeiling = 300;

    /// <summary>
    /// How this session's spend is attributed: <c>instance:model</c> when the instance is known.
    ///
    /// <para>The same string the UI shows and the same one <c>/stats</c> groups by, so a figure in
    /// the dashboard and a name in the panel do not have to be reconciled by the reader.</para>
    /// </summary>
    private string SpendLabel =>
        _runtime.InstanceName is { Length: > 0 } instance
            ? $"{instance}:{_runtime.Provider.ModelId}"
            : _runtime.Provider.ModelId;

    /// <summary>
    /// Points this session at a different model — see <see cref="Agent.SwapProvider"/>.
    ///
    /// <para>THE RUNTIME FOLLOWS TOO, not just the agent. <see cref="SpendLabel"/> reads it, so a
    /// swap that moved only the agent would keep attributing this session's spend to the model it was
    /// pointed at before — the figure in /stats and the name in the panel would disagree, which is
    /// exactly what SpendLabel's own doc says must never happen.</para>
    /// </summary>
    public void SwapProvider(ActiveModel model)
    {
        _runtime = _runtime with
        {
            Provider = model.Provider,
            InstanceName = model.InstanceName,
            ContextWindow = model.ContextWindow ?? _runtime.ContextWindow,
        };

        _agent.SwapProvider(model.Provider, model.InstanceName, model.ContextWindow);
    }

    private Agent BuildAgent()
    {
        var agent = new Agent(_runtime.Provider, _runtime.Executors, Ledger, _sink, _jobPanel, _stores.Logs,
            TurnCeiling,

            // THE CONTEXT BOUND, which is what the "no turn cap" decision above rests on: a
            // single-agent run ends when it runs out of room, not at an arbitrary turn number.
            // The agent's own bound: it compresses its own context from inside its turn loop,
            // which is the only place the measurement that triggers it is taken.
            compressAbove: (_runtime.Orchestrator ?? OrchestratorSettings.Unbounded).EffectiveCompressThreshold(_runtime.ContextWindow)
                ?? OrchestratorSettings.DefaultCompressThreshold,

            // THE HOST ALREADY KNEW THIS and used it for persistence and history; the agent read
            // the process instead. Same value in practice, different sources — which is only ever
            // true until something moves the process.
            workingDir: _runtime.WorkingDir,
            instanceName: _runtime.InstanceName,
            globalInstructionsDir: _runtime.GlobalInstructionsDir,
            mcp: _runtime.Mcp,
            briefing: _runtime.Briefing,

            // FORWARDED, and this is the third of the three signature changes the spec counted:
            // Agent takes it, AgentHost takes it, and BuildAgent must PASS it. Omitting this line
            // compiles perfectly and produces a session whose agent silently has no spawn tool.
            spawner: _runtime.Spawner,

            // THE SAME CONTEXT THROUGHOUT. The agent is built once now, so this is the context it
            // keeps for its whole life — prompt N+1 begins with everything prompt N learned.
            context: Context,

            // AND THE WAY TO ASK. Null in every headless path, and refused outright for a child —
            // Agent enforces that itself rather than trusting whoever constructs it.
            askUser: _runtime.AskUser,

            // THE EMBEDDER'S OWN, gated before they got here. Offered to this agent and, through
            // SubAgentRuntime, to every child it spawns.
            agentTools: _runtime.AgentTools,
            toolSelection: _runtime.ToolSelection,
            policy: _runtime.Policy,
            classifier: _runtime.Classifier)
        {
            // THE STARTING MODE, applied here rather than passed to the constructor: Mode is a
            // settable property precisely so it can change later, and an initialiser says that more
            // plainly than a constructor argument would.
            Mode = _mode,
        };

        // SUBSCRIBED, not assigned. These are events (Agent.cs:122-140), so a second consumer — a
        // sub-agent's telemetry reporter, an aggregator — adds itself rather than silently replacing
        // whoever came first. As settable Action<T> properties, `TurnCompleted = x` followed by
        // `TurnCompleted = y` lost x with no compiler warning.
        agent.TurnCompleted += calls =>
        {
            OnTurnCompleted(calls);
            // TOKENS TOO. Single-agent records to the Ledger itself, and TokensUpdated otherwise
            // fires only inside the fan-out driver's stream loop. Without this raise the ctx readout
            // and the panel sit at 0 for an entire single-agent session no matter how many tokens it
            // burns — and single-agent is the default mode.
            TokensUpdated?.Invoke(this, Ledger.TotalTokens);

            // AND THE TURN IS RECORDED, here rather than at exit: a crash is exactly when exit does
            // not happen. The whole context goes each time, because compression rewrites it wholesale
            // and an append-only log would have to be reconciled against a list that no longer
            // matches. The store swallows its own failures — see its class doc.
            // THE EDIT MODE GOES WITH IT. A session saved in always-ask that came back in the
            // accept-edits default would silently undo a decision the user made, at the moment they
            // are least likely to be watching.
            _stores.Resume?.SaveTurn(new SqliteSessionStore.ResumeTurn(
                agent.Id, Context.Messages, Ledger.InputTokens, Ledger.OutputTokens,
                _runtime.WorkingDir, Mode.Edits));

            // AND HISTORY, which is a different feature from resume and so a different database. The
            // resume store is a buffer worth nothing once a session ends cleanly; this survives, and
            // is the only place a question needing MANY sessions can be answered. Upserted every
            // turn for the same reason resume is: a crash is exactly when a final write never comes.
            _turns++;
            _stores.History?.SaveSession(new SessionRecord(
                agent.Id, _runtime.WorkingDir, SpendLabel, Mode.ToString(),
                Ledger.InputTokens, Ledger.OutputTokens, Ledger.SubAgentTokens, _turns,
                _startedAt, DateTimeOffset.UtcNow,
                Ledger.CachedInputTokens, Ledger.CacheWrittenTokens,
                Ledger.CacheHitRate is not null, Ledger.TotalCost));
        };

        // OCCUPANCY, which nothing else in this mode observes. Without it the status bar has only the
        // cumulative total to divide by the window — a sum that passes 100% while the context is half
        // empty, and that cannot fall when compression frees space.
        agent.ContextUsed += RecordInputTokens;
        agent.ContextCompressed += (b, a) =>
        {
            ContextCompressed?.Invoke(this, (b, a));
            // PRESSURE, not manual: this fires from the loop's own per-turn check. `/compress` writes
            // its own row with trigger "manual", and separating them is the point — a threshold that
            // fires too eagerly and one that never fires are indistinguishable from a bare count.
            _stores.History?.SaveCompaction(new CompactionRecord(agent.Id, DateTimeOffset.UtcNow, b, a, "pressure"));
        };
        agent.ContextEstimated += used => ContextEstimatedUpdated?.Invoke(this, used);

        // HISTORY SUBSCRIBES HERE, and nowhere in the loop. The kernel raises reports and does not
        // know a database exists; this is the one place that turns them into rows. Both writers
        // swallow their own failures, so a locked file costs statistics and nothing else.
        agent.ToolCallFinished += r => _stores.History?.SaveToolCall(new ToolCallRecord(
            r.CallId, r.AgentId, r.ToolName, r.JobType, r.Outcome, r.DurationMs,
            r.ResultChars, r.StartedAt, _runtime.WorkingDir));

        agent.ChildFinished += r => _stores.History?.SaveRun(new RunRecord(
            r.RunId, r.ParentAgentId, r.TypeName, r.ModelId, r.InputTokens, r.OutputTokens,
            r.Turns, r.ToolCalls, r.Outcome, r.StartedAt, r.DurationMs, _runtime.WorkingDir));

        // A CHILD'S SPEND, mid-turn. TokensUpdated otherwise fires only on THIS agent's turn
        // boundaries, and it completes none while blocked inside the spawn tool — so a worker
        // running on a second model left the per-model breakdown showing pre-spawn figures for the
        // whole run. The ledger is shared and was always right; only the repaint was missing.
        agent.ChildSpend += () => TokensUpdated?.Invoke(this, Ledger.TotalTokens);

        // SET, NOT PASSED — Agent takes this as a property rather than a 17th constructor argument,
        // and this line is the whole wiring. Null leaves the agent unsteerable, which is what every
        // headless host and every child gets.
        agent.TakePendingSteer = _runtime.TakePendingSteer;

        // THE SESSION'S MINTER WHEN IT GAVE ONE. A bare host — every AgentHostTests case — has no
        // session and no transcript to share, so the agent falls back to its own counter, which is
        // correct there.
        agent.MintTurnId = _mintTurnId;

        return agent;
    }

    /// <summary>
    /// Compresses <c>conversation</c> now, unconditionally — what <c>/compress</c> calls.
    ///
    /// <para>NO THRESHOLD TEST, deliberately: the user asked. The pressure checks that guard the two
    /// automatic routes exist to decide WHETHER to run, and re-applying one here would let the app
    /// decline a command whose entire content is "do it".</para>
    ///
    /// <para>Lives here rather than in the command dispatcher because everything it needs — the
    /// provider, the job panel to draw the row on, and the ledger to meter the call — is already held
    /// by this type. A dispatcher would have to reach for all three separately and, having no job
    /// panel, could only print a line of prose after the fact.</para>
    /// </summary>
    public Task<SessionCompressor.CompressResult> CompressNowAsync(CancellationToken ct) =>
        // THE AGENT'S CONTEXT, not the session conversation. That distinction is the whole bug: the
        // conversation holds only prompts and final answers, so compressing it freed nothing while
        // the list that was actually full went untouched.
        // THE AGENT'S ID, which is stable for the agent's whole life. Naming the row after whichever
        // goal last ran would leave a /compress issued before any prompt with no goal to name, filing
        // its row under a name no log directory has.
        CompressionRun.RunAsync(
            new CompressionRun.CompressionWork(Context, _runtime.Provider, _agent.SkillToolOffered),
            new CompressionRun.CompressionReport(_jobPanel, _agent.Id, "compress context · requested",
                usage =>
                {
                    Ledger.Record(usage, SpendLabel);
                    TokensUpdated?.Invoke(this, Ledger.TotalTokens);
                },
                (b, a) =>
                {
                    ContextCompressed?.Invoke(this, (b, a));
                    _stores.History?.SaveCompaction(new CompactionRecord(
                        _agent.Id, DateTimeOffset.UtcNow, b, a, "manual", _runtime.WorkingDir));
                }),
            ct);





    /// <summary>
    /// Nothing to release — this type owns no schedulers. Kept because the composition root disposes
    /// the outgoing runner on every F5 rewire.
    /// </summary>
    /// <summary>
    /// Releases the MCP subprocesses.
    ///
    /// <para>THE ONE FAILURE THAT OUTLIVES THE PROCESS. An F5 re-wire builds a fresh host on every
    /// provider change and disposes the outgoing one; without this, each re-wire would leave its
    /// servers running for the life of the app, holding whatever they had open. Best-effort and
    /// synchronous: shutdown is not a place to throw or to wait.</para>
    /// </summary>
    public void Dispose()
    {
        // THE TURN'S SCOPE IS NOT HERE ANY MORE. It moved to Session with the rest of turn ownership;
        // SessionManager.Close disposes the host and the session together, so the cleanup did not
        // need a new caller — see Session.DisposeTurnScope.
        foreach (var server in _mcpServers)
            try { server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* it is going away regardless */ }
    }
}
