namespace CxAgent.Core.Sessions;

/// <summary>
/// What the user typed while a turn was running, and how it reaches the model.
///
/// <para>ONE MESSAGE, NOT A LIST. Several lines typed in a burst are one thought completed — a
/// correction and then its qualifier — so a second line APPENDS rather than starting a second entry.
/// That removes the case where half is delivered and half is still pending, which is the whole
/// reason a front end would otherwise need to know WHICH lines went in.</para>
///
/// <para>DELIVERED AT THE TURN'S NEXT TOOL BARRIER, where the model can still act on it — a
/// correction typed mid-turn is the normal case, not an error.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>
    /// What the user typed while a turn was running, not yet given to the model.
    ///
    /// <para>ONE MESSAGE, NOT A LIST. Several lines typed in a burst are one thought completed — a
    /// correction and then its qualifier — so a second line APPENDS rather than starting a second
    /// entry. It was already effectively one message: the previous list was only ever consumed by
    /// joining it with newlines, so nothing downstream could tell the difference. Making the data
    /// model say so removes the case where half of it is delivered and half is still pending, which
    /// is the whole reason the UI needed to know WHICH lines went in.</para>
    ///
    /// <para>ON THE SESSION, so two sessions in one process cannot share a queue — a line typed into
    /// one must not reach whichever turn finishes first.</para>
    ///
    /// <para>LOCKED, because the two sides are on different threads: the UI appends from the render
    /// loop while the turn takes it from the agent's own flow.</para>
    /// </summary>
    private string? _pending;
    private readonly object _pendingGate = new();

    /// <summary>
    /// What is waiting changed: the whole queue, and the line just added.
    ///
    /// <para>BOTH, BECAUSE ONLY THIS MOMENT HAS BOTH. The append happens under the lock; afterwards a
    /// subscriber holding only the whole cannot recover what changed, and one holding only the
    /// increment has to read <see cref="PendingSteer"/> back — re-entering the lock this event exists
    /// to keep it out of. Carrying both is what makes these events a COMPLETE account of the queue,
    /// so a subscriber never needs the property at all.</para>
    ///
    /// <para>WHOLE FIRST because it is what every consumer today uses — the transcript block is
    /// rewritten from it — and because <see cref="Drained"/> and <see cref="Cancelled"/> carry the
    /// whole in the same position. A subscriber that only tracks current state reads parameter one
    /// everywhere.</para>
    ///
    /// <para>PER APPEND, NOT COALESCED. The UI already redraws once per line — ShowQueued was called
    /// inline after Steer, not on a render tick — so this reproduces existing behaviour exactly.
    /// Coalescing here would need a timer or a frame hook, which is UI knowledge Core does not have,
    /// and it would make the increment incoherent: three lines merged into one event have no single
    /// "added". A subscriber that wants a slower rate already has a render loop to do it in.</para>
    /// </summary>
    public event Action<string, string>? Pending;

    /// <summary>What was waiting has been given to the model. Carries what went.</summary>
    public event Action<string>? Drained;

    /// <summary>What was waiting has been taken back, and never sent. Carries what was returned.
    ///
    /// <para>SEPARATE FROM <see cref="Drained"/> even though both empty the queue, because a
    /// subscriber does opposite things: drained means the real message is about to appear and the
    /// stand-in should go, cancelled means put it back where it can be edited. One "emptied" event
    /// would force every subscriber to reconstruct which happened.</para></summary>
    public event Action<string>? Cancelled;

    /// <summary>Adds to what is waiting, starting it if nothing was. Newline-separated: the lines
    /// were separate thoughts when they were typed, and the break is structure a model reads.</summary>
    public void Steer(string text)
    {
        string whole;
        lock (_pendingGate)
            whole = _pending = string.IsNullOrEmpty(_pending) ? text : _pending + "\n" + text;

        // RAISED OUTSIDE THE LOCK, always. A subscriber doing synchronous work — reading the queue
        // back, or blocking on the UI thread while the UI thread waits here — deadlocks if this is
        // raised while holding _pendingGate. It compiles, it passes every test, and it hangs only in
        // the app: the same failure shape as a permission test that awaits a prompt nobody answers.
        Pending?.Invoke(whole, text);
    }

    /// <summary>
    /// Empties the queue and hands back what was in it, unsent.
    ///
    /// <para>AGNOSTIC ABOUT WHERE IT GOES. This does not know a composer exists; it raises
    /// <see cref="Cancelled"/> and whoever cares decides. The UI puts it back above whatever has been
    /// typed since — the queued lines came first — but a log writer would record it and a headless
    /// embedder would drop it, and neither is this method's business.</para>
    ///
    /// <para>SILENT WHEN EMPTY. Cancelling nothing is not an event: a subscriber that restored an
    /// empty string into a composer would clear what the user had typed since.</para>
    /// </summary>
    public void CancelPending()
    {
        string? taken;
        lock (_pendingGate) { taken = _pending; _pending = null; }

        if (taken is { Length: > 0 }) Cancelled?.Invoke(taken);
    }

    /// <summary>What is waiting, or null. For the UI to render — takes nothing.</summary>
    public string? PendingSteer
    {
        get { lock (_pendingGate) return _pending; }
    }

    /// <summary>
    /// Takes what is waiting and clears it, so it is delivered exactly once.
    ///
    /// <para>WHOLE OR NOT AT ALL. There is nothing to take partially, which is what makes the
    /// promoted/pending split a single boolean everywhere else: the transcript block is removed, not
    /// shrunk, and Escape returns everything still here because everything here is un-delivered.</para>
    /// </summary>
    public string? TakePendingSteer()
    {
        string? taken;
        lock (_pendingGate) { taken = _pending; _pending = null; }

        // OUTSIDE THE LOCK — see Steer. This one is raised from the agent's own flow while Steer
        // comes from the render loop, which is the two-thread pair the lock exists for and the pair
        // a subscriber would deadlock between.
        if (taken is { Length: > 0 }) Drained?.Invoke(taken);

        return taken;
    }
}
