using System.Text.Json;

namespace CxAgent.Core.Orchestrator;

/// <summary>What the orchestrator LLM decides after seeing a JobDigest (P8 Task 1).</summary>
public enum ConsultAction
{
    /// <summary>Results are as expected — do nothing. The common case, and deliberately free: no
    /// rationale, no summary required. Paying tokens to justify "nothing happened" would undercut the
    /// whole point of a feedback loop that only re-plans when something is actually surprising.</summary>
    Continue,
    /// <summary>Add/remove/re-parameterise jobs, or cancel one still running.</summary>
    Modify,
    /// <summary>The goal is met; stop the drive and report Summary to the user.</summary>
    FinishGoal,
}

/// <summary>
/// The raw material for a DagModification (P6), NOT a compiled one. <see cref="JobsToAdd"/> is the
/// model's `jobs_to_add` payload verbatim — same job shape as create_plan's `jobs` array, including
/// {{job.key}} references and depends_on — because DagModification.JobsToAdd wants already-compiled
/// Job objects, and compiling requires the live dag (resolving depends_on against jobs that already
/// exist, assigning real ids). That compilation is Task 3b's job. Parsing it here would mean doing
/// the work twice, or doing it against the wrong dag.
/// </summary>
public record ConsultModification(
    JsonElement? JobsToAdd,
    IReadOnlyDictionary<string, JsonElement> ParameterChanges);

/// <summary>The orchestrator's reply to a consult.</summary>
public record ConsultDecision(
    ConsultAction Action,
    ConsultModification? Modification,
    IReadOnlyList<string> CancelJobIds,
    string? Summary,
    string? Rationale);
