using System.Text;
using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>What the task tools can see.</summary>
/// <param name="Live">Shells running now.</param>
/// <param name="Finished">Shells that have ended.</param>
/// <param name="IsSessionAgent">
/// Whether an agent id is, or was, the session's own agent — who may see and stop everything.
///
/// <para>A TEST, NOT AN ID: a re-wire mints a new session agent while a command the previous one started
/// is still running and still the session's. Equality with the current id would name that command
/// somebody else's and hide it once it ends.</para>
/// </param>
/// <param name="Agents">Kept sub-agents, or null for an agent that cannot spawn.</param>
internal sealed record TaskSources(DetachedProcessRegistry Live, FinishedShells Finished,
    Func<string, bool> IsSessionAgent, Agents.SubAgentStore? Agents)
{
    /// <summary>For a caller that knows one session agent id and no other — a child, a test.</summary>
    public TaskSources(DetachedProcessRegistry live, FinishedShells finished, string sessionId,
        Agents.SubAgentStore? agents)
        : this(live, finished, id => id == sessionId, agents) { }

    /// <summary>
    /// A token cancelled when the user types into the running turn, asked for as a wait begins; null
    /// where nobody can — a child, a headless run, /tasks.
    /// </summary>
    public Func<CancellationToken>? UserSpoke { get; init; }
}

/// <summary>
/// <c>task_list</c>, <c>task_stop</c> and <c>task_output</c>: one vocabulary over everything running
/// in the background, addressed by a handle (a sub-agent) or a pid (a shell).
///
/// <para>THE HANDOFF, NOT THE RUN STATE, DECIDES WHETHER A SUB-AGENT HAS ENDED. A run's state flips
/// to finished slightly before its handoff completes, and the handoff is what decides whether the
/// report goes out as a message or to a waiting task_output. Reading "ended" from the state could
/// answer "finished" for a report that has not been settled yet, and a stop issued in that gap
/// would then be told the wrong thing. The state is used only for queued versus running.</para>
///
/// <para>THE SESSION AGENT SEES SUB-AGENTS AND A CHILD DOES NOT. A child has no handle for a
/// sibling and nothing it could do with one; listing them would also hand it the means to read a
/// sibling's report, which is the parent's to receive.</para>
/// </summary>
internal sealed class TaskTools(TaskSources sources)
{
    public const int DefaultWaitSeconds = 120;
    public const int MaxWaitSeconds = 600;

    public bool Claims(string name) => name is Tool.TaskList or Tool.TaskStop or Tool.TaskOutput;

    // REPORTS A CALL TOOK BACK FROM ITS MAILBOX, until the call is recorded. Between the two the reader's copy is
    // the only one, and a cancelled turn discards it — Dropped puts it back.
    //
    // KEYED BY THE CALL ITSELF, NOT ITS ID. A provider that sends no ids leaves every task_output under the name
    // "task_output", and some local servers reuse ids — so one call's Recorded would settle another call's report,
    // and the cancel backfill, which skips an id already answered anywhere in the conversation, would never drop
    // it. The agent hands the same ToolCall instance to the tool, to Record and to the backfill.
    private readonly Dictionary<ToolCall, List<ResultHandoff>> _unrecorded = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The call's result is in the conversation: whatever it took back stays taken. Called for every recorded call;
    /// one that took nothing back is a no-op.
    /// </summary>
    public void Recorded(ToolCall call)
    {
        foreach (var handoff in Untrack(call)) handoff.Settle();
    }

    /// <summary>
    /// The call's result was thrown away — its turn was cancelled first, and the call is answered "cancelled" — so
    /// whatever it took back is left for the agent again. See <see cref="ResultHandoff.Restore"/>.
    /// </summary>
    public void Dropped(ToolCall call)
    {
        foreach (var handoff in Untrack(call)) handoff.Restore();
    }

    private bool Reclaim(ResultHandoff handoff, ToolCall call)
    {
        // TRACKED BEFORE THE RECLAIM, so no moment exists where the copy was withdrawn and nothing would put it
        // back. A reclaim that takes nothing leaves an entry whose Settle and Restore are both no-ops.
        lock (_unrecorded)
        {
            if (!_unrecorded.TryGetValue(call, out var list)) _unrecorded[call] = list = [];
            list.Add(handoff);
        }
        return handoff.Reclaim();
    }

