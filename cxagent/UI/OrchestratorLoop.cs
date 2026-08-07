using System.Collections.Concurrent;
using System.Text;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>
/// The orchestrator feedback loop: drive → digest → consult → adapt → drive again.
///
/// <para>Structurally this is <see cref="GoalRunner"/>'s auto-diagnosis drain loop generalised —
/// subscribe to <c>JobTransitioned</c>, ENQUEUE during the drive, act only AFTER
/// <c>await scheduler.StartAsync()</c> — and it exists to fix a failure no prompt wording reliably
/// prevents. A live drive asked for two file reviews and "then in a final job that depends on both,
/// write one combined summary to &lt;path&gt;". The model planned three correct reviewer jobs,
/// generated the summary, and never planned a job to WRITE it: it read "write a summary to
/// &lt;path&gt;" as the agent's own task. That is a planning-shape error made before any output
/// exists, so no amount of instruction at plan time catches it. This loop catches it structurally —
/// the orchestrator sees the summary job finish, sees the path still empty, and adds the write job
/// THEN.</para>
///
/// <para>Four properties carry the design:</para>
/// <list type="number">
/// <item><b><c>continue</c> is genuinely free.</b> It is the expected common case, and it does
/// literally nothing: no re-compile, no re-drive, no bookkeeping. If a continue did re-planning work
/// the design collapses into "consult on everything, expensively".</item>
/// <item><b>Everything since the last consult goes in ONE consult.</b> No timer, no window, no config
/// knob (a user decision). Under load more finishes per drive and the batches grow by themselves;
/// there is no magic number to get wrong.</item>
/// <item><b>Never guess.</b> <see cref="ConsultTool.Parse"/> returns null when the reply is unusable;
/// this reports it and stops. Inferring <c>modify</c> from a half-parsed payload could add jobs the
/// user never authorised.</item>
/// <item><b>Never drive concurrently.</b> <c>DagScheduler.DriveAsync</c> throws
/// <see cref="InvalidOperationException"/> while anything is in flight or Queued, and unlike P6's
/// fire-and-forget diagnosis hook that throw would be OBSERVED here and kill the goal. Every
/// adaptation is applied and re-driven at quiescence, sequentially, on this one awaited path.</item>
/// </list>
///
/// <para>THE LOOP OWNS ALL DRIVING. Diagnosis (<c>AppBootstrap.DiagnoseJobAsync</c>, which itself
/// calls <c>DagModifier.TryApply</c> and then <c>scheduler.RetryAsync</c> — a drive — after awaiting a
/// user dialog of unbounded duration) must become a step INSIDE one iteration, at quiescence, BEFORE
/// the consult, so the orchestrator only ever sees repaired state and cannot double-repair a failure
/// the diagnoser was about to fix. That wiring belongs to the caller; what this class guarantees is
/// the seam where it can happen safely and that it never drives concurrently itself.</para>
/// </summary>
public sealed class OrchestratorLoop
{
    private readonly ILlmProvider _provider;
    private readonly TokenLedger _ledger;
    private readonly PluginRegistry _plugins;
    private readonly OrchestratorSettings _settings;
    private readonly IChatSink _sink;

    /// <summary>
    /// Copilot's mid-goal gate (P9b). Given the jobs the orchestrator wants to ADD, returns whether
    /// they may run. Null (the default) means "no gate" — exactly today's behaviour.
    ///
    /// <para>P9 gated only the INITIAL plan, so a goal approved at the start could still grow jobs
    /// the user never saw. Those jobs are validated — both compilers' guards run on them — but
    /// "valid" was never the promise copilot makes. The promise is that nothing runs unseen.</para>
    ///
    /// <para>Awaiting a user decision of unbounded duration HERE is safe, and that is not an
    /// accident: this class's contract is that every adaptation is applied and re-driven AT
    /// QUIESCENCE on one awaited path. The drive has already completed
    /// (<c>await scheduler.StartAsync()</c> before the consult loop), so nothing is in flight to
    /// race and DagScheduler's no-overlapping-drives guard cannot fire. P6's diagnosis flow already
    /// awaits a user dialog at this same point for the same reason.</para>
    /// </summary>
    private readonly Func<IReadOnlyList<Job>, CancellationToken, Task<bool>>? _approveAddedJobs;

