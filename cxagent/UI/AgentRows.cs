using CxAgent.Core.Agents;

namespace CxAgent.UI;

/// <summary>How a kept agent stands, as a row says it.</summary>
/// <summary>How a kept agent stands. <see cref="WaitingToLearn"/> is a colleague's ask held until what it lived is
/// distilled — see ColleagueDesk.</summary>
public enum AgentRowState { Queued, WaitingToLearn, Running, WaitingForPermission, Done, Stopped, Failed }

/// <summary>One kept agent as the Tasks panel and its tab show it.</summary>
/// <param name="Kept">The agent and the handle it answers to.</param>
/// <param name="State">How it stands.</param>
/// <param name="Foreground">The parent's turn is waiting on its answer — no composer and no <c>x</c> while it runs.</param>
/// <param name="Elapsed">Since its stretch began, or how long the stretch took once ended.</param>
/// <param name="Began">When the stretch began — the sort key among rows.</param>
/// <param name="End">How it ended, or null while it is going — the ✔/✗ mark (decision 16).</param>
public sealed record AgentRowView(StoredAgent Kept, AgentRowState State, bool Foreground, TimeSpan Elapsed,
    DateTimeOffset Began, AgentEnd? End)
{
    public string AgentId => Kept.Agent.Agent.Id;

    /// <summary>What it is when more than an ordinary worker — a colleague — or null.</summary>
    public WorkerBadge? Badge { get; init; }

    public bool Running => State is AgentRowState.Queued or AgentRowState.WaitingToLearn or AgentRowState.Running or AgentRowState.WaitingForPermission;

    /// <summary>
    /// A colleague's Talk worker here, by agent id — the conversation the user holds with it — or null. On the card of
    /// its Ask worker, which stands for the colleague: one card, both doors.
    /// </summary>
    public string? TalkAgentId { get; init; }

    /// <summary>What the card shows is the Talk worker at work — "talking with you" rather than "answering an ask".</summary>
    public bool Talking { get; init; }
}

/// <summary>
/// How each kept agent stands, read from the store — one account for the panel's rows and the tab's header, so the
/// two cannot disagree about whether an agent is running.
///
/// <para>PURE AND UI-FREE, so the rules are testable without a window: a background run's state wins, then a
/// claim (someone is waiting on it), then the store's record of how the last stretch ended.</para>
/// </summary>
public static class AgentRows
{
    /// <summary>
    /// How this agent stands now, or null for one that has never been given work.
    /// </summary>
    public static AgentRowView? Describe(SubAgentStore store, StoredAgent kept, DateTimeOffset now) =>
        DescribeState(store, kept, now) is { } row ? row with { Badge = store.BadgeOf(kept.Name) } : null;

    private static AgentRowView? DescribeState(SubAgentStore store, StoredAgent kept, DateTimeOffset now)
    {
        var name = kept.Name;
        var run = store.BackgroundOf(name);

        if (run is { State: BackgroundState.Queued })
            return new(kept, AgentRowState.Queued, false, now - run.Started, run.Started, null);

        if (run is { State: BackgroundState.Running })
        {
            var since = store.WorkingSince(name) ?? run.Started;
            return new(kept, Working(kept), false, now - since, since, null);
        }

        // AN ASK HELD UNTIL WHAT IT LIVED IS LEARNED — before "queued", which a claim with no start would otherwise read as.
        // ITS CLOCK RUNS FROM WHEN THE WAIT BEGAN, so a long distil shows as long.
        if (store.WaitingToLearnSince(name) is { } waiting)
            return new(kept, AgentRowState.WaitingToLearn, Foreground: store.IsBusy(name) && !store.IsUserSend(name),
                now - waiting, waiting, null);

        // A CLAIM WITH NO RUN GOING IS SOMEONE WAITING ON THE ANSWER — the parent's turn, unless it is the user's
        // own send. Unannounced, it is still waiting for a concurrency slot.
        if (store.IsBusy(name))
        {
            var since = store.WorkingSince(name);
            var state = since is null ? AgentRowState.Queued : Working(kept);
            var began = since ?? now;
            return new(kept, state, Foreground: !store.IsUserSend(name), now - began, began, null);
        }

        if (EndOf(store, name, run) is { } end)
            return new(kept, Ended(end), false, end.At - end.Began, end.Began, end);

        return null;
    }

