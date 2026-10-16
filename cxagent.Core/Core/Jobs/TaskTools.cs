using System.Text;
using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>What the task tools can see.</summary>
/// <param name="Live">Shells running now.</param>
/// <param name="Finished">Shells that have ended.</param>
/// <param name="SessionId">The session agent's id — who may see and stop everything.</param>
/// <param name="Agents">Kept sub-agents, or null for an agent that cannot spawn.</param>
public sealed record TaskSources(DetachedProcessRegistry Live, FinishedShells Finished, string SessionId,
    Agents.SubAgentStore? Agents);

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
public sealed class TaskTools(TaskSources sources)
{
    public const int DefaultWaitSeconds = 120;
    public const int MaxWaitSeconds = 600;

    public bool Claims(string name) => name is Tool.TaskList or Tool.TaskStop or Tool.TaskOutput;

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

        var wait = call.Arguments.ValueKind == JsonValueKind.Object
            && call.Arguments.TryGetProperty("wait", out var w) && w.ValueKind == JsonValueKind.True;
        int? timeout = call.Arguments.ValueKind == JsonValueKind.Object
            && call.Arguments.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number
            && t.TryGetInt32(out var seconds) ? seconds : null;

        if (id.Pid is { } pid)
            return call.Name == Tool.TaskStop
                ? StopShell(callerAgentId, pid)
                : await ShellOutputAsync(new ShellRead(callerAgentId, pid, wait, timeout), ct);

        var handle = id.Handle!;
        if (call.Name == Tool.TaskStop) return Stop(callerAgentId, handle);

        if (callerAgentId != sources.SessionId)
            return "error: only the session agent can read a sub-agent's task.";
        return await OutputAsync(handle, wait, timeout, ct);
    }

    /// <summary>What <c>/tasks</c> shows for one task: never waits and never takes a result, because
    /// a person looking at a task must not be the reason the model is never sent its report.</summary>
    public string Describe(string id)
    {
        if (int.TryParse(id, out var pid)) return DescribeShell(pid);
        if (sources.Agents?.Find(id) is null) return $"no task '{id}'.";
        if (sources.Agents.BackgroundOf(id) is not { } run) return $"'{id}' is not running in the background.";
        return run.Handoff.Ended ? run.Handoff.Result ?? $"'{id}' has ended." : Progress(id, run);
    }

    public string List(string callerAgentId)
    {
        var isSession = callerAgentId == sources.SessionId;
        var sb = new StringBuilder();

        if (isSession && sources.Agents is { } agents)
        {
            foreach (var kept in agents.All())
            {
                sb.Append("agent '").Append(kept.Name).Append("' (").Append(kept.TypeName).Append(") — ")
                  .Append(AgentState(agents, kept.Name)).AppendLine();
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
        if (callerAgentId != sources.SessionId) return "error: only the session agent can stop a sub-agent.";
        var agents = sources.Agents;
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

    private async Task<string> OutputAsync(string handle, bool wait, int? timeout, CancellationToken ct)
    {
        var agents = sources.Agents;
        if (agents?.Find(handle) is null) return $"no task '{handle}' — {Tool.TaskList} shows what exists.";

        var run = agents.BackgroundOf(handle);
        if (run is null)
            return $"'{handle}' is not running in the background; {Tool.AgentSend} asks it something.";

        if (!run.Handoff.Ended && !wait) return Progress(handle, run);

        var seconds = WaitSeconds(timeout);
        var read = run.Handoff.Ended
            ? run.Handoff.Read()
            : await run.Handoff.WaitAsync(TimeSpan.FromSeconds(seconds), ct);

        if (!read.Ended) return Progress(handle, run) + $"\nStill running after {seconds}s.";
        var report = read.Result ?? run.Handoff.Result ?? $"'{handle}' ended without a report.";
        return read.AlreadySent ? report + "\n(You were also sent this as a message.)" : report;
    }

    /// <summary>A pid's task_output, as one caller asks for it.</summary>
    /// <param name="Caller">Who asks — a child may read only its own commands.</param>
    /// <param name="Pid">The command.</param>
    /// <param name="Wait">Block until it ends or the timeout passes.</param>
    /// <param name="Timeout">Seconds, before clamping.</param>
    private sealed record ShellRead(string Caller, int Pid, bool Wait, int? Timeout);

    /// <summary>
    /// Whether a finished command belongs in this caller's view: the session sees its own and its
    /// children's, a child only its own.
    /// </summary>
    private bool Sees(string callerAgentId, string? owner) =>
        owner == callerAgentId
        || (callerAgentId == sources.SessionId
            && (owner == sources.SessionId || owner is not null && sources.Agents?.FindByAgentId(owner) is not null));

    /// <summary>How a row names who started a command: "me", "session", an agent id, or "unowned" for a
    /// process nothing ever described.</summary>
    private string Owner(string callerAgentId, BackgroundJob? job)
    {
        if (job is null) return "unowned";
        if (job.AgentId == callerAgentId) return "me";
        if (job.AgentId == sources.SessionId) return "session";
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
        if (callerAgentId != sources.SessionId && owner != callerAgentId)
        {
            var who = owner is null || owner == sources.SessionId ? "the session" : owner;
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
        if (read.Caller != sources.SessionId && job?.AgentId != read.Caller)
            return $"error: pid {read.Pid} was started by {(job is null || job.AgentId == sources.SessionId ? "the session" : job.AgentId)}, not you.";

        var handoff = sources.Finished.HandoffFor(read.Pid);
        if (live is not null && !(read.Wait && handoff is not null))
            return $"pid {read.Pid} is running ({(job is null ? "?" : Age(job.Started))}).{Tail(job?.OutputPath)}";

        var seconds = WaitSeconds(read.Timeout);
        var result = handoff is null ? null
            : handoff.Ended ? handoff.Read()
            : await handoff.WaitAsync(TimeSpan.FromSeconds(seconds), ct);

        if (result is { Ended: false })
            return $"pid {read.Pid} is still running after {seconds}s.{Tail(job?.OutputPath)}";

        var text = result?.Result ?? DescribeEnded(read.Pid);
        text += Tail(job?.OutputPath);
        return result is { AlreadySent: true } ? text + "\n(You were also sent this as a message.)" : text;
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
            "Stop a background sub-agent (by handle) or shell command (by pid). A sub-agent you stop "
            + "reports as cancelled. You may stop shell commands you started, or any of them if you are "
            + "the session agent.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": ["string", "integer"],
                      "description": "A handle or a pid, from a receipt or task_list"
                    }
                  },
                  "required": ["id"]
                }
                """).RootElement),

        new ToolDefinition(Tool.TaskOutput,
            "How far a background task has got, or its result once it has finished. With wait: true it "
            + "blocks until the task ends or the timeout passes — only when your next step cannot go on "
            + "without the result. A result you read here is not sent to you again.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": ["string", "integer"],
                      "description": "A handle or a pid, from a receipt or task_list"
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
