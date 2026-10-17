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
/// <para>ONE OUTCOME: a move, a stop and the child's own answer race, and whichever lands first is what the waiting
/// call answers. The answer counts, because a stop accepted after it would kill the shells of a child that finished
/// and mark it stopped while the parent reads its answer.</para>
/// </summary>
public sealed class ForegroundRun : IDisposable
{
    private readonly CancellationTokenSource _own = new();
    private readonly CancellationTokenRegistration _turnLink;
    private readonly TaskCompletionSource<ForegroundRelease> _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // 0 while the child is still the turn's; 1 once it answered or was released — whichever claimed it first.
    private int _settled;

    private readonly bool _movable;

    internal ForegroundRun(CancellationToken turn, DateTimeOffset since, bool movable = true)
    {
        Since = since;
        _movable = movable;
        _turnLink = turn.Register(() => _own.Cancel());
    }

    /// <summary>The child's token.</summary>
    public CancellationToken Token => _own.Token;

    /// <summary>When the parent began waiting on it — the composer's hint waits a few seconds on this.</summary>
    public DateTimeOffset Since { get; }

    /// <summary>Completes when the child is moved or stopped; never, when it simply answers.</summary>
    public Task<ForegroundRelease> Released => _released.Task;

    /// <summary>Moves it: Escape no longer reaches it. False once released either way, or when it is not movable.</summary>
    internal bool TryMove()
    {
        if (!_movable || Interlocked.Exchange(ref _settled, 1) != 0) return false;
        _released.SetResult(ForegroundRelease.Move);
        _turnLink.Dispose();
        return true;
    }

    /// <summary>Stops it alone. False once released either way.</summary>
    internal bool TryStop()
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
        _released.SetResult(ForegroundRelease.Stop);
        _own.Cancel();
        return true;
    }

    /// <summary>
    /// Claims the child's answer for its call. False when a move or a stop landed first: the call then answers as
    /// <see cref="Released"/> says.
    /// </summary>
    internal bool TryAnswer() => Interlocked.Exchange(ref _settled, 1) == 0;

    /// <summary>
    /// Cancels the child — the adopted background run's stop, after a move. A no-op once disposed: that stop can land in
    /// the instant the run ends and disposes this, and a throw there escapes the user's stop.
    /// </summary>
    internal void Cancel()
    {
        try { _own.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _turnLink.Dispose();
        _own.Dispose();
    }
}
