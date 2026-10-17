using CxAgent.Core.Agents;

namespace CxAgent.UI;

/// <summary>How a kept agent stands, as a row says it.</summary>
public enum AgentRowState { Queued, Running, WaitingForPermission, Done, Stopped, Failed }

/// <summary>One kept agent as the Tasks panel and its tab show it.</summary>
/// <param name="Kept">The agent and the handle it answers to.</param>
/// <param name="State">How it stands.</param>
/// <param name="Foreground">The parent's turn is waiting on its answer — no composer and no <c>x</c> while it runs.</param>
/// <param name="Elapsed">Since its stretch began, or how long the stretch took once ended.</param>
/// <param name="LatestCall">The newest tool call it started, or null before its first.</param>
/// <param name="Began">When the stretch began — the sort key among rows.</param>
/// <param name="End">How it ended, or null while it is going — the ✔/✗ mark (decision 16).</param>
public sealed record AgentRowView(StoredAgent Kept, AgentRowState State, bool Foreground, TimeSpan Elapsed,
    string? LatestCall, DateTimeOffset Began, AgentEnd? End)
{
    public string AgentId => Kept.Agent.Agent.Id;

    public bool Running => State is AgentRowState.Queued or AgentRowState.Running or AgentRowState.WaitingForPermission;
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
    public static AgentRowView? Describe(SubAgentStore store, StoredAgent kept, DateTimeOffset now)
    {
        var name = kept.Name;
        var run = store.BackgroundOf(name);
        var latest = LatestCall(kept.Agent);

        if (run is { State: BackgroundState.Queued })
            return new(kept, AgentRowState.Queued, false, now - run.Started, latest, run.Started, null);

        if (run is { State: BackgroundState.Running })
        {
            var since = store.WorkingSince(name) ?? run.Started;
            return new(kept, Working(kept), false, now - since, latest, since, null);
        }

        // A CLAIM WITH NO RUN GOING IS SOMEONE WAITING ON THE ANSWER — the parent's turn, unless it is the user's
        // own send. Unannounced, it is still waiting for a concurrency slot.
        if (store.IsBusy(name))
        {
            var since = store.WorkingSince(name);
            var state = since is null ? AgentRowState.Queued : Working(kept);
            var began = since ?? now;
            return new(kept, state, Foreground: !store.IsUserSend(name), now - began, latest, began, null);
        }

        if (EndOf(store, name, run) is { } end)
            return new(kept, Ended(end), false, end.At - end.Began, latest, end.Began, end);

        return null;
    }

    /// <summary>
    /// The agents the panel shows: every kept one that has been given work, working or ended (decision 4′).
    ///
    /// <para>NO FILTER HERE. Nothing clears on its own, and what the user forgets has left the store, so the store's
    /// list is the panel's.</para>
    /// </summary>
    public static IReadOnlyList<AgentRowView> For(SubAgentStore store, DateTimeOffset now) =>
        [.. store.All().Select(kept => Describe(store, kept, now)).OfType<AgentRowView>()];

    /// <summary>The word a row and a tab header use for a state.</summary>
    public static string Word(AgentRowState state) => state switch
    {
        AgentRowState.Queued => "queued",
        AgentRowState.Running => "running",
        AgentRowState.WaitingForPermission => "waiting for permission",
        AgentRowState.Done => "done",
        AgentRowState.Stopped => "stopped",
        _ => "failed",
    };

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

    /// <summary>The newest call the child started — the panel's "what is it doing" line.</summary>
    private static string? LatestCall(SubAgent child) =>
        child.Jobs.Jobs.OrderByDescending(j => j.StartedAt ?? j.CreatedAt).FirstOrDefault()?.DisplayName;
}
