namespace CxAgent.UI;

/// <summary>
/// What someone typed while a turn was running.
///
/// <para>A turn cannot accept a second prompt: two <c>SendAsync</c> calls on one <see cref="Core.Agent.Agent"/>
/// append to a single live <c>Context.Messages</c> from two loops, and the second submission disposes
/// the running turn's cancellation token, so the first throws <c>ObjectDisposedException</c> at its
/// next cancellation check instead of cancelling. Rejecting the keystroke would be safe and
/// unhelpful — the user typed something they meant. It is held here instead, and goes in when the
/// turn ends.</para>
///
/// <para>SEPARATED FROM AppBootstrap for the same reason <see cref="EscapeRouting"/> is: the decision
/// is worth testing and the wiring is not. Everything here is a pure function of what was typed, so
/// none of it needs a window, a provider or a running turn.</para>
/// </summary>
public static class PromptQueue
{
    /// <summary>
    /// What the composer should hold after Escape stops a turn with messages still queued.
    ///
    /// <para>THE QUEUE IS RETURNED, NOT DISCARDED. That text was never sent, so cancelling the run
    /// must not eat it — the user gets it back, editable, and decides whether to resend. Escape is
    /// how someone changes their mind, and losing their words is the opposite of what they asked
    /// for.</para>
    ///
    /// <para>ABOVE anything already in the composer, because the queued lines were typed FIRST.
    /// Order is the only thing that makes the result readable as the sequence of thoughts it
    /// was.</para>
    /// </summary>
    public static string Restore(IEnumerable<string> queued, string composer)
    {
        var pending = string.Join("\n", queued);
        if (string.IsNullOrEmpty(pending)) return composer;
        return string.IsNullOrEmpty(composer) ? pending : pending + "\n" + composer;
    }
}
