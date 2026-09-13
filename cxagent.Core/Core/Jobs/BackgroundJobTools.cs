using System.Text;
using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Jobs;

/// <summary>
/// Seeing and stopping the background commands this app is running.
///
/// <para>OUTSIDE THE JOB-BINDING TABLE, LIKE <see cref="Agents.AgentReachTools"/>. Both tools take no
/// job type of their own and run in-process against a registry already in memory — there is nothing
/// for a binding to dispatch to.</para>
///
/// <para>SYNCHRONOUS. Reading <see cref="DetachedProcessRegistry.Live"/> and calling
/// <see cref="DetachedProcess.Kill"/> touch no I/O this call has to wait on; an <c>async</c> signature
/// would only wrap a value that is already known.</para>
///
/// <para>NO PERMISSION GATE. Listing reveals nothing the process-wide registry does not already make
/// visible to anything with a pid, and a kill only reaches a command this same session or one of its
/// own children started — the command was approved when it was backgrounded.</para>
/// </summary>
public sealed class BackgroundJobTools(DetachedProcessRegistry registry, string sessionId)
{
    /// <summary>Whether this handles a call by that name.</summary>
    public bool Claims(string name) => name == Tool.JobList || name == Tool.JobKill;

    /// <summary>Runs one call and answers what the caller's model should read.</summary>
    public string Invoke(string toolName, string callerAgentId, int pid) =>
        toolName switch
        {
            Tool.JobList => List(callerAgentId),
            Tool.JobKill => Kill(callerAgentId, pid),
            _ => $"error: '{toolName}' is not a tool this handles.",
        };

    /// <summary>
    /// Every live background command, whoever started it.
    ///
    /// <para>ALL OF THEM, NOT JUST THE CALLER'S OWN. The registry is process-wide on purpose — see
    /// <see cref="DetachedProcessRegistry"/> — and a listing that hid another agent's job would draw a
    /// boundary that does not exist: that job is still killable by pid regardless of who is told
    /// about it.</para>
    ///
    /// <para>SAYS SO WHEN THERE IS NOTHING, rather than an empty table: a blank reply reads as a tool
    /// that failed, and the model's next move is to call it again.</para>
    /// </summary>
    private string List(string callerAgentId)
    {
        var live = registry.Live;
        if (live.Count == 0) return "no background commands are running.";

        var sb = new StringBuilder("background commands running now:\n");
        var index = 1;
        foreach (var entry in live)
        {
            var job = entry.Job;
            var age = job is null ? "?" : Age(job.Started);
            var owner = Owner(callerAgentId, job);
            var command = job?.Command ?? "(unknown — started outside the shell tool)";
            sb.Append(index++).Append(". pid ").Append(entry.Process.Pid)
              .Append(" (").Append(age).Append(", ").Append(owner).Append(") ")
              .Append(command)
              .AppendLine();
        }

        // THE SLOT COUNT, LAST. Add refuses at MaxConcurrent, and a model that cannot start a new
        // background command needs to know why rather than guess that the tool call failed.
        sb.Append(live.Count).Append(" of ").Append(DetachedProcessRegistry.MaxConcurrent)
          .Append(" running.");
        return sb.ToString();
    }

    /// <summary>How a row names who started a job: "me" for the caller's own, "session" for the
    /// session agent's (when that is not also the caller), else the agent id, and "unowned" for an
    /// entry nothing has ever described — the identities a flat owner check can produce.</summary>
    private string Owner(string callerAgentId, BackgroundJob? job)
    {
        if (job is null) return "unowned";
        if (job.AgentId == callerAgentId) return "me";
        if (job.AgentId == sessionId) return "session";
        return job.AgentId;
    }

    /// <summary>
    /// Elapsed time since a job started, coarse enough for a row and not a timestamp to parse.
    ///
    /// <para>INTERNAL RATHER THAN PRIVATE, so <see cref="Commands.JobsCommand"/> renders the same
    /// age a model reading <c>job_list</c> sees for the same row. A second formula here would drift
    /// from this one silently — a row that says "3m" to a user and "180s" to the model is the exact
    /// disagreement one shared registry was meant to rule out.</para>
    /// </summary>
    internal static string Age(DateTimeOffset started)
    {
        var elapsed = DateTimeOffset.UtcNow - started;
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m";
        return $"{(int)elapsed.TotalHours}h";
    }

    /// <summary>
    /// Stops one background command, if the caller is allowed to.
    ///
    /// <para>THE SESSION MAY KILL ANY JOB; A SUB-AGENT ONLY ITS OWN. Sub-agents cannot spawn
    /// sub-agents, so every child's parent IS the session — the check is flat equality against
    /// <paramref name="callerAgentId"/>, with no parent/child table to consult. An entry with no
    /// <see cref="BackgroundJob"/> has no recorded owner, so only the session may kill it.</para>
    ///
    /// <para>A REFUSAL NAMES THE OWNER, so the model learns why rather than retrying the same call:
    /// the remedy is asking that agent, not calling this again.</para>
    /// </summary>
    private string Kill(string callerAgentId, int pid)
    {
        var entry = registry.Live.FirstOrDefault(j => j.Process.Pid == pid);
        if (entry is null) return $"no background command with pid {pid}.";

        var isSession = callerAgentId == sessionId;
        var owner = entry.Job?.AgentId;
        if (!isSession && owner != callerAgentId)
        {
            var who = owner is null ? "the session" : owner;
            return $"error: pid {pid} was started by {who}, not you — only {who} or the session can "
                 + "stop it.";
        }

        entry.Process.Kill();
        return $"pid {pid} stopped.";
    }

    /// <summary>
    /// The two definitions, hand-built as <see cref="Agents.AgentReachTools.Definitions"/> is.
    ///
    /// <para>THE DESCRIPTIONS CARRY WHAT THE MODEL GETS WRONG. For <c>job_list</c> that is that the
    /// list is every agent's work, not only the caller's own — the natural assumption otherwise. For
    /// <c>job_kill</c> it is the kill rule itself, and that the output already backgrounded is in a
    /// file, not in this tool's answer: a model that expects the command's output here has no reason
    /// to go and read the path job_list gave it.</para>
    /// </summary>
    public static IReadOnlyList<ToolDefinition> Definitions =>
    [
        new ToolDefinition(Tool.JobList,
            "Background commands running right now, across every agent in this session — not only "
            + "yours. Each row shows who started it, its age, and its command line. The command's "
            + "output is being written to a file, named in the row; it is not repeated here.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {}
                }
                """).RootElement),

        new ToolDefinition(Tool.JobKill,
            "Stop a background command by pid, from job_list. You may stop one you started yourself, "
            + "or any of them if you are the session agent — a job another agent started cannot be "
            + "stopped by you, and the answer will name who did start it.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "properties": {
                    "pid": {
                      "type": "integer",
                      "description": "The process id, from a job_list row."
                    }
                  },
                  "required": ["pid"]
                }
                """).RootElement),
    ];
}