    private List<ResultHandoff> Untrack(ToolCall call)
    {
        lock (_unrecorded) return _unrecorded.Remove(call, out var list) ? list : [];
    }

    /// <summary>
    /// The wait, clamped. A model that asks for an hour would otherwise hold its turn for an hour,
    /// and zero or a negative number would return before anything could have happened.
    /// </summary>
    public static int WaitSeconds(int? requested) =>
        Math.Clamp(requested ?? DefaultWaitSeconds, 1, MaxWaitSeconds);

    public async Task<string> InvokeAsync(string callerAgentId, ToolCall call, CancellationToken ct)
    {
        if (call.Name == Tool.TaskList) return List(callerAgentId);
        if (call.Name is not (Tool.TaskStop or Tool.TaskOutput))
            return $"error: '{call.Name}' is not a tool this handles.";

        var id = ReadId(call.Arguments);
        if (id.Error is { } error) return error;

        // READ LENIENTLY, as every other flag the model sends is: a model that stringifies scalars meant
        // the same thing, and a "true" read as false answers at once a question that asked to wait.
        var wait = Agents.Args.ReadBool(call, "wait") ?? false;
        var timeout = Agents.Args.ReadInt(call, "timeout");

        if (id.Pid is { } pid)
            return call.Name == Tool.TaskStop
                ? StopShell(callerAgentId, pid)
                : await ShellOutputAsync(new ShellRead(callerAgentId, pid, wait, timeout, call), ct);

        var handle = sources.Agents?.HandleFor(id.Handle!) ?? id.Handle!;
        if (call.Name == Tool.TaskStop) return Stop(callerAgentId, handle);

        if (!sources.IsSessionAgent(callerAgentId))
            return "error: only the session agent can read a sub-agent's task.";
        return await OutputAsync(new AgentRead(handle, wait, timeout, call), ct);
    }

    /// <summary>What <c>/tasks</c> shows for one task: never waits and never takes a result, because
    /// a person looking at a task must not be the reason the model is never sent its report.</summary>
    public string Describe(string id)
    {
        if (int.TryParse(id, out var pid)) return DescribeShell(pid);
        id = sources.Agents?.HandleFor(id) ?? id;
        if (sources.Agents?.WasForgotten(id) == true) return Agents.SubAgentStore.Dismissed(id);
        if (sources.Agents?.Find(id) is null) return $"no task '{id}'.";
        if (sources.Agents.BackgroundOf(id) is not { } run) return LastRun(sources.Agents, id);
        return run.Handoff.Ended ? run.Handoff.Result ?? $"'{id}' has ended." : Progress(id, run);
    }

