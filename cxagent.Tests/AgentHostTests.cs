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

    /// <summary>
    /// Resuming REHYDRATES the agent rather than replaying it. The restored context is what the model
    /// sees on the next turn, so the session continues mid-thought instead of re-reading what it
    /// already read — which is the entire point of having saved it.
    /// </summary>
    [Fact]
    public void Resume_RehydratesTheContextAndLedger()
    {
        var snapshot = new CxAgent.Core.Storage.SessionSnapshot(
            "old-agent",
            [
                new ChatMessage { Role = "system", Content = "Your working directory is /tmp." },
                new ChatMessage { Role = "user", Content = "read Foo.cs" },
                new ChatMessage { Role = "assistant", Content = "it defines Foo" },
            ],
            InputTokens: 5_397, OutputTokens: 435,
            UpdatedAt: DateTimeOffset.UtcNow);

        var runner = new AgentHost(new MockLlmProvider(), new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(), resume: snapshot);

        Assert.Equal(3, runner.Context.Messages.Count);
        Assert.Equal("it defines Foo", runner.Context.Messages[2].Content);

        // The spend comes back too, so a resumed session reports what it has already cost rather
        // than restarting the count at zero.
        Assert.Equal(5_397 + 435, runner.Ledger.TotalTokens);
        Assert.Equal(5_397, runner.Ledger.InputTokens);
        Assert.Equal(435, runner.Ledger.OutputTokens);
    }

    /// <summary>
    /// A restored ledger must not fire Breached. The budget was already crossed in the previous
    /// process and the user was already told; re-announcing it on resume reports as new an error
    /// that is neither new nor actionable.
    /// </summary>
    [Fact]
    public void Resume_WithSpendOverBudget_DoesNotReRaiseTheBreach()
    {
        var sink = new RecordingSink();
        var snapshot = new CxAgent.Core.Storage.SessionSnapshot(
            "old-agent", [new ChatMessage { Role = "user", Content = "hello" }],
            InputTokens: 900_000, OutputTokens: 100_000, UpdatedAt: DateTimeOffset.UtcNow);

        _ = new AgentHost(new MockLlmProvider(), sink, new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(),
            orchestrator: new OrchestratorSettings(MaxTokensPerCall: null, GoalTokenBudget: 1_000),
            resume: snapshot);

        Assert.Empty(sink.Errors);
    }

    /// <summary>
    /// ZERO MEANS NO CAP — an explicit opt-out, the way opencode's <c>agent.steps ?? Infinity</c>
    /// leaves a session unbounded when nobody asked for a ceiling.
    ///
    /// <para>Taken literally, 0 would be a ceiling of zero turns: the agent would stop before its
    /// first call and do nothing at all. Nobody configures that on purpose, so the number is free to
    /// carry the meaning someone actually intends by it — "I do not want this bounded".</para>
    /// </summary>
    [Fact]
    public void TurnCeiling_OfZero_MeansUnbounded()
    {
        var runner = new AgentHost(new MockLlmProvider(), new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins())
        {
            ConfiguredMaxWorkerTurns = 0,
        };

        Assert.Equal(int.MaxValue, runner.TurnCeiling);
    }

    /// <summary>Records that it was disposed, standing in for a subprocess we do not want to spawn
    /// in a unit test.</summary>
    private sealed class SpyDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>
    /// DISPOSING THE HOST ENDS ITS MCP SUBPROCESSES.
    ///
    /// <para>The one failure that outlives the process. An F5 re-wire builds a fresh host on every
    /// provider change and disposes the outgoing one; without this each re-wire leaves its servers
    /// running for the life of the app, holding whatever they had open. Orphaned children are this
    /// task's stated risk, so it does not ship on inspection alone.</para>
    /// </summary>
    [Fact]
    public void Dispose_DisposesTheMcpServers()
    {
        var server = new SpyDisposable();
        var runner = new AgentHost(new MockLlmProvider(), new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(), mcpServers: [server]);

        runner.Dispose();

        Assert.True(server.Disposed, "an F5 re-wire would leak this server's subprocess");
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
