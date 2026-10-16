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

                    // A RESUME READS ITS MAIL FIRST: a message left for a settled child predates this
                    // prompt. A SPAWN DOES NOT — anything sent while it queued was sent after its brief, and
                    // the loop's own drain puts it there.
                    result = await kept.Agent.Agent.SendAsync(work.Prompt, run.Token, mailFirst: run.Start.IsResume);
                    run.MarkAnswered();
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

            try { envelope = work.Render(result); }
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
            var state = SubAgentEnvelope.StateOf(envelope) ?? "error";

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
            var shown = Shown(run, state, stopped);
            run.Handoff.Complete(ReportFor(work, envelope), text => DeliveryOutcomes.Delivered(stopped
                ? work.Delivery.Leave(run.Start.ParentAgentId, new AgentNotice(Shown: shown, ForModel: text))
                : work.Delivery.Tell(run.Start.ParentAgentId, new AgentNotice(Shown: shown, ForModel: text))));
        }
    }


    /// <summary>
    /// The report, self-contained: it may arrive after a compaction or a /clear, at a context that no
    /// longer holds why the child was asked — the reason ShellJobExecutor.Report states every fact.
    /// </summary>
    internal static string ReportFor(BackgroundWork work, string envelope)
    {
        var start = work.Run.Start;
        var head = start.IsResume
            ? $"The background agent '{start.Name}' has finished.\nYou asked it: {start.Brief}"
            : $"The background agent '{start.Name}' ({work.Kept.TypeName}) has finished. You spawned it to: "
              + $"{start.Description}\nIts brief was: {start.Brief}";
        return head + "\n" + envelope;
    }

    private static string Shown(BackgroundRun run, string state, bool stopped)
    {
        var took = DateTimeOffset.UtcNow - run.Started;
        var elapsed = took.TotalMinutes >= 1 ? $"{(int)took.TotalMinutes}m {took.Seconds}s" : $"{took.TotalSeconds:0}s";
        return stopped
            ? $"background agent '{run.Start.Name}' stopped · {elapsed}"
            : $"background agent '{run.Start.Name}' finished · {state} · {elapsed}";
    }
}
