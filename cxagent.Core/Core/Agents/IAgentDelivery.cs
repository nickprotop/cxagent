namespace CxAgent.Core.Agents;

/// <summary>
/// What happened to something handed to <see cref="IAgentDelivery.Tell(string, string)"/>.
///
/// <para>AN ENUM RATHER THAN A BOOL, because the callers say different things about each outcome and a
/// port that hid the difference would force every one of them to re-derive it from the state it just
/// asked about. <c>agent_send</c> already distinguishes "asked" from "delivered — it is mid-task and
/// will see this on its next turn" in its replies to the model, and that distinction is exactly this.
/// </para>
/// </summary>
public enum DeliveryOutcome
{
    /// <summary>Joined work already in progress; the agent reads it on its next lap.</summary>
    Injected,

    /// <summary>Started a turn that would not otherwise have happened.</summary>
    Woke,

    /// <summary>
    /// Held for an agent that has stopped working, and read when it is next resumed.
    ///
    /// <para>PRODUCED BY <see cref="IAgentDelivery.Leave"/>: a note left for the agent's next answer that
    /// starts nothing. <see cref="IAgentDelivery.Tell(string, string)"/> never produces it — an idle agent
    /// told something is RUN, because a message left for a resume that may never come is a result that
    /// was lost.</para>
    ///
    /// <para>KEPT BECAUSE A DESTINATION MAY YET HAVE NO LOOP TO START: an agent from a restored
    /// session, or one a caller may only leave a note for. A consumer must therefore still handle it,
    /// and handling it means the same thing it always did — the text is somewhere the agent reads
    /// before its next answer, and nothing was spent to put it there.</para>
    /// </summary>
    Queued,

    /// <summary>Nothing holds that id — a child from an earlier session, or a stale reference.</summary>
    Unknown,

    /// <summary>
    /// Nothing was delivered, and not because the agent could not be found.
    ///
    /// <para>TWO WAYS TO GET HERE, and they are one outcome rather than two because a caller does the
    /// same thing about both: a full mailbox, and text with nothing in it. Neither is a lost message —
    /// a full queue was never given one to lose, and an empty message carries nothing to deliver.</para>
    ///
    /// <para>DISTINCT FROM <see cref="Unknown"/> BECAUSE THE REMEDY DIFFERS. An unknown id is a
    /// mistake in the addressing, and retrying it will fail the same way; a refusal is about this
    /// message or this moment, and the same text to the same agent may well land later.</para>
    /// </summary>
    Refused,
}

/// <summary>
/// Where a caller sends something an agent should read.
///
/// <para>THE GAP THIS FILLS IS ADDRESSING. Four things already tell an agent something after the fact
/// — the <c>agent_send</c> tool, the <c>/agents send</c> command, a plugin's <c>IPluginClient</c>, and
/// a user typing mid-turn — and none of them can say "the agent that started THIS piece of work".
/// <c>IPluginClient</c> is built once per session, so the only target it has ever had is the session's
/// own agent; a sub-agent inherits the whole plugin surface and its plugin calls therefore wake
/// somebody else, with no error anywhere.</para>
///
/// <para>KEYED ON AN AGENT ID, NOT A HANDLE. Every caller that already reaches a child has a handle
/// and does not need this; the callers that do — a completing process, a plugin tool called by a child
/// — hold an agent id and nothing else, because an id is what rides with a tool call. A port keyed on
/// handles would be unreachable from exactly the callers it exists for.</para>
///
/// <para>ONE-WAY, AND NOT A REPLACEMENT FOR <c>agent_send</c>. That tool asks a child a question and
/// returns its reply. This delivers text and reports what became of it; a caller that wants an answer
/// wants the tool.</para>
/// </summary>
public interface IAgentDelivery
{
    /// <summary>
    /// Delivers to the agent with this id, choosing inject or wake from its state.
    ///
    /// <para>SYNCHRONOUS, because every underlying mechanism is: appending under a lock, or a bounded
    /// queue. Only a wake runs a turn, and the existing queue drain starts that unawaited — so a
    /// caller inside a <c>finally</c> cannot be made to block on somebody else's turn.</para>
    /// </summary>
    DeliveryOutcome Tell(string agentId, string text);

    /// <summary>
    /// <see cref="Tell(string, string)"/>, with a short line for the transcript.
    ///
    /// <para>A DEFAULT MEMBER, delivering the model's text alone: an implementation with no transcript to
    /// draw on — a test double, a headless port — has nowhere to put the short line, and need not say so.</para>
    /// </summary>
    DeliveryOutcome Tell(string agentId, AgentNotice notice) => Tell(agentId, notice.ForModel);

    /// <summary>
    /// Puts the notice where the agent reads it before its next answer, WITHOUT starting or extending a
    /// turn.
    ///
    /// <para>FOR A FACT NOBODY NEEDS ACTED ON NOW: a stopped background agent. The user stopped that work,
    /// so a request spent reacting is a cost they did not ask for — but the parent must still learn that
    /// what it waits for will not come.</para>
    ///
    /// <para>REFUSED BY DEFAULT: an implementation that cannot hold a note must not claim it left one.</para>
    /// </summary>
    DeliveryOutcome Leave(string agentId, AgentNotice notice) => DeliveryOutcome.Refused;
}

/// <summary>What a <see cref="DeliveryOutcome"/> means to a caller that only needs to know whether it landed.</summary>
public static class DeliveryOutcomes
{
    /// <summary>
    /// Whether the agent has it now, or will before its next answer.
    ///
    /// <para>QUEUED COUNTS: a note left for the next turn is read before the next answer, which is all a
    /// report needs. Unknown and Refused do not — nothing will ever read it.</para>
    /// </summary>
    public static bool Delivered(DeliveryOutcome outcome) =>
        outcome is DeliveryOutcome.Woke or DeliveryOutcome.Injected or DeliveryOutcome.Queued;
}
