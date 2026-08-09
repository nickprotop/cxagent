using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What GoalRunner still owns now that the dag is gone: the ledger, the events the status bar reads,
/// the agent context that outlives a goal, and turning a provider fault into a visible error rather
/// than an unobserved faulted task. The turn loop itself is SingleAgentLoop's, and is covered by
/// SingleAgentLoopChallengeTests.
/// </summary>
public class GoalRunnerTests
{
    private sealed class NullJobPanel : IJobPanel
    {
        public void SetJobs(IReadOnlyList<Job> jobs) { }
        public void UpdateJob(Job job) { }
        public void UpdateResources(string jobId, ResourceSnapshot snapshot) { }
        public void AppendText(string jobId, string delta) { }
        public void SetDraftMode(bool on) { }
    }

    private static GoalRunner NewRunner(ILlmProvider provider, RecordingSink? sink = null) =>
        new(provider, sink ?? new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins());

    [Fact]
    public async Task RunAsync_RecordsTokenUsage_IntoTheLedger()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 30, OutputTokens = 12 } });

        var runner = NewRunner(mock);
        await runner.RunAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(42, runner.Ledger.TotalTokens);
    }

    /// <summary>
    /// AppBootstrap's status-bar cost readout has no per-Record event on TokenLedger to subscribe to
    /// (only Breached, which fires once) — so GoalRunner raises TokensUpdated itself at the same point
    /// it calls Ledger.Record, giving AppBootstrap a live hook without adding a public event to the
    /// ledger's own object model.
    /// </summary>
    [Fact]
    public async Task RunAsync_RaisesTokensUpdated_MatchingLedgerTotal()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 30, OutputTokens = 12 } });

        var runner = NewRunner(mock);
        var seen = new List<int>();
        runner.TokensUpdated += (_, total) => seen.Add(total);

        await runner.RunAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        Assert.Contains(42, seen);
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_ShowsError_DoesNotLeakVendorBody()
    {
        var sink = new RecordingSink();
        var runner = NewRunner(new ThrowingProvider(), sink);

        var state = await runner.RunAsync("x", new List<ChatMessage>(), CancellationToken.None);

        Assert.NotEqual(GoalState.Completed, state);
        Assert.NotNull(sink.Error);
        Assert.Contains("auth failed", sink.Error!);
        Assert.DoesNotContain("secret-vendor-body", sink.Error!);  // VendorBody never surfaced
    }

    /// <summary>
    /// The context is the RUNNER's, not the loop's: a SingleAgentLoop is built per goal, so a context
    /// owned only by the loop would die with it and goal N+1 would start blank. This pins the seam
    /// /compress and the between-goal continuity both depend on.
    /// </summary>
    [Fact]
    public async Task Context_SurvivesAcrossGoals()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "first", StopReason = "end_turn" });
        mock.EnqueueResponse(new LlmResponse { Text = "second", StopReason = "end_turn" });

        var runner = NewRunner(mock);
        var conversation = new List<ChatMessage>();

        await runner.RunAsync("one", conversation, CancellationToken.None);
        var afterFirst = runner.Context.Messages.Count;
        await runner.RunAsync("two", conversation, CancellationToken.None);

        Assert.True(afterFirst > 0);
        Assert.True(runner.Context.Messages.Count > afterFirst);
    }
}
