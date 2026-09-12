namespace CxAgent.Core.Sessions;

/// <summary>
/// Delivers to any agent this session knows: its own, or a child it kept.
///
/// <para>THE SESSION HALF IS DELEGATED, NOT REIMPLEMENTED. <c>Session.Submit</c> already joins a
/// running turn and starts one otherwise, so this calls it rather than choosing between
/// <c>Steer</c> and <c>PluginSubmitQueue</c> itself. Reaching past it would strip the originator
/// <c>UnwirePluginAsync</c>'s sever check depends on: a goal routed through <c>Steer</c> inherits the
/// RUNNING turn's originator and can masquerade as whoever started it.</para>
///
/// <para>SUBMIT DIRECTLY, NOT VIA <c>IPluginClient</c>. A client is built once per PLUGIN, in the UI
/// layer, after a session is wired — and a session with no client-declaring plugin never has one at
/// all, so a port that required one would be unreachable in exactly the ordinary case. What the
/// client adds over <c>Submit</c> is a plugin name to stamp and a queue to spill into when busy, and
/// neither belongs to a delivery: see <see cref="Origin"/>.</para>
///
/// <para>WHAT IS NEW HERE IS THE SUB-AGENT HALF. Nothing could address a child by the id its tool
/// calls carry, which is why a plugin tool called by a child woke the parent instead.</para>
/// </summary>
public sealed class SessionAgentDelivery(Session session, Agents.SubAgentStore store)
    : Agents.IAgentDelivery
{
    /// <summary>
    /// Who a delivered turn belongs to: nobody in particular, which is what <c>User</c> means.
    ///
    /// <para>NOT <c>TurnOriginator.Plugin(name)</c>, EVEN WHEN A PLUGIN'S TOOL CAUSED IT. That label
    /// is a licence to cancel: <c>UnwirePluginAsync</c> cancels the running turn when
    /// <c>CurrentOriginator.IsFrom(pluginName)</c>, so stamping a plugin's name on a turn carrying
    /// text that some AGENT asked to be told would let unwiring that plugin destroy work it does not
    /// own. A delivery is caused by the agent whose tool call arranged it, and the agent outlives any
    /// plugin's wiring.</para>
    ///
    /// <para>THE COST IS DEFERRAL, KNOWINGLY. <c>User</c> matches no <c>IsFrom</c>, so a delivered
    /// turn is never severed and an unwire waits for it as it waits for anything the user typed —
    /// which is the correct side to err on, since the alternative discards a message rather than
    /// delaying a revocation.</para>
    /// </summary>
    private static TurnOriginator Origin => TurnOriginator.User;

    public Agents.DeliveryOutcome Tell(string agentId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Agents.DeliveryOutcome.Refused;

        // THE SESSION'S OWN AGENT FIRST, and Session.SessionId IS that agent's id — AgentHost exposes
        // `_agent.Id` under that name. The cheaper test also settles the common case before searching
        // every kept child for it.
        if (agentId == session.SessionId)
        {
            // BUSY READ BEFORE THE SUBMIT, because the submit changes it: an idle session starts a
            // turn and is busy immediately after, so reading it later would report every wake as an
            // injection.
            var wasBusy = session.IsBusy;

            // THE OUTCOME IS THE ACCEPT DECISION, NOT THE TURN. Submit is synchronous and hands back
            // a receipt — Started carries the turn as a Task nobody waits on here, because a caller
            // inside a `finally` must not be made to block on somebody else's turn.
            return session.Submit(text, origin: Origin) switch
            {
                Session.SubmitOutcome.Started => wasBusy
                    ? Agents.DeliveryOutcome.Injected
                    : Agents.DeliveryOutcome.Woke,

                // STEERED INTO THE RUNNING TURN, read at its next tool barrier.
                Session.SubmitOutcome.Queued => Agents.DeliveryOutcome.Injected,

                // NO MODEL, OR THE TEXT RAN AS A COMMAND. Neither delivered anything an agent will
                // read, and both are about this message rather than the addressing — which is what
                // Refused means as against Unknown.
                _ => Agents.DeliveryOutcome.Refused,
            };
        }

        // A KEPT CHILD, BY THE ID ITS TOOL CALLS CARRY. FindByAgentId exists for exactly a caller
        // that holds an id and no handle.
        if (store.FindByAgentId(agentId) is not { } kept) return Agents.DeliveryOutcome.Unknown;

        // THE CLAIM IS THE ONE AUTHORITY ON WHETHER A LOOP IS TURNING. A child raises nothing when it
        // goes idle, because a turn ending and a goal ending look identical from outside — so the
        // send-claim the store already holds is what separates "reads this on its next lap" from
        // "reads this whenever somebody resumes it".
        var running = store.IsBusy(kept.Name);

        return kept.Agent.Agent.Mailbox.TryEnqueue(text, out _)
            ? running ? Agents.DeliveryOutcome.Injected : Agents.DeliveryOutcome.Queued
            : Agents.DeliveryOutcome.Refused;
    }
}
