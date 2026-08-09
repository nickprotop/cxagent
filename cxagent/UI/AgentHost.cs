using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using CxAgent.Helpers;

namespace CxAgent.UI;

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
    private readonly ILlmProvider _provider;
    private readonly IChatSink _sink;
    private readonly IJobPanel _jobPanel;
    private readonly PluginRegistry _plugins;

    private readonly LogFileManager? _logs;

    private readonly int? _sessionTokenBudget;

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


    // The whole settings record, not just the token budget: OrchestratorLoop needs MaxConsults and
    // MaxEditsPerJob too, and those are NOT null-means-unbounded (see OrchestratorSettings' doc) — an
    // absent 'orchestrator' config block must still hand the loop real caps.
    private readonly OrchestratorSettings _orchestrator;

    /// <summary>
    /// The active provider instance's context window in tokens (ProviderInstanceConfig.ContextWindow —
    /// P11 Task 1), threaded through from ProviderResolution at construction time rather than read off
    /// <see cref="_provider"/> itself: ILlmProvider exposes identity (ProviderId/ModelId) but not this
    /// config-only number, and adding it to the interface would ripple into every vendor driver and
    /// test double for a value only ProviderResolver's config lookup actually has. Null when the user
    /// hasn't set contextWindow for this instance — EffectiveCompressThreshold treats that as "unknown"
    /// and falls back to the fixed constant, per its own precedence.
    /// </summary>
    private readonly int? _contextWindow;

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
    /// Raised every time Ledger.Record runs, carrying the new running total. TokenLedger itself only
    /// exposes Breached (fires once, on crossing the budget) — this gives the UI (the status-bar cost
    /// readout, Task 11) a live per-call hook without adding a general-purpose event to the ledger's
    /// own object model.
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
    /// MaxWorkerTurns as the USER set it, or null when they did not.
    ///
    /// <para>Distinct from <c>_orchestrator.MaxWorkerTurns</c>, which is 200 whenever the settings
    /// object exists — including the placeholder AppBootstrap supplies for an absent config block.
    /// Single-agent needs to tell "the user asked for 200" apart from "nobody said anything", and
    /// the settings record cannot express that difference.</para>
    /// </summary>
    public int? ConfiguredMaxWorkerTurns { get; init; }

    /// <summary>Raises <see cref="TurnCompleted"/>. Called by the loop, which is the only thing that
    /// knows a turn boundary.</summary>
    internal void OnTurnCompleted(int toolCalls) => TurnCompleted?.Invoke(this, toolCalls);

    /// <summary>
    /// Copilot mode (P9 Task 2): fires true the instant the goal parks in GoalState.Draft (same point
    /// as IChatSink.ShowApprovalRequest — see RunCoreAsync) and false the instant the gate resolves,
    /// whichever way. This is the seam MainWindow's F9/Esc footer hint subscribes to; mirrors
    /// TokensUpdated's "AgentHost raises it itself at the point of truth" shape. Never fires at all
    /// when Copilot is off.
    /// </summary>
    public event EventHandler<bool>? DraftPending;


    // Guards the copilot approval gate. Kept as the UI seam MainWindow/AppBootstrap bind to;
    // nothing arms it now that planning is gone, so F9/Esc are no-ops exactly as they were
    // whenever copilot was off.
    private readonly object _stateLock = new();
    private TaskCompletionSource<bool>? _pendingApproval;

    /// <summary>True while a copilot-mode goal is sitting in GoalState.Draft, waiting on
    /// ApproveDraft/DiscardDraft. This is the seam Task 2's F9 handler polls/binds against.</summary>
    public bool HasPendingApproval { get { lock (_stateLock) return _pendingApproval is not null; } }

    /// <summary>
    /// Approves the DAG currently on display and lets RunCoreAsync continue into the
    /// OrchestratorLoop, executing that EXACT compiled dag — no re-plan. No-op if nothing is
    /// currently awaiting approval (e.g. called twice, or after the goal already moved on).
    /// </summary>
    public void ApproveDraft()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_stateLock) tcs = _pendingApproval;
        tcs?.TrySetResult(true);
    }

    /// <summary>
    /// Discards the drafted plan: RunCoreAsync returns GoalState.Cancelled without ever constructing
    /// the OrchestratorLoop, so no job runs and no scheduler is left armed. No-op if nothing is
    /// currently awaiting approval.
    /// </summary>
    public void DiscardDraft()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_stateLock) tcs = _pendingApproval;
        tcs?.TrySetResult(false);
    }

    public AgentHost(ILlmProvider provider, IChatSink sink, IJobPanel jobPanel,
        PluginRegistry pluginRegistry, LogFileManager? logs = null,
        OrchestratorSettings? orchestrator = null,
        int? contextWindow = null)
    {
        _provider = provider;
        _sink = sink;
        _jobPanel = jobPanel;
        _plugins = pluginRegistry;
        _logs = logs;
        _orchestrator = orchestrator ?? OrchestratorSettings.Unbounded;
        _contextWindow = contextWindow;
        _sessionTokenBudget = _orchestrator.GoalTokenBudget;
        Ledger = new TokenLedger(_sessionTokenBudget);
        Context = new AgentContext(contextWindow);
        // On breach, pause and ask — never silently keep spending. For now (Task 4) that means
        // surfacing an error; the interactive raise/continue/cancel dialog is Task 9.
        Ledger.Breached += (_, total) =>
            _sink.ShowError($"token budget exceeded: spent {total}, budget was {_sessionTokenBudget}.");

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
    public async Task SendAsync(string prompt, List<ChatMessage> conversation, CancellationToken ct)
    {
        try
        {
            _sink.AddUserTurn(prompt);
            conversation.Add(new ChatMessage
                { Role = "user", Content = prompt, Timestamp = DateTimeOffset.UtcNow });

            // ONE AGENT WITH TOOLS, built once in the constructor and reused. The session IS the agent.
            var assistantId = _sink.BeginAssistantTurn();
            _sink.EndAssistantTurn(assistantId);   // the agent opens its own turns

            var answer = await _agent.SendAsync(prompt, ct);
            if (!string.IsNullOrWhiteSpace(answer))
                conversation.Add(new ChatMessage
                    { Role = "assistant", Content = answer, Timestamp = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _sink.ShowError(ex.Message);   // residual fault → visible, not an unobserved faulted task
        }
    }

    private Agent BuildAgent() =>
        new(_provider, _plugins, Ledger, _sink, _jobPanel, _logs,
            // NO DEFAULT CAP IN SINGLE-AGENT. MaxWorkerTurns exists to bound a WORKER inside a
            // fan-out — one job among many, where a runaway costs the whole plan. Single-agent
            // is the session itself: the user is watching it, can stop it, and a turn ceiling
            // just ends real work at an arbitrary number that has nothing to do with the task.
            //
            // The field agrees. crush ships no step cap at all (loop detection and context
            // pressure only); opencode's is `agent.steps ?? Infinity`, uncapped unless asked
            // for. What replaces it here is what already exists: stuck detection catches the
            // repeat loops a cap was standing in for, and the context window ends a session
            // that genuinely cannot continue.
            //
            // An explicitly CONFIGURED value is still honoured — someone who sets a limit meant
            // it. Only the invented 200 is gone.
            // NOT `?? int.MaxValue`, which never fired: AppBootstrap substitutes
            // OrchestratorSettings.Unbounded when the config has no orchestrator block, and that
            // object carries MaxWorkerTurns = 200 from the RECORD's default. "Unbounded" is
            // documented in its own source as describing "only the token fields" — so the null
            // check was testing for a null that does not reach here.
            //
            // The user's explicitly configured value, or no cap at all.
            ConfiguredMaxWorkerTurns ?? int.MaxValue,

            // THE CONTEXT BOUND, which is what the "no turn cap" decision above rests on: a
            // single-agent run ends when it runs out of room, not at an arbitrary turn number.
            // The agent's own bound: it compresses its own context from inside its turn loop,
            // which is the only place the measurement that triggers it is taken.
            compressAbove: _orchestrator.EffectiveCompressThreshold(_contextWindow)
                ?? OrchestratorSettings.DefaultCompressThreshold,

            // THE SAME CONTEXT THROUGHOUT. The agent is built once now, so this is the context it
            // keeps for its whole life — prompt N+1 begins with everything prompt N learned.
            context: Context)
        {
            TurnCompleted = calls =>
            {
                OnTurnCompleted(calls);
                // TOKENS TOO. Single-agent records to the Ledger itself and never raised
                // TokensUpdated — that event fires only inside the fan-out driver's stream loop.
                // So the ctx readout and the panel both sat at 0 for an entire single-agent
                // session no matter how many tokens it burned, which is the mode that is the
                // default.
                TokensUpdated?.Invoke(this, Ledger.TotalTokens);
            },

            // OCCUPANCY, which nothing else in this mode observes. Without it the status bar has
            // only the cumulative total to divide by the window — a sum that passes 100% while
            // the context is half empty, and that cannot fall when compression frees space.
            ContextUsed = RecordInputTokens,
            ContextCompressed = (b, a) => ContextCompressed?.Invoke(this, (b, a)),
            ContextEstimated = used => ContextEstimatedUpdated?.Invoke(this, used),
        };

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
        CompressionRun.RunAsync(Context, _provider, _jobPanel, _agent.Id,
            "compress context · requested", usage =>
            {
                Ledger.Record(usage);
                TokensUpdated?.Invoke(this, Ledger.TotalTokens);
            }, ct, compressed: (b, a) => ContextCompressed?.Invoke(this, (b, a)));





    /// <summary>
    /// Nothing to release: the schedulers this used to own died with the dag. Kept because the
    /// composition root disposes the outgoing runner on every F5 rewire.
    /// </summary>
    public void Dispose() { }
}