    public OrchestratorLoop(ILlmProvider provider, TokenLedger ledger, PluginRegistry plugins,
        OrchestratorSettings settings, IChatSink sink,
        Func<IReadOnlyList<Job>, CancellationToken, Task<bool>>? approveAddedJobs = null)
    {
        _provider = provider;
        _ledger = ledger;
        _plugins = plugins;
        _settings = settings;
        _sink = sink;
        _approveAddedJobs = approveAddedJobs;
    }

    /// <summary>
    /// Runs <paramref name="dag"/> to completion, consulting the orchestrator after each drive
    /// reaches quiescence. Returns the goal's final state — <c>scheduler.FinalGoalState</c> normally,
    /// but deliberately OVERRIDDEN to <see cref="GoalState.Failed"/> when a cap ended the goal (see
    /// <see cref="RunCoreAsync"/>).
    ///
    /// <para><paramref name="onQuiescence"/> is the seam the class doc's "THE LOOP OWNS ALL DRIVING"
    /// paragraph promises. It is invoked at quiescence, once per iteration, BEFORE the consult — which
    /// is exactly where P6's automatic diagnosis has to live. Its own drives (<c>RetryAsync</c> after a
    /// Recovery dialog) are then serialised with the loop's, and the orchestrator is only ever shown
    /// state the diagnoser has already repaired, so it cannot double-repair the same failure. It is
    /// awaited, so a dialog of unbounded duration simply holds the loop rather than racing it.</para>
    /// </summary>
    public async Task<GoalState> RunAsync(JobDag dag, DagScheduler scheduler,
        List<ChatMessage> conversation, CancellationToken ct,
        Func<CancellationToken, Task>? onQuiescence = null)
    {
        try
        {
            return await RunCoreAsync(dag, scheduler, conversation, ct, onQuiescence);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Same contract as GoalRunner.RunAsync: a residual fault is visible, never an unobserved
            // faulted task.
            _sink.ShowError(ex.Message);
            return GoalState.Failed;
        }
    }

