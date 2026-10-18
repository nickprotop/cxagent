namespace CxAgent.Core.Agents;

/// <summary>
/// Who a message an agent received came from — what an export labels it with.
///
/// <para>A CHILD HEARS FROM THREE PLACES, and a reader of its conversation needs to know which: the brief
/// and later messages are its parent's, a follow-up typed in its tab is the user's, and a background
/// command finishing is nobody's. Nothing on the message itself says so, so the label is taken where the
/// message is sent.</para>
/// </summary>
public enum Incoming
{
    /// <summary>The user typed it — in the session's composer, or in an agent's tab.</summary>
    You,

    /// <summary>The parent's first instruction to a sub-agent.</summary>
    Brief,

    /// <summary>A later message from the parent agent (<c>agent_send</c>).</summary>
    Parent,

    /// <summary>Something the application delivered: a background agent's report, a command's exit.</summary>
    Delivery,

    /// <summary>
    /// cxagent telling the model something on its own — a reply cut off, a call repeated to no effect. A user-role
    /// message nobody typed: never a turn of the user's, and in an export, injected text.
    /// </summary>
    Nudge,
}
