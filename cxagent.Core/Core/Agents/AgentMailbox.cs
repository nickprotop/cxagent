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

    private readonly Queue<Entry> _pending = new();

    /// <summary>
    /// One waiting message, whether it asks to be acted on now — see <see cref="HasWaking"/> — and who
    /// sent it, null meaning nobody in particular.
    ///
    /// <para>THE SENDER RIDES WITH THE TEXT because a lap started for mail in a turn's tail is a turn of
    /// its own, and its originator is what decides whether an unwire may cancel it. Recorded anywhere
    /// else it goes stale: the running turn drains this box at every lap top without telling anyone.</para>
    /// </summary>
    /// <para>WHO SENT IT, FOR THE RECORD: an export labels each message the agent received, and a drain loses
    /// the sender unless it travels with the text — see <see cref="Incoming"/>.</para>
    private readonly record struct Entry(string Text, bool Wakes, Sessions.TurnOriginator? Origin, Incoming? From);

    // A PLAIN object, matching PluginSubmitQueue and the rest of Core, which does not use
    // System.Threading.Lock anywhere.
    private readonly object _lock = new();

    /// <summary>Takes a message, or refuses it with a reason the caller can report.</summary>
    /// <param name="message">What the agent should read.</param>
    /// <param name="refusal">Why it was not taken, when it was not.</param>
    /// <param name="wakes">
    /// Whether this asks for a turn: false leaves a note that is read before the next answer but
    /// neither extends a running turn nor starts one — see <see cref="HasWaking"/>.
    /// </param>
    /// <param name="from">Who sent it, for the agent's history; null when nobody said.</param>
    public bool TryEnqueue(string message, out string? refusal, bool wakes = true, Incoming? from = null) =>
        TryEnqueue(new Entry(message, wakes, null, from), out refusal);

    /// <summary>Takes a waking message on someone's behalf — see <see cref="DrainedOrigin"/>.</summary>
    /// <remarks>ONLY THE APPLICATION DELIVERS THIS WAY — a session's System submit — so it is recorded as a delivery.</remarks>
    internal bool TryEnqueue(string message, Sessions.TurnOriginator origin, out string? refusal) =>
        TryEnqueue(new Entry(message, true, origin, Incoming.Delivery), out refusal);

    private bool TryEnqueue(Entry entry, out string? refusal)
    {
        lock (_lock)
        {
            if (_pending.Count >= MaxDepth)
            {
                refusal = $"its mailbox is full ({MaxDepth} waiting) — it has not read the earlier "
                        + "messages yet.";
                return false;
            }
            _pending.Enqueue(entry);
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
    public IReadOnlyList<string> Drain() => DrainWithOrigin(out _);

    /// <summary>
    /// <see cref="Drain"/>, also answering whose the drained messages were, as one originator.
    ///
    /// <para>ONE PLUGIN'S ONLY WHEN EVERY MESSAGE IS THAT PLUGIN'S, and <c>User</c> otherwise. A lap
    /// stamped with a plugin's name is one an unwire of that plugin may cancel, so a lap carrying anyone
    /// else's message beside it must not carry the name — the side to err on is a deferred unwire, not a
    /// destroyed report.</para>
    /// </summary>
    internal IReadOnlyList<string> DrainWithOrigin(out Sessions.TurnOriginator origin)
    {
        lock (_lock)
        {
            origin = DrainedOrigin(_pending);
            if (_pending.Count == 0) return [];
            var all = _pending.Select(e => e.Text).ToArray();
            _pending.Clear();
            return all;
        }
    }

    /// <summary>
    /// <see cref="Drain"/>, keeping who sent each message — for a drain that writes them into the context, where
    /// the agent's history records the sender.
    /// </summary>
    public IReadOnlyList<(string Text, Incoming? From)> DrainEntries()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return [];
            var all = _pending.Select(e => (e.Text, e.From)).ToArray();
            _pending.Clear();
            return all;
        }
    }

    private static Sessions.TurnOriginator DrainedOrigin(IEnumerable<Entry> entries)
    {
        var origins = entries.Select(e => e.Origin ?? Sessions.TurnOriginator.User).Distinct().ToArray();
        return origins is [var only] ? only : Sessions.TurnOriginator.User;
    }

    /// <summary>
    /// Whether anything waiting should be acted on now.
    ///
    /// <para>NOT EVERYTHING IN THE MAILBOX DOES. A note left without a wake — a stopped background agent —
    /// must be read before the next answer but must not extend a turn or start one: the user stopped that
    /// work, and a request spent hearing so is a cost they did not ask for.</para>
    /// </summary>
    public bool HasWaking { get { lock (_lock) return _pending.Any(e => e.Wakes); } }
}
