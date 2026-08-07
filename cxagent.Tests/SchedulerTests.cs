using System.Collections.Concurrent;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using Xunit;

namespace CxAgent.Tests;

public class SchedulerTests
{
    private static Job J(string id, params string[] deps) => new()
    {
        Id = id,
        GoalId = "g",
        PluginType = "shell",
        DisplayName = id,
        DependsOn = new List<string>(deps)
    };

    private static JobResult Ok() => new() { Success = true, ExitCode = 0 };
    private static JobResult Fail() => new() { Success = false, ExitCode = 1, ErrorMessage = "boom" };

    [Fact]
    public async Task RunsLinearChainToCompletion_InDependencyOrder()
    {
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a")); dag.AddJob(J("c", "b"));
        var ran = new List<string>();

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            lock (ran) ran.Add(job.Id);
            return Task.FromResult(Ok());
        });

        await sched.StartAsync();

        Assert.Equal(new[] { "a", "b", "c" }, ran);
        Assert.Equal(GoalState.Completed, sched.FinalGoalState);
        Assert.All(dag.AllJobs, j => Assert.Equal(JobState.Succeeded, j.State));
    }

    [Fact]
    public async Task NeverExceedsMaxParallel()
    {
        var dag = new JobDag();
        for (int i = 0; i < 10; i++) dag.AddJob(J($"j{i}")); // all independent
        int concurrent = 0, peak = 0;
        var gate = new object();

        var sched = new DagScheduler(dag, maxParallel: 3, runJob: async (job, ct) =>
        {
            lock (gate) { concurrent++; peak = Math.Max(peak, concurrent); }
            await Task.Delay(20, ct);
            lock (gate) { concurrent--; }
            return Ok();
        });

        await sched.StartAsync();
        Assert.True(peak <= 3, $"peak concurrency {peak} exceeded maxParallel 3");
        Assert.Equal(GoalState.Completed, sched.FinalGoalState);
    }

    [Fact]
    public async Task FailedJob_SkipsDependents_AndGoalFails()
    {
        // Was FailedJob_LeavesDependentsPending. The INTENT is unchanged and still asserted -- the
        // dependent must not run -- but Pending was the wrong resting state for it. Pending is not
        // terminal, so the UI drew a spinner beside a goal that had already declared itself Failed,
        // and EvaluateTerminal's "quiescent with no path forward" was exactly the misreading that
        // let a live fan-out strand two jobs behind one missing file. Skipped says "never got the
        // chance" without claiming the job is still working.
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a"));
        var ranB = false;

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            if (job.Id == "b") ranB = true;
            return Task.FromResult(job.Id == "a" ? Fail() : Ok());
        });

        await sched.StartAsync();

        Assert.Equal(JobState.Failed, dag.TryGet("a")!.State);
        Assert.False(ranB);                                        // the intent, asserted directly
        Assert.Equal(JobState.Skipped, dag.TryGet("b")!.State);
        Assert.Equal(GoalState.Failed, sched.FinalGoalState);
    }

    [Fact]
    public async Task SkippedJob_PropagatesLikeSuccess_WithSyntheticResult()
    {
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a"));
        var ranB = false;

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            if (job.Id == "b") ranB = true;
            return Task.FromResult(Ok());
        });

        // Skip 'a' before starting; 'b' must still run.
        dag.TryGet("a")!.State = JobState.Skipped;
        dag.TryGet("a")!.Result = new JobResult { Success = true }; // synthetic empty result

        await sched.StartAsync();

        Assert.True(ranB, "dependent of a Skipped job must run");
        Assert.Equal(GoalState.Completed, sched.FinalGoalState);
    }

    [Fact]
    public async Task RetryReenteringThroughQueued_IncrementsRetryCount_AndCanSucceed()
    {
        var dag = new JobDag();
        var a = J("a") with { MaxRetries = 1 };
        dag.AddJob(a);
        int attempts = 0;

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1 ? Fail() : Ok());
        });

        // First run fails; orchestrator/test drives a retry.
        await sched.StartAsync();
        Assert.Equal(JobState.Failed, a.State);

        bool queued = await sched.RetryAsync("a");   // Failed -> Queued (RetryCount++), re-run succeeds

        Assert.True(queued);
        Assert.Equal(JobState.Succeeded, a.State);
        Assert.Equal(1, a.RetryCount);
        Assert.Equal(GoalState.Completed, sched.FinalGoalState);
    }

    /// <summary>
    /// Task 11 review C1: RetryAsync's own RetryCount &gt;= MaxRetries guard silently no-ops even when
    /// the caller is a user-requested (F6) retry, which the brief requires to work on ANY failed job
    /// regardless of RetryCount. Without a bypass, F6 "succeeds" (DagModifier.TryApply mutates the
    /// live dag) and then the retry silently does nothing, leaving the dag edited but unexecuted.
    /// `force: true` bypasses the guard; the bool return means "did this actually queue" is never
    /// left for the caller to guess at.
    /// </summary>
    [Fact]
    public async Task RetryAsync_Exhausted_DoesNothing_UnlessForced()
    {
        var dag = new JobDag();
        var a = J("a") with { MaxRetries = 0 };   // already exhausted: RetryCount(0) >= MaxRetries(0)
        dag.AddJob(a);
        int attempts = 0;

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            attempts++;
            return Task.FromResult(Fail());
        });

        await sched.StartAsync();
        Assert.Equal(JobState.Failed, a.State);
        Assert.Equal(1, attempts);

        bool queuedUnforced = await sched.RetryAsync("a");
        Assert.False(queuedUnforced, "an exhausted job must not silently retry without force");
        Assert.Equal(1, attempts);   // no re-run happened

        bool queuedForced = await sched.RetryAsync("a", force: true);
        Assert.True(queuedForced, "force: true must bypass the RetryCount >= MaxRetries guard");
        Assert.Equal(2, attempts);   // the forced retry actually ran
        Assert.Equal(JobState.Failed, a.State);   // still fails (Fail() always), but it DID run
    }

    /// <summary>
    /// Task 11 review C2: DriveAsync's "no overlapping drives" contract throws if Retry/Skip is
    /// called while a previous drive (StartAsync, or another Retry/Skip) has jobs still in flight or
    /// Queued. A caller must be able to check this instead of firing blind and risking an unobserved
    /// faulted task.
    /// </summary>
    [Fact]
    public async Task RetryAsync_WhileAnotherDriveInFlight_Throws()
    {
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b"));
        var gate = new TaskCompletionSource();

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: async (job, ct) =>
        {
            if (job.Id == "a") await gate.Task;   // 'a' blocks until released
            return Ok();
        });

        var startTask = sched.StartAsync();   // 'b' finishes; 'a' is still in flight (blocked on gate)
        await Task.Delay(20);                 // let 'b' complete and 'a' start

        await Assert.ThrowsAsync<InvalidOperationException>(() => sched.RetryAsync("b"));

        gate.SetResult();
        await startTask;
    }

    /// <summary>
    /// WaitForQuiescenceAsync is the tool a caller uses to avoid the overlapping-drive exception above:
    /// it must not return until the in-flight drive (StartAsync here) has actually completed.
    /// </summary>
    [Fact]
    public async Task WaitForQuiescenceAsync_WaitsForInFlightDrive()
    {
        var dag = new JobDag();
        dag.AddJob(J("a"));
        var gate = new TaskCompletionSource();
        bool ranAfterWait = false;

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: async (job, ct) =>
        {
            await gate.Task;
            return Ok();
        });

        var startTask = sched.StartAsync();
        await Task.Delay(20);

        var waitTask = sched.WaitForQuiescenceAsync().ContinueWith(_ => ranAfterWait = true);
        Assert.False(ranAfterWait);   // still blocked — the drive hasn't finished

        gate.SetResult();
        await startTask;
        await waitTask;
        Assert.True(ranAfterWait);
    }

    [Fact]
    public async Task WaitForQuiescenceAsync_ReturnsImmediately_WhenAlreadyQuiescent()
    {
        var dag = new JobDag();
        dag.AddJob(J("a"));
        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) => Task.FromResult(Ok()));

        await sched.StartAsync();
        await sched.WaitForQuiescenceAsync();   // must not hang
    }

    [Fact]
    public async Task FanOutFanIn_AllPathsComplete()
    {
        var dag = new JobDag();
        dag.AddJob(J("root"));
        dag.AddJob(J("l", "root")); dag.AddJob(J("r", "root"));
        dag.AddJob(J("join", "l", "r"));
        var ran = new ConcurrentBag<string>();

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            ran.Add(job.Id);
            return Task.FromResult(Ok());
        });

        await sched.StartAsync();

        Assert.Equal(4, ran.Count);
        Assert.Equal(JobState.Succeeded, dag.TryGet("join")!.State);
        Assert.Equal(GoalState.Completed, sched.FinalGoalState);
    }

    [Fact]
    public async Task SkipAsync_MarksJobSkipped_AndRunsDependents()
    {
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a"));
        var ranB = false;

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            if (job.Id == "b") ranB = true;
            return Task.FromResult(Ok());
        });

        // Skip 'a' via the scheduler API (not by pre-setting State). SkipAsync drives 'a'
        // to Skipped, queues and runs 'b', and reaches quiescence — so the DAG is fully
        // complete when SkipAsync returns. StartAsync is a benign no-op here (nothing left
        // to schedule), but calling it verifies the one-drive-at-a-time guard passes on an
        // already-quiescent DAG.
        bool skipped = await sched.SkipAsync("a");

        Assert.True(skipped);
        Assert.Equal(JobState.Skipped, dag.TryGet("a")!.State);
        Assert.True(ranB, "dependent of a SkipAsync'd job must run");
        Assert.Equal(GoalState.Completed, sched.FinalGoalState);
    }

    [Fact]
    public async Task SkipAsync_AlreadySucceeded_ReturnsFalse_AndDoesNothing()
    {
        var dag = new JobDag();
        dag.AddJob(J("a"));
        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) => Task.FromResult(Ok()));

        await sched.StartAsync();
        Assert.Equal(JobState.Succeeded, dag.TryGet("a")!.State);

        bool skipped = await sched.SkipAsync("a");

        Assert.False(skipped, "skipping an already-Succeeded job must be a reported no-op, not silent");
        Assert.Equal(JobState.Succeeded, dag.TryGet("a")!.State);
    }

    [Fact]
    public async Task AFailedJobsDependents_AreSkipped_NotLeftPendingForever()
    {
        // THE LIVE BUG. A four-way fan-out audit: one of the reads hit a missing file and failed,
        // and the compile-report and save-report jobs behind it stayed Pending. EvaluateTerminal
        // counts only in-flight and Queued work, so the DAG looked quiescent and the goal printed
        // "Goal Failed" while the UI still drew those two jobs with a spinner (Pending is not
        // terminal, so it renders exactly like Running). They never ran and never would.
        var dag = new JobDag();
        dag.AddJob(J("read"));
        dag.AddJob(J("compile", "read"));
        dag.AddJob(J("save", "compile"));   // transitive: two hops behind the failure

        var sched = new DagScheduler(dag, maxParallel: 4,
            runJob: (job, ct) => Task.FromResult(job.Id == "read" ? Fail() : Ok()));

        await sched.StartAsync();

        Assert.Equal(JobState.Failed, dag.TryGet("read")!.State);
        Assert.Equal(JobState.Skipped, dag.TryGet("compile")!.State);
        Assert.Equal(JobState.Skipped, dag.TryGet("save")!.State);
        Assert.Equal(GoalState.Failed, sched.FinalGoalState);
    }

    [Fact]
    public async Task ASiblingOfAFailedJob_StillRuns()
    {
        // The fan-out shape: four independent reads, one fails. The other three are not downstream
        // of it and must be untouched -- a cascade that walked the whole DAG rather than the failed
        // job's dependents would silently skip three quarters of the audit.
        var dag = new JobDag();
        dag.AddJob(J("readA")); dag.AddJob(J("readB")); dag.AddJob(J("readC"));
        var ran = new List<string>();

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            lock (ran) ran.Add(job.Id);
            return Task.FromResult(job.Id == "readB" ? Fail() : Ok());
        });

        await sched.StartAsync();

        Assert.Equal(3, ran.Count);
        Assert.Equal(JobState.Succeeded, dag.TryGet("readA")!.State);
        Assert.Equal(JobState.Succeeded, dag.TryGet("readC")!.State);
    }

    [Fact]
    public async Task AJoinJobRUNSWhenOnlyONEOfItsDepsFails()
    {
        // Diamond: the join depends on a failing and a succeeding branch. It used to be skipped --
        // DepsMet requires ALL deps Succeeded/Skipped -- which is how one dead branch took out a
        // whole deliverable. It now RUNS on the input it has, with a spoken placeholder for the
        // branch that died. The breadth-first walk must still not transition it twice when reached
        // from two directions.
        var dag = new JobDag();
        dag.AddJob(J("root"));
        dag.AddJob(J("left", "root")); dag.AddJob(J("right", "root"));
        dag.AddJob(J("join", "left", "right"));
        var ran = new List<string>();

        var sched = new DagScheduler(dag, maxParallel: 4, runJob: (job, ct) =>
        {
            lock (ran) ran.Add(job.Id);
            return Task.FromResult(job.Id == "left" ? Fail() : Ok());
        });

        await sched.StartAsync();

        Assert.Equal(JobState.Succeeded, dag.TryGet("right")!.State);
        Assert.Equal(JobState.Succeeded, dag.TryGet("join")!.State);
        Assert.Single(ran.Where(id => id == "join"));      // released once, not twice
        Assert.Equal(GoalState.Failed, sched.FinalGoalState);   // 'left' still failed
    }

    [Fact]
    public async Task TheGoalStillTerminates_WhenEverythingDownstreamIsSkipped()
    {
        // EvaluateTerminal now treats Pending as ACTIVE, which would hang the goal forever if the
        // cascade ever missed a job. This is the guard that it does not.
        var dag = new JobDag();
        dag.AddJob(J("a"));
        for (var i = 0; i < 5; i++) dag.AddJob(J($"d{i}", "a"));

        var sched = new DagScheduler(dag, maxParallel: 2,
            runJob: (job, ct) => Task.FromResult(job.Id == "a" ? Fail() : Ok()));

        await sched.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(GoalState.Failed, sched.FinalGoalState);
        Assert.DoesNotContain(dag.AllJobs, j => j.State == JobState.Pending);
    }

    [Fact]
    public async Task AFanInJobStillRuns_WhenOnlySOMEOfItsInputsFailed()
    {
        // THE LOST DELIVERABLE. A four-file audit: one read failed, so its review was skipped -- and
        // the summarise and write jobs behind it were cascaded away too. Three reviews had SUCCEEDED
        // and AUDIT.md was never written. A partial report beats no report.
        var dag = new JobDag();
        dag.AddJob(J("rA")); dag.AddJob(J("rB")); dag.AddJob(J("rC"));
        dag.AddJob(J("summarise", "rA", "rB", "rC"));
        dag.AddJob(J("write", "summarise"));
        var ran = new List<string>();

        var sched = new DagScheduler(dag, maxParallel: 1, runJob: (job, ct) =>
        {
            lock (ran) ran.Add(job.Id);
            return Task.FromResult(job.Id == "rB" ? Fail() : Ok());
        });

        await sched.StartAsync();

        Assert.Contains("summarise", ran);
        Assert.Contains("write", ran);                                   // the deliverable survives
        Assert.Equal(JobState.Succeeded, dag.TryGet("write")!.State);
    }

    [Fact]
    public async Task AFanInJobIsStillSkipped_WhenEVERYInputFailed()
    {
        // The other side of it. With nothing usable left there is nothing to summarise, and running
        // the job would ask a worker to write a report out of placeholders.
        var dag = new JobDag();
        dag.AddJob(J("rA")); dag.AddJob(J("rB"));
        dag.AddJob(J("summarise", "rA", "rB"));

        var sched = new DagScheduler(dag, maxParallel: 1,
            runJob: (job, ct) => Task.FromResult(job.Id.StartsWith("r") ? Fail() : Ok()));

        await sched.StartAsync();

        Assert.Equal(JobState.Skipped, dag.TryGet("summarise")!.State);
    }

    [Fact]
    public async Task ASkippedJobsOutput_SAYSItWasSkippedAndNamesTheCause()
    {
        // The fan-in job reads {{dep.content}}. An EMPTY output failed substitution outright
        // ("produced no 'content' output"), which is how one dead branch used to take out the whole
        // deliverable. It must read as a gap, and name the job that actually broke -- the skipped
        // job is a messenger, not the cause.
        var dag = new JobDag();
        dag.AddJob(J("root"));
        dag.AddJob(J("dependent", "root"));

        var sched = new DagScheduler(dag, maxParallel: 1,
            runJob: (job, ct) => Task.FromResult(job.Id == "root" ? Fail() : Ok()));

        await sched.StartAsync();

        var content = dag.TryGet("dependent")!.Result!.Output["content"]!.ToString()!;
        Assert.Contains("not available", content);
        Assert.Contains("root", content);
    }

    [Fact]
    public async Task ASingleDependencyChain_StillCascades()
    {
        // The fan-in relaxation is gated on DependsOn.Count > 1. A plain linear chain has exactly
        // one input, so a dead parent still means an unreachable child -- unchanged behaviour.
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a")); dag.AddJob(J("c", "b"));

        var sched = new DagScheduler(dag, maxParallel: 1,
            runJob: (job, ct) => Task.FromResult(job.Id == "a" ? Fail() : Ok()));

        await sched.StartAsync();

        Assert.Equal(JobState.Skipped, dag.TryGet("b")!.State);
        Assert.Equal(JobState.Skipped, dag.TryGet("c")!.State);
    }

    [Fact]
    public async Task CancelJob_StopsARUNNINGJob()
    {
        // `cancel_job_ids` was a promise the scheduler could not keep: the orchestrator was told it
        // could cancel a running job, the implementation routed to SkipAsync -- which refuses a
        // running job outright -- and the shared _cts was never cancelled by anything. So a worker
        // that had gone wrong (an 800-second review, a loop burning tokens) ran to completion no
        // matter what anyone asked.
        var dag = new JobDag();
        dag.AddJob(J("slow"));
        var started = new TaskCompletionSource();

        var sched = new DagScheduler(dag, maxParallel: 1, runJob: async (job, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);    // runs forever unless cancelled
            return Ok();
        });

        var drive = sched.StartAsync();
        await started.Task;

        Assert.True(sched.CancelJob("slow"));
        await drive.WaitAsync(TimeSpan.FromSeconds(10));   // the drive must actually END

        Assert.Equal(JobState.Cancelled, dag.TryGet("slow")!.State);
    }

    [Fact]
    public async Task ACancelledJob_IsCancelled_NotFailed()
    {
        // Nothing is WRONG with a cancelled job -- someone stopped it. Reporting Failed invites the
        // diagnoser to spend a paid round repairing a deliberate decision.
        var dag = new JobDag();
        dag.AddJob(J("slow"));
        var started = new TaskCompletionSource();

        var sched = new DagScheduler(dag, maxParallel: 1, runJob: async (job, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });

        var drive = sched.StartAsync();
        await started.Task;
        sched.CancelJob("slow");
        await drive.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(JobState.Failed, dag.TryGet("slow")!.State);
    }

    [Fact]
    public async Task CancelJob_ReportsFalse_ForAJobThatIsNotRunning()
    {
        // Reported, never silent: a caller that thinks it cancelled something and did not is how a
        // runaway job survives an explicit attempt to stop it.
        var dag = new JobDag();
        dag.AddJob(J("a"));
        var sched = new DagScheduler(dag, maxParallel: 1, runJob: (j, ct) => Task.FromResult(Ok()));

        await sched.StartAsync();

        Assert.False(sched.CancelJob("a"));          // already finished
        Assert.False(sched.CancelJob("nosuchjob"));
    }
}
