using CxAgent.Core.Jobs;

namespace CxAgent.Core.Execution;

/// <summary>A background shell command that has ended, as it ended.</summary>
/// <param name="Job">The command, its owner and its output path.</param>
/// <param name="ExitCode">How it ended.</param>
/// <param name="Killed">Stopped rather than exited.</param>
/// <param name="Ended">When.</param>
public sealed record FinishedShell(BackgroundJob Job, int ExitCode, bool Killed, DateTimeOffset Ended);

/// <summary>
/// Background shell commands this app started, with the handoff each one's exit report goes through, and
/// the ones that have ended.
///
/// <para>WHY THIS EXISTS BESIDE <see cref="DetachedProcessRegistry"/>: the registry forgets a command the
/// moment it exits — right for "what is running", wrong for "what did it do". A parent asking after the
/// exit notice needs the exit code and the file, and a task_output answering "no such pid" for a command
/// that finished a second ago reads as a lost job.</para>
///
/// <para>A HANDOFF PER COMMAND, so its exit report reaches the agent exactly once: as the answer to a
/// task_output waiting on it, or as the exit notice — never both.</para>
///
/// <para>BOUNDED: the newest <see cref="Kept"/> ended commands, oldest dropped with its handoff. A list
/// nobody prunes in a long session is a leak.</para>
/// </summary>
public sealed class FinishedShells
{
    /// <summary>How many ended commands are remembered.</summary>
    public const int Kept = 50;

    /// <summary>The process-wide record, as <see cref="DetachedProcessRegistry.Default"/> is the
    /// process-wide registry.</summary>
    public static FinishedShells Default { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _byPid = [];
    private readonly LinkedList<int> _ended = [];

    private sealed class Entry(BackgroundJob job, ResultHandoff handoff)
    {
        public BackgroundJob Job { get; } = job;
        public ResultHandoff Handoff { get; } = handoff;
        public FinishedShell? Finished { get; set; }
    }

    /// <summary>
    /// Starts tracking a command, and hands back the handoff its exit report goes through.
    ///
    /// <para>A PID ALREADY HERE IS REPLACED: the OS reuses pids, and a new command must not inherit an
    /// old one's exit.</para>
    /// </summary>
    public ResultHandoff Track(BackgroundJob job, Func<string, bool> leaveIfAbandoned)
    {
        var handoff = new ResultHandoff(leaveIfAbandoned);
        lock (_gate)
        {
            if (_byPid.Remove(job.Pid)) _ended.Remove(job.Pid);
            _byPid[job.Pid] = new Entry(job, handoff);
        }
        return handoff;
    }

    /// <summary>The handoff for a tracked command, running or ended; null when nothing tracks the pid.</summary>
    public ResultHandoff? HandoffFor(int pid)
    {
        lock (_gate) return _byPid.GetValueOrDefault(pid)?.Handoff;
    }

    /// <summary>Records how a tracked command ended. A pid nothing tracks is ignored.</summary>
    public void Finished(int pid, int exitCode, bool killed)
    {
        lock (_gate)
        {
            if (!_byPid.TryGetValue(pid, out var entry) || entry.Finished is not null) return;
            entry.Finished = new FinishedShell(entry.Job, exitCode, killed, DateTimeOffset.UtcNow);
            _ended.AddLast(pid);
            while (_ended.Count > Kept)
            {
                _byPid.Remove(_ended.First!.Value);
                _ended.RemoveFirst();
            }
        }
    }

    /// <summary>How a command ended; null while it runs or when nothing tracks the pid.</summary>
    public FinishedShell? Find(int pid)
    {
        lock (_gate) return _byPid.GetValueOrDefault(pid)?.Finished;
    }

    /// <summary>Ended commands whose owner passes <paramref name="owns"/>, newest first.</summary>
    public IReadOnlyList<FinishedShell> Recent(Func<string?, bool> owns)
    {
        lock (_gate)
            return [.. _ended.Reverse().Select(pid => _byPid[pid].Finished!).Where(f => owns(f.Job.AgentId))];
    }
}
