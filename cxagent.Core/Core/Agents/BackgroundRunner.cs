namespace CxAgent.Core.Agents;

/// <summary>Everything one background stretch of a child's work needs, from start to report.</summary>
/// <param name="Store">The store the child is kept and claimed in; its claim is released through it.</param>
/// <param name="Kept">The child, under the handle the parent reaches it by.</param>
/// <param name="Run">The stretch itself: its token, its state, and the handoff its report goes through.</param>
/// <param name="Prompt">What the child is sent.</param>
/// <param name="Slot">The session's concurrency cap, or null when unbounded.</param>
/// <param name="Delivery">How the report reaches the parent.</param>
/// <param name="Render">
/// Turns the child's answer into the envelope the parent reads — the caller's, because a spawn adds what
/// only it knows (a planner's plan file) and a resume does not.
/// </param>
internal sealed record BackgroundWork(SubAgentStore Store, StoredAgent Kept, BackgroundRun Run,
    string Prompt, SemaphoreSlim? Slot, IAgentDelivery Delivery, Func<SendResult, string> Render);

/// <summary>
/// Runs a child's work off the parent's turn and reports it to the parent when it ends.
///
/// <para>ON ITS OWN TASK, ON THE RUN'S OWN TOKEN: the parent's turn has already returned the receipt
/// and may end long before the child does. Nothing awaits this but a test; everything a caller needs
/// afterwards — the state, the report — is on the run.</para>
/// </summary>
internal static class BackgroundRunner
{
    public static Task Start(BackgroundWork work) => Task.Run(() => RunAsync(work));

    /// <summary>
    /// What a run's handoff does with a result its waiters abandoned — left for the parent, not sent as a
    /// wake: the waiter was cancelled because the user pressed Escape.
    /// </summary>
    public static Func<string, bool> LeaveFor(IAgentDelivery delivery, string parentAgentId, string name) =>
        text => DeliveryOutcomes.Delivered(delivery.Leave(parentAgentId,
            new AgentNotice(Shown: $"background agent '{name}' — its result is waiting for the next turn", ForModel: text)));

    private static async Task RunAsync(BackgroundWork work)
    {
        var (store, kept, run) = (work.Store, work.Kept, work.Run);
        var announced = false;
        var result = new SendResult("", SendOutcome.Failed);

        // THE ANSWER A STOP OVERTOOK, when one did — rendered as the child gave it, while the run is
        // recorded and reported as stopped. See the override below.
        SendResult? overtaken = null;
        string? envelope = null;
        try
        {
            try
            {
                // THE CAP, WAITED ON THE RUN'S TOKEN: a stop while queued ends the wait, and the slot it
                // never took is left alone — the release below is inside the wait's success.
                if (work.Slot is not null) await work.Slot.WaitAsync(run.Token);
                try
                {
                    announced = true;
                    run.MarkRunning();
                    store.AnnounceBegin(kept.Name);

                    // EVERY BACKGROUND STRETCH IS THE PARENT'S ASK: a user's send waits in the foreground instead.
                    kept.Agent.Agent.EchoPrompt(work.Prompt, fromUser: false);

                    // A RESUME READS ITS MAIL FIRST: a message left for a settled child predates this
                    // prompt. A SPAWN DOES NOT — anything sent while it queued was sent after its brief, and
                    // the loop's own drain puts it there.
                    result = await kept.Agent.Agent.SendAsync(work.Prompt, run.Token, mailFirst: run.Start.IsResume);
                    // A STOP THE RUN ALREADY ACCEPTED WINS, even over an answer that came back in the same
                    // instant: whoever stopped it has been told it is stopping, and a "completed" report would
                    // contradict that. For the user's stop it would also leave the user-stop mark standing,
                    // refusing the parent's next agent_send to a child it was told had finished.
                    //
                    // THE ANSWER STILL GOES IN THE REPORT, under its own outcome: the work it describes was
                    // done, and an envelope saying "cancelled before finishing" over a whole answer would have
                    // the parent treat it as unfinished. The report's head says both facts — see ReportFor.
                    if (!run.MarkAnswered())
                    {
                        overtaken = result;
                        result = result with { Outcome = SendOutcome.Cancelled };
                    }
                }
                finally
                {
                    work.Slot?.Release();
                }
            }
            catch (OperationCanceledException) { result = new SendResult("", SendOutcome.Cancelled); }
            catch (Exception ex) { result = new SendResult(ex.Message, SendOutcome.Failed); }

            // ENDED THE MOMENT THE WORK IS, before the report is built or the claim released: a stop
            // accepted after this point would tell the user "stopped" over a report that says "error" or
            // "completed", and leave a user-stop mark that refuses the parent's next agent_send.
            run.Finish(result.Outcome == SendOutcome.Cancelled ? BackgroundState.Stopped : BackgroundState.Finished);

            try { envelope = work.Render(overtaken ?? result); }
            catch (Exception ex)
            {
                envelope = SubAgentEnvelope.Render(kept.Agent.Agent.Id, kept.Name, SendOutcome.Failed,
                    $"the report could not be built: {ex.Message}");
            }
        }
        finally
        {
            // WHATEVER HAPPENED ABOVE, THE RUN ENDS HERE: a run left Running holds its claim forever, and a
            // task_output wait on it burns its whole timeout for an answer that never comes.
            envelope ??= SubAgentEnvelope.Render(kept.Agent.Agent.Id, kept.Name, SendOutcome.Failed, "the run failed");
            // A STOP THAT OVERTOOK THE ANSWER IS ANNOUNCED AS ONE: the envelope carries the answer's own
            // "completed", but the row this word settles must read as the report and the notice do — cancelled.
            var state = overtaken is not null
                ? SubAgentEnvelope.WordFor(SendOutcome.Cancelled)
                : SubAgentEnvelope.StateOf(envelope) ?? "error";

            // THROUGH WHICHEVER CALL MATCHES THE ANNOUNCEMENT, as the foreground spawn does: an announced
            // stop is an archived run, and a run that never started must not archive one of zero turns.
            try { if (announced) store.EndSend(kept.Name, state); else store.Release(kept.Name); }
            catch { store.Release(kept.Name); }

            // A STOPPED RUN IS LEFT, NOT TOLD: somebody ended it on purpose, and waking the parent to say
            // so would start a turn about work that was meant to stop.
            var stopped = result.Outcome == SendOutcome.Cancelled;

            // ALREADY FINISHED ON THE ORDINARY PATH; this covers a throw that skipped it. Finish only
            // records the state, so a second call is harmless.
            run.Finish(stopped ? BackgroundState.Stopped : BackgroundState.Finished);

            // A STOPPED RUN'S SHELLS ARE SWEPT AGAIN NOW IT HAS ENDED: the stop killed what was running
            // then, but the child's last tool call may have handed a command to the background after it.
            if (stopped) store.StopShellsOf(kept.Name);
            var shown = Shown(run, state, stopped);
            run.Handoff.Complete(ReportFor(work, envelope, stoppedAsItFinished: overtaken is not null), text => DeliveryOutcomes.Delivered(stopped
                ? work.Delivery.Leave(run.Start.ParentAgentId, new AgentNotice(Shown: shown, ForModel: text))
                : work.Delivery.Tell(run.Start.ParentAgentId, new AgentNotice(Shown: shown, ForModel: text))));
        }
    }


