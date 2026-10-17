using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the store remembers about each stretch of an agent's work — when it began and how it ended — so a panel
/// row can mark it ✔ or ✗; the user's own send, which they can stop; and which agents count as running.
/// </summary>
public sealed class AgentBookkeepingTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bookkeeping-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;
    private BufferedChatSink? _sink;

    public AgentBookkeepingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _manager?.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static SubAgent Child(ILlmProvider? provider = null) => new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
    {
        Provider = provider ?? new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(),
        Ledger = new TokenLedger(), MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
    }).Create();

    internal static SubAgent ChildForTests() => Child();

    private Session Open()
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        return _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = _sink = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.FanOut);
    }

    [Fact]
    public void AStretch_RecordsWhenItBeganAndHowItEnded()
    {
        var clock = new Clock();
        var store = new SubAgentStore(new DetachedProcessRegistry(), new FinishedShells(), clock);
        store.Keep(Child(), "finder");
        var began = clock.Now;

        Assert.True(store.TryClaim("finder"));
        store.AnnounceBegin("finder");
        Assert.Equal(began, store.WorkingSince("finder"));
        clock.Now = began.AddSeconds(12);
        store.EndSend("finder", "capped");

        Assert.Equal(new AgentEnd("capped", began, began.AddSeconds(12)), store.LastEnd("finder"));
        Assert.False(store.LastEnd("finder")!.Succeeded);
        Assert.Null(store.WorkingSince("finder"));
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("capped", false)]
    [InlineData("stuck", false)]
    [InlineData("no-answer", false)]
    [InlineData("error", false)]
    [InlineData("cancelled", false)]
    [InlineData(null, false)]
    public void OnlyACompletedStretch_Succeeded(string? outcome, bool succeeded) =>
        Assert.Equal(succeeded, new AgentEnd(outcome, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch).Succeeded);

    /// <summary>
    /// A FOREGROUND SEND RECORDS ITS OUTCOME for the row — and the release still announces none, so the archive and
    /// the session tab's worker row see exactly what they saw before.
    /// </summary>
    [Fact]
    public async Task AForegroundSend_RecordsItsOutcome_WithoutAnnouncingOne()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var store = new SubAgentStore();
        store.Keep(Child(provider), "finder");
        var announced = new List<string?>();
        store.SendEnded += (_, outcome) => announced.Add(outcome);

        await new AgentReachTools(store).SendAsync(
            new SendRequest("finder", "more", Background: false, ParentAgentId: null, FromUser: true), CancellationToken.None);

        Assert.Equal("completed", store.LastEnd("finder")!.Outcome);
        Assert.Equal([null], announced);
    }

    [Fact]
    public void AUsersSend_CanBeStopped_Once()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "finder");

        var token = store.BeginUserSend("finder");
        Assert.True(store.IsUserSend("finder"));
        Assert.True(store.StopUserSend("finder"));
        Assert.True(token.IsCancellationRequested);
        Assert.False(store.StopUserSend("finder"));

        store.EndUserSend("finder", token);
        Assert.False(store.IsUserSend("finder"));
    }

    /// <summary>
    /// A SECOND SEND WHILE THE FIRST IS ANSWERING leaves the first stoppable when it ends. The second goes to the
    /// mailbox and returns at once; ending it must not take the first's token, or the first loses its Stop, its
    /// count and its composer, and reads as the parent waiting on it.
    /// </summary>
    [Fact]
    public void ASecondSend_EndingFirst_LeavesTheFirstStoppable()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "finder");
        var first = store.BeginUserSend("finder");
        var second = store.BeginUserSend("finder");

        store.EndUserSend("finder", second);

        Assert.True(store.IsUserSend("finder"));
        Assert.True(store.StopUserSend("finder"));
        Assert.True(first.IsCancellationRequested);
    }

    /// <summary>`/tasks stop` reaches the user's own send, and says it stopped that rather than a background run.</summary>
    [Fact]
    public void SlashTasksStop_StopsTheUsersOwnSend()
    {
        var s = Open();
        s.SubAgents.Keep(Child(), "finder");
        var token = s.SubAgents.BeginUserSend("finder");

        s.StopTask("stop finder");

        Assert.True(token.IsCancellationRequested);
        Assert.Contains(_sink!.Notices, n => n.Contains("stopped your send to 'finder'", StringComparison.Ordinal));
    }

    /// <summary>`/agents send` runs on the user-send token, so a stop typed while it works ends it.</summary>
    [Fact]
    public async Task SlashAgentsSend_IsStoppable()
    {
        var s = Open();
        var held = new HeldProvider("never");
        s.SubAgents.Keep(Child(held), "finder");

        s.SendToSpawnedAgent("send finder look again");
        Assert.True(await Poll.Until(() => held.Calls > 0));
        s.StopTask("stop finder");

        Assert.True(await Poll.Until(() => s.SubAgents.LastEnd("finder") is { Outcome: "cancelled" }));
        Assert.False(s.SubAgents.IsUserSend("finder"));
    }

    /// <summary>Decision 22: a foreground child and the user's own send are running work, as a background run is.</summary>
    [Fact]
    public void Working_CountsBackgroundRunsForegroundChildrenAndUserSends()
    {
        var store = new SubAgentStore();
        foreach (var name in new[] { "bg", "fg", "mine", "idle" }) store.Keep(Child(), name);
        store.TryClaim("bg");
        store.BeginBackground(new BackgroundStart("bg", "s", "b", "bg", false), new ResultHandoff(_ => true)).MarkRunning();
        store.TryClaim("fg");
        store.BeginUserSend("mine");
        store.TryClaim("mine");

        Assert.Equal(3, store.Working);
        Assert.Equal(1, store.UserSendsAlone);
    }

    /// <summary>Q12: closing a session ends the user's own send too, rather than leaving it to answer nobody.</summary>
    [Fact]
    public void StopAll_CancelsTheUsersSends()
    {
        var store = new SubAgentStore();
        store.Keep(Child(), "mine");
        var token = store.BeginUserSend("mine");

        store.StopAll();

        Assert.True(token.IsCancellationRequested);
    }
}
