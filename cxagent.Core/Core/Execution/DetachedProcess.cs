using System.Diagnostics;
using System.Text;
using CxAgent.Core.Jobs;

namespace CxAgent.Core.Execution;

/// <summary>
/// A child process the call that started it has stopped waiting for.
///
/// <para>IT OWNS THE <see cref="Process"/>, WHICH IS THE WHOLE POINT. <see cref="ProcessRunner"/>
/// holds its process in a <c>using</c>, so returning early from <c>RunAsync</c> would dispose the
/// object whose <c>OutputDataReceived</c> handlers are the only thing writing the output file — the
/// command would keep running and nothing would record it. Ownership therefore MOVES here: this
/// object keeps the <see cref="Process"/>, the handlers and the output writer alive until the child
/// exits, and disposes all three itself.</para>
///
/// <para>OUTPUT GOES TO A FILE, NOT A BUFFER. A detached command's caller is already gone, so there
/// is nobody to hand an in-memory tail to; the file is what the agent is later pointed at, and it is
/// written to the same per-job directory a spill goes to for the same reason —
/// <c>FolderSessionStore.Prune</c> already sweeps it.</para>
/// </summary>
public sealed class DetachedProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private bool _finished;
    private int _exitCode;

    /// <summary>Whether <see cref="ProcessRunner.DetachAsync"/> has finished starting the output
    /// readers, before which the <see cref="Process"/> must not be disposed — see
    /// <see cref="ReleaseTheHandle"/>.</summary>
    private bool _readingBegun;

    /// <summary>Set when the child exited before reading was established, so the disposal that was
    /// skipped then still happens once it is.</summary>
    private bool _releasePending;

    /// <summary>
    /// Signalled once BOTH redirected streams have reported end-of-stream, so the output file can be
    /// closed knowing nothing more is coming.
    ///
    /// <para>WAITED FOR BEFORE THE WRITER IS CLOSED, because the exit and the last line of output are
    /// noticed by different threads. The waiter thread learns of the exit from the OS, which happens
    /// BEFORE the pool-driven read handlers have delivered what the command printed — so closing the
    /// file on the exit alone truncates it, and a command's final line is exactly the part worth
    /// reading. Measured: `echo started; sleep 2; echo finished; exit 3` lost "finished" in a quarter
    /// of runs.</para>
    /// </summary>
    private readonly CountdownEvent _drained = new(2);

    /// <summary>The child's process id — the only handle a caller outside this process has on it.</summary>
    public int Pid { get; }

    /// <summary>Where everything the command prints is being written, both streams interleaved as a
    /// terminal would show them, or null when no directory was given to write into.</summary>
    public string? OutputPath { get; }

    /// <summary>
    /// Raised once with the exit code when the child finally exits.
    ///
    /// <para>ONCE, AND ONLY FROM INSIDE THE LOCK THAT SETS <see cref="_finished"/>. Both the waiter
    /// thread and a <see cref="Kill"/> reach the completion path, and an agent told twice that a
    /// command finished would report it twice.</para>
    ///
    /// <para>RAISED ON WHICHEVER OF THOSE NOTICED FIRST, so a handler must assume no particular thread.
    /// Ordinarily that is <see cref="WaitForTheChild"/>'s own thread, where a handler that blocks holds
    /// up nothing else — but it is also the only thing running there, so the exit report is delivered
    /// from it synchronously by design.</para>
    /// </summary>
    public event Action<int>? Exited;

    /// <summary>
    /// Takes ownership of an already-started process whose output handlers are wired to
    /// <paramref name="writer"/>.
    ///
    /// <para>Internal because the invariant it relies on — started, handlers attached, reading begun
    /// — cannot be expressed in a signature. <see cref="ProcessRunner.DetachAsync"/> is the only
    /// thing that can honour it.</para>
    /// </summary>
    internal DetachedProcess(Process process, StreamWriter? writer, string? outputPath)
    {
        _process = process;
        _writer = writer;
        OutputPath = outputPath;
        Pid = process.Id;

        // NO Process.Exited SUBSCRIPTION AT ALL, AND THAT IS THE POINT. It looks like the obvious way
        // to notice an exit and is the wrong one twice over: it is raised from a THREAD-POOL WORK ITEM,
        // so a saturated pool delays it indefinitely — measured at over four seconds for a child that
        // had already died, against a delivery deadline the agent's exit report has to meet — and it
        // fires BEFORE the read handlers have delivered the command's last lines, so completing from it
        // closes the output file mid sentence. BeginWaiting's thread has neither problem: it is not the
        // pool's to schedule, and it waits for both streams to finish before completing.
        //
        // Pid IS STILL READ FIRST, because Process.Id throws once the object has been disposed and
        // completion disposes it. Reading it here means this object can always answer which process it
        // was, including after the child is gone.

        // AND NOT COMPLETED HERE, EVEN FOR A CHILD THAT HAS ALREADY EXITED. Completing disposes the
        // Process, and the caller has not yet called BeginOutputReadLine on it — doing so would throw
        // "StandardError has not been redirected" out of DetachAsync, whose contract is to hand back a
        // running command. A command short enough to finish before this constructor runs still has to
        // be noticed, since EnableRaisingEvents does not replay Exited; BeginWaiting is what notices
        // it, once reading has been established.
    }

    /// <summary>
    /// Starts the thread that notices the child's exit. Called by
    /// <see cref="ProcessRunner.DetachAsync"/> once reading has begun, and exactly once.
    ///
    /// <para>SEPARATE FROM THE CONSTRUCTOR BECAUSE COMPLETION DISPOSES THE PROCESS. A `exit 9` is gone
    /// before either returns, so a waiter started in the constructor disposes the
    /// <see cref="Process"/> while <c>DetachAsync</c> is still calling
    /// <c>BeginOutputReadLine</c>/<c>BeginErrorReadLine</c> on it — which throws, out of a method whose
    /// contract is to hand back a running command. Reading must be established first; the exit has
    /// nowhere to go until it is.</para>
    ///
    /// <para>AND THE ORDER COSTS NOTHING, because no exit can be missed by waiting: the child is
    /// already dead or it is not, and <see cref="WaitForTheChild"/> asks the OS either way.</para>
    /// </summary>
    internal void BeginWaiting()
    {
        // READING IS ESTABLISHED BY THE TIME THIS IS CALLED, so the handle may now be released — and
        // must be here if an exit already tried and was deferred, or the Process leaks.
        bool owed;
        lock (_gate)
        {
            _readingBegun = true;
            owed = _releasePending;
        }
        if (owed) ReleaseTheHandle();

        // A THREAD OF OUR OWN, WHICH IS THE ONLY ROUTE THAT CANNOT BE STARVED.
        // Process.Exited above is raised from a THREAD-POOL WORK ITEM, so it is not a notification so
        // much as a request to be notified when the pool gets round to it — and the exit report is the
        // one thing here with a deadline. Measured: with the worker pool saturated, the event for a
        // child that had already died did not arrive within four seconds; in the suite, the agent was
        // never told its background command finished at all.
        //
        // THE STARVATION IS NOT A PATHOLOGICAL CASE, IT IS THE NORMAL ONE. Backgrounding is what a
        // model reaches for when the machine is busy, and the pool is busiest exactly then. Worse, a
        // FAILING command is the likeliest to be lost: `exit 9`, a rejected argument, a failed
        // authentication all return in microseconds, so they depend entirely on the notification
        // rather than on anyone still watching — the reports that go missing are the ones carrying
        // bad news.
        //
        // A DEDICATED THREAD RATHER THAN Task.Run OR WaitForExitAsync, both of which are the pool
        // again. One blocked thread per detached command is affordable precisely because
        // DetachedProcessRegistry.MaxConcurrent bounds them at sixteen; IsBackground so a thread
        // still waiting on a long command never keeps the app from exiting, since shutdown reaps the
        // children anyway.
        var waiter = new Thread(WaitForTheChild)
        {
            IsBackground = true,
            Name = $"detached-wait-{Pid}",
        };
        waiter.Start();
    }

    /// <summary>
    /// Blocks on the child until it exits, then completes — the starvation-proof half of the pair that
    /// notices an exit.
    ///
    /// <para>RACES <c>Process.Exited</c> ON PURPOSE, AND EITHER MAY WIN. <see cref="Complete"/> is
    /// idempotent under its lock, so the loser is a no-op; what matters is that this one's timing
    /// depends on nothing but the OS. Keeping the event as well costs nothing and still wins on an
    /// idle machine, where it fires first.</para>
    ///
    /// <para>A FINITE TIMEOUT IN A LOOP, NEVER THE PARAMETERLESS <c>WaitForExit()</c>. That overload
    /// also waits for the redirected output readers to reach end-of-stream — and those readers are
    /// thread-pool work items, so it starves in exactly the case this thread exists to survive.
    /// Measured with the pool saturated: the parameterless form had not returned after six seconds for
    /// a child that was already dead, while the finite form below answered in ten milliseconds with
    /// the right exit code. The finite overloads wait on the process handle alone, which is the only
    /// thing being asked about here. <c>Timeout.Infinite</c> is not an option either — it is routed to
    /// the same drain.</para>
    ///
    /// <para>THE LOOP IS THEREFORE NOT A POLL OF THE CHILD'S STATE: each call blocks on the handle for
    /// the full interval and returns early the moment the child dies, so a long command costs one
    /// wakeup a second rather than a spin. Flushing the output file is <see cref="Complete"/>'s job and
    /// happens under the lock the writers take, so nothing here depends on the readers having drained.</para>
    ///
    /// <para>SWALLOWS EVERYTHING, INCLUDING A DISPOSED HANDLE. The winning path disposes the
    /// <see cref="Process"/> inside <see cref="Complete"/>, so this thread can find the object gone
    /// mid-wait — and it is then asking about a child whose exit has already been reported. An
    /// exception escaping a thread with no caller to catch it would take the process down, which is a
    /// crash caused by a background command having finished.</para>
    /// </summary>
    private void WaitForTheChild()
    {
        try
        {
            while (!_process.WaitForExit(1_000))
                if (Finished) return;   // Kill or the event got there first; nothing left to wait for.
        }
        catch (Exception) { /* handle already released by whoever completed first */ }

        // THEN LET THE OUTPUT CATCH UP, BRIEFLY. The child's death reaches this thread before the read
        // handlers have delivered its last lines, so completing immediately closes the output file mid
        // sentence — see _drained. BOUNDED, because the handlers run on the pool and a starved pool is
        // the case this thread exists for: waiting for them without a limit would reintroduce exactly
        // the hang being fixed. Two seconds is far longer than a drain takes and still finite, and a
        // timeout costs a truncated tail rather than a lost report.
        try { _drained.Wait(2_000); }
        catch (Exception) { /* disposed by a completion that got here first */ }

        Complete();
    }

    /// <summary>
    /// Records that one redirected stream has reached end-of-stream. Called by
    /// <see cref="ProcessRunner.DetachAsync"/>'s read handlers, once each.
    ///
    /// <para>THE ONLY RELIABLE SIGNAL THAT OUTPUT IS COMPLETE. A null <c>Data</c> on the handler is
    /// how the framework says the stream is finished; without counting them, the waiter thread has no
    /// way to tell "nothing printed yet" from "nothing more will be printed" and closes the file on a
    /// guess.</para>
    /// </summary>
    internal void StreamFinished()
    {
        try { if (!_drained.IsSet) _drained.Signal(); }
        catch (Exception) { /* already at zero, or disposed: either way the drain is done. */ }
    }

    /// <summary>
    /// Stops the child and everything it spawned. Safe to call twice, and safe after it has exited on
    /// its own — a caller cancelling a background command has no way to know which.
    /// </summary>
    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone, or never ours to signal: either way it is not running. */ }

        // WAITED FOR, BRIEFLY. Kill only requests the signal, so a caller that killed and then asked
        // whether the pid exists would race the kernel — which is exactly what a test does, and what
        // a reap at shutdown does before the process exits underneath it.
        try { _process.WaitForExit(5_000); }
        catch (Exception) { /* best effort: the report below does not depend on it */ }

        Complete();
    }

    /// <summary>Flushes the output file, raises <see cref="Exited"/> once, and releases the process
    /// handle unless reading has yet to begin — see <see cref="ReleaseTheHandle"/>. Reached from the
    /// exit event, from the waiter thread, from <see cref="Kill"/> and from <see cref="Dispose"/>.</summary>
    private void Complete()
    {
        int code;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;

            // FLUSHED BEFORE THE EVENT, so a subscriber that immediately reads OutputPath finds the
            // command's last line there. The handlers are quiet by now for a natural exit, but "by
            // now" is a timing argument, so this runs under the same lock the handlers take.
            try { _writer?.Dispose(); }
            catch (Exception) { /* diagnostic file; a failed close does not change the exit code. */ }

            try { code = _process.ExitCode; }
            catch (Exception) { code = -1; }

            // KEPT, because the handle is disposed below and the event that carries this code is
            // raised only once — see ExitCode.
            _exitCode = code;
        }

        // OUTSIDE THE LOCK: a subscriber is free to call Kill or Dispose from its handler, and both
        // take this lock.
        Exited?.Invoke(code);

        ReleaseTheHandle();
    }

    /// <summary>
    /// Disposes the <see cref="Process"/>, but never before <see cref="ProcessRunner.DetachAsync"/> has
    /// finished starting the readers.
    ///
    /// <para>DISPOSING TOO EARLY THROWS OUT OF <c>DetachAsync</c>. A command like `exit 9` can complete
    /// between that method's <c>BeginOutputReadLine</c> and <c>BeginErrorReadLine</c> calls, and the
    /// second then fails with "StandardError has not been redirected" — the object it is called on was
    /// released underneath it. So completion is free to happen whenever the child dies, and only the
    /// disposal waits: <see cref="BeginWaiting"/> marks the point after which it is safe, and whichever
    /// side arrives second does it.</para>
    ///
    /// <para>THE HANDLE IS ALWAYS RELEASED, by one side or the other. If the exit wins, this is a no-op
    /// and <see cref="BeginWaiting"/> disposes; if reading was established first, the completion path
    /// disposes as it always did. What is never left behind is an undisposed Process, which would leak
    /// a file descriptor per background command.</para>
    /// </summary>
    private void ReleaseTheHandle()
    {
        lock (_gate)
        {
            if (!_readingBegun) { _releasePending = true; return; }
        }

        try { _process.Dispose(); }
        catch (Exception) { /* handle already released */ }
    }

    /// <summary>Kills the child rather than abandoning it — a DetachedProcess going out of scope
    /// with its command still running is the orphan this whole type exists to prevent.</summary>
    public void Dispose() => Kill();

    /// <summary>Whether the exit has already been reported, for a caller that subscribed too late to
    /// have heard it — see <see cref="DetachedProcessRegistry.Add"/>.</summary>
    public bool Finished { get { lock (_gate) return _finished; } }

    /// <summary>
    /// How the child ended, once <see cref="Finished"/> is true; 0 before that, which is meaningless.
    ///
    /// <para>STORED RATHER THAN READ BACK FROM THE PROCESS, because <see cref="Complete"/> disposes
    /// the handle the moment it has the code — <c>Process.ExitCode</c> throws afterwards. Exists for
    /// the caller that subscribes too late to hear <see cref="Exited"/>: the event is raised once and
    /// never replayed, so a command short enough to finish before its subscriber exists — a failing
    /// command, typically, since those fail fastest — would otherwise be reported by nothing. The
    /// <see cref="Finished"/>-then-read pair is how such a caller covers that gap.</para>
    /// </summary>
    public int ExitCode { get { lock (_gate) return _exitCode; } }

    /// <summary>
    /// Appends one output line to the file, if there is one.
    ///
    /// <para>WRITING GOES THROUGH HERE RATHER THAN THE RUNNER HOLDING THE WRITER, so the lock that
    /// serialises the writes is the same lock <see cref="Complete"/> disposes it under. Two locks —
    /// the runner's for writing, this one for closing — is a write to a disposed StreamWriter on the
    /// line a command exits, which is every line for a command that exits promptly.</para>
    ///
    /// <para>Both streams land in ONE file, interleaved in arrival order, because that is how the
    /// user saw them in a terminal and a background command's output is read as a narrative. A
    /// split pair would also double the files a job directory accumulates for no question anyone
    /// asks of it.</para>
    /// </summary>
    internal void Write(string line)
    {
        lock (_gate)
        {
            // SILENT AFTER COMPLETION: a handler for a line still in flight when the child exits
            // arrives after the writer is gone, and a background command must not crash the thread
            // pool over its last line of output.
            if (_finished || _writer is null) return;
            try { _writer.Write(line); _writer.Write('\n'); }
            catch (Exception) { /* diagnostic file: a full disk does not stop the command. */ }
        }
    }
}