    public string List(string callerAgentId)
    {
        var isSession = sources.IsSessionAgent(callerAgentId);
        var sb = new StringBuilder();

        if (isSession && sources.Agents is { } agents)
        {
            // WHAT IS STILL GOING FIRST, each group in spawn order: a listing is read for what is running, and a
            // session that has spawned twenty agents should not bury the two still working under eighteen that
            // finished. The sort is stable, so within a group the order is the store's.
            foreach (var kept in agents.All().OrderBy(k => IsGoing(agents, k.Name) ? 0 : 1))
            {
                sb.Append("agent '").Append(kept.Name).Append("' (").Append(kept.TypeName).Append(") — ")
                  .Append(AgentState(agents, kept.Name));

                // HOW LONG A RUN HAS GONE, AND WHAT THE CHILD WAS SPAWNED TO DO. The handle is a slug of
                // the description, cut to forty characters; a model that has lost the spawn from its
                // context has only this row to tell two agents apart by, and only the age to judge whether
                // one is stuck or merely long.
                if (agents.BackgroundOf(kept.Name) is { Handoff.Ended: false } going)
                    sb.Append(" · ").Append(Age(going.Started));
                if (kept.Description is { Length: > 0 } description)
                    sb.Append(" — ").Append(description);
                sb.AppendLine();
            }
        }

        // EVERY LIVE SHELL, WHOEVER STARTED IT. The registry is process-wide on purpose, and hiding another
        // agent's command would draw a boundary that does not exist: it is still stoppable by pid by the
        // session, and its owner is named so a child knows whom to ask.
        var live = sources.Live.Live;
        foreach (var entry in live)
        {
            var job = entry.Job;
            sb.Append("shell pid ").Append(entry.Process.Pid)
              .Append(" (").Append(job is null ? "?" : Age(job.Started)).Append(", ")
              .Append(Owner(callerAgentId, job)).Append(") running: ")
              .Append(job?.Command ?? "(unknown — started outside the shell tool)");

            // AND WHERE TO READ IT: the output is in a file, and the run_shell result that named it may
            // have long since left the model's context.
            if (job?.OutputPath is { } path) sb.Append(" -> ").Append(path);
            sb.AppendLine();
        }

        foreach (var done in sources.Finished.Recent(owner => Sees(callerAgentId, owner)))
        {
            sb.Append("shell pid ").Append(done.Job.Pid)
              .Append(done.Killed ? " stopped" : $" exited {done.ExitCode}")
              .Append(" (").Append(Age(done.Ended)).Append(" ago): ").Append(done.Job.Command);
            if (done.Job.OutputPath is { } path) sb.Append(" -> ").Append(path);
            sb.AppendLine();
        }

        // THE SLOT COUNT, when anything runs: a model that cannot start another background command needs
        // to know why rather than guess that the tool call failed.
        if (live.Count > 0)
            sb.Append(live.Count).Append(" of ").Append(DetachedProcessRegistry.MaxConcurrent)
              .AppendLine(" shell slots in use.");

        return sb.Length == 0 ? "nothing is running in the background." : sb.ToString().TrimEnd();
    }

    /// <summary>Queued, running or busy — what <see cref="AgentState"/> reports as anything but ended or idle.</summary>
    private static bool IsGoing(Agents.SubAgentStore agents, string name) =>
        AgentState(agents, name) is "queued" or "running" or "busy";

    private static string AgentState(Agents.SubAgentStore agents, string name)
    {
        // A FOREGROUND CLAIM BEATS AN ENDED BACKGROUND RUN. The run entry stays after its report, so
        // a child resumed by a foreground agent_send still has it; reading that entry alone would
        // call a child that is working right now "finished".
        if (agents.BackgroundOf(name) is { } run && !(run.Handoff.Ended && agents.IsBusy(name)))
        {
            if (run.Handoff.Ended)
            {
                var ended = run.State == Agents.BackgroundState.Stopped ? "stopped" : "finished";
                return run.Handoff.Undelivered ? ended + ", report not delivered" : ended;
            }
            return run.State == Agents.BackgroundState.Queued ? "queued" : "running";
        }
        return agents.IsBusy(name) ? "busy" : "idle";
    }

    private string Stop(string callerAgentId, string handle)
    {
        if (!sources.IsSessionAgent(callerAgentId)) return "error: only the session agent can stop a sub-agent.";
        var agents = sources.Agents;
        if (agents?.WasForgotten(handle) == true) return Agents.SubAgentStore.DismissedForModel(handle);
        if (agents?.Find(handle) is null) return $"no task '{handle}' — {Tool.TaskList} shows what exists.";

        var run = agents.BackgroundOf(handle);
        if (run is null || (run.Handoff.Ended && agents.IsBusy(handle)))
        {
            return agents.IsBusy(handle)
                ? $"'{handle}' is not running in the background; it stops with the turn that asked it."
                : $"'{handle}' is idle; there is nothing to stop.";
        }

        if (run.Handoff.Ended) return $"'{handle}' has already finished; {Tool.TaskOutput} shows its result.";
        if (agents.Stop(handle, byUser: false))
            return $"stopping '{handle}' — it will report as cancelled.";

        // Stop refuses once the child's answer is in: the work is over and only the report is left.
        return $"'{handle}' has completed its work and is reporting; {Tool.TaskOutput} shows its result.";
    }

