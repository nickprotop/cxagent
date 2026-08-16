namespace CxAgent.UI;

/// <summary>
/// What someone typed while a turn was running.
///
/// <para>A turn cannot accept a second prompt: two <c>SendAsync</c> calls on one <see cref="Core.Agent.Agent"/>
/// append to a single live <c>Context.Messages</c> from two loops, and the second submission disposes
/// the running turn's cancellation token, so the first throws <c>ObjectDisposedException</c> at its
/// next cancellation check instead of cancelling. Rejecting the keystroke would be safe and
/// unhelpful — the user typed something they meant. It is held on the <see cref="Core.Agent.Session"/>
/// instead, and the turn takes it at its next tool barrier.</para>
///
/// <para>WHAT IS LEFT HERE IS THE ESCAPE PATH. Joining several messages moved to
/// <c>Session.Steer</c>, which appends as they are typed — there is one pending message, never a
/// list, so nothing here needs to combine anything.</para>
///
/// <para>SEPARATED FROM AppBootstrap for the same reason <see cref="EscapeRouting"/> is: the decision
/// is worth testing and the wiring is not. Everything here is a pure function of what was typed, so
/// none of it needs a window, a provider or a running turn.</para>
/// </summary>
public static class PromptQueue
{
    /// <summary>
    /// What the composer should hold after Escape stops a turn with a message still pending.
    ///
    /// <para>THE TEXT IS RETURNED, NOT DISCARDED. It was never sent, so cancelling the run must not
    /// eat it — the user gets it back, editable, and decides whether to resend. Escape is how
    /// someone changes their mind, and losing their words is the opposite of what they asked
    /// for.</para>
    ///
    /// <para>ABOVE anything already in the composer, because the pending text was typed FIRST.
    /// Order is the only thing that makes the result readable as the sequence of thoughts it
    /// was.</para>
    ///
    /// <para>ONE STRING, NOT A COLLECTION. It took an IEnumerable while a list of queued messages
    /// existed; once that became a single appended message the only caller passed a one-element
    /// array, which is a parameter inviting a second element that would then be joined here rather
    /// than where every other append happens. Two places that combine pending text is one too
    /// many.</para>
    /// </summary>
    public static string Restore(string? pending, string composer)
    {
        if (string.IsNullOrEmpty(pending)) return composer;
        return string.IsNullOrEmpty(composer) ? pending : pending + "\n" + composer;
    }
}
