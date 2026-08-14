using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using CxAgent.Helpers;

namespace CxAgent.Core.Agent;

/// <summary>
/// The UI's side of one <see cref="Agent"/>: owns it for the session, feeds it what the user types,
/// and republishes what it reports as the events the status bar and panels bind to.
///
/// <para>A HOST, NOT A RUNNER. It was <c>GoalRunner</c>, and it genuinely ran things — streaming a
/// decomposition turn, compiling a create_plan call into a JobDag, driving a scheduler and executor.
/// All of that is gone; what is left is composition and translation. It holds the ledger, the
/// context and the token budget because those outlive any one prompt, and it turns a provider fault
/// into a visible error rather than an unobserved faulted task.</para>
///
/// <para>Every sink call is the UI-update seam — marshalling is the sink's responsibility, not
/// this type's.</para>
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly AgentRuntime _runtime;
    private readonly SessionStores _stores;
    private readonly IChatSink _sink;
    private readonly IJobPanel _jobPanel;


    /// <summary>
    /// cxagent's own config directory, where a user-level CXAGENT.md may sit — or null when there is
    /// none to read.
    ///
    /// <para>OUR CONFIG FOLDER ONLY — whatever <c>AppPaths.ConfigDir</c> resolves to on this OS, not a
    /// hardcoded <c>~/.config</c>. opencode also reads <c>~/.claude/CLAUDE.md</c>, another product's
    /// user-level file; honouring that would mean silently obeying instructions written for a
    /// different agent with different tools. A repo's CLAUDE.md is different — it describes the
    /// PROJECT, so it is read where the project is.</para>
    /// </summary>

    /// <summary>Connected MCP servers, passed straight to the agent. Null when none are configured.</summary>

    /// <summary>What this host's agent was created to do, or null for a plain session. Fixed here so
    /// an F5 re-wire rebuilds the agent with the SAME briefing rather than silently dropping it.</summary>

    /// <summary>
    /// The subprocesses behind <see cref="_runtime.Mcp"/>, held only so <see cref="Dispose"/> can end them.
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
    /// Usage history — a DIFFERENT database from <see cref="_stores.Resume"/>, and optional for the same
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
    /// <para>ONE AGENT, NOT ONE PER PROMPT. It used to be constructed inside <c>RunCoreAsync</c> with
    /// a freshly minted id, so every user message started a new identity: the log directory changed
    /// under the user mid-session, turn numbering restarted at 000, and the only thing carried across
    /// was the context handed in from here. The agent owns its id, its context and its session state
    /// now, which is what makes it continuous rather than a loop that happens to be re-entered.</para>
    /// </summary>
    private readonly Agent _agent;


    // The whole settings record rather than the one field read today: the compaction threshold is
    // derived from it per-agent (see BuildAgent), and the turn ceiling reads MaxTurns.

    /// <summary>How this session's agent spawns children, or null when sub-agents are not wired.
    /// Forwarded to the agent in BuildAgent — a field it must be, since BuildAgent runs after the
    /// constructor body.</summary>

    /// <summary>The mode this session starts in — from the command line. Applied to the agent in
    /// BuildAgent, after which <see cref="Mode"/> is the live value.</summary>
    private readonly AgentMode _mode;

    /// <summary>
    /// The folder this session runs in, recorded with every saved turn so resume can be scoped to it.
    ///
    /// <para>Null in tests and anywhere that does not persist — a session saved without one is never
    /// OFFERED for resume, which is the safe direction: a row that cannot say where it came from
    /// could have come from anywhere.</para>
    /// </summary>

    /// <summary>
    /// The active provider instance's context window in tokens (ProviderInstanceConfig.ContextWindow —
    /// P11 Task 1), threaded through from ProviderResolution at construction time rather than read off
    /// <see cref="_runtime.Provider"/> itself: ILlmProvider exposes identity (ProviderId/ModelId) but not this
    /// config-only number, and adding it to the interface would ripple into every vendor driver and
    /// test double for a value only ProviderResolver's config lookup actually has. Null when the user
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
    /// <para>CONSTRUCTED HERE, OWNED BY THE AGENT. It used to be held here because the loop was built
    /// per goal and a context owned solely by the loop would die with it. The <see cref="Agent"/> now
    /// outlives every prompt, so it could own this outright; the property stays because
    /// <c>/compress</c> and the status bar read it, and handing the agent a context at construction
    /// keeps the runner's view of it identical to the agent's rather than a copy.</para>
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
    /// sum, so it rises and FALLS, and in particular it falls after compression. The status bar drove
    /// its context percentage off the cumulative total and so could show 107% of a window that was
    /// half empty, and could not move at all when compression freed space.</para>
    /// </summary>
    public event EventHandler<int>? ContextUsedUpdated;

    /// <summary>
    /// Raised when a compression has actually shrunk the conversation.
    ///
    /// <para>The true new occupancy is not knowable until the next turn — it is only ever read from
    /// what a provider reports it received — so this says "the last reading no longer describes the
    /// conversation" rather than carrying a number. The alternative was to keep displaying the
    /// pre-compression figure, which is the reported bug: compress, and the gauge does not move.</para>
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
    /// <para>Nothing is CACHED here any more. A <c>_lastInputTokens</c> field used to hold it for the
    /// between-goals compression check; that check is gone, and the agent reads occupancy from its own
    /// <see cref="AgentContext"/> — the only place that has a size to compare against.</para>
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
    /// <para>A PROPERTY, NOT AN EVENT. It was <c>GoalStarted</c>, raised on every prompt because every
    /// prompt minted a new id; the status bar's session id therefore churned as the user typed. The id
    /// is now fixed for the agent's life, so there is no event left to raise — the composition root
    /// reads it once at wire-up. Firing it from the constructor instead would reach nobody: the
    /// subscription happens after construction.</para>
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
    internal void OnTurnCompleted(int toolCalls) => TurnCompleted?.Invoke(this, toolCalls);

    /// <param name="store">
    /// Where completed turns are recorded so a crash is recoverable, or null for a session that is
    /// not worth persisting (every test that does not care, and any run whose store failed to open).
    /// Optional because an agent without one is degraded, not broken.
    /// </param>
    /// <summary>
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
        public required PluginRegistry Plugins { get; init; }

        /// <summary>Where the session works. Data, never the process's own directory.</summary>
        public string? WorkingDir { get; init; }

        /// <summary>cxagent's config folder, so a user-level CXAGENT.md applies wherever they work.</summary>
        public string? GlobalInstructionsDir { get; init; }

        /// <summary>The real window, so compaction derives its threshold from actual headroom.</summary>
        public int? ContextWindow { get; init; }

        /// <summary>Turn cap and compaction threshold. Null means nothing was configured.</summary>
        public OrchestratorSettings? Orchestrator { get; init; }

        public Core.Mcp.McpToolset? Mcp { get; init; }

        /// <summary>The servers themselves, held only so the session can dispose them.</summary>
        public IReadOnlyList<IAsyncDisposable>? McpServers { get; init; }

        public string? Briefing { get; init; }
        public ISubAgentSpawner? Spawner { get; init; }
        public AgentMode Mode { get; init; } = AgentMode.FanOut;

        /// <summary>
        /// How the model asks the user. Null in every headless path — a host with no UI must not
        /// offer a tool whose whole behaviour is to wait for a person.
        /// </summary>
        public Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<QuestionAnswers>>? AskUser { get; init; }
    }

    public AgentHost(AgentRuntime runtime, IChatSink sink, IJobPanel jobPanel,
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
    /// One user message: put it on the transcript, hand it to the agent, put the answer back.
    ///
    /// <para>NO STATUS RETURN. This was <c>RunAsync</c> returning a <c>GoalState</c> that nobody read
    /// — the composition root calls it as <c>_ = host.SendAsync(...)</c> — while the thing a user
    /// actually sees, an error, goes to the sink. Returning a status nothing consumes only invites a
    /// caller to start branching on it.</para>
    ///
    /// <para>NO COMPRESSION AROUND THIS CALL either. One used to sit in a <c>finally</c> here, on the
    /// reasoning that a request has "nine early returns" and only one reached the end. An agent
    /// compresses its OWN context from inside its own turn loop, where the measurement that triggers
    /// it is taken. Both routes read the same last-reported input-token figure, and occupancy only
    /// refreshes when a provider reports it — so after the loop compressed, nothing had re-measured,
    /// this guard saw the same over-threshold figure and compressed again: two identical rows on a
    /// live drive, 24.5s and 26.1s, the second summarising a context whose older half was already a
    /// summary.</para>
    /// </summary>
    /// <remarks>
    /// NO CONVERSATION PARAMETER. There was one — a List&lt;ChatMessage&gt; owned by AppBootstrap that
    /// this method appended the prompt and the answer to, and that NOTHING ever read. What the model
    /// sees is the agent's own context; what the user sees is the transcript control. This third list
    /// was a leftover from before the agent owned its context, and it is this codebase's recurring rot
    /// pattern: a value written and never read.
    ///
    /// <para>It also made /clear's comment wrong — "MUST CLEAR BOTH LISTS" — implying the model would
    /// otherwise remember. Clearing the agent's context is the whole operation.</para>
    /// </remarks>
    /// <param name="echo">
    /// What to show on the transcript, when that differs from what is sent.
    ///
    /// <para>FOR <c>/init</c>, WHERE THE TWO GENUINELY DIFFER. The user typed three words; the model
    /// receives a long briefing about what to explore and what is worth writing down. Echoing the
    /// briefing would put words in the user's mouth — a message they never wrote, attributed to them,
    /// which they then have to scroll past on every later read of the transcript.</para>
    ///
    /// <para>Null means they are the same, which is every other caller.</para>
    /// </param>
    public async Task SendAsync(string prompt, CancellationToken ct, string? echo = null)
    {
        try
        {
            _sink.AddUserTurn(echo ?? prompt);

            // ONE AGENT WITH TOOLS, built once in the constructor and reused. The session IS the agent.
            var assistantId = _sink.BeginAssistantTurn();
            _sink.EndAssistantTurn(assistantId);   // the agent opens its own turns

            await _agent.SendAsync(prompt, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _sink.ShowError(ex.Message);   // residual fault → visible, not an unobserved faulted task
        }
    }

    /// <summary>
    /// A backstop, not a budget. The user's configured value when they set one, otherwise
    /// <see cref="DefaultTurnCeiling"/>.
    ///
    /// <para>NO LOW DEFAULT, and that reasoning is unchanged: the cap exists to bound a
    /// WORKER inside a fan-out — one job among many, where a runaway cost the whole plan. A session
    /// is not that. The user is watching it and can stop it, and a ceiling in the low hundreds just
    /// ends real work at a number that has nothing to do with the task. crush ships no step cap at
    /// all; opencode's is <c>agent.steps ?? Infinity</c>. The invented 200 deserved to go.</para>
    ///
    /// <para>WHAT DID NOT FOLLOW is <c>int.MaxValue</c>. "No arbitrary limit" and "no limit" are
    /// different claims, and the argument for the first was used to justify the second. The stated
    /// replacement was stuck detection — which, until the change alongside this one, only ever
    /// nudged. So the common case, an unconfigured session, had nothing bounding it at all.</para>
    /// </summary>
    /// <para>ZERO MEANS UNBOUNDED, an explicit opt-out. Read literally it would be a ceiling of zero
    /// turns — the agent stopping before its first call and doing nothing — which nobody configures
    /// on purpose, so the number is free to carry the meaning someone actually intends by it. It
    /// matches opencode's <c>agent.steps ?? Infinity</c>: a session nobody asked to bound is not
    /// bounded.</para>
    /// <summary>
    /// Turns one request may take before it is stopped — this agent's, and every child's.
    ///
    /// <para>ONE RESOLUTION, SHARED. It used to be computed here for the session agent and again, by
    /// a separate expression, for sub-agents — and the two disagreed: a configured <c>0</c> made the
    /// parent unbounded while children silently fell back to the default. A static so the
    /// composition root can resolve it once, before the host exists, and hand the same number to the
    /// factory.</para>
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

    private Agent BuildAgent()
    {
        var agent = new Agent(_runtime.Provider, _runtime.Plugins, Ledger, _sink, _jobPanel, _stores.Logs,
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
            askUser: _runtime.AskUser)
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
            // TOKENS TOO. Single-agent records to the Ledger itself and never raised TokensUpdated —
            // that event fires only inside the fan-out driver's stream loop. So the ctx readout and
            // the panel both sat at 0 for an entire single-agent session no matter how many tokens it
            // burned, which is the mode that is the default.
            TokensUpdated?.Invoke(this, Ledger.TotalTokens);

            // AND THE TURN IS RECORDED, here rather than at exit: a crash is exactly when exit does
            // not happen. The whole context goes each time, because compression rewrites it wholesale
            // and an append-only log would have to be reconciled against a list that no longer
            // matches. The store swallows its own failures — see its class doc.
            _stores.Resume?.SaveTurn(agent.Id, Context.Messages, Ledger.InputTokens, Ledger.OutputTokens,
                _runtime.WorkingDir);

            // AND HISTORY, which is a different feature from resume and so a different database. The
            // resume store is a buffer worth nothing once a session ends cleanly; this survives, and
            // is the only place a question needing MANY sessions can be answered. Upserted every
            // turn for the same reason resume is: a crash is exactly when a final write never comes.
            _turns++;
            _stores.History?.SaveSession(new SessionRecord(
                agent.Id, _runtime.WorkingDir, _runtime.Provider.ModelId, Mode.ToString(),
                Ledger.InputTokens, Ledger.OutputTokens, Ledger.SubAgentTokens, _turns,
                _startedAt, DateTimeOffset.UtcNow));
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
            r.CallId, r.AgentId, r.ToolName, r.PluginType, r.Outcome, r.DurationMs,
            r.ResultChars, r.StartedAt, _runtime.WorkingDir));

        agent.ChildFinished += r => _stores.History?.SaveRun(new RunRecord(
            r.RunId, r.ParentAgentId, r.TypeName, r.ModelId, r.InputTokens, r.OutputTokens,
            r.Turns, r.ToolCalls, r.Outcome, r.StartedAt, r.DurationMs, _runtime.WorkingDir));

        // A CHILD'S SPEND, mid-turn. TokensUpdated otherwise fires only on THIS agent's turn
        // boundaries, and it completes none while blocked inside the spawn tool — so a worker
        // running on a second model left the per-model breakdown showing pre-spawn figures for the
        // whole run. The ledger is shared and was always right; only the repaint was missing.
        agent.ChildSpend += () => TokensUpdated?.Invoke(this, Ledger.TotalTokens);

        return agent;
    }

    /// <summary>
    /// Compresses <paramref name="conversation"/> now, unconditionally — what <c>/compress</c> calls.
    ///
    /// <para>NO THRESHOLD TEST, deliberately: the user asked. The pressure checks that guard the two
    /// automatic routes exist to decide WHETHER to run, and re-applying one here would let the app
    /// decline a command whose entire content is "do it".</para>
    ///
    /// <para>Lives here rather than in the command dispatcher because everything it needs — the
    /// provider, the job panel to draw the row on, and the ledger to meter the call — is already held
    /// by this type. The dispatcher previously reached for all three separately and, having no job
    /// panel, could only print a line of prose after the fact.</para>
    /// </summary>
    public Task<SessionCompressor.CompressResult> CompressNowAsync(CancellationToken ct) =>
        // THE AGENT'S CONTEXT, not the session conversation. That distinction is the whole bug: the
        // conversation holds only prompts and final answers, so compressing it freed nothing while
        // the list that was actually full went untouched.
        // THE AGENT'S ID. This used to be the id of whichever goal last ran, falling back to the
        // literal "session" before the first one — so a /compress issued before any prompt filed its
        // row under a name no log directory had. The agent has one id for its whole life.
        CompressionRun.RunAsync(Context, _runtime.Provider, _jobPanel, _agent.Id,
            "compress context · requested", usage =>
            {
                Ledger.Record(usage, _runtime.Provider.ModelId);
                TokensUpdated?.Invoke(this, Ledger.TotalTokens);
            }, ct, compressed: (b, a) =>
            {
                ContextCompressed?.Invoke(this, (b, a));
                _stores.History?.SaveCompaction(new CompactionRecord(
                    _agent.Id, DateTimeOffset.UtcNow, b, a, "manual", _runtime.WorkingDir));
            });





    /// <summary>
    /// Nothing to release: the schedulers this used to own died with the dag. Kept because the
    /// composition root disposes the outgoing runner on every F5 rewire.
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
        foreach (var server in _mcpServers)
            try { server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* it is going away regardless */ }
    }
}
