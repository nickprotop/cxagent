using CxAgent.Core.Models;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// Drives a JobDag to quiescence, respecting maxParallel. All scheduling decisions
/// (dependency propagation + slot fill) are serialized under _schedulerLock so two
/// jobs completing at once cannot double-start a dependent or double-release a slot.
/// Job *execution* still runs concurrently (runJob is fired, not awaited, inside the lock).
///
/// Each "drive" operation — StartAsync, RetryAsync, SkipAsync — introduces new runnable
/// work and then returns only once the DAG is quiescent again. Quiescence is tracked by
/// _inFlight (running jobs) + the Queued count; a per-operation TaskCompletionSource is
/// completed by the reentry path when both hit zero.
///
/// CONTRACT: drive operations must not overlap. Calling a second drive while the first is
/// still awaiting quiescence (jobs in flight or Queued) will throw InvalidOperationException.
/// </summary>
public class DagScheduler : IDisposable
{
    private readonly JobDag _dag;
    private readonly Func<Job, CancellationToken, Task<JobResult>> _runJob;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _schedulerLock = new(1, 1);
    private readonly HashSet<string> _slotHeld = new();     // jobs currently occupying a slot
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Per-job cancellation, so a SINGLE running job can be stopped without killing the drive.
    ///
    /// <para>Until this existed, `cancel_job_ids` was a promise the scheduler could not keep: the
    /// orchestrator was told it could cancel a running job, and the implementation routed to
    /// SkipAsync, which refuses one outright ("job.State is Succeeded or Running → return false").
    /// The shared _cts was never cancelled by anything. So a worker that had gone wrong — an
    /// 800-second review, a loop burning tokens — ran to completion no matter what anyone asked.</para>
    /// </summary>
    private readonly Dictionary<string, CancellationTokenSource> _jobCts = new();
    private int _inFlight;                                   // running job count

    // Signals quiescence for the currently-running drive operation. Replaced per operation
    // so a later Retry/Skip can await its own quiescence after an earlier op already completed.
    private TaskCompletionSource? _quiescent;

    public event EventHandler<Job>? JobTransitioned;
    public event EventHandler<GoalState>? GoalTerminal;
    public GoalState FinalGoalState { get; private set; } = GoalState.Active;

    public DagScheduler(JobDag dag, int maxParallel, Func<Job, CancellationToken, Task<JobResult>> runJob)
    {
        _dag = dag;
        _runJob = runJob;
        _slots = new SemaphoreSlim(maxParallel, maxParallel);
    }

    /// <summary>Queues initially-ready jobs and returns when the DAG is quiescent (terminal).</summary>
    public Task StartAsync() => DriveAsync(prime: () =>
    {
        foreach (var job in _dag.GetReadyJobs())
            Transition(job, JobState.Queued);
        return true;
    });

    /// <summary>
    /// Retry a Failed job: re-enter through Queued (re-acquires a slot), RetryCount++. Returns
    /// whether the job was actually queued — false (a reported no-op, never a silent one) when the
    /// job isn't Failed, or when it has exhausted RetryCount/MaxRetries and <paramref name="force"/>
    /// is false. <paramref name="force"/> is for a user-requested retry (e.g. F6 "Apply: retry"):
    /// the brief requires manual diagnosis to work on ANY failed job regardless of retry headroom,
    /// which this guard would otherwise silently defeat at the very last step of that flow — see
    /// Task 11 review C1. Automatic retries must NOT pass force: true; only a user's explicit request
    /// should bypass the cap. Returns when the DAG is quiescent again.
    /// </summary>
    public Task<bool> RetryAsync(string jobId, bool force = false) => DriveAsync(prime: () =>
    {
        var job = _dag.TryGet(jobId);
        if (job is null || job.State != JobState.Failed) return false;
        if (job.RetryCount >= job.MaxRetries && !force) return false;
        job.RetryCount++;
        Transition(job, JobState.Queued);
        return true;
    });

