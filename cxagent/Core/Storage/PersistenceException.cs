namespace CxAgent.Core.Storage;

/// <summary>
/// Thrown when persisted data cannot be loaded or written. Carries the goal/job id
/// so a caller can skip the affected goal instead of crashing startup.
/// </summary>
public class PersistenceException : Exception
{
    public string? GoalId { get; }
    public string? JobId { get; }

    public PersistenceException(string message, string? goalId = null, string? jobId = null, Exception? inner = null)
        : base(message, inner)
    {
        GoalId = goalId;
        JobId = jobId;
    }
}
