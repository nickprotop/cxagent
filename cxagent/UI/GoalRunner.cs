using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
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

    /// <summary>Run as ONE agent with tools rather than planning a dag. Set by the caller from
    /// --fan-out / orchestrator.fanOut. An EXPLICIT flag rather than "is llm_agent registered":
    /// absence is equally true of any registry built without a provider, and every dag test builds
    /// one — inferring the mode silently rerouted twenty-three of them into a loop they were not
    /// written for.</summary>
    private readonly bool _singleAgent;
    private readonly LogFileManager? _logs;
    private readonly int _maxParallel;
    private readonly Func<Job, JobDag, DagScheduler, CancellationToken, Task>? _onJobFailed;

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

    // Guards CurrentDag + _currentScheduler together (review I1 #3): without it, two overlapping
    // RunCoreAsync calls (a second goal submitted while a first goal's recovery flow is still
    // pending) could interleave the two fields' writes, or a reader could observe one goal's dag
    // paired with another goal's scheduler.
    private readonly object _stateLock = new();
    private JobDag? _currentDag;
    private DagScheduler? _currentScheduler;

    // Copilot mode's approval seam (P9 Task 1). Non-null only while a goal is actually sitting in
    // GoalState.Draft awaiting the UI's answer; the gate below creates it right before awaiting and
    // clears it right after, all under _stateLock — so ApproveDraft/DiscardDraft (called from the UI
    // thread, per Task 2's F9 binding) never race a fresh RunAsync call replacing it for the NEXT
    // goal. TaskCompletionSource<bool>, not a bare Task: RunOnceAsync-style helpers all assume
    // .Result/.Wait() is safe off the UI thread, but this app installs a SynchronizationContext, so
    // the awaiting side (RunCoreAsync) must genuinely `await` it — see the class doc's sync-context
    // note reflected in the brief. RunContinuationsAsynchronously so ApproveDraft/DiscardDraft (called
    // synchronously from a UI key handler) never runs RunCoreAsync's continuation inline on the UI
    // thread's call stack.
    private TaskCompletionSource<bool>? _pendingApproval;

    // Every DagScheduler this runner has ever created, disposed together in Dispose() (review round
    // 2, N2). Round 1's fix disposed the PREVIOUS scheduler as soon as a new goal's scheduler swapped
    // in and the previous one was quiescent — but "quiescent right now" says nothing about whether a
    // caller is mid-recovery-dialog holding a TryGetSession()-captured reference to it (WaitForQuiescenceAsync
    // is a point-in-time sample, not a lease — see its own doc). Disposing eagerly there recreated
    // C1's exact symptom one layer up: a captured scheduler could go disposed out from under a
    // pending F6/automatic recovery. Trade-off: schedulers from earlier goals in a long session now
    // accumulate until Dispose() (app shutdown, or an F5 provider rewire) instead of being released
    // one goal at a time — bounded by session length, and each instance is just a
    // CancellationTokenSource + two SemaphoreSlims, so this is a deliberately cheap price for
    // guaranteeing a captured scheduler is never pulled out from under an in-flight recovery.
    private readonly List<DagScheduler> _allSchedulers = new();

    /// <summary>
    /// The JobDag for the goal currently running (or most recently run), if any. Exposed so a
    /// diagnosis/recovery flow triggered from outside RunAsync — F6 (manual, ungated) or the
    /// automatic post-failure hook above — can locate the failed job's dag to apply a
    /// DagModifier.TryApply against. Set when a plan compiles; not cleared after the goal ends, so a
    /// diagnosis triggered on a job in a just-finished goal still resolves.
    ///
    /// NOTE (review round 2, N2): reading this alone and later calling <see cref="RetryJobAsync"/> is
    /// UNSAFE across an unbounded await (e.g. a recovery dialog) — a second goal can start in between,
    /// swap in a NEW dag/scheduler pair, and the later RetryJobAsync call would then act on (or
    /// silently miss, or throw against a disposed) the WRONG scheduler. Callers that hold a dag across
    /// an await and later need to act on it MUST use <see cref="TryGetSession"/> up front instead and
    /// retry through the CAPTURED scheduler, not through this property + RetryJobAsync/SkipJobAsync.
    /// </summary>
    public JobDag? CurrentDag { get { lock (_stateLock) return _currentDag; } }

    /// <summary>
    /// Atomically captures the dag+scheduler pair for the goal currently running (or most recently
    /// run) as a single matched snapshot. This is the review-round-2 fix for N2: a caller that needs
    /// to act on a dag later (after an unbounded await, e.g. a recovery confirmation dialog) must
    /// capture BOTH here, up front, and retry through the returned <paramref name="scheduler"/>
    /// directly (<c>DagScheduler.RetryAsync</c>/<c>SkipAsync</c> are public) — never by re-reading
    /// <see cref="CurrentDag"/> and calling <see cref="RetryJobAsync"/> afterward, which re-reads
    /// <c>_currentScheduler</c> at that later point and can land on a different goal's scheduler (or
    /// one already disposed) if a second goal started while the caller was awaiting the user.
    /// </summary>
    public bool TryGetSession(out JobDag? dag, out DagScheduler? scheduler)
    {
        lock (_stateLock)
        {
            dag = _currentDag;
            scheduler = _currentScheduler;
        }
        return dag is not null && scheduler is not null;
    }

    /// <summary>
    /// Retries a Failed job in the CURRENTLY-tracked dag through the live scheduler — i.e. re-reads
    /// state at call time. Safe only when called back-to-back with no intervening await on user input
    /// (see <see cref="CurrentDag"/>'s note); a caller spanning a user dialog must use
    /// <see cref="TryGetSession"/> and drive the captured <c>DagScheduler</c> directly instead. Returns
    /// whether the retry actually queued (false — reported, never silent, per review C1 — if there was
    /// no scheduler, the job wasn't Failed, or it had exhausted RetryCount/MaxRetries and
    /// <paramref name="force"/> was false). <paramref name="force"/> bypasses the retry-count cap for
    /// a user-requested retry (F6); the automatic post-failure path must never pass true.
    /// </summary>
    public Task<bool> RetryJobAsync(string jobId, bool force = false)
    {
        DagScheduler? scheduler;
        lock (_stateLock) scheduler = _currentScheduler;
        return scheduler?.RetryAsync(jobId, force) ?? Task.FromResult(false);
    }

    /// <summary>Skips a job in the CURRENTLY-tracked dag through the live scheduler (same
    /// re-reads-at-call-time caveat as <see cref="RetryJobAsync"/> — see <see cref="CurrentDag"/>'s
    /// note). Returns whether the skip actually applied (false — reported, never silent — if there
    /// was no scheduler or the job was already Succeeded/Running/unknown).</summary>
    public Task<bool> SkipJobAsync(string jobId)
    {
        DagScheduler? scheduler;
        lock (_stateLock) scheduler = _currentScheduler;
        return scheduler?.SkipAsync(jobId) ?? Task.FromResult(false);
    }

    /// <summary>
    /// The gate for AUTOMATIC post-failure diagnosis (spec §Diagnosis Request): fire only while
    /// retry headroom remains, AND never for a user permission denial — that failure means "the
    /// user said no", not "something is broken", and a paid diagnosis round cannot repair a
    /// user's decision. Extracted as a pure, directly-testable predicate — manual (F6) diagnosis
    /// in AppBootstrap deliberately does NOT call this; it is ungated by design.
    /// </summary>
    public static bool ShouldAutoDiagnose(Job job) =>
        job.RetryCount < job.MaxRetries && job.Result?.PermissionDenied != true;

    /// <summary>True while a copilot-mode goal is sitting in GoalState.Draft, waiting on
    /// ApproveDraft/DiscardDraft. This is the seam Task 2's F9 handler polls/binds against.</summary>
    public bool HasPendingApproval { get { lock (_stateLock) return _pendingApproval is not null; } }

    /// <summary>
    /// P9b: the mid-goal gate. Shows the jobs the orchestrator wants to ADD and waits on the SAME
    /// F9/Esc seam the initial plan uses, so there is one approval mechanism rather than two.
    ///
    /// <para>Called by OrchestratorLoop at quiescence — after a drive has finished and before the
    /// next one starts — so awaiting a decision of unbounded duration here cannot race
    /// DagScheduler's no-overlapping-drives contract. The loop's own contract guarantees that
    /// position; see the note on its <c>approveAddedJobs</c> parameter.</para>
    ///
    /// <para>Returns false when the user declines OR when the goal is cancelled while waiting: both
    /// mean "do not run these", and the loop treats a refusal as a clean stop rather than an error.</para>
    /// </summary>
    private async Task<bool> AwaitAddedJobApprovalAsync(IReadOnlyList<Job> added, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock) _pendingApproval = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        // Name them. "3 new jobs" is not reviewable — the user is being asked to approve THESE, and
        // the whole point of copilot is that they can see what they are approving.
        _jobPanel.SetDraftMode(true);
        DraftPending?.Invoke(this, true);
        _sink.ShowApprovalRequest(
            $"Orchestrator wants to add {added.Count} job(s): "
            + string.Join(", ", added.Select(j => $"{j.DisplayName} ({j.PluginType})")));

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return false;   // goal cancelled while waiting — decline, do not throw into the loop
        }
        finally
        {
            lock (_stateLock) _pendingApproval = null;
            _jobPanel.SetDraftMode(false);
            DraftPending?.Invoke(this, false);
        }
    }

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
        PluginRegistry pluginRegistry, LogFileManager? logs = null, int maxParallel = 4,
        OrchestratorSettings? orchestrator = null,
        Func<Job, JobDag, DagScheduler, CancellationToken, Task>? onJobFailed = null,
        int? contextWindow = null,
        bool singleAgent = false)
    {
        _singleAgent = singleAgent;
        _provider = provider;
        _sink = sink;
        _jobPanel = jobPanel;
        _plugins = pluginRegistry;
        _logs = logs;
        _maxParallel = maxParallel;
        _onJobFailed = onJobFailed;
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

        // SINGLE-AGENT MODE takes a different path entirely: no plan, no dag, no consult.
        //
        // An EXPLICIT flag, not "is llm_agent registered". Absence is true of single-agent mode and
        // equally true of any registry built without a provider — every DAG test constructs one, and
        // inferring the mode silently rerouted twenty-three of them into a loop they were not
        // written for. Schema and guidance still derive from the registry, because there the
        // question really is "what may be planned"; control flow is a different question.
        if (_singleAgent)
        {
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
            };
            return await single.RunAsync(goalId, conversation, ct);
        }

        ToolCall? planCall = null;
        var assistantText = new StringBuilder();

        try
        {
            await foreach (var chunk in _provider.ChatStreamAsync(conversation, new() { CreatePlanTool.BuildDefinition(_plugins) }, ct))
            {
                if (chunk.TextDelta is { } td && td.Length > 0)
                {
                    assistantText.Append(td);
                    _sink.AppendAssistant(assistantId, td);
                }
                if (chunk.ToolCallDelta is { Name: "create_plan" } tc)
                    planCall = tc;   // v1: the driver surfaces the whole create_plan call in one delta
                if (chunk.Usage is { } usage)
                {
                    Ledger.Record(usage);
                    TokensUpdated?.Invoke(this, Ledger.TotalTokens);
                    RecordInputTokens(usage.InputTokens);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (LlmProviderException ex)
        {
            // End the turn before reporting: a provider failure otherwise leaves the spinner running
            // UNDER the error message, which reads as "it is retrying" when nothing is.
            _sink.EndAssistantTurn(assistantId);
            _sink.ShowError(ex.Message);          // Message only — never VendorBody
            return GoalState.Failed;
        }
        catch (Exception ex)
        {
            _sink.EndAssistantTurn(assistantId);
            _sink.ShowError(ex.Message);
            return GoalState.Failed;
        }

        // Close the planning turn. It was opened with thinking:true, and the control only clears that
        // when a message gets BODY CONTENT — so a turn where the model returned a create_plan call and
        // no prose (the NORMAL case) span forever, reading as "still working" long after the goal
        // finished.
        //
        // When there was no prose, SAY WHAT HAPPENED rather than collapsing to an empty bubble. The
        // model did do something — it designed a plan — and a blank turn between the user's goal and a
        // sudden list of jobs reads as a gap where the reasoning should be. The plan's own one-line
        // summary is used when it gave one, since that is the model describing its own intent.
        if (assistantText.Length == 0 && planCall is not null)
            _sink.AppendAssistant(assistantId, DescribePlan(planCall.Arguments));

        _sink.EndAssistantTurn(assistantId);

        if (assistantText.Length > 0)
            conversation.Add(new ChatMessage { Role = "assistant", Content = assistantText.ToString(), Timestamp = DateTimeOffset.UtcNow });

        if (Ledger.TotalTokens > 0)
        {
            var tokensTurnId = _sink.BeginAssistantTurn();
            _sink.AppendAssistant(tokensTurnId, $"tokens: {Ledger.TotalTokens}");
            _sink.EndAssistantTurn(tokensTurnId);
        }
        if (Ledger.IsBreached)
            return GoalState.Failed;   // Breached already reported via ShowError (see ctor); pause, don't proceed.

        if (planCall is null)
        {
            // NOT an error when the model simply ANSWERED. "hello" needs no plan, and neither does a
            // question about the codebase — the reply is already in the transcript above, and
            // stamping a red "invalid plan" under it told the user something had gone wrong when
            // nothing had. A conversational turn is a legitimate outcome, not a failed goal.
            //
            // It IS an error when the model returned NOTHING at all: no plan and no text means the
            // turn produced no usable result and the user must be told, or the app just sits there.
            // ...but a model that TRIED to plan and emitted the JSON as prose instead of calling the
            // tool is NOT a conversational answer, and treating it as one ends the goal having done
            // nothing. Seen live against a real repo: the model wrote a complete, valid create_plan
            // payload — ids, depends_on, {{list_files.stdout}} references and all — straight into the
            // transcript, the user got a wall of JSON, and no job ever ran.
            //
            // Detected by SHAPE, not by parsing: a conversational reply does not contain create_plan's
            // structural keys. One repair attempt, same contract as the compile-failure path below —
            // the error is specific and actionable, so a model that cannot use it once will not use
            // it five times, and each attempt is billed.
            if (LooksLikeAPlanAsText(assistantText.ToString()))
            {
                _sink.ShowError("the model wrote the plan as text instead of calling create_plan "
                    + "— asking it to use the tool.");

                conversation.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "That was written as ordinary text, so nothing ran. Do not print the "
                            + "plan. CALL the create_plan tool with those same jobs.",
                    Timestamp = DateTimeOffset.UtcNow,
                });

                var retried = await TryPlanOnceAsync(conversation, assistantId, ct);
                if (retried is not null)
                {
                    planCall = retried;
                    goto HavePlan;
                }

                _sink.ShowError("the model did not call create_plan; nothing was run.");
                return GoalState.Failed;
            }

            if (assistantText.Length > 0)
                return GoalState.Completed;

            _sink.ShowError("the model returned neither a plan nor an answer.");
            return GoalState.Failed;
        }

        HavePlan:
        JobDag dag;
        try
        {
            dag = PlanCompiler.BuildDag(goalId, planCall.Arguments, _plugins);
        }
        catch (Exception ex)
        {
            // ONE repair attempt before giving up, because the compiler's guards reject conditions
            // the model can genuinely fix — a reference missing from depends_on (D11), a paraphrased
            // job id (D8), a non-writing role told to overwrite what it read (D7).
            //
            // The consult path has always worked this way: ConsultJobCompiler RETURNS its error and
            // the orchestrator re-plans against it, which is the entire point of P8's adaptive loop.
            // PlanCompiler THROWS, and this catch turned a recoverable planning mistake into a dead
            // goal. Measured: a fan-out trial that previously wrote a 19-byte placeholder file (bad)
            // began producing NOTHING AT ALL (worse) the moment D11's guard started firing here.
            //
            // Deliberately ONE retry, not a loop: the error text is specific and actionable, so a
            // model that cannot use it once will not use it five times — and each attempt is a paid
            // call against the goal's budget.
            _sink.ShowError($"invalid plan: {ex.Message} — asking the model to correct it.");

            conversation.Add(new ChatMessage
            {
                Role = "user",
                Content = $"That plan was rejected: {ex.Message}\n\n"
                        + "Call create_plan again with that specific problem fixed. Change nothing else.",
                Timestamp = DateTimeOffset.UtcNow,
            });

            // A FRESH TURN for the repair. The original turn was ended at :378, long before this
            // path runs — so passing assistantId here streamed the corrected plan into a CLOSED
            // message: no spinner, no sign anything was happening, and the error line left standing
            // alone as if the goal had simply failed. The user reported exactly this: "we lose the
            // assistant spinner, and we have no feedback of what re-arranged/re-ran. Only the error
            // persists."
            var repairId = _sink.BeginAssistantTurn();
            ToolCall? repaired;
            try
            {
                repaired = await TryPlanOnceAsync(conversation, repairId, ct);
            }
            finally
            {
                // Ended unconditionally: a repair that returns no text must not leave a spinner
                // running forever, and EndAssistantTurn drops an empty turn entirely rather than
                // leaving a blank block behind.
                _sink.EndAssistantTurn(repairId);
            }
            if (repaired is null)
            {
                _sink.ShowError("invalid plan: the model did not return a corrected create_plan call.");
                return GoalState.Failed;
            }

            try
            {
                dag = PlanCompiler.BuildDag(goalId, repaired.Arguments, _plugins);

                // SAY THE REPAIR WORKED, and what it produced. Without this the transcript showed
                // only the rejection — the user could not tell a successful correction from a goal
                // that had quietly died, because the next thing they saw was jobs appearing with no
                // explanation of why the plan had changed.
                _sink.ShowSystemMessage(
                    $"[green]✔ plan corrected — re-planned with {dag.AllJobs.Count} job(s).[/]");
            }
            catch (Exception retryEx)
            {
                // Rejected twice on the same goal — report the SECOND error, which reflects what the
                // model actually produced after being told, rather than the stale first one.
                _sink.ShowError($"invalid plan (after retry): {retryEx.Message}");
                return GoalState.Failed;
            }
        }

        var executor = new JobExecutor(_plugins, dag, _logs,
            onResource: (jobId, snapshot) => _jobPanel.UpdateResources(jobId, snapshot),
            // The wire that makes a worker's prose visible AS IT GENERATES rather than in one lump
            // when the job ends.
            onTextDelta: (jobId, delta) => _jobPanel.AppendText(jobId, delta));
        var scheduler = new DagScheduler(dag, _maxParallel, executor.RunJobAsync);

        // NOT `using`, and NOT disposed here on swap (review round 2, N2 — see _allSchedulers' doc
        // comment for why round 1's "dispose the previous one once quiescent" was unsafe: quiescent
        // now doesn't mean nobody captured a reference via TryGetSession for a still-open recovery
        // dialog). Every scheduler this runner ever creates is tracked and disposed together in
        // Dispose(). CurrentDag/_currentScheduler are swapped together under _stateLock (review I1 #3)
        // so a concurrent reader never observes one goal's dag paired with another goal's scheduler.
        lock (_stateLock)
        {
            _currentDag = dag;
            _currentScheduler = scheduler;
            _allSchedulers.Add(scheduler);
        }

        _jobPanel.SetJobs(dag.AllJobs);

        // Copilot mode (P9 Task 1): the plan is now fully visible (SetJobs just ran) and nothing has
        // executed yet — this is the gate. Off (default) falls straight through with no await and no
        // state change, so the existing behaviour above is byte-for-byte unchanged. On: park the goal
        // in Draft and await the UI's answer instead of driving the loop immediately.
        if (_orchestrator.Copilot)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock) _pendingApproval = tcs;

            // Linked so a Ctrl+C (ct cancelled) while drafting abandons the wait instead of hanging
            // forever — the registration self-unregisters via `using` once the gate resolves either way.
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            // Task 2's UI seam: flip the job panel into draft mode and tell the UI a decision is
            // wanted, in that order — SetJobs above already made the plan visible, so the banner must
            // land before (or atomically with) ShowApprovalRequest, never after.
            _jobPanel.SetDraftMode(true);
            DraftPending?.Invoke(this, true);
            _sink.ShowApprovalRequest();

            bool approved;
            try
            {
                approved = await tcs.Task;
            }
            finally
            {
                lock (_stateLock) _pendingApproval = null;
                // Cleared on EVERY exit from the await — approve, discard, or a cancelled draft (the
                // `finally` still runs when tcs.Task throws OperationCanceledException) — so the panel
                // can never be left showing "DRAFT" past the goal that raised it.
                _jobPanel.SetDraftMode(false);
                DraftPending?.Invoke(this, false);
            }

            if (!approved)
                return GoalState.Cancelled;   // discarded — no loop constructed, nothing runs
            // approved: fall through and execute the EXACT dag shown above — no re-plan.
        }

        // Automatic diagnosis is collected here, not invoked here (review C2). Firing _onJobFailed
        // fire-and-forget from inside JobTransitioned races DagScheduler's "no overlapping drives"
        // contract: JobTransitioned fires from INSIDE the live StartAsync drive (sibling jobs may
        // still be Running/Queued), and RetryJobAsync/SkipJobAsync issued from the hook would then
        // hit DriveAsync's InvalidOperationException guard — swallowed as an unobserved faulted task,
        // since the hook was launched with `_ =`. Instead, just enqueue which jobs qualified; a queue
        // (not a List) because a job diagnosed-and-retried below can itself fail again and re-enter
        // this same JobTransitioned handler WHILE the drain loop below is still running — mutating a
        // List mid-`foreach` throws "Collection was modified".
        //
        // ConcurrentQueue, not Queue (re-review N1): enqueue and dequeue are NOT reliably on one
        // thread. In production ConsoleUISynchronizationContext marshals RunAndReenterAsync's
        // post-await continuation onto the UI thread, so both ends land there — but with no sync
        // context installed (exactly how GoalRunnerTests drives this), that continuation resumes on a
        // thread-pool thread, so Transition -> JobTransitioned -> Enqueue can run on one pool thread
        // while the drain loop's TryDequeue runs on another. The C2 regression test creates that
        // interleaving itself. Queue<T> under concurrent access can drop or duplicate an item or tear
        // its internal size; relying on an ambient sync context that nothing here asserts is not a
        // guarantee.
        var pendingAutoDiagnosis = new ConcurrentQueue<Job>();
        scheduler.JobTransitioned += (_, job) =>
        {
            _jobPanel.UpdateJob(job);
            if (job.State == JobState.Failed && ShouldAutoDiagnose(job))
                pendingAutoDiagnosis.Enqueue(job);
        };

        // The drive is no longer a bare StartAsync here — OrchestratorLoop owns it, and owns every
        // subsequent re-drive too. The diagnosis drain that used to sit after StartAsync is now the
        // loop's per-iteration quiescence step (see DrainAutoDiagnosisAsync): the loop reaches
        // quiescence, runs diagnosis, THEN consults, so the orchestrator only ever sees repaired state
        // and the diagnoser's own RetryAsync can never land mid-drive.
        //
        // The conversation list is the SAME one the planning call above appended to, so the
        // orchestrator keeps its planning context across consults.
        // P9b: in copilot mode the gate follows the goal INTO the loop. P9 gated only the initial
        // plan, so an approved goal could still grow jobs the user never saw — validated, but unseen,
        // and "unseen" is the thing copilot exists to prevent. Null when copilot is off, so the
        // no-gate path stays byte-for-byte what it was.
        var loop = new OrchestratorLoop(_provider, Ledger, _plugins, _orchestrator, _sink,
            approveAddedJobs: _orchestrator.Copilot ? AwaitAddedJobApprovalAsync : null);

        // Returned as-is. Do NOT re-read scheduler.FinalGoalState afterwards: the loop deliberately
        // OVERRIDES it to Failed when a cap ended the goal, and a goal stopped by a cap must never be
        // reported as Completed just because every job that did run happened to succeed. The loop also
        // calls ShowGoalResult itself, so there is no result line to emit here either.
        var result = await loop.RunAsync(dag, scheduler, conversation, ct,
            onQuiescence: _ => DrainAutoDiagnosisAsync(pendingAutoDiagnosis, dag, scheduler, ct));

        return result;
    }

    /// <summary>
    /// Fires AFTER the goal completes, so it never shrinks the conversation a still-running goal is
    /// mid-way through reading.
    ///
    /// P11 Task 3: SUMMARISES the oldest turns via <see cref="SessionCompressor"/> instead of
    /// truncating them outright — truncation deleted four goals' worth of findings on a live drive.
    /// SessionCompressor falls back to <see cref="SessionCommands.Compress"/> — the same routine
    /// <c>/compress</c> calls — only when the summarising call fails, so the two paths can never
    /// diverge in behaviour on the happy path, and the short-conversation floor is inherited either
    /// way. `async` only because that provider call needs it; the call site (<c>RunCoreAsync</c>) was
    /// already `await`ing the goal loop immediately before this, so this was a one-line change there,
    /// not a refactor.
    ///
    /// Governing principle (the user's explicit decision): memory is kept in FULL and reduced only
    /// under MEASURED pressure — never pre-emptively trimmed to a guessed size. A cap that fires while
    /// the context window is half empty throws away the user's context for nothing, so the trigger is
    /// an OBSERVATION (the provider's own last-reported InputTokens — the live context size, exactly,
    /// for free), not a constant like "keep last N goals" or a fixed char budget.
    ///
    /// A null <see cref="_lastInputTokens"/> (no provider usage observed yet — see its own doc for why
    /// a reported 0 never counts as a measurement) means there is nothing to act on, so this is a
    /// deliberate no-op rather than a guess.
    /// </summary>
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
        CompressionRun.RunAsync(Context.Messages, _provider, _jobPanel, _currentGoalId ?? "session",
            "compress context · requested", usage =>
            {
                Ledger.Record(usage);
                TokensUpdated?.Invoke(this, Ledger.TotalTokens);
            }, ct, compressed: (b, a) => ContextCompressed?.Invoke(this, (b, a)));


    /// <summary>
    /// P6's automatic post-failure diagnosis, relocated (not deleted) from its old position after
    /// <c>StartAsync</c> into <see cref="OrchestratorLoop"/>'s per-iteration quiescence step. F6, the
    /// Recovery dialog, and retry/skip all still work exactly as before — what changed is only WHEN
    /// this runs, and that it is now serialised with the loop's own driving.
    ///
    /// <para>Still a drain rather than a snapshot-then-foreach: a retried job that fails again enqueues
    /// a fresh entry (see the JobTransitioned handler's comment) which must be processed, not
    /// dropped.</para>
    /// </summary>
    /// <summary>
    /// What to show for a planning turn the model answered with a tool call and no prose — the normal
    /// case. Prefers the plan's own one-line <c>summary</c> (the model describing its own intent) and
    /// falls back to the job count, so the turn always says SOMETHING rather than collapsing to an
    /// empty bubble between the goal and a sudden list of jobs.
    /// </summary>
    private static string DescribePlan(JsonElement planArgs)
    {
        var jobCount = planArgs.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array
            ? jobs.GetArrayLength()
            : 0;

        var plural = jobCount == 1 ? "job" : "jobs";

        // `summary` is optional in the schema, so a plan without one still gets a useful line.
        if (planArgs.TryGetProperty("summary", out var summary)
            && summary.ValueKind == JsonValueKind.String
            && summary.GetString() is { Length: > 0 } text)
        {
            return $"{text}\n\nDispatching {jobCount} {plural}.";
        }

        return $"Planned {jobCount} {plural}. Dispatching…";
    }

    /// <summary>
    /// One create_plan turn against the current conversation: streams it, bills the tokens, and
    /// returns the tool call — or null if the model produced none.
    ///
    /// <para>Used for the ONE plan-repair attempt after PlanCompiler rejects a plan. It streams
    /// through the same provider call the first attempt used so the repair cannot silently diverge
    /// from it — a second hand-written copy of this loop would drift, and the ledger would stop
    /// counting the retry's tokens the first time someone edited one and not the other.</para>
    ///
    /// <para>Cancellation propagates (the caller is already inside the goal's ct scope); a provider
    /// failure returns null rather than throwing, because the caller's next step is to report a
    /// failed goal either way.</para>
    /// </summary>
    /// <summary>
    /// Whether an assistant reply is a create_plan payload written as PROSE rather than called as a
    /// tool. Detected by SHAPE, deliberately not by parsing: the text may be fenced, truncated
    /// mid-stream, or wrapped in commentary, and a parse that fails on any of those would send a
    /// genuine planning attempt down the "the model just answered" path — which is the bug this
    /// exists to catch.
    ///
    /// <para>Requires BOTH a jobs array and a job-shaped key. A conversational answer that merely
    /// mentions the word "jobs" does not have <c>"type":</c> and <c>"depends_on"</c> next to it, and
    /// a false positive here costs one wasted repair call while a false negative silently ends the
    /// goal having run nothing.</para>
    /// </summary>
    public static bool LooksLikeAPlanAsText(string text)
    {
        if (text.Length < 40) return false;

        var compact = text.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        return compact.Contains("\"jobs\":[", StringComparison.OrdinalIgnoreCase)
            && (compact.Contains("\"depends_on\":", StringComparison.OrdinalIgnoreCase)
                || compact.Contains("\"type\":", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ToolCall?> TryPlanOnceAsync(
        List<ChatMessage> conversation, ChatMessageId assistantId, CancellationToken ct)
    {
        ToolCall? planCall = null;
        try
        {
            await foreach (var chunk in _provider.ChatStreamAsync(
                conversation, new() { CreatePlanTool.BuildDefinition(_plugins) }, ct))
            {
                if (chunk.TextDelta is { } td && td.Length > 0)
                    _sink.AppendAssistant(assistantId, td);
                if (chunk.ToolCallDelta is { Name: "create_plan" } tc)
                    planCall = tc;
                if (chunk.Usage is { } usage)
                {
                    Ledger.Record(usage);
                    TokensUpdated?.Invoke(this, Ledger.TotalTokens);
                    RecordInputTokens(usage.InputTokens);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (LlmProviderException ex)
        {
            _sink.ShowError(ex.Message);   // Message only — never VendorBody
            return null;
        }
        return planCall;
    }

    private async Task DrainAutoDiagnosisAsync(ConcurrentQueue<Job> pending, JobDag dag,
        DagScheduler scheduler, CancellationToken ct)
    {
        if (_onJobFailed is null)
            return;

        while (pending.TryDequeue(out var job))
        {
            // The accounting hole the pre-flight found: OrchestratorEditCount is incremented by the
            // consult loop for ITS edits, so a DagModifier edit made by the DIAGNOSER was invisible to
            // the per-job cap — a job could be repaired indefinitely by alternating between the two
            // mechanisms. Charging the diagnosis round to the same counter puts both repair paths on
            // one budget. Charged BEFORE the round rather than after: the count is what the loop reads
            // when deciding whether this job may still be edited, and whether the round ends in an
            // apply, a skip, or the user cancelling the dialog, it consumed an attempt at repairing
            // this job. (Charging only on a successful TryApply would let a diagnoser that fails to
            // apply retry forever, which is the same runaway from the other side.)
            job.OrchestratorEditCount++;

            try
            {
                // Sequential and awaited: each job's diagnose→confirm→apply→retry/skip round
                // (AppBootstrap's shared DiagnoseJobAsync) runs to full completion — including any
                // resulting drive — before the next one starts, so no two automatic diagnoses can
                // ever overlap a drive against the same scheduler either. And because the whole drain
                // is itself the loop's quiescence step, none of them can overlap the LOOP's drives.
                await _onJobFailed(job, dag, scheduler, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Diagnosis is best-effort and must never take down the goal or vanish silently
                // into an unobserved faulted task (review C2's secondary hazard) — RecoveryFlow,
                // DagModifier, and the scheduler wrappers are not documented never-throw the way
                // JobDiagnoser itself is. This matters MORE now than it did after StartAsync: a throw
                // escaping here would surface inside OrchestratorLoop.RunAsync, where it is observed
                // and would end the goal.
                _sink.ShowError($"automatic diagnosis failed for '{job.DisplayName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Disposes EVERY scheduler this runner has ever created (review I1 #1 — otherwise a goal's
    /// scheduler, and its CancellationTokenSource/SemaphoreSlims, would never be released for the
    /// rest of the process's lifetime once nothing references it any more; round 2's N2 fix means
    /// they're no longer disposed one-at-a-time on each new goal's swap, so this is now the ONLY
    /// release point — see _allSchedulers' doc comment for why). Idempotent and clears the tracking
    /// state (review round 2, N7): a second Dispose() call is then a no-op instead of double-disposing
    /// the same instances, and any RetryJobAsync/SkipJobAsync/TryGetSession call after Dispose
    /// correctly sees "no scheduler" (returns false / no session) instead of dereferencing a disposed
    /// instance. A scheduler still referenced elsewhere via a round-2 TryGetSession() capture (e.g. a
    /// recovery dialog open when Dispose() is called — app shutdown, or an F5 provider rewire) will
    /// still be disposed here; that is an accepted, narrow edge case at those two specific moments
    /// (shutdown, provider swap) rather than on every ordinary goal transition.
    /// </summary>
    public void Dispose()
    {
        List<DagScheduler> schedulers;
        lock (_stateLock)
        {
            schedulers = new List<DagScheduler>(_allSchedulers);
            _allSchedulers.Clear();
            _currentScheduler = null;
        }
        foreach (var scheduler in schedulers)
            scheduler.Dispose();
    }
}
