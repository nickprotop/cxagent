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

    /// <summary>The child's process id — the only handle a caller outside this process has on it.</summary>
    public int Pid { get; }

    /// <summary>Where everything the command prints is being written, both streams interleaved as a
    /// terminal would show them, or null when no directory was given to write into.</summary>
    public string? OutputPath { get; }

    /// <summary>
    /// Raised once with the exit code when the child finally exits.
    ///
    /// <para>ONCE, AND ONLY FROM INSIDE THE LOCK THAT SETS <see cref="_finished"/>. Both
    /// <c>Process.Exited</c> and a <see cref="Kill"/> can reach the completion path, and an agent
    /// told twice that a command finished would report it twice.</para>
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

        // SUBSCRIBED AFTER Pid IS READ, because Process.Id throws once the object has been disposed
        // and Complete disposes it. Reading it first means this object can always answer which
        // process it was, including after the child is gone.
        process.Exited += (_, _) => Complete();

        // AND CHECKED ONCE BY HAND: a command short enough to finish before this constructor runs
        // has already raised Exited, and EnableRaisingEvents does not replay it. Without this, `echo
        // hi &` would leave a DetachedProcess nobody ever hears from.
        if (process.HasExited) Complete();
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
    /// handle. Reached from the exit event, from <see cref="Kill"/> and from <see cref="Dispose"/>.</summary>
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
        }

        // OUTSIDE THE LOCK: a subscriber is free to call Kill or Dispose from its handler, and both
        // take this lock.
        Exited?.Invoke(code);

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
