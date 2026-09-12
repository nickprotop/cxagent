namespace CxAgent.Core.Sessions;

/// <summary>
/// Delivers to any agent this session knows: its own, or a child it kept.
///
/// <para>THE SESSION HALF IS DELEGATED, NOT REIMPLEMENTED. <c>Session.Submit</c> already joins a
/// running turn and starts one otherwise, and <c>SessionPluginClient</c> layers the plugin's own
/// version on top — so this reaches for the client rather than choosing between <c>Steer</c> and
/// <c>PluginSubmitQueue</c> itself. Reaching past it would strip the originator
/// <c>UnwirePluginAsync</c>'s sever check depends on: a goal routed through <c>Steer</c> inherits the
/// RUNNING turn's originator and can masquerade as whoever started it.</para>
///
/// <para>WHAT IS NEW HERE IS THE SUB-AGENT HALF. Nothing could address a child by the id its tool
/// calls carry, which is why a plugin tool called by a child woke the parent instead.</para>
/// </summary>
public sealed class SessionAgentDelivery(
    Session session, Agents.SubAgentStore store, Plugins.IPluginClient client)
    : Agents.IAgentDelivery
{
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

            // NOT AWAITED, AND THE RESULT STILL READ. Submit only awaits a turn when wantResult is
            // set, which it is not here; what comes back is the accept/refuse decision, which is
            // available without the turn finishing.
            var result = client.Submit(text).GetAwaiter().GetResult();

            return !result.Accepted ? Agents.DeliveryOutcome.Refused
                : wasBusy ? Agents.DeliveryOutcome.Injected
                : Agents.DeliveryOutcome.Woke;
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