    /// <summary>A sub-agent's task_output, as the session agent asks for it.</summary>
    /// <param name="Handle">The agent.</param>
    /// <param name="Wait">Block until it ends or the timeout passes.</param>
    /// <param name="Timeout">Seconds, before clamping.</param>
    /// <param name="Call">The call asking — what a report taken back is tracked under until it is recorded.</param>
    private sealed record AgentRead(string Handle, bool Wait, int? Timeout, ToolCall Call);

    private async Task<string> OutputAsync(AgentRead request, CancellationToken ct)
    {
        var (handle, wait, timeout) = (request.Handle, request.Wait, request.Timeout);
        var agents = sources.Agents;
        if (agents?.WasForgotten(handle) == true) return Agents.SubAgentStore.DismissedForModel(handle);
        if (agents?.Find(handle) is null) return $"no task '{handle}' — {Tool.TaskList} shows what exists.";

        var run = agents.BackgroundOf(handle);
        if (run is null) return LastRun(agents, handle);

        if (!run.Handoff.Ended && !wait) return Progress(handle, run);

        var seconds = WaitSeconds(timeout);
        var waited = run.Handoff.Ended
            ? new Waited(run.Handoff.Read(), UserSpoke: false)
            : await WaitOrHearAsync(run.Handoff, seconds, ct);
        var read = waited.Read;

        // THE USER SPOKE: how far it has got, and why the wait ended. An interrupt that raced the end finds
        // the report already settled by the handoff — sent, left as a note, or kept for this read.
        if (waited.UserSpoke)
        {
            if (!read.Ended) return Progress(handle, run) + UserSpokeNote;
            return (read.AlreadySent
                ? $"'{handle}' has just finished; its report comes to you as a message."
                : read.Result ?? $"'{handle}' ended without a report.") + UserSpokeNote;
        }

        if (!read.Ended) return Progress(handle, run) + $"\nStill running after {seconds}s.";
        var report = read.Result ?? run.Handoff.Result ?? $"'{handle}' ended without a report.";
        if (!read.AlreadySent) return report;

        // SENT BUT MAYBE NOT YET READ: a report that ended between two calls waits in the mailbox until the
        // turn's next lap, which comes after this result. Taken back, this copy is the only one. ONLY WHEN THE
        // SESSION'S AGENT WAS THE ONE SENT IT — a report sent to a sub-agent is the sub-agent's, and withdrawing
        // it would leave that agent without the answer it asked for. Not taken back, it was read already, maybe
        // long ago and since compacted away, so it is repeated with a note saying so.
        if (sources.IsSessionAgent(run.Start.ParentAgentId) && Reclaim(run.Handoff, request.Call)) return report;
        return report + "\n(You were also sent this as a message.)";
    }

    /// <summary>A pid's task_output, as one caller asks for it.</summary>
    /// <param name="Caller">Who asks — a child may read only its own commands.</param>
    /// <param name="Pid">The command.</param>
    /// <param name="Wait">Block until it ends or the timeout passes.</param>
    /// <param name="Timeout">Seconds, before clamping.</param>
    /// <param name="Call">The call asking — what an exit taken back is tracked under until it is recorded.</param>
    private sealed record ShellRead(string Caller, int Pid, bool Wait, int? Timeout, ToolCall Call);

    /// <summary>
    /// Whether a finished command belongs in this caller's view: the session sees its own and its
    /// children's; a child sees every one, as it sees every live command (spec decision 29) — hiding an
    /// ended command it could list a moment ago would make a sibling's finished work look like it never ran.
    /// </summary>
    private bool Sees(string callerAgentId, string? owner) =>
        owner == callerAgentId
        || !sources.IsSessionAgent(callerAgentId)
        || owner is not null && (sources.IsSessionAgent(owner) || sources.Agents?.FindByAgentId(owner) is not null);

    /// <summary>How a row names who started a command: "me", "session", an agent id, or "unowned" for a
    /// process nothing ever described.</summary>
    private string Owner(string callerAgentId, BackgroundJob? job)
    {
        if (job is null) return "unowned";
        if (job.AgentId == callerAgentId) return "me";
        if (sources.IsSessionAgent(job.AgentId)) return "session";
        return job.AgentId;
    }

