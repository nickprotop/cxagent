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

        if (id.Pid is { } pid)
        {
            // THE SHELL HALF IS NOT HERE YET: a pid is a valid address, so it gets an honest answer
            // rather than an exception or a claim that no such task exists.
            return $"pid {pid} is a shell command; shell tasks are handled by {Tool.JobList} and "
                + $"{Tool.JobKill} for now.";
        }

        var handle = id.Handle!;
        if (call.Name == Tool.TaskStop) return Stop(callerAgentId, handle);

        if (callerAgentId != sources.SessionId)
            return "error: only the session agent can read a sub-agent's task.";
        var wait = call.Arguments.TryGetProperty("wait", out var w) && w.ValueKind == JsonValueKind.True;
        int? timeout = call.Arguments.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number
            && t.TryGetInt32(out var seconds) ? seconds : null;
        return await OutputAsync(handle, wait, timeout, ct);
    }

    /// <summary>What <c>/tasks</c> shows for one task: never waits and never takes a result, because
    /// a person looking at a task must not be the reason the model is never sent its report.</summary>
    public string Describe(string id)
    {
        if (int.TryParse(id, out var pid)) return $"pid {pid}: shell tasks are listed by {Tool.JobList}.";
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

        foreach (var entry in sources.Live.Live)
        {
            var job = entry.Job;
            sb.Append("shell pid ").Append(entry.Process.Pid).Append(" (")
              .Append(job is null ? "?" : BackgroundJobTools.Age(job.Started)).Append(") running: ")
              .AppendLine(job?.Command ?? "(started outside the shell tool)");
        }

        foreach (var done in sources.Finished.Recent(owner => owner == callerAgentId))
        {
            sb.Append("shell pid ").Append(done.Job.Pid).Append(done.Killed ? " stopped" : $" exited {done.ExitCode}")
              .Append(" (").Append(BackgroundJobTools.Age(done.Ended)).Append(" ago): ")
              .AppendLine(done.Job.Command);
        }

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

    private string Progress(string handle, Agents.BackgroundRun run)
    {
        var state = run.State == Agents.BackgroundState.Queued ? "queued" : "running";
        var rows = sources.Agents?.Find(handle)?.Agent.Jobs.Jobs ?? [];
        var latest = rows.OrderBy(j => j.CreatedAt).TakeLast(3).Select(j => j.DisplayName).ToList();
        var text = $"'{handle}' is {state} · {BackgroundJobTools.Age(run.Started)} · {rows.Count} tool calls.";
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
