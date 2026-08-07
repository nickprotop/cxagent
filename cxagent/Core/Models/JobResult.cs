namespace CxAgent.Core.Models;

public record JobResult
{
    public required bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>True when this failure is a user permission refusal, not a plugin fault.
    /// GoalRunner.ShouldAutoDiagnose reads this to skip automatic diagnosis: a paid diagnosis
    /// round cannot repair a user's decision.</summary>
    public bool PermissionDenied { get; init; }
    // Output values, like JobParameters, become JsonElement after persistence —
    // read them through a converting accessor, never a cast (see JobParameters.Get).
    public Dictionary<string, object?> Output { get; init; } = new();
    public string? LogFile { get; init; }
    public TimeSpan Duration { get; init; }
}