    /// <summary>
    /// The report, self-contained: it may arrive after a compaction or a /clear, at a context that no
    /// longer holds why the child was asked — the reason ShellJobExecutor.Report states every fact.
    /// </summary>
    /// <param name="work">The stretch being reported.</param>
    /// <param name="envelope">The child's answer, or its account of why there is none.</param>
    /// <param name="stoppedAsItFinished">
    /// A stop was accepted in the instant the answer came back: the run is stopped and the answer is whole.
    /// </param>
    internal static string ReportFor(BackgroundWork work, string envelope, bool stoppedAsItFinished = false)
    {
        var run = work.Run;
        var start = run.Start;

        // A STOP SAYS SO, AND SAYS WHO. "Finished" over a stopped run is false, and a model told only that
        // the agent was cancelled treats the work as still owed and redoes a user-stopped task by hand. A
        // user's stop is a decision about the work, not a failure of the agent, so the report carries that
        // decision.
        //
        // A STOP THAT OVERTOOK THE ANSWER SAYS BOTH: the answer below is whole, and the work was still
        // stopped — which, for the user's stop, still means it is not to be redone.
        const string doNotRedo = " Do not redo its work unless the user asks for it again; tell them it was stopped.";
        var by = run.StoppedByUser ? " by the user" : "";
        var ended = run.State != BackgroundState.Stopped ? "has finished."
            : stoppedAsItFinished
                ? $"was stopped{by} just as it finished; its answer is below, but the work was stopped."
                  + (run.StoppedByUser ? doNotRedo : "")
            : run.StoppedByUser
                ? "was stopped by the user before it finished." + doNotRedo
                : "was stopped before it finished.";

        var head = start.IsResume
            ? $"The background agent '{start.Name}' {ended}\nYou asked it: {start.Brief}"
            : $"The background agent '{start.Name}' ({work.Kept.TypeName}) {ended} You spawned it to: "
              + $"{start.Description}\nIts brief was: {start.Brief}";
        return head + "\n" + envelope;
    }

    /// <summary>The line the user sees when a run ends.</summary>
    /// <param name="run">The run that ended.</param>
    /// <param name="state">The envelope's word for how it ended.</param>
    /// <param name="stopped">Whether something stopped it.</param>
    /// <param name="took">How long it ran; null measures up to now, which is every caller but a test.</param>
    internal static string Shown(BackgroundRun run, string state, bool stopped, TimeSpan? took = null)
    {
        var elapsed = Helpers.DisplayNumber.Took(took ?? DateTimeOffset.UtcNow - run.Started);
        return stopped
            ? $"background agent '{run.Start.Name}' stopped · {elapsed}"
            : $"background agent '{run.Start.Name}' finished · {state} · {elapsed}";
    }
}