    /// <summary>
    /// Stops ONE running job. Returns whether it was actually running and got the signal — false,
    /// reported rather than silent, for an unknown id or a job that is not running.
    ///
    /// <para>NOT a drive: it signals and returns immediately, without waiting for quiescence. The
    /// job's own continuation re-enters the scheduler as it would on any completion, so this can be
    /// called while a drive is in flight — which is the whole point, since the job to cancel is by
    /// definition running inside one.</para>
    /// </summary>
    public bool CancelJob(string jobId)
    {
        var job = _dag.TryGet(jobId);
        if (job is null || job.State != JobState.Running) return false;

        CancellationTokenSource? cts;
        lock (_jobCts) _jobCts.TryGetValue(jobId, out cts);
        if (cts is null) return false;

        // Marked BEFORE cancelling: the continuation reads this to decide Cancelled vs Failed, and
        // it runs as soon as the token trips.
        job.CancelRequested = true;
        cts.Cancel();
        return true;
    }

    /// <summary>Skip a not-yet-succeeded, not-running job: synthetic success + propagate. Returns
    /// whether the job was actually skipped (false — reported, not silent — if it was already
    /// Succeeded/Running or unknown). Returns when the DAG is quiescent again.</summary>
    public Task<bool> SkipAsync(string jobId) => DriveAsync(prime: () =>
    {
        var job = _dag.TryGet(jobId);
        if (job is null || job.State is JobState.Succeeded or JobState.Running) return false;
        job.Result = new JobResult { Success = true }; // synthetic empty result for downstream
        Transition(job, JobState.Skipped);
        QueueNewlyReadyDependents(job);
        return true;
    });

    /// <summary>
    /// Returns a task that completes once the currently in-flight drive (if any) reaches quiescence.
    /// Completes immediately if nothing is in flight or Queued right now. Lets a caller that must not
    /// overlap a drive (Task 11 review C2 — e.g. a diagnosis-triggered retry arriving while the
    /// originating StartAsync drive is still live) wait out of the way instead of racing
    /// DriveAsync's InvalidOperationException guard.
    ///
    /// NOT a lease (review round 2, N3): this is a point-in-time sample — "is a drive active right
    /// now" — not a guarantee that one won't start immediately after it returns. Do not use it as a
    /// signal that it is now SAFE TO DISPOSE this scheduler; a caller elsewhere may still be holding a
    /// live reference (e.g. via GoalRunner.TryGetSession) with every intention of driving a retry
    /// through it later, regardless of whether the scheduler happens to be quiescent at this instant.
    /// (GoalRunner no longer uses this method as a dispose guard for exactly this reason — see its
    /// _allSchedulers field doc comment.)
    /// </summary>
    public async Task WaitForQuiescenceAsync()
    {
        await _schedulerLock.WaitAsync();
        Task? pending;
        try
        {
            pending = (_inFlight > 0 || _dag.GetJobsByState(JobState.Queued).Count > 0) ? _quiescent?.Task : null;
        }
        finally { _schedulerLock.Release(); }
        if (pending is not null) await pending;
    }

    // Shared driver: install a fresh quiescence signal, run the prime step (which adds
    // runnable work) under the lock, fill slots, then await quiescence. If priming added
    // no runnable work and the DAG is already quiescent, EvaluateTerminal completes the
    // signal immediately.
    //
    // One-drive-at-a-time contract: throws if a previous drive is still in progress
    // (i.e. _inFlight > 0 or there are Queued jobs at entry). This turns a silent
    // quiescence-orphan hang into a loud, correct error.
    private async Task<bool> DriveAsync(Func<bool> prime)
    {
        var quiescent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool primed;
        await _schedulerLock.WaitAsync();
        try
        {
            // Guard: a prior drive is still active if there is work in flight or Queued.
            if (_inFlight > 0 || _dag.GetJobsByState(JobState.Queued).Count > 0)
                throw new InvalidOperationException(
                    "A drive operation (Start/Retry/Skip) is already in progress; drive operations must not overlap.");

            _quiescent = quiescent;
            primed = prime();
            await ScheduleReadyAsync();
            EvaluateTerminal();
        }
        finally { _schedulerLock.Release(); }

        await quiescent.Task;
        return primed;
    }

