using CxAgent.Core.Agent;
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
        await runner.SendAsync("goal", CancellationToken.None);

        Assert.Equal(42, runner.Ledger.TotalTokens);
    }

    /// <summary>
    /// AppBootstrap's status-bar cost readout has no per-Record event on TokenLedger to subscribe
    /// to, so AgentHost raises TokensUpdated itself at the same point it calls Ledger.Record —
    /// giving AppBootstrap a live hook without adding a public event to the ledger's own object
    /// model.
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

        await runner.SendAsync("goal", CancellationToken.None);

        Assert.Contains(42, seen);
    }

    [Fact]
    public async Task SendAsync_ProviderThrows_ShowsError_DoesNotLeakVendorBody()
    {
        var sink = new RecordingSink();
        var runner = NewRunner(new ThrowingProvider(), sink);

        var conversation = new List<ChatMessage>();
        await runner.SendAsync("x", CancellationToken.None);

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

        await runner.SendAsync("one", CancellationToken.None);
        var afterFirst = runner.Context.Messages.Count;
        await runner.SendAsync("two", CancellationToken.None);

        Assert.True(afterFirst > 0);
        Assert.True(runner.Context.Messages.Count > afterFirst);
    }

    /// <summary>
    /// An unconfigured session is still bounded.
    ///
    /// <para>An earlier version left the COMMON case — no orchestrator block in config — with no
    /// ceiling at all. The reasoning that removed the invented 200 was
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

            // WITH A WORKING DIRECTORY, as AppBootstrap wires it: resume is scoped to the folder a
            // session ran in, and a row saved without one is deliberately never offered.
            var runner = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
                PluginRegistry.CreateWithBuiltins(), store: store, workingDir: "/projects/here");

            await runner.SendAsync("remember this", CancellationToken.None);

            var snap = store.LoadLatestUnfinished("/projects/here");
            Assert.NotNull(snap);
            Assert.Equal(runner.SessionId, snap!.AgentId);
            Assert.Contains(snap.Context, m => m.Content.Contains("remember this"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// THE EXIT HINT'S GUARD. A session is written per turn, so one where nothing was said was never
    /// stored — printing "cxagent --resume XXXXXX" for it would hand the user a command that answers
    /// "no session matches" and makes resume look broken on its first use.
    /// </summary>
    [Fact]
    public async Task HasSavedTurn_IsFalseUntilSomethingIsSaid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-saved-" + Guid.NewGuid().ToString("N"));
        var paths = new CxAgent.Core.Storage.AppPaths(dir);
        paths.EnsureCreated();

        try
        {
            var store = new CxAgent.Core.Storage.SqliteSessionStore(paths);
            var mock = new MockLlmProvider();
            mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

            var runner = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
                PluginRegistry.CreateWithBuiltins(), store: store, workingDir: "/projects/here");

            Assert.False(runner.HasSavedTurn);

            await runner.SendAsync("say something", CancellationToken.None);

            Assert.True(runner.HasSavedTurn);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A restored session was loaded FROM a stored row, so there is already a way back.</summary>
    [Fact]
    public void HasSavedTurn_IsTrueImmediatelyForARestoredSession()
    {
        var mock = new MockLlmProvider();
        var snapshot = new CxAgent.Core.Storage.SessionSnapshot(
            "AAAA01KZXC",
            [new ChatMessage { Role = "user", Content = "earlier work" }],
            InputTokens: 10,
            OutputTokens: 5,
            UpdatedAt: DateTimeOffset.UtcNow);

        var runner = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(), resume: snapshot);

        Assert.True(runner.HasSavedTurn);
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
                PluginRegistry.CreateWithBuiltins(), store: store, workingDir: "/projects/here");
            await runner.SendAsync("hello", CancellationToken.None);

            // IT WAS OFFERABLE FIRST — otherwise this test passes vacuously, since an unscoped save
            // returns null whether or not MarkSessionFinished does anything at all.
            Assert.NotNull(store.LoadLatestUnfinished("/projects/here"));

            runner.MarkSessionFinished();

            Assert.Null(store.LoadLatestUnfinished("/projects/here"));
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

    // ---- 0c: the ledger is the composition root's ------------------------------------------------

    /// <summary>
    /// A GIVEN LEDGER IS USED, not shadowed by one of the host's own.
    ///
    /// <para>The point of the parameter (D7): a ledger built inside this constructor can only ever be
    /// the session's ONE ledger, which is the assumption per-model attribution and the sub-agent
    /// factory both have to break. Handing one in is how a caller answers "which ledger does this
    /// agent get?" — and the answer has to actually take effect, so a recorded turn must land in the
    /// caller's instance and nowhere else.</para>
    /// </summary>
    [Fact]
    public async Task Ledger_WhenGiven_IsTheOneUsed()
    {
        var mine = new TokenLedger();
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 30, OutputTokens = 12 } });

        var runner = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(), ledger: mine);

        Assert.Same(mine, runner.Ledger);

        await runner.SendAsync("goal", CancellationToken.None);

        Assert.Equal(42, mine.TotalTokens);
    }

    /// <summary>
    /// Omitting it keeps today's behaviour exactly — which is what leaves the ~10 other construction
    /// sites in this suite untouched by the hoist.
    /// </summary>
    [Fact]
    public void Ledger_WhenNotGiven_IsStillMadeHere()
    {
        var runner = new AgentHost(new MockLlmProvider(), new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins());

        Assert.NotNull(runner.Ledger);
        Assert.Equal(0, runner.Ledger.TotalTokens);
    }

    /// <summary>
    /// AN F5 PROVIDER CHANGE STILL RESETS THE SPEND TO ZERO. This is a REGRESSION test, and it is the
    /// whole reason ledger construction moved into <c>WireRunner</c> rather than to the top of
    /// <c>AppBootstrap</c>.
    ///
    /// <para>Hoisting it to startup would make one ledger SURVIVE the re-wire, reporting one
    /// session's spend across two providers as though it were one model's. Since <c>WireRunner</c>
    /// constructs a fresh
    /// <c>AgentHost</c> per re-wire, this asserts the seam it uses — a new host with a new ledger
    /// starts at zero however much the outgoing one had spent.</para>
    /// </summary>
    [Fact]
    public async Task Rewire_StartsANewLedgerAtZero_AsItDidBeforeTheHoist()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" }
            with { Usage = new LlmUsage { InputTokens = 900, OutputTokens = 100 } });

        var before = new TokenLedger();
        var first = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(), ledger: before);
        await first.SendAsync("spend something", CancellationToken.None);
        Assert.Equal(1_000, before.TotalTokens);

        // What WireRunner does on F5: dispose the outgoing host, build a fresh ledger, build a fresh
        // host over it.
        first.Dispose();
        var second = new AgentHost(mock, new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(), ledger: new TokenLedger());

        Assert.Equal(0, second.Ledger.TotalTokens);
    }

    /// <summary>
    /// A RESUMED SESSION GETS ITS SPEND BACK THROUGH THE GIVEN LEDGER TOO.
    ///
    /// <para>The trap this pins is in <c>WireRunner</c>, not here. <c>pendingResume</c> is consumed by
    /// an <c>Interlocked.Exchange</c>, and both the ledger's seed and the host's context now read it.
    /// Exchanging in the argument list while also reading it for the ledger hands the second reader a
    /// null — seeding the ledger and silently discarding the whole restored conversation, with every
    /// test still green. The fix is one local; this asserts the shape that local has to produce.</para>
    /// </summary>
    [Fact]
    public void Resume_WithAGivenLedger_RestoresBothContextAndSpend()
    {
        var snapshot = new CxAgent.Core.Storage.SessionSnapshot(
            "old-agent", [new ChatMessage { Role = "user", Content = "read Foo.cs" }],
            InputTokens: 5_397, OutputTokens: 435, UpdatedAt: DateTimeOffset.UtcNow);

        // Seeded from the SAME snapshot that is passed as `resume` — the invariant WireRunner keeps
        // by reading one local twice.
        var seeded = new TokenLedger(snapshot.InputTokens, snapshot.OutputTokens);

        var runner = new AgentHost(new MockLlmProvider(), new RecordingSink(), new NullJobPanel(),
            PluginRegistry.CreateWithBuiltins(), resume: snapshot, ledger: seeded);

        Assert.Single(runner.Context.Messages);      // the conversation, NOT discarded
        Assert.Equal(5_397 + 435, runner.Ledger.TotalTokens);
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
            ConfiguredMaxTurns = 0,
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
            ConfiguredMaxTurns = 25,
        };

        Assert.Equal(25, runner.TurnCeiling);
    }

    /// <summary>
    /// ONE RESOLUTION FOR PARENT AND CHILDREN, which is the bug this static exists to prevent.
    ///
    /// <para>The ceiling used to be computed twice — here for the session agent, and again by a
    /// separate expression in the composition root for sub-agents. They disagreed on the one value
    /// that has a documented meaning: a configured 0 left the parent unbounded while children
    /// silently fell back to the default.</para>
    /// </summary>
    [Fact]
    public void CeilingFor_GivesOneAnswer_ForEveryCaller()
    {
        Assert.Equal(AgentHost.DefaultTurnCeiling, AgentHost.CeilingFor(null));
        Assert.Equal(int.MaxValue, AgentHost.CeilingFor(0));
        Assert.Equal(42, AgentHost.CeilingFor(42));
    }

    /// <summary>The default is a real number, not "unbounded by accident".</summary>
    [Fact]
    public void DefaultTurnCeiling_IsFiniteAndGenerous()
    {
        Assert.Equal(300, AgentHost.DefaultTurnCeiling);

        // A live agentic drive on a real repo used 66 turns. The default has to clear that by a
        // wide margin and still bound a model that has stopped making progress.
        Assert.True(AgentHost.DefaultTurnCeiling > 66 * 2);
    }
}
