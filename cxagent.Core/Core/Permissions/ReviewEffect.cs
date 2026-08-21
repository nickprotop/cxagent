namespace CxAgent.Core.Permissions;

/// <summary>
/// What a classifier verdict is permitted to CHANGE for one request.
///
/// <para>NOT "MAY THIS BE REVIEWED", WHICH IS THE QUESTION A BOOL ANSWERS. The gate's next line
/// returns true on an ALLOW, so a bool admitting a population the classifier must never silence
/// silences it. An earlier draft of this design proposed exactly that — one predicate covering both
/// "would have been silent" and "would have prompted" — and it would have handed an ALLOW on an
/// http_request the power to send data off the machine with no prompt at all. The guard this
/// replaced said so itself: "Without AllowsSilentWrites here, a classifier's ALLOW would return true
/// below and hand auto the one power no mode is allowed to have: widening past a trust decision the
/// user made."</para>
///
/// <para>THE ENUM IS THE FIX BECAUSE IT SEPARATES TWO FACTS A BOOL CONFLATES: whether the classifier
/// is consulted, and what its answer is allowed to do. Widening the review population is then a
/// change that cannot silently widen the SILENCING population, which is the property that has to
/// hold as later tasks add kinds.</para>
/// </summary>
public enum ReviewEffect
{
    /// <summary>Not reviewed. Untrusted, not auto mode, or a kind with no classifier.</summary>
    None,

    /// <summary>ALLOW silences it, DENY refuses it, ASK prompts. File writes, and shell within its bound.</summary>
    MayApprove,

    /// <summary>
    /// The verdict shapes the PROMPT and never the outcome. An ALLOW changes nothing.
    ///
    /// <para>For kinds whose damage cannot be undone — egress above all — and for actions a stored
    /// rule already silences, where the useful direction is adding a question, not removing one.</para>
    /// </summary>
    MayAnnotate,
}