    /// <summary>
    /// The agents the panel shows: every kept one that has been given work, working or ended (decision 4′), and every
    /// colleague here — one brought without a task has never worked, and is still a teammate in the room.
    ///
    /// <para>NO FILTER HERE. Nothing clears on its own, and what the user forgets has left the store, so the store's
    /// list is the panel's.</para>
    /// </summary>
    public static IReadOnlyList<AgentRowView> For(SubAgentStore store, DateTimeOffset now)
    {
        var rows = new List<AgentRowView>();
        foreach (var kept in store.All())
        {
            // ONE CARD PER COLLEAGUE: its Talk worker folds into the card of its Ask worker when one is here — and stands
            // alone when none is, a kept worker's conversation among them.
            var origin = store.OriginOf(kept.Name);
            if (store.KeepsConversation(kept.Name) && origin is not null && store.NameOfOrigin(origin) is not null) continue;
            if (Row(store, kept, now) is not { } row) continue;
            if (origin is null || store.BadgeOf(kept.Name) is null) { rows.Add(row); continue; }

            var talk = store.KeepsConversation(kept.Name) ? kept : store.ConversationOf(origin) is { } t ? store.Find(t) : null;
            row = row with { TalkAgentId = talk?.Agent.Agent.Id, Talking = talk is not null && ReferenceEquals(talk, kept) && row.Running };
            // THE CONVERSATION AT WORK SHOWS ON THE CARD while the asks are at rest: the colleague is busy either way.
            if (!row.Running && talk is not null && !ReferenceEquals(talk, kept) && Row(store, talk, now) is { Running: true } talking)
                row = talking with { Kept = kept, TalkAgentId = talk.Agent.Agent.Id, Talking = true, Badge = row.Badge };
            rows.Add(row);
        }
        return rows;
    }

    private static AgentRowView? Row(SubAgentStore store, StoredAgent kept, DateTimeOffset now) =>
        Describe(store, kept, now) ?? (store.BadgeOf(kept.Name) is { } badge ? Idle(kept) with { Badge = badge } : null);

    /// <summary>
    /// A row for one that has never been given work — as idle as one whose last task ended, which is what a colleague
    /// is between tasks. Sorted after every row that has a time of its own.
    /// </summary>
    public static AgentRowView Idle(StoredAgent kept) =>
        new(kept, AgentRowState.Done, false, TimeSpan.Zero, DateTimeOffset.MinValue, null);

    /// <summary>The word a row and a tab header use for a state.</summary>
    public static string Word(AgentRowState state) => state switch
    {
        AgentRowState.Queued => "queued",
        AgentRowState.WaitingToLearn => "waiting to learn",
        AgentRowState.Running => "running",
        AgentRowState.WaitingForPermission => "waiting for permission",
        AgentRowState.Done => "done",
        AgentRowState.Stopped => "stopped",
        _ => "failed",
    };

    /// <summary>
    /// A colleague's word for a state. NEVER "done" or "finished": a colleague stays in the room between tasks, so an
    /// ended task leaves it idle — and a failed one says so beside "idle" rather than calling the colleague failed.
    /// While its rounds are being distilled it is learning, unless it has been given work again meanwhile.
    /// </summary>
    public static string ColleagueWord(AgentRowState state, bool learning) => state switch
    {
        AgentRowState.Running => "working",
        AgentRowState.Queued => "queued",
        AgentRowState.WaitingToLearn => "waiting to learn",
        AgentRowState.WaitingForPermission => "waiting for permission",
        _ when learning => "learning",
        AgentRowState.Failed => "idle · last task failed",
        _ => "idle",
    };

    /// <summary>
    /// A colleague's word for a state, from its card — the one both front ends draw: frozen says so after the state, and a
    /// frozen colleague is never "learning", since nothing of it is distilled.
    /// </summary>
    public static string ColleagueWord(AgentRowState state, CxAgent.Core.Colleagues.ColleagueCard card) =>
        ColleagueWord(state, card.Learning && !card.Frozen) + (card.Frozen ? " · frozen" : "");

    /// <summary>
    /// A colleague card's word — both front ends draw it: what its work is, asks or the conversation with the user, and
    /// otherwise its state as <see cref="ColleagueWord(AgentRowState, CxAgent.Core.Colleagues.ColleagueCard)"/> says it.
    /// </summary>
    public static string ColleagueWord(AgentRowView row, CxAgent.Core.Colleagues.ColleagueCard card) =>
        row.State is AgentRowState.Running
            ? (row.Talking ? "talking with you" : "answering an ask") + (card.Frozen ? " · frozen" : "")
            : ColleagueWord(row.State, card);

    private static AgentRowState Working(StoredAgent kept) =>
        kept.Agent.Agent.IsWaitingOnPermission ? AgentRowState.WaitingForPermission : AgentRowState.Running;

    private static AgentRowState Ended(AgentEnd end) =>
        end.Succeeded ? AgentRowState.Done
        : end.Outcome == "cancelled" ? AgentRowState.Stopped
        : AgentRowState.Failed;

    /// <summary>
    /// How the last stretch ended: the store's record — unless a background run ended after it without announcing,
    /// which is a run stopped while still queued, and reads as stopped from the run itself.
    ///
    /// <para>DERIVED FROM THE RUN'S START, so the same run yields an equal value on every read.</para>
    /// </summary>
    private static AgentEnd? EndOf(SubAgentStore store, string name, BackgroundRun? run)
    {
        var recorded = store.LastEnd(name);
        if (run is { State: BackgroundState.Stopped or BackgroundState.Finished }
            && (recorded is null || recorded.At < run.Started))
            return new AgentEnd(run.State == BackgroundState.Stopped ? "cancelled" : "error", run.Started, run.Started);
        return recorded;
    }

}
