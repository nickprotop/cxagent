namespace CxAgent.Core.Models;

/// <summary>What the LLM proposes doing about a failed job.</summary>
public enum RecoveryAction
{
    /// <summary>Re-run the job unchanged (transient failure).</summary>
    Retry,
    /// <summary>Change parameters, then re-run.</summary>
    ModifyAndRetry,
    /// <summary>Insert one or more new jobs before this one, then re-run it.</summary>
    InsertBefore,
    /// <summary>Give up on this job and let dependents proceed.</summary>
    Skip,
    /// <summary>The LLM cannot decide; surface to the user verbatim.</summary>
    AskUser,
}

/// <summary>
/// A set of changes to apply to a live DAG. Deliberately the SAME shape whether the LLM authored it
/// (diagnosis, P6) or the user did (copilot editing, P7) — so the apply path is written once.
/// </summary>
public record DagModification(
    IReadOnlyList<Job> JobsToAdd,
    IReadOnlyList<string> JobIdsToRemove,
    IReadOnlyDictionary<string, JobParameters> ParameterChanges)
{
    public static DagModification Empty { get; } = new(
        System.Array.Empty<Job>(),
        System.Array.Empty<string>(),
        new Dictionary<string, JobParameters>());
}

/// <summary>The LLM's read of one failure, plus what it suggests doing.</summary>
/// <param name="Cause">One-line human-readable cause, shown in the UI.</param>
/// <param name="Action">The suggested recovery.</param>
/// <param name="Rationale">Why — shown under the cause so the user can judge the suggestion.</param>
/// <param name="Modification">Null for Retry/Skip/AskUser; populated for ModifyAndRetry/InsertBefore.</param>
public record AiDiagnosis(string Cause, RecoveryAction Action, string Rationale, DagModification? Modification);