/// <summary>
/// Every detached process this app started, and the one thing that kills them all.
///
/// <para>WHY IT EXISTS AT ALL: a detached child has no plugin, so
/// <c>IPluginContext.RegisterChildProcess</c> — which needs a plugin runtime and a plugin name —
/// cannot see it, and <c>ChildProcessStore</c> therefore never reaps it. Without this, backgrounding
/// a command means a process that outlives the app with nothing left that knows its pid.</para>
///
/// <para>AN INSTANCE WITH A PROCESS-WIDE <see cref="Default"/>, not a static bag. A static bag is
/// reachable from <see cref="ProcessRunner"/> — which is static and has no session to hang anything
/// off — and it is also shared by every test in a parallel suite, so one test's <c>ReapAll</c> kills
/// another's process and a leaked entry from one test makes the next one's count wrong. Splitting the
/// two gives the production path one registry and each test its own.</para>
/// </summary>
public sealed class DetachedProcessRegistry
{
    /// <summary>
    /// The one every detached command lands in unless a caller names another.
    ///
    /// <para>PROCESS-WIDE BECAUSE THE THING IT BOUNDS IS. A registry per session would leave a
    /// command backgrounded in a session that is later closed with nobody holding its pid, and the
    /// OS's process table is not per-session either. Shutdown reaps this one — see
    /// <c>SessionManager.Dispose</c>.</para>
    /// </summary>
    public static DetachedProcessRegistry Default { get; } = new();