    /// <summary>
    /// Stops one background command, if the caller is allowed to.
    ///
    /// <para>THE SESSION MAY STOP ANY; A SUB-AGENT ONLY ITS OWN. Sub-agents cannot spawn sub-agents, so
    /// every child's parent IS the session — the check is flat equality, with no parent/child table to
    /// consult. A command with no recorded owner is the session's alone to stop.</para>
    ///
    /// <para>A REFUSAL NAMES THE OWNER THE WAY THE LIST DOES, so the model learns whom to ask rather than
    /// retrying the same call.</para>
    /// </summary>
    private string StopShell(string callerAgentId, int pid)
    {
        var entry = sources.Live.Live.FirstOrDefault(j => j.Process.Pid == pid);
        if (entry is null)
            return sources.Finished.Find(pid) is { } done
                ? $"pid {pid} has already {(done.Killed ? "been stopped" : $"exited with code {done.ExitCode}")}."
                : $"no background command with pid {pid}.";

        var owner = entry.Job?.AgentId;
        if (!sources.IsSessionAgent(callerAgentId) && owner != callerAgentId)
        {
            var who = owner is null || sources.IsSessionAgent(owner) ? "the session" : owner;
            return who == "the session"
                ? $"error: pid {pid} was started by the session, not you — only the session can stop it."
                : $"error: pid {pid} was started by {who}, not you — only {who} or the session can stop it.";
        }

        entry.Process.Kill();
        return $"pid {pid} stopped.";
    }

    /// <summary>
    /// A command's output and state, or — waiting — its result once it ends. The result is taken through
    /// its handoff, so an exit notice does not follow an answer the model already has.
    /// </summary>
    private async Task<string> ShellOutputAsync(ShellRead read, CancellationToken ct)
    {
        var live = sources.Live.Live.FirstOrDefault(j => j.Process.Pid == read.Pid);
        var job = live?.Job ?? sources.Finished.JobFor(read.Pid);
        if (job is null && live is null) return $"no background command with pid {read.Pid}.";
        if (!sources.IsSessionAgent(read.Caller) && job?.AgentId != read.Caller)
            return $"error: pid {read.Pid} was started by {(job is null || sources.IsSessionAgent(job.AgentId) ? "the session" : job.AgentId)}, not you.";

        var handoff = sources.Finished.HandoffFor(read.Pid);
        if (live is not null && !(read.Wait && handoff is not null))
            return $"pid {read.Pid} is running ({(job is null ? "?" : Age(job.Started))}).{Tail(job?.OutputPath)}";

        var seconds = WaitSeconds(read.Timeout);
        var waited = handoff is null ? null
            : handoff.Ended ? new Waited(handoff.Read(), UserSpoke: false)
            : await WaitOrHearAsync(handoff, seconds, ct);
        var result = waited?.Read;

        if (waited is { UserSpoke: true, Read.Ended: false })
            return $"pid {read.Pid} is running ({(job is null ? "?" : Age(job.Started))}).{Tail(job?.OutputPath)}"
                   + UserSpokeNote;
        if (result is { Ended: false })
            return $"pid {read.Pid} is still running after {seconds}s.{Tail(job?.OutputPath)}";
        if (waited is { UserSpoke: true, Read.AlreadySent: true })
            return $"pid {read.Pid} has just ended; its result comes to you as a message." + UserSpokeNote;

        var text = result?.Result ?? DescribeEnded(read.Pid);
        text += Tail(job?.OutputPath);
        if (waited is { UserSpoke: true }) return text + UserSpokeNote;
        if (result is not { AlreadySent: true }) return text;

        // TAKEN BACK WHEN STILL UNREAD, as an agent's report is — see OutputAsync. ONLY BY THE AGENT IT WAS SENT
        // TO: the session reading a child's command must not withdraw the exit the child is waiting for.
        if (job is not null && SentTo(read.Caller, job.AgentId) && Reclaim(handoff!, read.Call)) return text;
        return text + "\n(You were also sent this as a message.)";
    }

