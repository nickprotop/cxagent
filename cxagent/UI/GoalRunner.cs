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
/// Runs one goal from the UI: streams the decomposition turn into the chat sink, captures the
/// create_plan tool call from that same stream, compiles it to a JobDag (PlanCompiler), and runs it
/// via a DagScheduler + JobExecutor. One LLM call; P1 reused as libraries; all sink calls are the
/// UI-update seam (marshalling is the sink's responsibility).
/// </summary>
public sealed class GoalRunner : IDisposable
{
    private readonly ILlmProvider _provider;
    private readonly IChatSink _sink;
    private readonly IJobPanel _jobPanel;
    private readonly PluginRegistry _plugins;

    private readonly LogFileManager? _logs;

    private readonly int? _goalTokenBudget;

    /// <summary>
    /// The most recent InputTokens the provider reported — i.e. the live context size, exactly, for
    /// free, straight off the wire (OpenAiWire's prompt_tokens / AnthropicWire's input_tokens).
    /// Null means "no usage has been reported yet this runner's lifetime", which is DIFFERENT from a
    /// reported 0: both wires fall back to 0 when a provider omits usage on a given call, and treating
    /// that 0 as "plenty of room" would mean a provider that never reports usage silently NEVER
    /// auto-compresses while the session grows until the provider rejects it outright.
    ///
    /// Decision (documented per the P10 Task 3 brief): a 0 is never treated as a measurement — it
    /// never triggers compression (there is nothing to justify throwing history away) but it also
    /// never resets or overrides the last REAL positive reading, so a provider that briefly omits
    /// usage on one call doesn't erase the pressure signal from the call before it. A provider that
    /// NEVER reports usage (this field stays null forever) simply never participates in
    /// auto-compression — that is a documented limitation of relying on provider-reported usage
    /// rather than a tokenizer (which this design deliberately does not add), not a silent "safe"
    /// default; it is surfaced in the task report, not hidden behind a fake number.
    /// </summary>
    private int? _lastInputTokens;

    /// <summary>
    /// The id of the goal currently running, for the between-goals compression row.
    ///
    /// <para>A field rather than a parameter because that compression runs in <c>RunAsync</c>'s
    /// <c>finally</c> — outside the scope where <c>RunCoreAsync</c> mints the id, and deliberately so
    /// (see the call site: nine early returns, only one of which reached the old position).</para>
    /// </summary>
    private string? _currentGoalId;


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
    /// The single agent's context, living across every goal this runner drives.
    ///
    /// <para>HELD HERE BECAUSE THE LOOP IS NOT. A <see cref="SingleAgentLoop"/> is constructed per
    /// goal, so a context owned solely by the loop would still die with it — the rebuild-and-discard
    /// this change exists to end, one level up. Owning it here is what actually makes the agent
    /// continuous, and it is also what gives <c>/compress</c> something to compress between goals:
    /// the session conversation holds only prompts and answers, so compressing it reported "nothing
    /// to free" on a session measured at 58,000 tokens.</para>
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
    /// Records a turn's reported input tokens, if it is a real measurement.
    ///
    /// <para>A reported 0 is never a measurement (see <see cref="_lastInputTokens"/>): it must not
    /// overwrite a real prior reading, and it must not read as "the context shrank".</para>
    /// </summary>
    private void RecordInputTokens(int inputTokens)
    {
        if (inputTokens <= 0) return;
        _lastInputTokens = inputTokens;
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

    /// <summary>A goal began; the payload is its id, which is also the name of its log directory.</summary>
    public event EventHandler<string>? GoalStarted;

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
    /// TokensUpdated's "GoalRunner raises it itself at the point of truth" shape. Never fires at all
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

    public GoalRunner(ILlmProvider provider, IChatSink sink, IJobPanel jobPanel,
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
        _goalTokenBudget = _orchestrator.GoalTokenBudget;
        Ledger = new TokenLedger(_goalTokenBudget);
        Context = new AgentContext(contextWindow);
        // On breach, pause and ask — never silently keep spending. For now (Task 4) that means
        // surfacing an error; the interactive raise/continue/cancel dialog is Task 9.
        Ledger.Breached += (_, total) =>
            _sink.ShowError($"token budget exceeded: spent {total}, budget was {_goalTokenBudget}.");
    }

    public async Task<GoalState> RunAsync(string goalText, List<ChatMessage> conversation, CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(goalText, conversation, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _sink.ShowError(ex.Message);   // residual fault → visible, not an unobserved faulted task
            return GoalState.Failed;
        }
        finally
        {
            // HERE, not at the end of RunCoreAsync, because RunCoreAsync has NINE early returns and
            // only one of them reached the old call site. A conversational turn ("what is 2+2?")
            // returns Completed at :385 before a DAG is ever built — so the goals that grow the
            // conversation most cheaply were exactly the ones that never triggered compression.
            //
            // Measured: a live session at 16,241 tokens and 10 messages, against a threshold of 100,
            // never auto-compressed. Both gates wide open, the call simply unreachable.
            //
            // In a finally so it also runs when a goal fails or is cancelled: the conversation grew
            // regardless of how the goal ended, so the bound must apply regardless too.
            // NO COMPRESSION HERE. An agent compresses its OWN context, from inside its own turn
            // loop, where the measurement that triggers it is taken.
            //
            // This route used to run alongside that one, and both read the SAME number —
            // `_lastInputTokens` is the very value the per-turn check acts on, and occupancy is only
            // refreshed when a provider reports it. So after the loop compressed, nothing had
            // re-measured, this guard saw the same over-threshold figure and compressed again: two
            // identical rows on a live drive, 24.5s and 26.1s, the second summarising a context whose
            // older half was already a summary.
            //
            // Fan-out is not a reason to keep it. Sub-agents are self-contained — each owns a
            // SingleAgentLoop and therefore its own context, its own threshold and its own
            // compression — so there is no conversation left that grows a goal at a time with nobody
            // watching it.
        }
    }

    private async Task<GoalState> RunCoreAsync(string goalText, List<ChatMessage> conversation, CancellationToken ct)
    {
        _sink.AddUserTurn(goalText);
        conversation.Add(new ChatMessage { Role = "user", Content = goalText, Timestamp = DateTimeOffset.UtcNow });
        var assistantId = _sink.BeginAssistantTurn();

        var goalId = UlidGenerator.NewId();
        _currentGoalId = goalId;
        // The log directory is named by THIS id, so it is what a user needs to find the run again.
        GoalStarted?.Invoke(this, goalId);

        // ONE AGENT WITH TOOLS. No plan, no dag: the session IS the agent, and this is the
        // only path a goal can take.
        _sink.EndAssistantTurn(assistantId);   // the loop opens its own turns
        var single = new SingleAgentLoop(_provider, _plugins, Ledger, _sink, _jobPanel, _logs,
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

            // THE SAME CONTEXT EVERY GOAL. The loop is rebuilt per goal; the context is not, so
            // goal N+1 begins with everything goal N learned.
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
        return await single.RunAsync(goalId, conversation, ct);
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
        CompressionRun.RunAsync(Context, _provider, _jobPanel, _currentGoalId ?? "session",
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
