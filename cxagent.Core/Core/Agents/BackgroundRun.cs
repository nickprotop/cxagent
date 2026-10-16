using CxAgent.Core.Jobs;

namespace CxAgent.Core.Agents;

/// <summary>Where a background stretch of a child's work stands.</summary>
public enum BackgroundState
{
    /// <summary>Waiting for a concurrency slot; nothing has been announced yet.</summary>
    Queued,

    Running,

    /// <summary>Ended on its own — completed, capped, stuck, failed or silent; the report says which.</summary>
    Finished,

    /// <summary>Ended because something stopped it.</summary>
    Stopped,
}

/// <summary>What one background stretch was started with.</summary>
/// <param name="Name">The handle the child is kept under.</param>
/// <param name="ParentAgentId">The agent told when it ends — the session agent that asked.</param>
/// <param name="Brief">The prompt this stretch was given, repeated in its report.</param>
/// <param name="Description">The spawn's label; null for a resume, which has none of its own.</param>
/// <param name="IsResume">An agent_send to a finished child rather than a spawn.</param>
public sealed record BackgroundStart(string Name, string ParentAgentId, string Brief,
    string? Description, bool IsResume);

/// <summary>
/// One background stretch of a child's work: its own token, its state, and the handoff its report goes
/// through.
///
/// <para>ITS OWN TOKEN, NOT THE TURN'S, because outliving the turn is the whole point: a child on the
/// turn's token would die the moment the user pressed Escape on unrelated work. Stopping one is a
/// separate act — task_stop, /tasks stop, closing the tab.</para>
/// </summary>
public sealed class BackgroundRun
{
    private readonly object _gate = new();

    /// <summary>
    /// The run's own cancellation.
    ///
    /// <para>NEVER DISPOSED. A stop and the run's end race by design — the user may stop it in the same
    /// instant it finishes — and disposing on finish would turn that race into an
    /// ObjectDisposedException on the stopping thread. A source with no timer holds nothing that needs
    /// releasing; the collector takes it with the run.</para>
    /// </summary>
    private readonly CancellationTokenSource _cancel = new();

    private BackgroundState _state = BackgroundState.Queued;
    private bool _stoppedByUser;
    private bool _stopRequested;
    private bool _answered;

    internal BackgroundRun(BackgroundStart start, ResultHandoff handoff)
    {
        Start = start;
        Handoff = handoff;
        Started = DateTimeOffset.UtcNow;
    }

    public BackgroundStart Start { get; }

    public DateTimeOffset Started { get; }

    /// <summary>Where the report goes — to a waiting task_output, or to the parent as a message.</summary>
    public ResultHandoff Handoff { get; }

    public BackgroundState State { get { lock (_gate) return _state; } }

    /// <summary>
    /// Whether the USER stopped it, as against the parent.
    ///
    /// <para>THE DIFFERENCE IS WHO MAY UNDO IT: the parent's agent_send to a child the user stopped is
    /// refused, so the model cannot quietly restart work the user ended; a child the parent stopped is
    /// the parent's to resume.</para>
    /// </summary>
    public bool StoppedByUser { get { lock (_gate) return _stoppedByUser; } }

    internal CancellationToken Token => _cancel.Token;

    internal void MarkRunning()
    {
        lock (_gate) if (_state == BackgroundState.Queued) _state = BackgroundState.Running;
    }

    /// <summary>
    /// Records that the child has answered, and answers whether a stop got there first.
    ///
    /// <para>FROM HERE A STOP IS REFUSED: the work is done, and a stop accepted now would tell the user
    /// "stopped" over a report that says "completed".</para>
    /// </summary>
    internal bool MarkAnswered()
    {
        lock (_gate)
        {
            if (_stopRequested || _state is BackgroundState.Finished or BackgroundState.Stopped) return false;
            _answered = true;
            return true;
        }
    }

    internal void Finish(BackgroundState state)
    {
        lock (_gate) _state = state;
    }

    /// <summary>
    /// Cancels the run, remembering whether the user did it. False once it has answered or ended — there
    /// is nothing left to stop, and claiming a stop would make the caller report one.
    ///
    /// <para>THE USER'S STOP OUTRANKS THE PARENT'S: once the user has stopped a run it stays the user's,
    /// whoever else also stops it, since the user's decision is the one the refusal protects.</para>
    /// </summary>
    internal bool RequestStop(bool byUser)
    {
        lock (_gate)
        {
            if (_answered || _state is BackgroundState.Finished or BackgroundState.Stopped) return false;
            _stopRequested = true;
            _stoppedByUser |= byUser;
        }

        // OUTSIDE THE LOCK: cancellation runs the token's callbacks synchronously, and they belong to the
        // child's turn, which must not run under this run's lock.
        _cancel.Cancel();
        return true;
    }

    /// <summary>
    /// Hands the run back to the model's control — the user resumed it themselves.
    /// </summary>
    internal void ClearUserStop()
    {
        lock (_gate) _stoppedByUser = false;
    }
}