    /// <summary>
    /// Whether a message sent to <paramref name="recipient"/> is the <paramref name="caller"/>'s to take back: the
    /// same agent, or the session's own agent under either of its ids — a re-wire mints a new one, and a report
    /// sent under the old id sits in the same session's mailbox.
    /// </summary>
    private bool SentTo(string caller, string recipient) =>
        caller == recipient || (sources.IsSessionAgent(caller) && sources.IsSessionAgent(recipient));

    /// <summary>What a wait found, and whether it ended because the user spoke rather than because the task did.</summary>
    /// <param name="Read">The handoff as the wait left it — read again after an interrupt.</param>
    /// <param name="UserSpoke">The wait ended because the user typed into the turn.</param>
    private sealed record Waited(HandoffRead Read, bool UserSpoke);

    /// <summary>Said after a wait the user's message ended, so the model reads it before waiting again.</summary>
    private const string UserSpokeNote =
        "\nThe wait ended because the user sent a message. Read it before you wait on this again.";

    /// <summary>
    /// Waits for the result, ending early when the user types into the turn.
    ///
    /// <para>TWO TOKENS, TOLD APART. The turn's cancellation is Escape, and rethrows as it always does — the
    /// turn is ending. The user speaking is not a cancellation of anything: the turn goes on, and the wait
    /// answers with what it has so the message is read now rather than after the task ends.</para>
    ///
    /// <para>EXACTLY ONCE STILL HOLDS. If the interrupt lands after the handoff chose this waiter,
    /// <see cref="ResultHandoff.WaitAsync"/> treats the wait as abandoned and leaves the result as a note
    /// before rethrowing — the result is not lost and is not delivered twice. The read that follows then
    /// sees it as already sent, or takes it when the note was refused.</para>
    /// </summary>
    private async Task<Waited> WaitOrHearAsync(ResultHandoff handoff, int seconds, CancellationToken ct)
    {
        var spoke = sources.UserSpoke?.Invoke() ?? CancellationToken.None;
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, spoke);
        try
        {
            return new Waited(await handoff.WaitAsync(TimeSpan.FromSeconds(seconds), either.Token), UserSpoke: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && spoke.IsCancellationRequested)
        {
            return new Waited(handoff.Read(), UserSpoke: true);
        }
    }

    /// <summary>What /tasks shows for a pid: never waits and never takes a result.</summary>
    private string DescribeShell(int pid)
    {
        if (sources.Live.Live.FirstOrDefault(j => j.Process.Pid == pid) is { } live)
            return $"pid {pid} is running ({(live.Job is null ? "?" : Age(live.Job.Started))}).{Tail(live.Job?.OutputPath)}";
        if (sources.Finished.Find(pid) is not { } done) return $"no background command with pid {pid}.";
        return (sources.Finished.HandoffFor(pid)?.Result ?? DescribeEnded(pid)) + Tail(done.Job.OutputPath);
    }

    private string DescribeEnded(int pid) =>
        sources.Finished.Find(pid) is { } done
            ? $"pid {pid} {(done.Killed ? "was stopped" : $"exited with code {done.ExitCode}")}."
            : $"pid {pid} has ended.";