    private async Task<GoalState> RunCoreAsync(JobDag dag, DagScheduler scheduler,
        List<ChatMessage> conversation, CancellationToken ct,
        Func<CancellationToken, Task>? onQuiescence)
    {
        // ConcurrentQueue, not Queue (P6 review N1): JobTransitioned fires on whatever thread the
        // completing job's continuation resumed on. In production ConsoleUISynchronizationContext
        // marshals that back to the UI thread, but with no sync context installed — exactly how the
        // tests drive this — it lands on a thread-pool thread while the drain below runs on another.
        // Queue<T> under concurrent access can drop or duplicate an item, which here means silently
        // never showing the orchestrator a job that finished.
        var finished = new ConcurrentQueue<Job>();
        EventHandler<Job> onTransition = (_, job) =>
        {
            if (IsTerminal(job.State))
                finished.Enqueue(job);
        };
        scheduler.JobTransitioned += onTransition;

        // The goal id comes off the dag rather than a parameter: every job in a compiled plan carries
        // it, and ConsultJobCompiler needs it to stamp jobs it adds (JobDiagnoser's bug was leaving
        // this "").
        var goalId = dag.AllJobs.FirstOrDefault()?.GoalId ?? "";

        // Whether the orchestrator has already given the user a CLOSING word — i.e. finish_goal
        // reported its Summary. Guards against paying for a second closing turn that would restate
        // what was just said. A mid-run rationale deliberately does NOT count; see where it is said.
        var saidSomething = false;

        // One challenge per goal when finish_goal arrives with work still unrun. Not a loop: if the
        // orchestrator looks again and still says done, that is its call.
        var finishChallenged = false;
        var challengePending = false;

        try
        {
            await scheduler.StartAsync();

            var consults = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Diagnosis FIRST, at quiescence, before anything is shown to the orchestrator. This is
                // the ruling: the loop owns all driving, so P6's diagnose→dialog→apply→retry round runs
                // as a step of THIS iteration on THIS awaited path. Two consequences, both wanted:
                // its RetryAsync can no longer land mid-StartAsync (which here would be an OBSERVED
                // InvalidOperationException that kills the goal, not the swallowed one P6's
                // fire-and-forget hook produced); and any transitions its retries produce land in the
                // batch drained BELOW, so the orchestrator is shown repaired state and cannot spend a
                // second repair on a failure the diagnoser already handled.
                if (onQuiescence is not null)
                    await onQuiescence(ct);

                // Drain, not snapshot-then-foreach: a job started by a re-drive can finish and enqueue
                // while we are still reading, and must be seen, not dropped.
                var batch = new List<Job>();
                while (finished.TryDequeue(out var job))
                    batch.Add(job);

                // Nothing finished since the last consult and nothing is left to run: the drive is
                // genuinely over. This is also the exit for `continue` — it deliberately does no work,
                // so the next pass finds an empty queue and falls out here.
                //
                // But it must not exit SILENTLY. `continue` requires no summary by design (paying
                // tokens to justify "nothing happened" would undercut a loop that only re-plans when
                // something is surprising) — correct mid-run, wrong as the LAST word. A live drive of
                // "list ~/bin. what it does?" ran its jobs, gathered the file types and script
                // contents, then ended on this branch having answered nothing: the user got a green
                // "Goal completed" and no reply to the question they actually asked.
                //
                // finish_goal has a Summary path for exactly this. So when the work runs out after a
                // continue, ask for that closing word rather than assuming the jobs spoke for
                // themselves — they are rendered as job blocks, not as an answer.
                // A pending CHALLENGE keeps the loop alive for exactly one more consult. Without
                // this the challenge is appended to the conversation and never sent: the batch is
                // empty and nothing is runnable, so the loop breaks here and the orchestrator never
                // sees the question it was asked.
                if (batch.Count == 0 && !HasRunnableWork(dag))
                {
                    if (!saidSomething)
                        await SayClosingAnswerAsync(dag, conversation, ct);
                    break;
                }

                // A pending CHALLENGE must reach the orchestrator. Without this it is appended to
                // the conversation and never sent: an empty batch takes the D14 re-drive path below
                // and breaks, so the question is asked into the void.
                if (batch.Count == 0 && !challengePending)
                {
                    // D14: this used to be a bare `continue`, commented "let the drive settle" — but
                    // there is no drive to settle. StartAsync returned at QUIESCENCE before this loop
                    // began, so nothing is in flight and nothing will enqueue on its own. With an
                    // empty batch and HasRunnableWork true, neither exit above fires and this spun at
                    // 100% CPU forever: no drive, no await, no consult.
                    //
                    // It is reachable from an ordinary goal. A failed job leaves its dependents
                    // Pending PERMANENTLY — JobDag.IsSatisfied accepts only Succeeded/Skipped
                    // (JobDag.cs:38) and DagScheduler does not cascade-skip — so any goal where a job
                    // fails and something depends on it lands here.
                    //
                    // Re-drive ONCE to be sure: if anything became runnable (a retry re-armed a job,
                    // a dependency was satisfied late), this picks it up and the next pass sees a
                    // non-empty batch. If nothing did, the drive is a no-op and the goal is genuinely
                    // stuck — break rather than ask the orchestrator about a graph that cannot move.
                    // Safe here for the usual reason: this is the at-quiescence path, so a drive
                    // cannot overlap another.
                    await scheduler.StartAsync();

                    if (finished.IsEmpty)
                        break;

                    continue;
                }
                challengePending = false;

                if (++consults > _settings.MaxConsults)
                {
                    // A cap that ends a goal silently is worse than no cap: the user must learn WHY it
                    // stopped, and must not be told it succeeded. scheduler.FinalGoalState would read
                    // Completed here whenever every job happened to succeed — so override it.
                    _sink.ShowError(
                        $"orchestrator stopped: reached the maximum of {_settings.MaxConsults} consults for this goal. "
                        + "The plan was still being changed when the cap was hit, so the goal is incomplete.");
                    return GoalState.Failed;
                }

                // A VISIBLE INDICATOR while the orchestrator decides what to do next. Without this
                // the consult was a bare await: jobs finished, their rows stopped changing, and the
                // UI showed nothing at all until the next decision landed — which on a slow local
                // model is tens of seconds of a screen that looks finished or hung. The user cannot
                // tell "thinking" from "stopped".
                //
                // Reuses the assistant-turn spinner rather than inventing a widget. The
                // Begin/End contract is strict (IChatSink.EndAssistantTurn: every Begin MUST be
                // ended, including turns that produce no text, or the spinner spins forever), so the
                // End goes in a finally — a consult that throws or is cancelled must not leave a
                // permanent spinner behind.
                var thinkingId = _sink.BeginAssistantTurn();
                ConsultDecision? decision;
                try
                {
                    decision = await ConsultAsync(batch, dag, conversation, ct);
                }
                finally
                {
                    _sink.EndAssistantTurn(thinkingId);
                }
                if (decision is null)
                {
                    // ConsultTool.Parse returned null: the reply was unusable. Report and stop — never
                    // guess an action from a half-parsed payload.
                    // Failed, not break-into-Completed. `break` falls through to
                    // ShowGoalResult(scheduler.FinalGoalState), which reads Completed whenever every
                    // job happened to succeed — so a goal that stopped because the orchestrator
                    // became UNINTELLIGIBLE was reported as success. Same reasoning, and the same
                    // override, as the MaxConsults cap immediately above.
                    //
                    // Observed live on the third chained goal of a session: the consult reply became
                    // unparseable as the conversation grew, and the user was shown an error line
                    // followed by a green "Goal completed".
                    _sink.ShowError("orchestrator stopped: the consult reply could not be understood.");
                    _sink.ShowGoalResult(GoalState.Failed, dag.AllJobs.Count(j => j.State == JobState.Failed));
                    return GoalState.Failed;
                }

                // NOT counted as having addressed the user: a rationale explains why the
                // orchestrator is doing something next ("because the file is missing"), which is
                // narration mid-run, not an answer to what was asked. Treating it as one suppressed
                // the closing answer on every goal, since `continue` carries a rationale too.
                if (!string.IsNullOrWhiteSpace(decision.Rationale))
                    Say(decision.Rationale!);

                if (decision.Action == ConsultAction.FinishGoal
                    && UnrunWorkRemains(dag) is { } unrun && !finishChallenged)
                {
                    // CHALLENGE IT, ONCE. A live drive of "review X and Y, then write a combined
                    // report to AUDIT.md" reviewed both files, answered in prose, and called
                    // finish_goal — the file was never written and the goal reported success. A
                    // deliverable the user has to notice is missing is worse than a visible failure.
                    //
                    // Once, not a loop: if the orchestrator looks again and still says done, that is
                    // its call to make. This costs one consult against a goal that would otherwise
                    // have ended silently incomplete.
                    finishChallenged = true;
                    challengePending = true;
                    conversation.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = $"Not so fast: {unrun}\n\n"
                                + "Re-read the user's original request. If every part of it has "
                                + "genuinely been done, call finish_goal again and it will be "
                                + "honoured. If something it asked for has NOT been produced — a "
                                + "file that was never written, a step that never ran — use `modify` "
                                + "to add the job that produces it.",
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    continue;
                }

                if (decision.Action == ConsultAction.FinishGoal)
                {
                    var closing = string.IsNullOrWhiteSpace(decision.Summary)
                        ? "orchestrator: goal finished."
                        : decision.Summary!;
                    Say(closing);
                    // The answer, not the working: a follow-up prompt in this same conversation needs
                    // to know what this goal FOUND, not the jobs it ran to find it. Job digests are
                    // bounded per goal but UNBOUNDED across goals — appending them here would make the
                    // conversation grow without limit across a whole session's worth of goals.
                    conversation.Add(new ChatMessage
                        { Role = "assistant", Content = closing, Timestamp = DateTimeOffset.UtcNow });
                    saidSomething = true;
                    break;
                }

                if (decision.Action == ConsultAction.Continue)
                    continue;   // the whole point: costs nothing.

                await ApplyModificationAsync(dag, scheduler, goalId, batch, decision, conversation, ct);
            }
        }
        finally
        {
            // Unsubscribe on every exit path: the scheduler outlives this call (GoalRunner keeps every
            // scheduler it creates until Dispose), so a leaked handler would keep enqueueing into a
            // queue nobody drains, and a second loop over the same scheduler would see this one's
            // events too.
            scheduler.JobTransitioned -= onTransition;
        }

