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
            // SYSTEM, NOT USER, AND THAT IS WHAT MAKES Injected TRUE. A delivery is something the
            // application knows — a command that finished, a job's result — and Submit routes it to
            // the agent's mailbox for the turn's next lap rather than steering it, which is announced
            // as a user turn and drawn as the user's own queued block. See Session.SubmitSource for
            // why this cannot ride on `origin`.
            return session.Submit(text, origin: Origin, source: Session.SubmitSource.System) switch
            {
                Session.SubmitOutcome.Started => wasBusy
                    ? Agents.DeliveryOutcome.Injected
                    : Agents.DeliveryOutcome.Woke,

                // HANDED TO THE RUNNING TURN, read at the top of its next lap.
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

        // CLAIMED, NOT READ. The claim is the one authority on whether a loop is turning — a child
        // raises nothing when it goes idle, because a turn ending and a goal ending look identical
        // from outside — and it must be a TEST-AND-SET rather than an IsBusy read, because the idle
        // branch below RUNS the child: a read that said idle a moment ago, followed by a send, is two
        // loops appending to one live Context.Messages, which corrupts the conversation rather than
        // throwing. TryClaim answering false IS the discovery that somebody else is running it.
        if (!store.TryClaim(kept.Name))
            // ALREADY RUNNING, SO THE MAILBOX IS ENOUGH: its loop drains that at the top of every
            // lap, which is the next place it will look.
            return kept.Agent.Agent.Mailbox.TryEnqueue(text, out _)
                ? Agents.DeliveryOutcome.Injected
                : Agents.DeliveryOutcome.Refused;

        // SETTLED, SO IT IS RUN — and this is where a delivery differs from `agent_send`'s mailbox
        // drop. Nothing else will ever resume a child that has finished its goal: the parent took its
        // final report and moved on, so a message left waiting is a result that exists nowhere and is
        // read by nobody. The text a background command produced is exactly that, which is why
        // waking is worth the turn it costs.
        if (!kept.Agent.Agent.Mailbox.TryEnqueue(text, out _))
        {
            store.Release(kept.Name);
            return Agents.DeliveryOutcome.Refused;
        }

        RunSettled(store, kept);
        return Agents.DeliveryOutcome.Woke;
    }

    /// <summary>
    /// Resumes a settled child on the message just left for it, releasing the claim however it ends.
    ///
    /// <para>NOT AWAITED BY <see cref="Tell"/>, WHICH IS SYNCHRONOUS ON PURPOSE: a caller inside a
    /// <c>finally</c> — a process exiting, a watch firing — must not be made to block on somebody
    /// else's turn. The claim taken before this starts is what keeps a second delivery from starting a
    /// second loop on the same context while this one runs.</para>
    ///
    /// <para>THE MAILBOX BECOMES THE PROMPT, not a silent append ahead of an empty one. The text is
    /// already enqueued when this begins — the enqueue is what proved there was room for it — and
    /// <c>SendAsync</c> appends whatever prompt it is given unconditionally, so passing "" would put a
    /// user message saying nothing in front of the model. Anything that was waiting from an earlier
    /// delivery goes first, for the reason <c>agent_send</c> drains before it appends: two messages
    /// read in the wrong order are worse than one arriving late.</para>
    ///
    /// <para>SILENT: <c>Release</c>, AND NEVER <c>AnnounceBegin</c>/<c>EndSend</c>. A begin starts the
    /// child's row ticking and rebases its clock, and a listener records the matching end as a
    /// finished RUN — but nobody asked a child for this work, so painting it as a run would put a
    /// spend nobody requested into the archive /stats averages over. <c>Release</c> is the pair the
    /// store documents for a claim that announced nothing.</para>
    ///
    /// <para>SWALLOWS WHAT THE TURN THROWS, because there is nobody to throw to: this runs on a thread
    /// no caller holds, and an escaping exception there takes the process down rather than reporting
    /// anything. The child's own sink already recorded whatever failed.</para>
    /// </summary>
    private static void RunSettled(Agents.SubAgentStore store, Agents.StoredAgent kept)
        => _ = Task.Run(async () =>
        {
            try
            {
                // DRAINED, NOT READ THEN DRAINED. A second delivery cannot be running concurrently —
                // it would have failed to claim — but the child's own loop drains this mailbox too,
                // and taking it once is what keeps a message from being read in both places.
                var waiting = kept.Agent.Agent.Mailbox.Drain();
                if (waiting.Count == 0) return;

                // CancellationToken.None, BECAUSE NO TURN OWNS THIS. Every other resume runs under
                // the token of the turn that asked, so Escape cancels it; nothing asked for this one,
                // and there is no token in scope that cancelling would be about.
                await kept.Agent.Agent.SendAsync(string.Join("\n\n", waiting),
                    CancellationToken.None);
            }
            catch
            {
                // Reported already by the child's own observer — see the doc above.
            }
            finally
            {
                store.Release(kept.Name);
            }
        });
}
