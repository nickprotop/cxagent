
namespace CxAgent.Core.Agents;

/// <summary>
/// How a request ended. The kernel knows this at the point it returns; nothing downstream has to
/// infer it.
/// </summary>
public enum SendOutcome
{
    /// <summary>The model answered with no further tool calls. The ordinary path.</summary>
    Completed,

    /// <summary>
    /// The turn cap was reached and a summary was salvaged. The text IS an answer, but it is an
    /// account of unfinished work — not the finished work.
    /// </summary>
    Capped,

    /// <summary>
    /// The same tool was called with the same arguments repeatedly, returning the same result, and
    /// the nudge did not break the loop. The run was not making progress.
    /// </summary>
    Stuck,

    /// <summary>The request failed. Reserved for callers that catch around <c>SendAsync</c> — the
    /// loop itself surfaces provider faults through the sink rather than returning.</summary>
    Failed,

    /// <summary>Cancelled. Reserved for the same reason as <see cref="Failed"/>: cancellation
    /// propagates as an exception today.</summary>
    Cancelled,

    /// <summary>
    /// The loop ended without the model ever producing text.
    ///
    /// <para>DISTINCT FROM <see cref="Completed"/> WITH AN EMPTY ANSWER, because to a caller those
    /// were the same thing and they are not. A provider call that never returns — a request dropped,
    /// a local server saturated by concurrent children — leaves the loop with nothing to say and no
    /// exception to report, so the run looked finished and returned "".</para>
    ///
    /// <para>MEASURED, NOT THEORISED. A builder sub-agent was mid-implementation when its provider
    /// call vanished: its context for the next turn was written, no response arrived, and it
    /// returned empty. Its parent read that as "the child returned nothing", guessed at a cause
    /// (wrongly — it blamed a plan file the child had read successfully) and re-spawned. The tree
    /// was left half-edited in between. Nothing anywhere said the child had died.</para>
    /// </summary>
    Silent,
}

/// <summary>
/// What a request produced: the answer, and how it ended.
///
/// <para>WHY THE OUTCOME IS A FIELD RATHER THAN SOMETHING TO INFER. <c>SendAsync</c> returns text on
/// all three of its exits — a normal answer, a salvaged summary at the turn cap, and a stuck run —
/// and the two unhappy ones announce themselves only through <c>ISessionObserver.Failed</c>. That is
/// enough for a human watching a transcript and nothing at all for a caller: a sub-agent's sink is a
/// buffer nobody is reading, so a capped run and a finished one are the same string.</para>
///
/// <para>THE COUNT DOES NOT WORK EITHER, which is worth stating because it is the obvious
/// alternative. <c>_maxTurns</c> is private with no accessor; the turn counter reads exactly
/// <c>maxTurns</c> BOTH when the cap fires and when a run finishes naturally on its last turn, so
/// <c>count >= maxTurns</c> reports a false cap; and the salvage turn raises no
/// <c>TurnCompleted</c> at all.</para>
///
/// <para>AND MATCHING THE ERROR TEXT IS WORSE. "stopped after N turns without finishing" is prose
/// written for a person, and a caller keyed to its wording breaks the first time someone improves
/// the sentence.</para>
///
/// <para>What must not happen is a capped run reporting <see cref="SendOutcome.Completed"/>: the
/// caller then treats a salvage summary as a finished answer, which is the whole reason this type
/// exists.</para>
/// </summary>
/// <param name="Text">The final assistant text, reasoning already stripped.</param>
/// <param name="Outcome">How the request ended.</param>
public readonly record struct SendResult(string Text, SendOutcome Outcome)
{
    /// <summary>
    /// The text, for the ~69 call sites that await and discard, and the four that read it. Keeps
    /// this a mechanical change at every site that never cared how the run ended.
    /// </summary>
    public static implicit operator string(SendResult result) => result.Text;
}
