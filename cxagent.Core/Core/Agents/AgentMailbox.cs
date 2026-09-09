namespace CxAgent.Core.Agents;

/// <summary>
/// Messages sent to an agent while it was mid-task, waiting for its next lap.
///
/// <para>WHY THIS EXISTS: a sub-agent's run is long — a live drive measured 54 seconds for one file
/// read — and for all of it the parent could do nothing but wait, including notice something the
/// child needed to know. Three agents spawned in parallel, one finishes and learns the schema
/// changed: without this the other two run to completion against a stale assumption and are
/// re-spawned. With it, they are told.</para>
///
/// <para>A MAILBOX RATHER THAN A DIRECT WRITE, because the sender is on another thread.
/// <c>AgentContext.Messages</c> is "EXPOSED AS THE LIVE LIST, deliberately" — the turn loop appends
/// to it constantly and the compressor rewrites it in place — so appending from outside is exactly
/// the corruption <c>Agent</c> warns about when it says two concurrent sends would corrupt it. The
/// child drains this itself, at a point in its own loop where rewriting the list is already
/// legitimate.</para>
///
/// <para>BOUNDED, for the reason <see cref="Plugins.PluginSubmitQueue"/> is: a sender in a loop must
/// hit a limit rather than grow a list, and a queue deep enough to hide a malfunction is worse than
/// a refusal.</para>
/// </summary>
public sealed class AgentMailbox
{
    /// <summary>How many messages may wait for one agent at once.</summary>
    public const int MaxDepth = 8;

    private readonly Queue<string> _pending = new();

    // A PLAIN object, matching PluginSubmitQueue and the rest of Core, which does not use
    // System.Threading.Lock anywhere.
    private readonly object _lock = new();

    /// <summary>Takes a message, or refuses it with a reason the caller can report.</summary>
    public bool TryEnqueue(string message, out string? refusal)
    {
        lock (_lock)
        {
            if (_pending.Count >= MaxDepth)
            {
                refusal = $"its mailbox is full ({MaxDepth} waiting) — it has not read the earlier "
                        + "messages yet.";
                return false;
            }
            _pending.Enqueue(message);
            refusal = null;
            return true;
        }
    }

    /// <summary>
    /// Everything waiting, in the order it was sent, emptying the mailbox.
    ///
    /// <para>ALL OF IT AT ONCE rather than one per lap: two corrections sent together are one
    /// thought, and delivering them a turn apart would let the agent act on half of it.</para>
    /// </summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return [];
            var all = _pending.ToArray();
            _pending.Clear();
            return all;
        }
    }

    /// <summary>Whether anything is waiting — for a caller that wants to avoid an allocation.</summary>
    public bool HasPending
    {
        get { lock (_lock) return _pending.Count > 0; }
    }
}
