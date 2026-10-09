using CxAgent.Core.Execution;

namespace CxAgent.Core.Jobs;

/// <summary>
/// How a background job stands.
///
/// <para>NOT <see cref="Models.JobState"/>, which describes a tool call's lifecycle — pending, queued,
/// succeeded — and shares a name and nothing else with this.</para>
/// </summary>
public enum BackgroundJobState
{
    Running,

    /// <summary>Ended on its own, with a zero or non-zero exit code.</summary>
    Exited,

    /// <summary>Ended because something called Kill: a tab, <c>/jobs kill</c>, <c>job_kill</c>.</summary>
    Killed,
}

/// <summary>One job as of the moment it was read: what it is, how it stands, and for how long.</summary>
/// <param name="Job">The command, its owner, and where its output goes.</param>
/// <param name="State">Running, or how it ended.</param>
/// <param name="ExitCode">Meaningful only once the state is not Running.</param>
/// <param name="Elapsed">Running time so far, or frozen at the exit.</param>
public sealed record JobView(BackgroundJob Job, BackgroundJobState State, int ExitCode, TimeSpan Elapsed);

/// <summary>
/// A described job and its process.
///
/// <para>STATE IS READ FROM THE PROCESS, NOT COPIED. Finished, ExitCode and Killed live on
/// <see cref="DetachedProcess"/>, and a second copy here could disagree with it about whether a job
/// has ended. The one thing the process does not keep is WHEN — so that, alone, is kept here.</para>
///
/// <para>A CLASS, held by reference, so a job tab opened from an entry keeps working after the board
/// has cleared it.</para>
/// </summary>
public sealed class BackgroundJobEntry
{
    private readonly object _gate = new();
    private DateTimeOffset? _ended;

    internal BackgroundJobEntry(BackgroundJob job, DetachedProcess process)
    {
        Job = job;
        Process = process;
    }

    public BackgroundJob Job { get; }
    public DetachedProcess Process { get; }
    public bool Finished => Process.Finished;

    internal DateTimeOffset? Ended { get { lock (_gate) return _ended; } }

    /// <summary>
    /// Records the end, once.
    ///
    /// <para>ALSO CALLED BY <see cref="View"/>, because Finished turns true inside the process's lock
    /// and the Exited handlers run after it: a read landing between the two sees a finished job with
    /// no end time. Latching "now" there keeps the clock from jumping — the difference is the
    /// microseconds between two lines on another thread.</para>
    /// </summary>
    internal void MarkEnded()
    {
        lock (_gate) _ended ??= DateTimeOffset.UtcNow;
    }

    public JobView View()
    {
        var finished = Process.Finished;
        if (finished) MarkEnded();

        var end = finished ? Ended!.Value : DateTimeOffset.UtcNow;
        var state = !finished ? BackgroundJobState.Running
            : Process.Killed ? BackgroundJobState.Killed
            : BackgroundJobState.Exited;
        return new JobView(Job, state, finished ? Process.ExitCode : 0, end - Job.Started);
    }
}

/// <summary>
/// Every background job a shell call described, for whoever shows them.
///
/// <para>WHY NOT <see cref="DetachedProcessRegistry.Live"/>: that is the RUNNING set, and drops a job
/// the moment it exits — which is exactly when a panel has the most to say about it. This keeps a
/// finished job until the session it belongs to has had a chance to see it.</para>
///
/// <para>ONE LOCK, SNAPSHOT READS. Described arrives on a tool's thread, Exited on the process's
/// waiter thread, reads come from the UI thread and clears from whichever thread the user submitted
/// on.</para>
/// </summary>
public sealed class BackgroundJobBoard
{
    /// <summary>
    /// The board over <see cref="DetachedProcessRegistry.Default"/>.
    ///
    /// <para>TOUCHED WHEN THE MAIN WINDOW IS BUILT, before any session opens. A static is created on
    /// first use, and a board created after a job was described has never heard of it.</para>
    /// </summary>
    public static BackgroundJobBoard Default { get; } = new(DetachedProcessRegistry.Default);

    /// <summary>
    /// How many finished entries are kept, oldest dropped first.
    ///
    /// <para>A session's finished jobs normally leave when its user next speaks. This is for the ones
    /// whose user never will — a closed tab's, an idle session's — which would otherwise accumulate
    /// for the life of the process.</para>
    /// </summary>
    public const int MaxFinished = 64;

    private readonly object _gate = new();
    private readonly List<BackgroundJobEntry> _entries = [];

    public BackgroundJobBoard(DetachedProcessRegistry registry) => registry.Described += OnDescribed;

    private void OnDescribed(DetachedProcess process, BackgroundJob job)
    {
        var entry = new BackgroundJobEntry(job, process);
        lock (_gate) _entries.Add(entry);

        // SUBSCRIBE, THEN RE-CHECK — the order Registry.Add uses. Exited is raised once and never
        // replayed, so a job that ended before this line is caught by the check, and one that ends
        // after it by the handler. Both firing is harmless: MarkEnded latches once.
        process.Exited += _ => OnEnded(entry);
        if (process.Finished) OnEnded(entry);
    }

    private void OnEnded(BackgroundJobEntry entry)
    {
        entry.MarkEnded();
        lock (_gate)
        {
            var finished = _entries.Where(e => e.Finished).ToList();
            var excess = finished.Count - MaxFinished;
            if (excess <= 0) return;

            // AN UNLATCHED END SORTS AS NEWEST: it is a job finishing right now, not an old one.
            foreach (var old in finished.OrderBy(e => e.Ended ?? DateTimeOffset.MaxValue).Take(excess))
                _entries.Remove(old);
        }
    }

    /// <summary>The asking session's jobs: running first, then newest first within each group.</summary>
    public IReadOnlyList<BackgroundJobEntry> For(Func<string, bool> owns)
    {
        lock (_gate)
            return [.. _entries.Where(e => owns(e.Job.AgentId))
                               .OrderBy(e => e.Finished ? 1 : 0)
                               .ThenByDescending(e => e.Job.Started)];
    }

    /// <summary>How many jobs are running that the asking session does not own.</summary>
    public int RunningElsewhere(Func<string, bool> owns)
    {
        lock (_gate) return _entries.Count(e => !e.Finished && !owns(e.Job.AgentId));
    }

    /// <summary>Removes the asking session's finished jobs — its user has spoken since.</summary>
    public void ClearFinished(Func<string, bool> owns)
    {
        lock (_gate) _entries.RemoveAll(e => e.Finished && owns(e.Job.AgentId));
    }
}
