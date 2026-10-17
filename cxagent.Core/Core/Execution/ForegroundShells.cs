using System.Collections.Concurrent;

namespace CxAgent.Core.Execution;

/// <summary>
/// The foreground shell calls an agent is waiting on, each with a token that moves it to the background — Ctrl+B.
///
/// <para>PER SESSION: Ctrl+B moves what that session's turn is waiting on, and nothing of another tab's.</para>
/// </summary>
public sealed class ForegroundShells
{
    private readonly ConcurrentDictionary<long, Entry> _calls = new();
    private long _next;

    private sealed record Entry(string AgentId, DateTimeOffset Since, CancellationTokenSource Detach);

    /// <summary>A foreground call's place in the registry; disposed when the call returns, however.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly ForegroundShells _owner;
        private readonly long _id;
        private readonly CancellationTokenSource _detach;

        internal Registration(ForegroundShells owner, long id, CancellationTokenSource detach) =>
            (_owner, _id, _detach) = (owner, id, detach);

        /// <summary>Cancelled when the user moves the call to the background.</summary>
        public CancellationToken Token => _detach.Token;

        public void Dispose()
        {
            _owner._calls.TryRemove(_id, out _);
            _detach.Dispose();
        }
    }

    /// <summary>Registers a foreground call by the agent that made it.</summary>
    public Registration Begin(string agentId)
    {
        var id = Interlocked.Increment(ref _next);
        var detach = new CancellationTokenSource();
        _calls[id] = new Entry(agentId, DateTimeOffset.UtcNow, detach);
        return new Registration(this, id, detach);
    }

    /// <summary>
    /// Moves every waiting call whose agent matches; answers how many.
    ///
    /// <para>A CALL ALREADY MOVED IS SKIPPED: it stays registered until its call returns, and a second Ctrl+B (tmux
    /// needs two) would otherwise report it moved again. A command that exits in the same instant still counts — the
    /// answer is given before the runner sees either.</para>
    /// </summary>
    public int DetachWhere(Func<string, bool> owner)
    {
        var moved = 0;
        foreach (var entry in Waiting(owner))
        {
            try { entry.Detach.Cancel(); moved++; } catch (ObjectDisposedException) { }
        }
        return moved;
    }

    /// <summary>How many matching calls are waiting — one task each in the composer's hint.</summary>
    public int Count(Func<string, bool> owner) => Waiting(owner).Count();

    /// <summary>When the earliest matching call began waiting; null with none.</summary>
    public DateTimeOffset? WaitingSince(Func<string, bool> owner) =>
        Waiting(owner).Select(e => (DateTimeOffset?)e.Since).Min();

    /// <summary>The matching calls still waiting — not yet moved, so still the turn's.</summary>
    private IEnumerable<Entry> Waiting(Func<string, bool> owner) =>
        _calls.Values.Where(e => owner(e.AgentId) && !IsMoved(e));

    private static bool IsMoved(Entry entry)
    {
        try { return entry.Detach.IsCancellationRequested; }
        catch (ObjectDisposedException) { return true; }
    }
}