    private void Transition(Job job, JobState next)
    {
        job.State = next;
        if (next == JobState.Running) job.StartedAt = DateTimeOffset.UtcNow;
        if (next is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped)
            job.CompletedAt = DateTimeOffset.UtcNow;
        JobTransitioned?.Invoke(this, job);
    }

    // Runs under _schedulerLock. Fills slots from Queued jobs in ULID order.
    private async Task ScheduleReadyAsync()
    {
        foreach (var job in _dag.GetJobsByState(JobState.Queued).OrderBy(j => j.Id, StringComparer.Ordinal))
        {
            if (!await _slots.WaitAsync(0)) break;   // no slots free

            try
            {
                // Bookkeeping first so the catch can correctly reverse it if Transition throws.
                _slotHeld.Add(job.Id);
                _inFlight++;
                Transition(job, JobState.Running);
                _ = RunAndReenterAsync(job);          // fire-and-forget; completion re-enters the lock
            }
            catch (Exception ex)
            {
                _slots.Release();
                _slotHeld.Remove(job.Id);
                _inFlight--;
                job.Result = new JobResult { Success = false, ErrorMessage = ex.Message };
                Transition(job, JobState.Failed);
            }
        }
    }

    private async Task RunAndReenterAsync(Job job)
    {
        // Linked, not replaced: disposing the scheduler must still stop everything, while
        // CancelJobAsync stops exactly one.
        var jobCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_jobCts) _jobCts[job.Id] = jobCts;

