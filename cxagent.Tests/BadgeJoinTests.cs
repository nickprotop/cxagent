using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE JOIN, not the ends. Task 8's tests proved PermissionGatedExecutor/GatedAgentTool stamp
/// a JobResult's DecidedBy, and separately that InlineJobSink renders a badge given a job carrying
/// it — and the feature still shipped broken, because nothing exercised the path BETWEEN them: a
/// tool call driven through the real Agent, with a gate that hands back an auto-allow verdict.
///
/// <para>THE BUG THIS WOULD HAVE CAUGHT: Agent.InvokeAndShowAsync rebuilds job.Result from the
/// STRING ToolBindings.InvokeAsync returns, discarding the JobResult the gate wrapper stamped
/// DecidedBy onto — see ToolOutcome, which ended that severing.
/// Reported live: `du -sh . 2>&amp;1 | tail -1` was recorded auto-allowed in the DB and /stats, and
/// the row rendered plain "done", no badge.</para>
///
/// <para>Asserting on the JOB the sink would read — job.DecidedBy via a job observer — rather than
/// on the gate's own return value, because what the sink actually reads is exactly the thing that
/// was severed.</para>
///
/// <para>AND NOW ON WHEN, not only whether. The badge used to be read off job.Result, and a
/// JobResult exists ONLY once the call has finished — so the word appeared at the wrong moment: a
/// classifier-approved `dotnet build` showed nothing for the minutes it ran, and an auto-DENIED
/// call, which never executes, had no result to be badged from. The gate decides BEFORE the tool
/// runs; these tests pin the badge to that moment and to its survival onto the finished row.</para>
/// </summary>
public class BadgeJoinTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "badgejoin-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "badgejoin-work-" + Guid.NewGuid().ToString("N"));

    public BadgeJoinTests()
    {
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    /// <summary>A gate that hands back one fixed outcome to every request — the classifier's
    /// AutoAllow, standing in for the real ActionClassifier without needing a second LLM call in
    /// this test.</summary>
    private sealed class FixedOutcomeGate(PermissionOutcome outcome) : IPermissionGate
    {
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct) =>
            Task.FromResult(outcome);
    }

    /// <summary>
    /// A job observer that keeps every mid-flight ToolProgressed report, not just the last state.
    ///
    /// <para>BufferedJobPanel CANNOT ANSWER THIS QUESTION. It stores one Job per id and drops
    /// ToolProgressed entirely, so by the time a test inspects it the call has finished and every
    /// row looks the same whether the badge arrived at the gate or at completion — which is the one
    /// distinction these tests exist to make. The Job is a mutable record the agent stamps in
    /// place, so the SNAPSHOT is taken here, at report time, rather than kept as a reference.</para>
    /// </summary>
    private sealed class ProgressRecordingPanel : IToolObserver
    {
        private readonly object _lock = new();
        private readonly List<Job> _final = [];

        /// <summary>What each header-only report said, in order: the decider, and whether a result
        /// existed yet. A result is the proxy for "has finished" — it is created only at
        /// completion, which is precisely why the badge could not be read from it.</summary>
        public List<(string? DecidedBy, bool HadResult, JobState State)> Progress { get; } = [];

        public IReadOnlyList<Job> Jobs { get { lock (_lock) return [.. _final]; } }

        public void ToolsChanged(IReadOnlyList<Job> jobs) { }

        public void ToolUpdated(Job job)
        {
            lock (_lock)
            {
                _final.RemoveAll(j => j.Id == job.Id);
                _final.Add(job);
            }
        }

        public void ToolProgressed(Job job)
        {
            lock (_lock) Progress.Add((job.DecidedBy, job.Result is not null, job.State));
        }

        public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
        public void ToolOutputAppended(string jobId, string delta) { }
    }

    [Fact]
    public async Task AnAutoApprovedShellCall_CarriesDecidedByThroughToTheJobTheSinkReads()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("run_shell", new { command = "echo hi" }));
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_state),
            Config = ResolvedConfig.ForTesting(provider),

            // THE CLASSIFIER'S VERDICT, standing in for what PermissionDecider produces after a
            // real ActionClassifier call — an Allow with DeniedBy "auto" (PermissionOutcome.AutoAllow).
            BuildGate = _ => new FixedOutcomeGate(PermissionOutcome.AutoAllow),
        });

        var jobs = new BufferedJobPanel();
        var session = manager.Open(_work,
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = jobs,
                Policy = new PermissionPolicy(_work, manager.Rules!, EditMode.Auto),
            });

        await session.SendAndWait("run something");

        var shellJob = Assert.Single(jobs.Jobs, j => j.JobType == "shell");

        // WHAT THE SINK ACTUALLY READS (InlineJobSink.Badge: job.DecidedBy == "auto") — not the
        // gate's own outcome, which was never in question. The bug lived entirely in whether this
        // survived from the gate to here.
        Assert.Equal("auto", shellJob.DecidedBy);
    }

    /// <summary>THE CONTROL: an ordinary allow (no classifier involved, DeniedBy null) must leave
    /// the row unbadged. Without this, a fix that stamped "auto" unconditionally would pass the
    /// test above and badge every shell call — the opposite failure.</summary>
    [Fact]
    public async Task AnOrdinaryAllowedShellCall_CarriesNoDecidedBy()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("run_shell", new { command = "echo hi" }));
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_state),
            Config = ResolvedConfig.ForTesting(provider),
            BuildGate = _ => new FixedOutcomeGate(PermissionOutcome.Allow),
        });

        var jobs = new BufferedJobPanel();
        var session = manager.Open(_work,
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = jobs,
                Policy = new PermissionPolicy(_work, manager.Rules!, EditMode.Auto),
            });

        await session.SendAndWait("run something");

        var shellJob = Assert.Single(jobs.Jobs, j => j.JobType == "shell");
        Assert.Null(shellJob.DecidedBy);
    }

    /// <summary>
    /// THE TIMING, which is the whole requirement: the badge exists while the tool is still
    /// RUNNING, before any result has been built.
    ///
    /// <para>Asserting on a ToolProgressed report whose job carries the decider AND no result yet.
    /// A JobResult is created only at completion, so "decider present, result still null" is
    /// exactly "badged before it finished" — and it is unfakeable by a fix that merely renames the
    /// field it reads at the end.</para>
    ///
    /// <para>ToolProgressed, NOT ToolUpdated, and the test pins that too: ToolUpdated force-expands
    /// a row and blanks its body, which is ruinous for a header-only stamp on a row the user may
    /// have collapsed. A badge delivered down that path would leave Progress empty here.</para>
    /// </summary>
    [Fact]
    public async Task AnAutoApprovedShellCall_IsBadgedWhileItIsStillRunning()
    {
        var jobs = await DriveShellCall(PermissionOutcome.AutoAllow);

        var badgedMidFlight = jobs.Progress.Where(p => p.DecidedBy == "auto").ToList();

        Assert.NotEmpty(badgedMidFlight);

        // NO RESULT YET on at least one of them — the gate reported before the tool ran.
        Assert.Contains(badgedMidFlight, p => !p.HadResult);

        // AND THE ROW WAS STILL LIVE. A terminal state here would mean the report arrived at the
        // end after all, with the badge merely travelling by a different road.
        Assert.Contains(badgedMidFlight, p => p.State is JobState.Running or JobState.Pending
                                                     or JobState.Queued or JobState.WaitingOnPermission);
    }

    /// <summary>IT STAYS. A badge that appeared at the gate and vanished when the row settled would
    /// be worse than one that arrived late — the user would watch it disappear.</summary>
    [Fact]
    public async Task AnAutoApprovedShellCall_KeepsItsBadgeOnTheFinishedRow()
    {
        var jobs = await DriveShellCall(PermissionOutcome.AutoAllow);

        var shellJob = Assert.Single(jobs.Jobs, j => j.JobType == "shell");

        Assert.Equal("auto", shellJob.DecidedBy);

        // Finished, so the badge on it is the one a user reads back later rather than a live tick.
        Assert.NotNull(shellJob.Result);
    }

    /// <summary>
    /// THE CASE A RESULT-BORNE BADGE COULD NEVER SERVE. An auto-DENIED tool never executes, so
    /// there is no run to hang the word on — the refusal IS the event, and it should be visible the
    /// instant the classifier makes it.
    /// </summary>
    [Fact]
    public async Task AnAutoDeniedShellCall_IsBadgedEvenThoughItNeverRan()
    {
        var jobs = await DriveShellCall(PermissionOutcome.ByClassifier("auto review refused this"));

        var shellJob = Assert.Single(jobs.Jobs, j => j.JobType == "shell");

        Assert.Equal("auto", shellJob.DecidedBy);

        // The state is what turns "auto" into the WORD auto-denied — see InlineJobSink.Badge, which
        // reads the decider from the job and the verdict from the state.
        Assert.Equal(JobState.Failed, shellJob.State);
        Assert.Contains("auto-denied", CxAgent.UI.InlineJobSink.CompactHeaderForTest(shellJob));

        // And it was reported at the gate, not merely present at the end.
        Assert.Contains(jobs.Progress, p => p.DecidedBy == "auto" && !p.HadResult);
    }

    /// <summary>THE CONTROL, EXTENDED TO THE LIVE PATH. An ordinary allow must leave the row
    /// unbadged at every moment, not merely at the end — a fix that stamped "auto" on every gate
    /// would pass the tests above and badge every shell call.</summary>
    [Fact]
    public async Task AnOrdinaryAllowedShellCall_IsNeverBadged_AtAnyPoint()
    {
        var jobs = await DriveShellCall(PermissionOutcome.Allow);

        Assert.DoesNotContain(jobs.Progress, p => p.DecidedBy is not null);
        Assert.Single(jobs.Jobs, j => j.JobType == "shell" && j.DecidedBy is null);
    }

    /// <summary>One run_shell call through the real Session/Agent under a fixed gate verdict,
    /// recording every mid-flight report as well as the finished job.</summary>
    private async Task<ProgressRecordingPanel> DriveShellCall(PermissionOutcome outcome)
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall("run_shell", new { command = "echo hi" }));
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_state),
            Config = ResolvedConfig.ForTesting(provider),
            BuildGate = _ => new FixedOutcomeGate(outcome),
        });

        var jobs = new ProgressRecordingPanel();
        var session = manager.Open(_work,
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = jobs,
                Policy = new PermissionPolicy(_work, manager.Rules!, EditMode.Auto),
            });

        await session.SendAndWait("run something");

        return jobs;
    }
}
