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
/// <param name="CallId">The call's id, as the model issued it.</param>
/// <param name="ToolName">The tool name the model used.</param>
/// <param name="JobType">Which executor serviced it, or null for a tool with none.</param>
/// <param name="Outcome">How the call ended.</param>
/// <param name="DurationMs">How long it took, in milliseconds.</param>
/// <param name="StartedAt">When it ran.</param>
public sealed record ToolCallReport(
    string CallId,
    string AgentId,
    string ToolName,
    string? JobType,
    string Outcome,
    long DurationMs,
    int ResultChars,
    DateTimeOffset StartedAt)
{
    /// <summary>
    /// WHAT the call acted on — <c>Agent.cs</c>, <c>dotnet build</c>, <c>"PluginType"</c> — or null
    /// for a tool that acts on nothing nameable.
    ///
    /// <para>THE TOOL NAME ALONE DOES NOT DISTINGUISH TWO CALLS. A worker that ran nine
    /// <c>read_file</c>s produces nine identical rows without this, which is a count rather than a
    /// record of the work; the file names are the whole reason anyone reads the list.</para>
    ///
    /// <para>The job's <c>DisplayName</c>, which is already the human label the UI puts on rows —
    /// so this reports a label that exists rather than composing a second one that could disagree
    /// with what the row shows.</para>
    ///
    /// <para>AN INIT-ONLY PROPERTY, not a positional field, for the reason
    /// <see cref="ChildRunReport.Skills"/> gives: every construction site would otherwise have to
    /// name it, including the persistence layer, which stores measurements and not labels.</para>
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// What the call returned, capped — or null for a call that returned nothing, and for one that
    /// never ran.
    ///
    /// <para>KEPT FOR A SURFACE THAT DOES NOT EXIST YET. Nothing renders this: an expandable row, a
    /// diff view, a "what did that actually return" affordance all need the text to have been kept
    /// from the start, and a field added the day it is first read has no history behind it.</para>
    ///
    /// <para>AN INIT-ONLY PROPERTY, for the reason <see cref="Target"/> gives — every construction
    /// site would otherwise have to name it, including the persistence layer, which stores
    /// measurements and not labels. This is not a measurement at all.</para>
    ///
    /// <para>CAPPED, because the UI holds a session's worth of these. <see cref="ResultChars"/>
    /// records the true length, so nothing about size is lost by not keeping every byte.</para>
    /// </summary>
    public string? Output { get; init; }

    /// <summary>How much of a call's output is kept.</summary>
    public const int OutputCap = 4096;

    /// <summary>
    /// Trims an output to <see cref="OutputCap"/>, saying so when it does.
    ///
    /// <para>THE MARKER MATTERS MORE THAN THE BYTES. Text that simply stops looks like a tool that
    /// returned exactly that much, and someone debugging from it would be reading a lie.</para>
    /// </summary>
    public static string? Cap(string? output) =>
        output is { Length: > OutputCap }
            ? output[..OutputCap] + $"\n… truncated at {OutputCap:N0} characters"
            : output;
}

/// <summary>
/// One finished sub-agent run, reported by its parent.
/// </summary>
/// <param name="TypeName">The resolved type — <c>general</c> unless config named another.</param>
/// <param name="ModelId">Which model it actually ran on, which need not be the session's.</param>
/// <param name="Outcome">completed · failed · cancelled — what the envelope said.</param>
/// <param name="RunId">This run's id.</param>
/// <param name="ParentAgentId">The agent that spawned it — how a run joins its session.</param>
/// <param name="InputTokens">Tokens the child sent.</param>
/// <param name="OutputTokens">Tokens the child generated.</param>
/// <param name="Turns">Turns the child took.</param>
/// <param name="ToolCalls">How many tools it called.</param>
/// <param name="StartedAt">When it was spawned.</param>
/// <param name="DurationMs">How long it ran, in milliseconds.</param>
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
