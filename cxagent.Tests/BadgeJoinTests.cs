using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE JOIN, not the ends. Task 8's tests proved PermissionGatedPlugin/GatedAgentTool stamp
/// JobResult.DecidedBy, and separately that InlineJobSink renders a badge given a result carrying
/// it — and the feature still shipped broken, because nothing exercised the path BETWEEN them: a
/// tool call driven through the real Agent, with a gate that hands back an auto-allow verdict.
///
/// <para>THE BUG THIS WOULD HAVE CAUGHT: Agent.InvokeAndShowAsync rebuilds job.Result from the
/// STRING WorkerToolset.InvokeAsync returns, discarding the JobResult the gate wrapper stamped
/// DecidedBy onto — see AgentToolset's "why a side channel" comment and IJobContext.DecidedBy.
/// Reported live: `du -sh . 2>&amp;1 | tail -1` was recorded auto-allowed in the DB and /stats, and
/// the row rendered plain "done", no badge.</para>
///
/// <para>Asserting on the JOB the sink would read — job.Result.DecidedBy via BufferedJobPanel —
/// rather than on the gate's own return value, because the object the sink actually reads is
/// exactly the thing that was severed.</para>
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

        var shellJob = Assert.Single(jobs.Jobs, j => j.PluginType == "shell");

        // WHAT THE SINK ACTUALLY READS (InlineJobSink.cs: job.Result?.DecidedBy == "auto") — not
        // the gate's own outcome, which was never in question. The bug lived entirely in whether
        // this survived from the gate to here.
        Assert.Equal("auto", shellJob.Result?.DecidedBy);
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

        var shellJob = Assert.Single(jobs.Jobs, j => j.PluginType == "shell");
        Assert.Null(shellJob.Result?.DecidedBy);
    }
}