    /// <summary>The last lines a command wrote, under its own heading; empty when there is no file.</summary>
    private static string Tail(string? path)
    {
        var lines = OutputTail.LastLines(path, 20);
        return lines.Count == 0 ? "" : "\nLast output:\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// Elapsed time since a moment, coarse enough for a row and not a timestamp to parse.
    ///
    /// <para>INTERNAL, so <see cref="Commands.JobsCommand"/> renders the same age a model reading
    /// task_list sees for the same row: a row that says "3m" to a user and "180s" to the model is the
    /// disagreement one shared formula rules out.</para>
    /// </summary>
    internal static string Age(DateTimeOffset started)
    {
        var elapsed = DateTimeOffset.UtcNow - started;
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m";
        return $"{(int)elapsed.TotalHours}h";
    }

    /// <summary>
    /// A kept agent with no background run: working in the foreground, or how its last run ended and what it did.
    ///
    /// <para>SO A FOREGROUND AGENT CAN BE LOOKED INTO TOO. One that answered nothing left the model nothing to go on
    /// but a re-run, which redoes every turn; what it did and why it came back empty say whether asking it again is
    /// enough.</para>
    /// </summary>
    private static string LastRun(Agents.SubAgentStore agents, string handle)
    {
        var kept = agents.Find(handle)!;
        var work = Agents.SubAgentEnvelope.WorkOf(kept.Agent);
        if (agents.IsBusy(handle)) return $"'{handle}' is working in the foreground · {work}.";
        if (agents.LastEnd(handle) is not { } end) return $"'{handle}' has not run yet; {Tool.AgentSend} asks it something.";

        var why = end.Outcome == "no-answer" && kept.Agent.Agent.LastEmptyReply is { } reason ? $" — {reason}" : "";
        return $"'{handle}' is idle. Its last run ended {end.Outcome ?? "without a recorded outcome"}{why}, after {work}. "
             + $"{Tool.AgentSend} asks it something.";
    }

    private string Progress(string handle, Agents.BackgroundRun run)
    {
        var state = run.State == Agents.BackgroundState.Queued ? "queued" : "running";
        var rows = sources.Agents?.Find(handle)?.Agent.Jobs.Jobs ?? [];
        var latest = rows.OrderBy(j => j.CreatedAt).TakeLast(3).Select(j => j.DisplayName).ToList();
        var text = $"'{handle}' is {state} · {Age(run.Started)} · {rows.Count} tool calls.";
        return latest.Count == 0 ? text : text + " Latest: " + string.Join(", ", latest);
    }

    private readonly record struct TaskId(int? Pid, string? Handle, string? Error);

    private static TaskId ReadId(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number)
            {
                // A fraction or an out-of-range number is a wrong id, not a missing one: saying
                // "required" would send the model to supply a field it already supplied.
                return id.TryGetInt32(out var number)
                    ? new(number, null, null)
                    : new(null, null, $"error: 'id' must be a whole number pid or a handle, not {id.GetRawText()}.");
            }
            if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } text)
                return int.TryParse(text, out var pid) ? new(pid, null, null) : new(null, text, null);
        }
        return new(null, null, "error: 'id' is required — a handle or a pid from task_list.");
    }

    public static IReadOnlyList<ToolDefinition> Definitions =>
    [
        new ToolDefinition(Tool.TaskList,
            "Everything this session has running in the background, or has kept: sub-agents (by handle) "
            + "and shell commands (by pid), with their state and age. You are told when each one ends; "
            + "use this to see what is still going, never to wait.",
            JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement),

        new ToolDefinition(Tool.TaskStop,
            "Stop a background sub-agent (by handle or id) or shell command (by pid). A sub-agent you stop "
            + "reports as cancelled. You may stop shell commands you started, or any of them if you are "
            + "the session agent.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": ["string", "integer"],
                      "description": "A sub-agent's handle or id, or a shell's pid, from a receipt or task_list"
                    }
                  },
                  "required": ["id"]
                }
                """).RootElement),

        // THE JUST-SPAWNED CASE IS NAMED, because "only when your next step cannot go on" is a judgement a
        // model makes about the task, and it judges an explore it was asked to background to be such a
        // step — spawning and then waiting at once, which blocks the user's turn as background: false does.
        new ToolDefinition(Tool.TaskOutput,
            "How far a background task has got, or its result once it has finished. With wait: true it "
            + "blocks until the task ends or the timeout passes — only when your next step cannot go on "
            + "without the result; a message from the user ends the wait. Waiting on an agent you have just "
            + "spawned blocks the user's turn exactly as background: false does, so never do it when the user "
            + "asked for a background agent. A result you read here is not sent to you again.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": ["string", "integer"],
                      "description": "A sub-agent's handle or id, or a shell's pid, from a receipt or task_list"
                    },
                    "wait": {
                      "type": "boolean",
                      "description": "Block until the task ends or the timeout passes (default false)"
                    },
                    "timeout": {
                      "type": "integer",
                      "description": "Seconds to wait (default 120, max 600)"
                    }
                  },
                  "required": ["id"]
                }
                """).RootElement),
    ];
}
