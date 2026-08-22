using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>
/// What one dispatched tool call produced: the text the MODEL is told, and the plugin's own
/// <see cref="JobResult"/> when there was one.
///
/// <para>A RECORD RATHER THAN A TUPLE, though two members would be within the house rule. These two
/// are not a pair of coordinates — they are two AUDIENCES for one call, and a <c>(string, JobResult?)</c>
/// at four call sites invites exactly the transposition the naming rule exists to prevent. The names
/// also carry the asymmetry: <c>Text</c> is always present, <c>Result</c> is not.</para>
///
/// <para>NULLABLE AT THE CALL SITES, AND THAT NULL IS LOAD-BEARING. Agent's dispatch chain is one
/// <c>??</c> per source, where null means "I do not own this name, try the next link" — never "ran
/// and returned nothing". Returning <c>ToolOutcome?</c> keeps that distinction exactly as the old
/// <c>string?</c> did; an empty <c>Text</c> is a tool that answered with nothing, which is a
/// different fact and must stay one.</para>
///
/// <para>WHY IT EXISTS AT ALL. Agent used to rebuild <c>job.Result</c> from the returned STRING,
/// which split every field in two: the ones it can re-derive (Duration, Success, ExitCode) survived,
/// and the ones only the plugin knows (Output, DecidedBy, LogFile) were silently dropped. Two
/// side channels had already been added to smuggle values past that rebuild — AgentToolset's
/// LastDisplay, for a show_diff row that rendered the model's text confirmation instead of the diff,
/// and IJobContext.DecidedBy, for a classifier verdict the row never showed. A third field in that
/// category would have meant a third channel; carrying the object instead ends the category.</para>
/// </summary>
/// <param name="Text">What goes back to the model as this call's tool result. Never null.</param>
/// <param name="Result">
/// The plugin's own result, when the call went through a plugin. Null for the sources that have no
/// JobResult to give — the spawn branch, skills, todos, ask_user, MCP — whose text IS the whole
/// answer.
/// </param>
public sealed record ToolOutcome(string Text, JobResult? Result = null)
{
    /// <summary>A text-only answer, for the sources that never had a JobResult. Implicit so the
    /// links in Agent's chain that only produce a string stay one expression each.</summary>
    public static implicit operator ToolOutcome(string text) => new(text);
}
