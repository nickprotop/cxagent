namespace CxAgent.UI;

/// <summary>
/// What this front end does with a turn's task once the turn ends: a fault is handed to a reporter, never
/// rethrown.
///
/// <para>THE CALLER IS AN ASYNC VOID — the submit path fires a turn and does not wait for it — so an
/// exception escaping here reaches no caller and can take the whole app down with it. A turn faults on
/// purpose when an event subscriber fails after the turn has still read the user's steer; that failure
/// belongs on screen, not in a crash.</para>
/// </summary>
public static class TurnEnds
{
    /// <summary>
    /// Awaits the turn and reports a fault through <paramref name="onFault"/>.
    ///
    /// <para>CANCELLATION IS NOT A FAULT: Escape, a closed tab and quitting all end a turn by cancelling it,
    /// and each is something the user asked for.</para>
    ///
    /// <para>A REPORTER THAT THROWS IS SWALLOWED for the reason this method exists — nothing above it can
    /// catch, and a failure to describe a failure must not become the crash it was preventing.</para>
    /// </summary>
    public static async Task ObserveAsync(Task turn, Action<Exception> onFault)
    {
        try
        {
            await turn;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try { onFault(ex); }
            catch (Exception) { }
        }
    }
}
