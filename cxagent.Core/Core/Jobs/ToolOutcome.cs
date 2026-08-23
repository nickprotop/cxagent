using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>
/// What one dispatched tool call produced: the text the MODEL is told, and the executor's own
/// <see cref="JobResult"/> when there was one.
///
/// <para>A RECORD RATHER THAN A TUPLE, though two members would be within the house rule. These two
/// are not a pair of coordinates — they are two AUDIENCES for one call, and a <c>(string, JobResult?)</c>
/// at four call sites invites exactly the transposition the naming rule exists to prevent. The names
/// also carry the asymmetry: <c>Text</c> is always present, <c>Result</c> is not.</para>
///
/// <para>NULLABLE AT THE CALL SITES, AND THAT NULL IS LOAD-BEARING. Agent's dispatch chain is one
/// <c>??</c> per source, where null means "I do not own this name, try the next link" — never "ran
/// and returned nothing". Returning <c>ToolOutcome?</c> keeps that distinction; an empty <c>Text</c>
/// is a tool that answered with nothing, which is a different fact and must stay one.</para>
///
/// <para>WHY IT EXISTS AT ALL: the executor's <see cref="JobResult"/> must travel WITH the text rather
/// than be rebuilt from it. Reconstructing <c>job.Result</c> from the returned STRING splits every
/// field in two — the ones a caller can re-derive (Duration, Success, ExitCode) survive, and the ones
/// only the executor knows (Output, DecidedBy, LogFile) are silently dropped. Each dropped field then
/// wants its own side channel to smuggle it past the rebuild: a LastDisplay on AgentToolset so a
/// tool row renders the tool's own output rather than the model's text confirmation, a DecidedBy on
/// IJobContext so a classifier verdict reaches the row at all. Carrying the object ends that
/// category instead of adding to it.</para>
/// </summary>
/// <param name="Text">What goes back to the model as this call's tool result. Never null.</param>
/// <param name="Result">
/// The executor's own result, when the call went through an executor. Null for the sources that have no
/// JobResult to give — the spawn branch, skills, todos, ask_user, MCP — whose text IS the whole
/// answer.
/// </param>
public sealed record ToolOutcome(string Text, JobResult? Result = null)
{
    /// <summary>A text-only answer, for the sources that never had a JobResult. Implicit so the
    /// links in Agent's chain that only produce a string stay one expression each.</summary>
    public static implicit operator ToolOutcome(string text) => new(text);
}
