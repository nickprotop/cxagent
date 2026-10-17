namespace CxAgent.Core.Agents;

/// <summary>How a foreground child was let go of before it answered.</summary>
public enum ForegroundRelease
{
    /// <summary>Moved to the background: its call is answered with a receipt, and it reports later.</summary>
    Move,
    /// <summary>Stopped by the user: its call is answered "stopped", and the turn carries on.</summary>
    Stop,
}

/// <summary>
/// A foreground child's own lifetime, linked to the turn that waits on it.
///
/// <para>ITS OWN TOKEN, NOT THE TURN'S: the turn's token cannot cancel one child without the rest, and a child moved to
/// the background must outlive Escape. So the child runs here, and the turn reaches it through a link that a move
/// disposes.</para>
///
/// <para>ONE RELEASE: a move and a stop race, and whichever lands first is what the waiting call answers.</para>
/// </summary>
public sealed class ForegroundRun : IDisposable
{
    private readonly CancellationTokenSource _own = new();
    private readonly CancellationTokenRegistration _turnLink;
    private readonly TaskCompletionSource<ForegroundRelease> _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ForegroundRun(CancellationToken turn, DateTimeOffset since)
    {
        Since = since;
        _turnLink = turn.Register(() => _own.Cancel());
    }

    /// <summary>The child's token.</summary>
    public CancellationToken Token => _own.Token;

    /// <summary>When the parent began waiting on it — the composer's hint waits a few seconds on this.</summary>
    public DateTimeOffset Since { get; }

    /// <summary>Completes when the child is moved or stopped; never, when it simply answers.</summary>
    public Task<ForegroundRelease> Released => _released.Task;

    /// <summary>Moves it: Escape no longer reaches it. False once released either way.</summary>
    internal bool TryMove()
    {
        if (!_released.TrySetResult(ForegroundRelease.Move)) return false;
        _turnLink.Dispose();
        return true;
    }

    /// <summary>Stops it alone. False once released either way.</summary>
    internal bool TryStop()
    {
        if (!_released.TrySetResult(ForegroundRelease.Stop)) return false;
        _own.Cancel();
        return true;
    }

    /// <summary>Cancels the child — the adopted background run's stop, after a move.</summary>
    internal void Cancel() => _own.Cancel();

    public void Dispose()
    {
        _turnLink.Dispose();
        _own.Dispose();
    }
}
