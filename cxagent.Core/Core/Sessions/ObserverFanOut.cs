using CxAgent.Core.Commands;

namespace CxAgent.Core.Sessions;

/// <summary>
/// One <see cref="ISessionObserver"/> over several, so a session can be watched by more than one
/// front end at a time.
///
/// <para>THE SESSION IS NOT THE ONLY SUBSCRIBER TO FAN OUT FOR. <c>SessionPorts.Observer</c> reaches
/// both the session and the agent host, and each reports different things through it — turn
/// boundaries and the session's own words come from one, the model's text from the other. Adding a
/// list to <c>Session</c> would therefore cover half the events, which is why this is an observer
/// rather than a change to how a session stores one: both receive this, and neither learns that
/// there is more than one listener behind it.</para>
///
/// <para>A SUBSCRIBER THAT THROWS MUST NOT END THE TURN. Delivery is a plain method call made from
/// the agent's own flow, so an exception escaping one subscriber would unwind into the turn loop and
/// fail a turn over a rendering bug in something merely WATCHING it. Each call is therefore isolated,
/// and a thrown exception costs that subscriber that one event.</para>
///
/// <para>DELIVERY IS SYNCHRONOUS AND IN ORDER, which is what today's single observer already
/// promises: <see cref="ISessionObserver.AssistantTextAppended"/> arrives token by token and a
/// subscriber that received them out of order would render nonsense. Fanning out does not change
/// that — but it does mean a SLOW subscriber holds up the turn, and a remote one must therefore hand
/// off rather than write to its socket inline. That is the remote subscriber's obligation; stating it
/// here is cheaper than discovering it as a stalled turn.</para>
/// </summary>
public sealed class ObserverFanOut : ISessionObserver
{
    /// <summary>
    /// The subscribers, copied on write.
    ///
    /// <para>SUBSCRIBING IS RARE AND DELIVERY IS CONSTANT — a front end attaches once and then
    /// receives every token of every turn — so the cost belongs on the attach. A copy on write also
    /// means delivery iterates a snapshot that a concurrent attach or detach cannot mutate underneath
    /// it, which a lock around each event would otherwise have to hold for the duration of every
    /// subscriber's work.</para>
    /// </summary>
    private volatile ISessionObserver[] _observers = [];

    private readonly object _gate = new();

    /// <summary>How many are listening. For a caller deciding whether anyone would see something.</summary>
    public int Count => _observers.Length;

    /// <summary>
    /// Adds a subscriber and hands back the token that removes it.
    ///
    /// <para>A TOKEN RATHER THAN A REMOVE(observer) METHOD, because the same front end may attach
    /// twice — a reconnect that races its own teardown — and removing "the one that equals this"
    /// would take out the wrong registration. It also makes detaching something a caller cannot
    /// forget the argument to.</para>
    /// </summary>
    public IDisposable Add(ISessionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            _observers = [.. _observers, observer];
        }

        return new Registration(this, observer);
    }

    /// <summary>
    /// Removes one registration.
    ///
    /// <para>BY REFERENCE AND ONLY ONCE, so a subscriber added twice loses exactly the registration
    /// being disposed rather than both.</para>
    /// </summary>
    private void Remove(ISessionObserver observer)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_observers, observer);
            if (index < 0) return;

            var next = new ISessionObserver[_observers.Length - 1];
            Array.Copy(_observers, next, index);
            Array.Copy(_observers, index + 1, next, index, _observers.Length - index - 1);
            _observers = next;
        }
    }

    public void UserTurnAdded(ChatMessageId id, string text) =>
        Each(o => o.UserTurnAdded(id, text));

    public void AssistantTurnBegan(ChatMessageId id) =>
        Each(o => o.AssistantTurnBegan(id));

    public void AssistantTextAppended(ChatMessageId id, string token) =>
        Each(o => o.AssistantTextAppended(id, token));

    public void AssistantReasoningAppended(ChatMessageId id, string text) =>
        Each(o => o.AssistantReasoningAppended(id, text));

    public void AssistantTurnEnded(ChatMessageId id) =>
        Each(o => o.AssistantTurnEnded(id));

    public void AssistantLabelled(ChatMessageId id, string header) =>
        Each(o => o.AssistantLabelled(id, header));

    public void Said(Message message) =>
        Each(o => o.Said(message));

    /// <summary>
    /// Delivers one event to every subscriber, isolating each.
    ///
    /// <para>THE LOOP CONTINUES PAST A THROW. One subscriber failing must not deny the event to the
    /// ones after it in the list, which would make delivery depend on registration order.</para>
    ///
    /// <para>THE EXCEPTION IS SWALLOWED, NOT LOGGED, and that is the uncomfortable half. Core has no
    /// logger at this seam, and reaching for one to report a fault in a subscriber would put a
    /// dependency here to serve the least important participant in a turn. A subscriber that wants to
    /// know it failed catches its own.</para>
    /// </summary>
    private void Each(Action<ISessionObserver> deliver)
    {
        foreach (var observer in _observers)
        {
            try { deliver(observer); }
            catch (Exception) { }
        }
    }

    private sealed class Registration(ObserverFanOut owner, ISessionObserver observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // ONCE, because a double dispose would remove a LATER registration of the same observer.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(observer);
        }
    }
}
