namespace CxAgent.Core.Models;

public enum JobState
{
    Pending,      // Waiting for dependencies
    Queued,       // Dependencies met, waiting for execution slot
    Running,      // Currently executing

    /// <summary>
    /// Stopped at a permission prompt, waiting for the user to answer.
    ///
    /// <para>ADDED FOR CONCURRENCY, and pointless without it. With one agent, blocked and slow look
    /// the same to a user and it does not matter — they know what they started and there is only one
    /// thing it can be waiting for. With three children up, one parked on approval is
    /// indistinguishable from one grinding through a long turn, and the user is left guessing which
    /// row their answer would unblock.</para>
    ///
    /// <para>Between Running and Paused deliberately: Paused is a state the USER chose, this is one
    /// they are being asked about.</para>
    /// </summary>
    WaitingOnPermission,

    Paused,       // User paused (can resume -> Running)
    Succeeded,    // Completed successfully
    Failed,       // Completed with error
    Cancelled,    // User cancelled
    Skipped       // User or LLM chose to skip
}

public record Job
{
    public required string Id { get; init; }           // ULID — sortable, unique

    /// <summary>
    /// The id the orchestrator used for this job inside its create_plan call (e.g. "r1"), or null for
    /// jobs not born from a plan (DagModifier's recovery inserts). PlanCompiler rewrites plan-local ids
    /// to ULIDs for DependsOn; this keeps the original so a parameter can reference an upstream job by
    /// the name the orchestrator itself chose. Without it, {{r1.content}} refers to nothing.
    /// </summary>
    public string? PlanLocalId { get; init; }
    /// <summary>
    /// The agent this job belongs to — the grouping key for its log directory and its panel row.
    ///
    /// <para>Was <c>GoalId</c>. Nothing ever interpreted the value, so this is a rename that makes the
    /// key honest: what it holds is an agent's id, stable for the session, not a per-message goal.</para>
    /// </summary>
    public required string AgentId { get; init; }
    public required string PluginType { get; init; }   // "shell", "docker", etc.
    public required string DisplayName { get; init; }
    public JobParameters Parameters { get; init; } = new();
    public List<string> DependsOn { get; init; } = new(); // Job IDs (ULIDs), not names
    public JobState State { get; set; } = JobState.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public JobResult? Result { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// The classifier's verdict on the permission gate this call passed — "auto" when auto mode's
    /// classifier ruled, null on every ordinary path (a stored rule, a silent in-boundary pass, a
    /// prompt the user answered). The tool row reads it to badge "auto-approved"/"auto-denied".
    ///
    /// <para>ON THE JOB RATHER THAN ON <see cref="Result"/>, WHICH IS THE WHOLE POINT. A JobResult
    /// exists only once the call has FINISHED, so a badge read from there could not appear until
    /// then — and the fact it reports is known much earlier, at the permission gate, before the
    /// tool has run at all. A `dotnet build` can run for minutes; learning after the fact that a
    /// model approved it is strictly worse than seeing it while it runs. An auto-DENIED call never
    /// executes, so a result-borne badge is the one case that would render at the wrong moment
    /// entirely.</para>
    ///
    /// <para>Settable like <see cref="State"/> and <see cref="Result"/>: the value arrives mid-flight,
    /// from the gate, on a job the row is already showing.</para>
    /// </summary>
    public string? DecidedBy { get; set; }

    public double? Progress { get; set; }
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// What a still-running job has to show BEHIND its expand — as opposed to
    /// <see cref="ProgressMessage"/>, which is the one line on the header.
    ///
    /// <para>WHY A RUNNING ROW NEEDS ONE AT ALL: a job's body comes from its Result, and a Result
    /// exists only once the call has finished. So expanding a running row showed an empty block —
    /// reported by a user watching a sub-agent, and it is the worst moment to have nothing, because
    /// "is this on the right track?" is exactly the question a minutes-long child provokes.</para>
    ///
    /// <para>The header answers "is it alive" (turns, occupancy, elapsed). This answers "what is it
    /// doing", which no amount of counters can.</para>
    /// </summary>
    public string? ProgressBody { get; set; }
}
