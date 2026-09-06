using CxAgent.Core.Models;

namespace CxAgent.Core.Sessions;

/// <summary>
/// One <see cref="IToolObserver"/> over several, so a session's tool rows can reach more than one
/// front end.
///
/// <para>THE SIBLING OF <see cref="ObserverFanOut"/> AND NECESSARY FOR THE SAME REASON. Fanning out
/// only <see cref="ISessionObserver"/> would give a second client the conversation and none of the
/// working — every tool row, worker panel and progress line arrives through THIS interface, and
/// <c>AgentHost</c> takes exactly one of them.</para>
///
/// <para>A SUBSCRIBER THAT THROWS COSTS ITSELF ONE EVENT. Delivery is a plain call from the job
/// executor's own thread, so an exception escaping here would unwind into work that is running,
/// failing a tool over a rendering bug in something watching it. The loop continues past a throw, or
/// a subscriber's health would decide whether the ones after it are reached.</para>
///
/// <para>THESE EVENTS CARRY LIVE, MUTABLE <see cref="Job"/> OBJECTS — ten settable properties,
/// changed mid-flight by design. Fanning them out hands the SAME instance to every subscriber, which
/// is what the single-subscriber arrangement already did; a subscriber that needs a stable view takes
/// its own copy. Nothing here copies on the sender's behalf, because doing so on every event for
/// subscribers that mostly re-render immediately would cost more than it saves.</para>
/// </summary>
public sealed class ToolObserverFanOut : IToolObserver
{
    /// <summary>
    /// The subscribers, copied on write.
    ///
    /// <para>SUBSCRIBING IS RARE AND DELIVERY IS CONSTANT — a tool that streams output calls this
    /// continuously — so the cost belongs on the attach, and delivery iterates a snapshot that a
    /// concurrent attach cannot mutate underneath it.</para>
    /// </summary>
    private volatile IToolObserver[] _observers = [];

    private readonly object _gate = new();

    /// <summary>How many are listening.</summary>
    public int Count => _observers.Length;

    /// <summary>Adds a subscriber and hands back the token that removes it.</summary>
    public IDisposable Add(IToolObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            _observers = [.. _observers, observer];
        }

        return new Registration(this, observer);
    }

    /// <summary>Removes one registration, by reference and only once — see <see cref="ObserverFanOut"/>
    /// for why the same observer may legitimately be registered twice.</summary>
    private void Remove(IToolObserver observer)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_observers, observer);
            if (index < 0) return;

            var next = new IToolObserver[_observers.Length - 1];
            Array.Copy(_observers, next, index);
            Array.Copy(_observers, index + 1, next, index, _observers.Length - index - 1);
            _observers = next;
        }
    }

    public void ToolsChanged(IReadOnlyList<Job> jobs) => Each(o => o.ToolsChanged(jobs));

    public void ToolUpdated(Job job) => Each(o => o.ToolUpdated(job));

    public void ToolProgressed(Job job) => Each(o => o.ToolProgressed(job));

    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) =>
        Each(o => o.ToolResourcesSampled(jobId, snapshot));

    public void ToolOutputAppended(string jobId, string delta) =>
        Each(o => o.ToolOutputAppended(jobId, delta));

    /// <summary>Delivers one event to every subscriber, isolating each. See
    /// <see cref="ObserverFanOut"/> for why the exception is swallowed rather than logged.</summary>
    private void Each(Action<IToolObserver> deliver)
    {
        foreach (var observer in _observers)
        {
            try { deliver(observer); }
            catch (Exception) { }
        }
    }

    private sealed class Registration(ToolObserverFanOut owner, IToolObserver observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(observer);
        }
    }
}