        JobResult result;
        try { result = await _runJob(job, jobCts.Token); }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // Cancelled ON PURPOSE. Not a failure: nothing is wrong with the job, someone stopped
            // it — and reporting it as Failed would invite the diagnoser to "repair" a deliberate
            // decision.
            result = new JobResult { Success = false, ExitCode = -1, ErrorMessage = "cancelled." };
        }
        catch (Exception ex) { result = new JobResult { Success = false, ExitCode = -1, ErrorMessage = ex.Message }; }
        finally
        {
            lock (_jobCts) { _jobCts.Remove(job.Id); }
            jobCts.Dispose();
        }

        await _schedulerLock.WaitAsync();
        try
        {
            job.Result = result;
            ReleaseSlotOnce(job);
            _inFlight--;
            Transition(job, result.Success ? JobState.Succeeded
                         : job.CancelRequested ? JobState.Cancelled
                         : JobState.Failed);

            if (job.State is JobState.Succeeded or JobState.Skipped)
                QueueNewlyReadyDependents(job);
            else
                CascadeUnreachable(job);

            await ScheduleReadyAsync();
            EvaluateTerminal();
        }
        finally { _schedulerLock.Release(); }
    }

    // Under _schedulerLock. Move any Pending dependent whose deps are now all met to Queued.
    private void QueueNewlyReadyDependents(Job job)
    {
        foreach (var dep in _dag.GetDependents(job.Id))
        {
            if (dep.State != JobState.Pending) continue;

            // DepsMet is the normal release. The second clause covers the fan-in job whose LAST
            // dependency is the one finishing now while an EARLIER one failed: DepsMet stays false
            // forever (that dep will never be Succeeded), and the cascade already passed this job
            // over as still-usable. Without this it sits Pending and the goal never terminates.
            var settledWithSomethingUsable =
                dep.DependsOn.Count > 1
                && dep.DependsOn.All(d => _dag.TryGet(d) is { } u && IsSettled(u.State))
                && HasAnyUsableInput(dep);

            if (DepsMet(dep) || settledWithSomethingUsable)
                Transition(dep, JobState.Queued);
        }
    }

    /// <summary>
    /// A job failed (or was cancelled), so everything downstream of it can never run. Mark those
    /// dependents Skipped, transitively.
    ///
    /// <para>Without this they sit in Pending FOREVER. <see cref="EvaluateTerminal"/> counts only
    /// in-flight and Queued work, so a DAG whose remaining jobs are all stranded in Pending looks
    /// quiescent: the goal is declared terminal while jobs that never ran are still on screen. The
    /// UI renders Pending with a spinner, identical to Running, so it reads as "Goal Failed" printed
    /// underneath two jobs still working — which is what a live fan-out showed when one of four file
    /// reads hit a missing file, stranding the compile-report and save-report jobs behind it.</para>
    ///
    /// <para>Skipped, not Failed: these jobs did not fail, they never got the chance. The distinction
    /// is load-bearing — Skipped carries a synthetic success result for downstream, and the failure
    /// count in the goal's closing line should name the ONE job that actually broke, not every job
    /// that was waiting on it.</para>
    ///
    /// <para>Breadth-first over a work list rather than recursion: a diamond dependency would visit
    /// the join node twice, and the Pending guard makes the second visit a no-op instead of a
    /// double transition.</para>
    /// </summary>
    private void CascadeUnreachable(Job job)
    {
        var queue = new Queue<Job>();
        queue.Enqueue(job);

        while (queue.Count > 0)
        {
            foreach (var dep in _dag.GetDependents(queue.Dequeue().Id))
            {
                // Only Pending: a dependent that is Queued or Running was released by some OTHER
                // satisfied dependency and is legitimately live — killing it here would abort work
                // already in flight.
                if (dep.State != JobState.Pending) continue;

                // A FAN-IN job keeps its chance. "Summarise all four reviews" depending on four jobs
                // is not unreachable because one of them died — it has three real inputs and a
                // spoken placeholder for the fourth, which is a partial report instead of no report.
                // A live audit lost AUDIT.md exactly this way: three reviews succeeded, the fourth
                // was skipped, and the summarise and write jobs were cascaded away behind it.
                //
                // Strictly better than relaxing DepsMet, which already accepts Skipped: the job runs
                // at its normal point in the graph, once every dependency has settled one way or the
                // other. A job whose deps are ALL unusable still gets skipped — there is nothing to
                // summarise, and running it would ask a worker to write a report from placeholders.
                if (dep.DependsOn.Count > 1 && HasAnyUsableInput(dep))
                {
                    // SPARING IT IS NOT ENOUGH — it has to become runnable. Left Pending, its failed
                    // dependency never turns Succeeded, so DepsMet never passes and nothing ever
                    // queues it; with Pending counting as active in EvaluateTerminal, the goal then
                    // hangs forever. (Caught by this very scenario's test deadlocking.)
                    //
                    // Queue it once every dependency has SETTLED, so it still runs at its natural
                    // point in the graph rather than racing siblings that are legitimately pending.
                    if (dep.DependsOn.All(d => _dag.TryGet(d) is { } u && IsSettled(u.State)))
                        Transition(dep, JobState.Queued);
                    continue;
                }
                // A SPOKEN result, not an empty one. A fan-in job — "summarise all four reviews",
                // "write the report" — references {{each_dep.content}}, and an empty Output made
                // that fail with "produced no 'content' output", so ONE unreachable dependency took
                // out the whole deliverable. A live audit lost its report exactly this way: three of
                // four reviews succeeded and AUDIT.md was never written.
                //
                // Saying so in `content` lets the fan-in job RUN, and the model reads the gap in its
                // own prompt rather than inferring it from a missing section. Naming the job that
                // actually broke matters: the skipped job is a messenger, not the cause.
                dep.Result = new JobResult
                {
                    Success = true,
                    Output = new Dictionary<string, object?>
                    {
                        ["content"] = $"[not available — this job was skipped because it depended on "
                                    + $"'{job.PlanLocalId ?? job.Id}', which did not succeed]",
                        ["skipped"] = true,
                    },
                };
                Transition(dep, JobState.Skipped);
                queue.Enqueue(dep);
            }
        }
    }

    /// <summary>
    /// Whether any dependency of <paramref name="job"/> can still deliver real content — either it
    /// already succeeded, or it has not run yet and might.
    ///
    /// <para>A dependency counts as unusable only when it is Failed/Cancelled, or Skipped (which
    /// carries the spoken "not available" placeholder rather than output). Pending and Queued count
    /// as usable: they have not had their chance yet, and cascading a fan-in job away because one
    /// branch died while another was still queued would discard work that was about to succeed.</para>
    /// </summary>
    /// <summary>A job that will never change state again — so a dependent waiting on it has its
    /// final answer, whether that answer is output or a placeholder.</summary>
    private static bool IsSettled(JobState state) =>
        state is JobState.Succeeded or JobState.Skipped or JobState.Failed or JobState.Cancelled;

    private bool HasAnyUsableInput(Job job) =>
        job.DependsOn.Any(d => _dag.TryGet(d) is { State: not (JobState.Failed or JobState.Cancelled
                                                              or JobState.Skipped) });

    private bool DepsMet(Job job) =>
        job.DependsOn.All(d => _dag.TryGet(d) is { State: JobState.Succeeded or JobState.Skipped });

    // Idempotent per job: a slot is released exactly once on a slot-vacating transition.
    private void ReleaseSlotOnce(Job job)
    {
        if (_slotHeld.Remove(job.Id))
            _slots.Release();
    }

    /// <summary>
    /// Whether a Pending job could ever be released. False when any dependency is missing from the
    /// dag entirely — nothing will ever transition a job that is not there, so waiting on it is
    /// waiting forever.
    ///
    /// <para>A dependency that EXISTS but has not finished is still runnable: it will settle, and
    /// the cascade or the normal release path will move this job then.</para>
    /// </summary>
    private bool CanStillRun(Job job) =>
        job.DependsOn.All(d => _dag.TryGet(d) is not null);

    // Called under _schedulerLock. Quiescent when nothing is Running or Queued. Goal is
    // Completed if every job is Succeeded/Skipped, else Failed. Completes the current
    // drive operation's signal exactly once.
    private void EvaluateTerminal()
    {
        // Pending counts as ACTIVE. CascadeUnreachable should have drained it — anything still
        // Pending here is reachable work that no path has released, and calling the goal terminal
        // on top of it is what stranded two jobs behind a failed read on a live fan-out. Treating it
        // as active turns that silent mis-report into a visibly stuck goal, which is a far easier
        // failure to notice and diagnose than a wrong "Goal Failed".
        // ...but only Pending work that can still MOVE. A job depending on an id that is not in the
        // dag at all will never be released by anything, so counting it as active hangs the goal
        // forever instead of ending it — the drive never returns, which is worse than the silent
        // mis-report this check was added to prevent. Caught by a test that used exactly that shape.
        bool anyActive = _inFlight > 0
                         || _dag.GetJobsByState(JobState.Queued).Count > 0
                         || _dag.GetJobsByState(JobState.Pending).Any(CanStillRun);
        if (anyActive) return;

        bool allDone = _dag.AllJobs.All(j => j.State is JobState.Succeeded or JobState.Skipped);
        FinalGoalState = allDone ? GoalState.Completed : GoalState.Failed;
        GoalTerminal?.Invoke(this, FinalGoalState);
        _quiescent?.TrySetResult();
    }

    public void Dispose()
    {
        _cts.Dispose();
        _schedulerLock.Dispose();
        _slots.Dispose();
    }
}
