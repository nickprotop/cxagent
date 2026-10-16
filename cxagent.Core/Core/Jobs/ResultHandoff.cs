namespace CxAgent.Core.Jobs;

/// <summary>What a look at a <see cref="ResultHandoff"/> found.</summary>
/// <param name="Result">The result, once there is one.</param>
/// <param name="Ended">Whether the work has ended.</param>
/// <param name="AlreadySent">True when the parent was (or is being) given it another way.</param>
public sealed record HandoffRead(string? Result, bool Ended, bool AlreadySent);

/// <summary>
/// The one place that decides how a background result reaches its parent.
///
/// <para>THERE ARE TWO WAYS IN: a message delivered to the parent, and the answer to a
/// <c>task_output</c> call that is waiting for it. A result that arrives both ways is a wake that
/// repeats it; one that arrives neither way is a lost report. Every race found in review came from
/// two places each deciding which way it went, so the decision is made here, once, under one lock,
/// and whoever loses the race is told "already sent" rather than delivering it again.</para>
///
/// <para>THE CALLBACKS RUN OUTSIDE THE LOCK. <c>send</c> and <c>leaveIfAbandoned</c> call into a
/// session that may do arbitrary work — including reading this handoff back. The state moves to
/// <c>Sending</c> first, so anyone who looks meanwhile sees it as already on its way.</para>
/// </summary>
/// <param name="leaveIfAbandoned">
/// Called when the result was handed to waiters that were then all cancelled (the user pressed
/// Escape on the turn that was waiting), so nobody received it. It is left as a note rather than
/// sent as a wake: the user has just stopped that turn, and a wake would start another one. True
/// when the note was taken; false or a throw leaves the result for <see cref="Read"/>.
/// </param>
public sealed class ResultHandoff(Func<string, bool> leaveIfAbandoned)
{
    private enum State { Pending, ToWaiters, Sending, Sent, Undelivered, Silenced, Taken }

    // A PLAIN object, matching the rest of Core, which does not use System.Threading.Lock.
    private readonly object _lock = new();

    private State _state = State.Pending;
    private string? _result;
    private bool _ended;

    // ONE SOURCE SHARED BY EVERY WAITER: several task_output calls in one parent turn all want the
    // same answer, and completing one source hands it to all of them in the same step. Created with
    // RunContinuationsAsynchronously because it is completed under the lock, and a waiter's
    // continuation run inline there would run the parent's code while holding it.
    private TaskCompletionSource<string>? _waiting;

    // HOW MANY WAITERS MAY STILL RECEIVE THE RESULT. A waiter that returns the result does not
    // decrement it, and that is the point: once the result has gone to waiters, a cancelled waiter
    // that brings this to zero proves none of them received it, and only then is it left. A waiter
    // that got it keeps the count above zero, so a sibling's cancellation leaves nothing.
    private int _waiters;

    /// <summary>Whether the work has ended (whether or not anyone has its result).</summary>
    public bool Ended { get { lock (_lock) return _ended; } }

    /// <summary>The result, once the work has ended.</summary>
    public string? Result { get { lock (_lock) return _result; } }

    /// <summary>The result could not be sent and nobody has read it yet.</summary>
    public bool Undelivered { get { lock (_lock) return _state == State.Undelivered; } }

    /// <summary>
    /// Records the result and hands it on: to the waiters if there are any, otherwise through
    /// <paramref name="send"/>. A second call is ignored — the first result is the one reported.
    /// Never throws: the work that produced the result is finishing, and has nobody to report a
    /// throw to; a <paramref name="send"/> that throws leaves the result for <see cref="Read"/>.
    /// </summary>
    /// <param name="result">What the work produced.</param>
    /// <param name="send">Delivers it to the parent; false when the parent would not take it.</param>
    public void Complete(string result, Func<string, bool> send)
    {
        lock (_lock)
        {
            if (_ended) return;
            _result = result;
            _ended = true;
            // SILENCED keeps the result for Read but tells nobody: the conversation it would go to
            // is closing.
            if (_state == State.Silenced) return;
            if (_waiters > 0)
            {
                _waiting!.TrySetResult(result);
                _state = State.ToWaiters;
                return;
            }
            _state = State.Sending;
        }
        Deliver(send, result);
    }

    /// <summary>
    /// Stops a result that has not ended yet from being sent or left anywhere; <see cref="Read"/> still
    /// returns it. Does nothing once the result is on its way.
    /// </summary>
    public void Silence()
    {
        lock (_lock)
            if (_state == State.Pending) _state = State.Silenced;
    }

    /// <summary>
    /// Looks at the result. A result nobody else will deliver — undelivered or silenced — is taken by
    /// this read, so the next read reports it as already sent.
    /// </summary>
    public HandoffRead Read()
    {
        lock (_lock) return ReadLocked();
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the result. A wait that times out leaves the result
    /// to be sent as usual; one that is cancelled after the result reached it, with no other waiter
    /// having received it, leaves the result through <c>leaveIfAbandoned</c> before rethrowing.
    /// </summary>
    /// <param name="timeout">How long to wait before answering "not ended".</param>
    /// <param name="ct">Cancels the wait; throws <see cref="OperationCanceledException"/>.</param>
    public async Task<HandoffRead> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource<string> waiting;
        lock (_lock)
        {
            if (_ended) return ReadLocked();
            _waiters++;
            waiting = _waiting ??= new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            return new HandoffRead(await waiting.Task.WaitAsync(timeout, ct), true, false);
        }
        catch (TimeoutException)
        {
            lock (_lock)
            {
                // THE RESULT ARRIVED AS THE TIMEOUT FIRED: it was handed to this waiter, so this
                // waiter returns it, and stays counted as one that received it.
                if (waiting.Task.IsCompletedSuccessfully) return new HandoffRead(waiting.Task.Result, true, false);
                ForgetWaiterLocked();
                return new HandoffRead(null, false, false);
            }
        }
        catch (OperationCanceledException)
        {
            string abandoned;
            lock (_lock)
            {
                // CANCELLED BEFORE THE END, or with another waiter still holding it: nothing to do.
                // WaitAsync can throw here even when the source completed in the same instant, so
                // what decides is the state, not which way the await came out.
                if (_state != State.ToWaiters)
                {
                    ForgetWaiterLocked();
                    throw;
                }
                if (--_waiters > 0) throw;
                _state = State.Sending;
                abandoned = _result!;
            }
            Deliver(leaveIfAbandoned, abandoned);
            throw;
        }
    }

    // A waiter leaving before the end. The last one drops the source, so the result is sent rather
    // than handed to a source nobody is listening to.
    private void ForgetWaiterLocked()
    {
        if (--_waiters == 0) _waiting = null;
    }

    private HandoffRead ReadLocked()
    {
        if (!_ended) return new HandoffRead(null, false, false);
        if (_state is State.Undelivered or State.Silenced)
        {
            _state = State.Taken;
            return new HandoffRead(_result, true, false);
        }
        return new HandoffRead(_result, true, true);
    }

    // Runs outside the lock: the callback reaches into a session that may read this handoff back.
    private void Deliver(Func<string, bool> deliver, string result)
    {
        bool delivered;
        try { delivered = deliver(result); }
        catch (Exception) { delivered = false; }
        lock (_lock) _state = delivered ? State.Sent : State.Undelivered;
    }
}
