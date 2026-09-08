namespace CxAgent.Core.Plugins;

/// <summary>What came of a submitted goal.</summary>
/// <param name="Accepted">Whether the session took it. False means <paramref name="Refusal"/> says why.</param>
/// <param name="Text">
/// The turn's final assistant text — present only when the caller passed <c>wantResult: true</c> AND
/// the turn produced text. Null otherwise, which is not an error.
/// </param>
/// <param name="Refusal">Why the goal was not accepted, or null when it was.</param>
public sealed record SubmitResult(bool Accepted, string? Text, string? Refusal);

/// <summary>
/// A plugin's handle on the session it was loaded into.
///
/// <para>DECLARED, NOT UNIVERSAL. A plugin asks for this in its manifest and the load prompt says so;
/// a plugin that offers only tools never sees it. That is what makes the disclosure meaningful.</para>
///
/// <para>ITS REACH IS ITS OWN SESSION, FULL STOP. There is no target parameter and there will not be
/// one: a plugin that could start work in another session is a back door around the boundary that
/// makes plugins safe to install per project.</para>
///
/// <para>IT DOES NOT OBSERVE. There is no transcript here, no token stream, no tool rows — a plugin
/// ACTS on a session without WATCHING it. The nearest thing is the answer to a goal this plugin itself
/// submitted, and only when it asks for one.</para>
///
/// <para>AND IT CAN BE SEVERED. Every member throws <see cref="ObjectDisposedException"/> once the
/// plugin is unwired, which is why this is an interface Core hands out rather than the session object
/// itself: <c>Session</c> publishes every public member it has today and every one it gains later, to
/// a binary we do not compile.</para>
/// </summary>
public interface IPluginClient
{
    /// <summary>
    /// Starts a turn in this plugin's own session.
    ///
    /// <para>FIRE-AND-FORGET BY DEFAULT. With <paramref name="wantResult"/> false this returns as soon
    /// as the session ACCEPTS the goal — queued or started — which is what a scheduled wake wants: it
    /// has nobody to hand an answer to and should not hold a call open across a turn that may run for
    /// minutes.</para>
    ///
    /// <para>WITH <paramref name="wantResult"/> TRUE IT WAITS for the turn and returns its final
    /// assistant text. That is a pull, and it is the only one on this interface — it cannot widen,
    /// because the question is always "what came of the thing I just asked for".</para>
    ///
    /// <para>A SUBMIT MID-TURN IS QUEUED, not refused and not interleaved, and runs when the session
    /// next goes idle. A queue that is full refuses, saying so.</para>
    /// </summary>
    /// <param name="goal">What to ask the agent to do, as a user would type it.</param>
    /// <param name="wantResult">Whether to wait for the turn and return its answer.</param>
    /// <param name="ct">Abandons the wait. Does not cancel the turn — someone else's session is running it.</param>
    /// <exception cref="ObjectDisposedException">The plugin has been unwired from this session.</exception>
    Task<SubmitResult> Submit(string goal, bool wantResult = false, CancellationToken ct = default);
}
