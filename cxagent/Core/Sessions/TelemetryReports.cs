namespace CxAgent.Core.Sessions;

/// <summary>
/// One finished tool call, as the loop saw it.
///
/// <para>A KERNEL TYPE CARRYING NO STORAGE CONCEPTS. The loop raises these and knows nothing about
/// what listens — history, a log, a metrics endpoint, or nothing at all. Everything here is already
/// on the <c>Job</c> the call built; it is reported rather than newly measured.</para>
/// </summary>
/// <param name="AgentId">Who made the call. A CHILD's id for a child's calls, which is the point:
/// that is where the expensive reading happens, and attributing it to the parent would hide it.</param>
/// <param name="ResultChars">
/// What the result put into the context. The one figure here that is not otherwise recoverable, and
/// the one that says whether a tool is cheap or is quietly filling the window.
/// </param>
public sealed record ToolCallReport(
    string CallId,
    string AgentId,
    string ToolName,
    string? PluginType,
    string Outcome,
    long DurationMs,
    int ResultChars,
    DateTimeOffset StartedAt);

/// <summary>
/// One finished sub-agent run, reported by its parent.
/// </summary>
/// <param name="TypeName">The resolved type — <c>general</c> unless config named another.</param>
/// <param name="ModelId">Which model it actually ran on, which need not be the session's.</param>
/// <param name="Outcome">completed · failed · cancelled — what the envelope said.</param>
public sealed record ChildRunReport(
    string RunId,
    string ParentAgentId,
    string TypeName,
    string? ModelId,
    int InputTokens,
    int OutputTokens,
    int Turns,
    int ToolCalls,
    string Outcome,
    DateTimeOffset StartedAt,
    long DurationMs)
{
    /// <summary>
    /// The skills this child loaded, comma-separated, or null when it loaded none.
    ///
    /// <para>HERE AND NOT ONLY ON THE ROW, because this record exists for the question one session
    /// cannot answer — "is a planner worth spawning?" — and a loaded skill changes that answer. "The
    /// planner runs that loaded the deployment skill cost 3x the ones that did not" is exactly the
    /// cross-session comparison this is for, and the row that carries it scrolls away.</para>
    ///
    /// <para>AN INIT-ONLY PROPERTY, not a positional field: every construction site would otherwise
    /// have to name it, including tests and persistence that have nothing to do with skills.</para>
    /// </summary>
    public string? Skills { get; init; }
}