        var final = scheduler.FinalGoalState;
        _sink.ShowGoalResult(final, dag.AllJobs.Count(j => j.State == JobState.Failed));
        return final;
    }

    /// <summary>
    /// Applies one <c>modify</c> decision at quiescence: cancel what was asked, compile the
    /// modification against the LIVE dag, apply it, then re-drive. Every failure here is reported and
    /// survivable — <see cref="ConsultJobCompiler"/> returns false rather than throwing precisely so a
    /// bad consult cannot kill a goal mid-flight.
    /// </summary>
    private async Task ApplyModificationAsync(JobDag dag, DagScheduler scheduler, string goalId,
        IReadOnlyList<Job> batch, ConsultDecision decision, List<ChatMessage> conversation, CancellationToken ct)
    {
        // Cancellation first, and via SkipAsync — the scheduler's only way to take a job out of the
        // run. It is a drive, so it happens here at quiescence like everything else, never from
        // inside the JobTransitioned handler.
        foreach (var jobId in decision.CancelJobIds)
        {
            var target = dag.TryGet(jobId) ?? dag.AllJobs.FirstOrDefault(j => j.PlanLocalId == jobId);
            if (target is null)
            {
                _sink.ShowError($"orchestrator asked to cancel unknown job '{jobId}'.");
                continue;
            }
            // A RUNNING job is cancelled; anything else is skipped. Routing everything through
            // SkipAsync made `cancel_job_ids` a promise the scheduler could not keep — SkipAsync
            // refuses a running job outright, so the one case the field exists for silently did
            // nothing. CancelJob is not a drive, so it is safe to call on a job that is in flight.
            if (!scheduler.CancelJob(target.Id))
                await scheduler.SkipAsync(target.Id);
        }

        if (decision.Modification is not { } mod)
            return;

        // The per-job edit cap. It is charged to the jobs whose outcome PROMPTED this edit — the batch
        // the orchestrator was just shown — because that is the cycle the cap exists to break: a job
        // fails, gets edited in response, fails again. A job already at its limit stops the edit
        // rather than silently absorbing an unbounded run of them.
        var chargeable = batch
            .Where(j => dag.TryGet(j.Id) is not null)
            .DistinctBy(j => j.Id)
            .ToList();
        var exhausted = chargeable.Where(j => j.OrchestratorEditCount >= _settings.MaxEditsPerJob).ToList();
        if (exhausted.Count > 0)
        {
            _sink.ShowError(
                $"orchestrator edit limit reached for {string.Join(", ", exhausted.Select(j => $"'{j.DisplayName}'"))} "
                + $"({_settings.MaxEditsPerJob} edits); ignoring further changes prompted by it.");
            return;
        }

        if (!ConsultJobCompiler.TryCompile(dag, goalId, mod, _plugins, out var compiled, out var compileError))
        {
            // FEED IT BACK, don't just report it. Reporting and returning left the orchestrator to
            // propose the SAME broken change on its next consult — a live screenshot showed the
            // identical "Plan-local id 'review' is already used" error printed twice in a row, which
            // is the model never being told. GoalRunner's initial-plan path already does this
            // (GoalRunner.cs:428); the consult path did not, and the asymmetry was invisible until
            // the duplicate showed up on screen.
            //
            // No retry loop here: the next consult IS the retry. This just makes sure the model goes
            // into it knowing what was wrong.
            _sink.ShowError($"orchestrator's proposed change could not be compiled: {compileError} "
                + "— telling the model so it can correct it.");

            conversation.Add(new ChatMessage
            {
                Role = "user",
                Content = $"That change was rejected and NOT applied: {compileError}\n\n"
                        + "Propose it again with that specific problem fixed, or choose a different "
                        + "action. Do not repeat the same change unchanged.",
                Timestamp = DateTimeOffset.UtcNow,
            });
            return;
        }

        if (compiled.JobsToAdd.Count == 0 && compiled.ParameterChanges.Count == 0)
            return;   // an empty modification is a no-op, not a re-drive.

        // P9b: the mid-goal copilot gate. AFTER compiling (so the user is shown jobs that actually
        // passed both compilers' guards, not a proposal that would have been rejected anyway) and
        // BEFORE TryApply (so a refusal leaves the dag untouched — nothing to unwind).
        //
        // Safe to await a decision of unbounded duration here: this path runs at QUIESCENCE, after
        // `await scheduler.StartAsync()` returned and before the next drive, so no job is in flight.
        // Refusal is NOT an error — the user looked and said no. Report it and return; the goal ends
        // with whatever it had already completed, exactly as an empty modification does.
        if (_approveAddedJobs is not null && compiled.JobsToAdd.Count > 0)
        {
            if (!await _approveAddedJobs(compiled.JobsToAdd, ct))
            {
                _sink.ShowError(
                    $"declined {compiled.JobsToAdd.Count} job(s) the orchestrator proposed mid-goal; "
                    + "keeping the plan as it stands.");
                return;
            }
        }

        // insertBeforeJobId is null: consult-added jobs wire themselves up through their own
        // depends_on (resolved against the live dag by ConsultJobCompiler), unlike a diagnosis insert
        // which splices in front of one specific failed job.
        if (!DagModifier.TryApply(dag, compiled, insertBeforeJobId: null, out var applyError))
        {
            _sink.ShowError($"orchestrator's proposed change was rejected: {applyError}");
            return;
        }

        foreach (var job in chargeable)
            job.OrchestratorEditCount++;

        // A parameter change on a job that already finished only takes effect if it runs again — the
        // orchestrator changed it BECAUSE of how it turned out. Re-arm those before driving; without
        // this a re-parameterisation is applied to the graph and then never executed.
        foreach (var jobId in compiled.ParameterChanges.Keys)
            if (dag.TryGet(jobId) is { } changed && IsTerminal(changed.State))
                changed.State = JobState.Pending;

        ct.ThrowIfCancellationRequested();

        // The re-drive. Awaited, at quiescence, on this one path — the whole reason modifications are
        // collected during a drive and applied after it.
        await scheduler.StartAsync();
    }

    /// <summary>
    /// The goal's closing word, when the loop ran out of work without the orchestrator ever
    /// addressing the user.
    ///
    /// <para>Job blocks are not an answer. A live drive of "list ~/bin. what it does?" listed the
    /// directory, read the scripts, and ended having said nothing — the data was gathered and shown
    /// as job output, but the QUESTION was never answered. This asks for that, once, with the
    /// finished jobs and their outputs in view.</para>
    ///
    /// <para>Plain text, no tool: the goal is over, so there is nothing left to decide. A failure
    /// here is deliberately silent — the goal itself succeeded, and turning "could not write a
    /// summary" into a visible error would report a failure that did not happen.</para>
    /// </summary>
    private async Task SayClosingAnswerAsync(JobDag dag, List<ChatMessage> conversation, CancellationToken ct)
    {
        var finished = dag.AllJobs.Where(j => IsTerminal(j.State)).ToList();
        if (finished.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("Every job for this goal has finished:");
        sb.AppendLine();
        foreach (var job in finished)
            // FULL output, deliberately — BuildConsultPrompt passes bulkOutputAsPlaceholder:true and
            // this must NOT. The answer to "list ~/bin. what it does?" is written from these bodies;
            // withhold them and this says "I read three scripts" instead of what they do. There is
            // no recovering it either — this call passes tools:null, so nothing can fetch more.
            sb.AppendLine(JobDigest.From(job).Render());
        sb.AppendLine(
            "Answer the user's original request directly, using these results. Be concise. Do not "
            + "describe the jobs themselves — the user can already see them — and do not call a tool.");

        var messages = new List<ChatMessage>(conversation)
        {
            new() { Role = "user", Content = sb.ToString(), Timestamp = DateTimeOffset.UtcNow },
        };

        try
        {
            var response = await _provider.ChatAsync(messages, tools: null, ct);
            _ledger.Record(response.Usage);
            if (!string.IsNullOrWhiteSpace(response.Text))
            {
                Say(response.Text!);
                // Same reasoning as the finish_goal branch: record the ANSWER, not the job digests
                // that prompted it, so a follow-up in this conversation can build on what was found.
                conversation.Add(new ChatMessage
                    { Role = "assistant", Content = response.Text!, Timestamp = DateTimeOffset.UtcNow });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Deliberately swallowed — see the doc comment.
        }
    }

    /// <summary>
    /// One consult: render the batch as digests, ask the model, meter the usage, parse. Returns null
    /// on anything unusable — no reply, no consult call, or a payload
    /// <see cref="ConsultTool.Parse"/> refuses.
    /// </summary>
    private async Task<ConsultDecision?> ConsultAsync(IReadOnlyList<Job> batch, JobDag dag,
        List<ChatMessage> conversation, CancellationToken ct)
    {
        var messages = new List<ChatMessage>(conversation)
        {
            new() { Role = "user", Content = BuildConsultPrompt(batch, dag), Timestamp = DateTimeOffset.UtcNow },
        };
        var tools = new List<ToolDefinition> { ConsultTool.BuildDefinition(_plugins) };

        LlmResponse response;
        try
        {
            // ChatAsync, not ChatStreamAsync — same reasoning as JobDiagnoser: there is no streaming
            // UI to feed here, and what is wanted is one complete tool call to parse.
            response = await _provider.ChatAsync(messages, tools, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (LlmProviderException ex)
        {
            _sink.ShowError(ex.Message);   // Message only — never VendorBody.
            return null;
        }

        // Metered unconditionally: the consult IS a provider call. JobDiagnoser recorded nowhere until
        // P6's I3, which made GoalTokenBudget fiction; a loop that MULTIPLIES provider calls must not
        // repeat that.
        _ledger.Record(response.Usage);

        var call = response.ToolCalls.FirstOrDefault(c => c.Name == "consult");
        return call is null ? null : ConsultTool.Parse(call);
    }

    /// <summary>
    /// The consult prompt: what just finished, as bounded <see cref="JobDigest"/>s. The digest carries
    /// each job's RESOLVED parameters as well as its outcome — a live drive showed the orchestrator
    /// referencing an id it had paraphrased and being unable to see the mistake, because nothing ever
    /// showed it what it had actually written.
    ///
    /// <para>D13: it also lists the jobs that have NOT run yet, with their params. <c>parameter_changes</c>
    /// is documented as "the FULL replacement params object" (ConsultTool.cs), and the batch contains
    /// only jobs that FINISHED — so a job that never ran was never shown, and the contract was
    /// unsatisfiable in exactly the case where repair matters most. A live drive caught the model
    /// saying so, correctly: "I don't have the current params of the 'file' job in the prompt history.
    /// I only have the error." It refused to guess rather than inventing a path, which is why this was
    /// visible at all.</para>
    /// </summary>
    /// <para>PUBLIC for the same reason <see cref="GoalRunner.ShouldAutoDiagnose"/> is: it is a pure
    /// function of (batch, dag) that decides what the orchestrator can SEE, and the test project has
    /// no InternalsVisibleTo grant. Driving it through a whole loop run cannot reach every state —
    /// notably a job that is in the dag but has not run, which the loop re-drives past too quickly to
    /// observe.</para>
    public static string BuildConsultPrompt(IReadOnlyList<Job> batch, JobDag dag)
    {
        var sb = new StringBuilder();
        sb.AppendLine("These jobs finished since you were last consulted:");
        sb.AppendLine();
        foreach (var job in batch)
            // PLACEHOLDER for non-worker output — deliberately NOT what SayClosingAnswerAsync does,
            // which renders the same digests in full. `consult` decides continue/modify/finish over
            // JOBS; a file read's bytes answer none of that, and were measured being truncated
            // 9,007->2,078 on every goal. The closing answer, by contrast, is written FROM these
            // bodies. If you change one call site, read the other first.
            sb.AppendLine(JobDigest.From(job).Render(bulkOutputAsPlaceholder: true));

        // D13: the jobs that have NOT run. Without these, `parameter_changes` — documented as the
        // FULL replacement params — cannot be produced for the one kind of job most likely to need
        // repairing, because a job that never ran never entered a batch.
        //
        // Params only, no outcome: there is nothing to report yet, and a full digest here would
        // repeat what the batch above already says for anything that HAS run. Capped, because a
        // large plan's pending list would otherwise grow the prompt on every consult.
        // UNCAPPED. D13 capped this at 12 to stop a large plan re-listing itself every consult, but
        // the cap silently hid the jobs most likely to need repairing: a plan's later jobs are the
        // ones that depend on everything before them. `parameter_changes` is documented as the FULL
        // replacement params, so a job the orchestrator cannot SEE is a job it cannot fix. Params
        // are still bounded per value by JobDigest's perValueCap, which is where the real weight of
        // this list lives.
        var pending = dag.AllJobs
            .Where(j => !IsTerminal(j.State))
            .Where(j => !batch.Any(b => b.Id == j.Id))
            .ToList();

        if (pending.Count > 0)
        {
            sb.AppendLine("Jobs still to run (their CURRENT params — send these back in full if you change one):");
            sb.AppendLine();
            foreach (var job in pending)
            {
                var digest = JobDigest.From(job);
                sb.Append($"[{(string.IsNullOrEmpty(digest.PlanLocalId) ? digest.Id : digest.PlanLocalId)}] ");
                sb.AppendLine($"{digest.DisplayName} ({digest.PluginType}) — {digest.State}");
                foreach (var (key, value) in digest.Parameters)
                    sb.AppendLine($"    {key}: {value}");
            }
            sb.AppendLine();
        }

        // EVERY plan-local id in the dag, including jobs that finished in an EARLIER batch. Those
        // appear in neither list above — the batch holds only what just finished, and `pending`
        // filters to non-terminal — yet their ids are still live and still collide. A live fan-out
        // failed exactly this way twice: the model proposed 'read_hex' (and 'find_files') for a new
        // job, having no way to know an early, already-completed job owned that name, and the whole
        // modification was rejected. Cheap to send: ids only, no params, no output.
        // Every job in the dag with its STATE, including ones that finished in an EARLIER batch.
        // `batch` is drained per consult (finished.TryDequeue), so a job appears in exactly one
        // consult and is invisible afterwards; `pending` only covers non-terminal work. On a
        // four-way fan-out the reviews land across several consults, so by the last one the
        // orchestrator could see neither the three that already succeeded nor their ids — and it
        // re-planned work that was already done, twice, at real token cost. Naming the state as well
        // as the id is what makes this a work-ledger rather than just a collision guard.
        var ledger = dag.AllJobs
            .Where(j => !string.IsNullOrEmpty(j.PlanLocalId))
            .Select(j => $"{j.PlanLocalId} ({StateWord(j.State)})")
            .ToList();

        if (ledger.Count > 0)
        {
            sb.AppendLine("EVERY job in this plan and its current state. Work marked `done` has "
                          + "ALREADY RUN — do not re-add or repeat it, and do not reuse one of these "
                          + "ids for a new job; reference it in `depends_on` instead:");
            sb.AppendLine($"  {string.Join(", ", ledger)}");
            sb.AppendLine();
        }

        sb.AppendLine(
            "Call `consult` to decide what happens next. Choose `continue` unless something here "
            + "actually calls for a change — it is the common case and costs nothing.");
        return sb.ToString();
    }

    // MaxPendingShown (D13's cap of 12) is GONE. Its premise — "the ones that matter for a repair
    // are the ones near the front" — is backwards: a plan's later jobs are the ones depending on
    // everything before them, so they are the likeliest to need repair and were the first to be
    // hidden. And a hidden job is unrepairable, because parameter_changes requires the FULL
    // replacement params for a job the orchestrator can no longer see.

    /// <summary>The one word the ledger uses for a job's state. Deliberately plainer than the enum:
    /// `done` is unambiguous about "already ran, do not repeat", which is the decision this line
    /// exists to inform, whereas `Succeeded` invites the reading that it merely succeeded at being
    /// planned. Skipped reads as `not run` because that is what the orchestrator must act on — a
    /// skipped job is work still missing, not work completed.</summary>
    private static string StateWord(JobState state) => state switch
    {
        JobState.Succeeded => "done",
        JobState.Failed => "FAILED",
        JobState.Running => "running now",
        JobState.Queued or JobState.Pending => "not run yet",
        JobState.Skipped => "not run (skipped)",
        JobState.Cancelled => "cancelled",
        _ => state.ToString(),
    };

    /// <summary>
    /// A one-line description of work the plan still has outstanding, or null when everything has
    /// genuinely settled.
    ///
    /// <para>Pending/Queued/Running jobs only. A FAILED or SKIPPED job is not "unrun work" — it had
    /// its chance, and challenging finish_goal over it would nag on every goal that ends with a
    /// known failure, which is exactly how a useful check becomes noise the model learns to
    /// dismiss.</para>
    /// </summary>
    private static string? UnrunWorkRemains(JobDag dag)
    {
        var unrun = dag.AllJobs
            .Where(j => j.State is JobState.Pending or JobState.Queued or JobState.Running)
            .ToList();
        if (unrun.Count == 0) return null;

        var names = string.Join(", ", unrun.Take(5).Select(j => $"'{j.PlanLocalId ?? j.Id}'"));
        var more = unrun.Count > 5 ? $" (and {unrun.Count - 5} more)" : "";
        return $"{unrun.Count} job(s) in this plan have not run: {names}{more}.";
    }

    private void Say(string text)
    {
        var id = _sink.BeginAssistantTurn();
        _sink.AppendAssistant(id, text);
        _sink.EndAssistantTurn(id);
    }

    /// <summary>Is there still work the scheduler could pick up? Guards the loop's exit: breaking
    /// while jobs are Pending/Queued/Running would abandon a live drive.</summary>
    private static bool HasRunnableWork(JobDag dag) =>
        dag.AllJobs.Any(j => j.State is JobState.Pending or JobState.Queued or JobState.Running);

    private static bool IsTerminal(JobState state) =>
        state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;
}
