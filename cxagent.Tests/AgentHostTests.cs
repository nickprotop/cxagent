using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What AgentHost still owns now that the dag is gone: the ledger, the events the status bar reads,
/// the agent context that outlives a prompt, and turning a provider fault into a visible error rather
/// than an unobserved faulted task. The turn loop itself is <see cref="CxAgent.UI.Agent"/>'s, and is
/// covered by AgentChallengeTests and AgentTests.
/// </summary>
public class AgentHostTests
{
    private static AgentHost NewRunner(ILlmProvider provider, RecordingSink? sink = null) =>
        new(provider, sink ?? new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins());

    [Fact]
    public async Task SendAsync_RecordsTokenUsage_IntoTheLedger()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 30, OutputTokens = 12 } });

        var runner = NewRunner(mock);
        await runner.SendAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        Assert.Equal(42, runner.Ledger.TotalTokens);
    }

    /// <summary>
    /// AppBootstrap's status-bar cost readout has no per-Record event on TokenLedger to subscribe to
    /// (only Breached, which fires once) — so AgentHost raises TokensUpdated itself at the same point
    /// it calls Ledger.Record, giving AppBootstrap a live hook without adding a public event to the
    /// ledger's own object model.
    /// </summary>
    [Fact]
    public async Task SendAsync_RaisesTokensUpdated_MatchingLedgerTotal()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 30, OutputTokens = 12 } });

        var runner = NewRunner(mock);
        var seen = new List<int>();
        runner.TokensUpdated += (_, total) => seen.Add(total);

        await runner.SendAsync("goal", new List<ChatMessage>(), CancellationToken.None);

        Assert.Contains(42, seen);
    }

    [Fact]
    public async Task SendAsync_ProviderThrows_ShowsError_DoesNotLeakVendorBody()
    {
        var sink = new RecordingSink();
        var runner = NewRunner(new ThrowingProvider(), sink);

        var conversation = new List<ChatMessage>();
        await runner.SendAsync("x", conversation, CancellationToken.None);

        // NO ANSWER on the transcript — the request produced an error, not a reply. That absence is
        // what a failed exchange looks like now the status enum nothing consumed is gone.
        Assert.DoesNotContain(conversation, m => m.Role == "assistant");
        Assert.NotNull(sink.Error);
        Assert.Contains("auth failed", sink.Error!);
        Assert.DoesNotContain("secret-vendor-body", sink.Error!);  // VendorBody never surfaced
    }

    /// <summary>
    /// One context across prompts. The runner constructs it and hands it to the agent, which now
    /// outlives every prompt — so prompt N+1 begins with everything prompt N learned rather than
    /// blank. This pins the seam /compress and the session's continuity both depend on.
    /// </summary>
    [Fact]
    public async Task Context_SurvivesAcrossPrompts()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "first", StopReason = "end_turn" });
        mock.EnqueueResponse(new LlmResponse { Text = "second", StopReason = "end_turn" });

        var runner = NewRunner(mock);
        var conversation = new List<ChatMessage>();

        await runner.SendAsync("one", conversation, CancellationToken.None);
        var afterFirst = runner.Context.Messages.Count;
        await runner.SendAsync("two", conversation, CancellationToken.None);

        Assert.True(afterFirst > 0);
        Assert.True(runner.Context.Messages.Count > afterFirst);
    }

    /// <summary>
    /// An unconfigured session is still bounded.
    ///
    /// <para><c>ConfiguredMaxWorkerTurns ?? int.MaxValue</c> meant the COMMON case — no orchestrator
    /// block in config — had no ceiling at all. The reasoning that removed the invented 200 was
    /// sound, but "no arbitrary limit" and "no limit" are different claims and only the first was
    /// argued for.</para>
    /// </summary>
    [Fact]
    public void TurnCeiling_IsBounded_WhenNothingIsConfigured()
    {
        var runner = NewRunner(new MockLlmProvider());

        Assert.Equal(AgentHost.DefaultTurnCeiling, runner.TurnCeiling);
        Assert.True(runner.TurnCeiling < int.MaxValue, "an unconfigured session must still be bounded");
    }

    /// <summary>
    /// A crash is precisely when the exit path does not run, so the save cannot live there. Every
    /// completed turn leaves the session recoverable.
    /// </summary>
    [Fact]
    public async Task SendAsync_PersistsTheContext_AfterEachTurn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-p3-" + Guid.NewGuid().ToString("N"));
        var paths = new CxAgent.Core.Storage.AppPaths(dir);
        paths.EnsureCreated();
        try
        {
            var store = new CxAgent.Core.Storage.SqliteSessionStore(paths);
            var mock = new MockLlmProvider();
            mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

            var runner = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
                PluginRegistry.CreateWithBuiltins(), store: store);

            await runner.SendAsync("remember this", new List<ChatMessage>(), CancellationToken.None);

            var snap = store.LoadLatestUnfinished();
            Assert.NotNull(snap);
            Assert.Equal(runner.SessionId, snap!.AgentId);
            Assert.Contains(snap.Context, m => m.Content.Contains("remember this"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A session that ended properly is not offered for resume. That distinction is the whole point
    /// of the store: an unfinished row means the process never got to say goodbye.
    /// </summary>
    [Fact]
    public async Task MarkSessionFinished_StopsItBeingOfferedForResume()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-p3f-" + Guid.NewGuid().ToString("N"));
        var paths = new CxAgent.Core.Storage.AppPaths(dir);
        paths.EnsureCreated();
        try
        {
            var store = new CxAgent.Core.Storage.SqliteSessionStore(paths);
            var mock = new MockLlmProvider();
            mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

            var runner = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
                PluginRegistry.CreateWithBuiltins(), store: store);
            await runner.SendAsync("hello", new List<ChatMessage>(), CancellationToken.None);

            runner.MarkSessionFinished();

            Assert.Null(store.LoadLatestUnfinished());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Someone who sets a limit meant it — a configured value wins over the backstop, in
    /// either direction.</summary>
    [Fact]
    public void TurnCeiling_HonoursAConfiguredValue()
    {
        var runner = new AgentHost(new MockLlmProvider(), new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins())
        {
            ConfiguredMaxWorkerTurns = 25,
        };

        Assert.Equal(25, runner.TurnCeiling);
    }
}
