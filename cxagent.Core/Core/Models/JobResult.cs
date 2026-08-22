namespace CxAgent.Core.Models;

public record JobResult
{
    public required bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>True when this failure is a user permission refusal, not an executor fault.
    /// AgentHost.ShouldAutoDiagnose reads this to skip automatic diagnosis: a paid diagnosis
    /// round cannot repair a user's decision.</summary>
    public bool PermissionDenied { get; init; }

    /// <summary>
    /// "auto" when a permission gate along this job's path was decided by auto mode's classifier
    /// rather than the user or a silent rule/boundary pass — null otherwise, including every silent
    /// allow. Read by the tool row (InlineJobSink.CompactHeader, Task 8) to badge "auto-approved" /
    /// "auto-denied": those are the two surprising outcomes, the ones where the user was not asked
    /// and a stored rule is not why. A prompt the user answered, or a rule-driven silent pass, sets
    /// nothing here and the row stays exactly as it reads today.
    ///
    /// <para>MIRRORS <see cref="Permissions.PermissionOutcome.DeniedBy"/>'s "auto" vocabulary rather
    /// than inventing a second one — both gate wrappers (GatedAgentTool, PermissionGatedExecutor) copy
    /// it straight from the outcome they already have, on both the deny return and the success
    /// return once every gate has cleared.</para>
    /// </summary>
    public string? DecidedBy { get; init; }
    // Output values, like JobParameters, become JsonElement after persistence —
    // read them through a converting accessor, never a cast (see JobParameters.Get).
    public Dictionary<string, object?> Output { get; init; } = new();
    public string? LogFile { get; init; }
    public TimeSpan Duration { get; init; }
}
