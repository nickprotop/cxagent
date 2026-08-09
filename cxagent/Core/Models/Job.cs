namespace CxAgent.Core.Models;

public enum JobState
{
    Pending,      // Waiting for dependencies
    Queued,       // Dependencies met, waiting for execution slot
    Running,      // Currently executing
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

    public double? Progress { get; set; }
    public string? ProgressMessage { get; set; }
}