    private readonly object _gate = new();
    private readonly List<DetachedProcess> _live = [];

    /// <summary>
    /// How many detached processes may run at once, after which <see cref="Add"/> refuses.
    ///
    /// <para>A BOUND EXISTS BECAUSE NOTHING ELSE BOUNDS IT. A model that backgrounds a command per
    /// turn accumulates one process per turn for the life of the session, and a hundred of them is a
    /// machine the user cannot use rather than a tool being used badly. Sixteen is well past any
    /// plausible number of simultaneous long jobs and well short of that.</para>
    /// </summary>
    public const int MaxConcurrent = 16;

    /// <summary>The ones still running, for a caller that lists background work.</summary>
    public IReadOnlyList<DetachedProcess> Live
    {
        get { lock (_gate) return [.. _live]; }
    }

    /// <summary>
    /// Records a detached process and drops it again when it exits, so the list is the LIVE set
    /// rather than a log. Returns false when <see cref="MaxConcurrent"/> is already reached, and the
    /// caller must then kill what it was about to hand over.
    /// </summary>
    public bool Add(DetachedProcess detached)
    {
        lock (_gate)
        {
            if (_live.Count >= MaxConcurrent) return false;
            _live.Add(detached);
        }

        // SELF-REMOVING, so a long session's registry does not grow one dead entry per command and
        // so the MaxConcurrent bound counts what is running rather than what ever ran. Subscribing
        // after the Add above means an exit racing this line still finds itself in the list.
        detached.Exited += _ => { lock (_gate) _live.Remove(detached); };

        // AND AGAIN BY HAND for the process that exited while it was being registered: the
        // constructor may already have raised Exited before this subscription existed.
        if (detached.Finished) { lock (_gate) _live.Remove(detached); }

        return true;
    }

    /// <summary>
    /// Kills every detached process still running.
    ///
    /// <para>THE ONLY REAPING THESE GET. Called at shutdown; safe to call more than once, and safe
    /// when nothing is running.</para>
    /// </summary>
    public void ReapAll()
    {
        DetachedProcess[] doomed;
        lock (_gate) doomed = [.. _live];

        // ITERATED OVER A COPY, because each Kill raises Exited, which removes the entry under the
        // same lock — mutating the list being walked.
        foreach (var process in doomed) process.Kill();

        lock (_gate) _live.Clear();
    }
}
