using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The routing table, one test per cell.
///
/// <para>THE CELLS ARE THE CONTRACT, so each gets its own test naming the state it pins. One test with
/// a switch over states would pass while any single cell was wrong, which is the shape of coverage
/// that reads as coverage without being it.</para>
/// </summary>
public class AgentDeliveryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "agent-delivery-" + Guid.NewGuid().ToString("N"));

    private SessionManager? _manager;

    /// <summary>The session Wired built, for a test that must read its live busy state.</summary>
    private Session? _session;

    /// <summary>The transcript Wired's session reports to, for a test that reads how a turn was drawn.</summary>
    private BufferedChatSink? _sink;

    public AgentDeliveryTests() => Directory.CreateDirectory(_dir);

    /// <summary>A WAKE STARTS A TURN NOBODY WAITS FOR, which is the behaviour under test, so that turn
    /// is still writing its log at teardown — see <see cref="TempDirectory.Delete"/>.</summary>
    public void Dispose()
    {
        _manager?.Dispose();
        TempDirectory.Delete(_dir);
    }

    /// <summary>
    /// A delivery over a real session, an empty store, and a child that is not yet kept.
    ///
    /// <para>THE SESSION IS REAL BECAUSE THE ID UNDER TEST IS ITS AGENT'S. <c>SessionId</c> is the id
    /// <c>AgentHost</c> minted for the agent it runs turns on, so a stub session would let the
    /// self-versus-child branch be proved against an id this code never actually sees.</para>
    /// </summary>
    private (SessionAgentDelivery Delivery, SubAgentStore Store, SubAgent Child) Wired() =>
        Wired(out _, out _);

    /// <summary>The same wiring, handing back the provider the CHILD answers from.</summary>
    private (SessionAgentDelivery Delivery, SubAgentStore Store, SubAgent Child) WiredWithChild(
        out MockLlmProvider childProvider)
    {
        var wired = Wired(out _, out _, out childProvider);
        return wired;
    }

    /// <summary>The same wiring over any provider, for a test that needs one which blocks.</summary>
    private (SessionAgentDelivery Delivery, SubAgentStore Store, string SessionAgentId) WiredOn(
        ILlmProvider provider)
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        var session = _manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = _sink = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        _session = session;
        // ONE STORE, HANDED BACK — two would let a test keep a child in an object the delivery never
        // consults, and the child branch would answer Unknown for a child the test thinks it kept.
        var store = new SubAgentStore();
        return (new SessionAgentDelivery(session, store), store, session.SessionId!);
    }

    private (SessionAgentDelivery Delivery, SubAgentStore Store, SubAgent Child) Wired(
        out MockLlmProvider provider, out string sessionAgentId) =>
        Wired(out provider, out sessionAgentId, out _);

    private (SessionAgentDelivery Delivery, SubAgentStore Store, SubAgent Child) Wired(
        out MockLlmProvider provider, out string sessionAgentId, out MockLlmProvider childProvider)
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        provider = new MockLlmProvider();
        var session = _manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = _sink = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        _session = session;
        sessionAgentId = session.SessionId!;
        var store = new SubAgentStore();
        return (new SessionAgentDelivery(session, store), store, Child(out childProvider));
    }

    /// <summary>
    /// Waits for the session's provider to be asked for a completion, which is what a started turn
    /// does first.
    ///
    /// <para>POLLED RATHER THAN AWAITED, because the behaviour under test is that nothing is awaited:
    /// a wake starts a turn nobody holds a handle to, precisely so a caller inside a <c>finally</c>
    /// cannot be made to block on it. There is therefore no Task for a test to await either, and the
    /// only honest observation is that the round trip happened.</para>
    ///
    /// <para>ANSWERS RATHER THAN ASSERTS, so a caller proving the NEGATIVE — an empty message starts
    /// no turn — uses the same wait and reads false from it.</para>
    /// </summary>
    private static bool Reached(MockLlmProvider provider)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (provider.ChatCallCount > 0) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>
    /// Answers only once the test releases it, so a session stays busy for an exact window.
    ///
    /// <para>MockLlmProvider CANNOT DO THIS, AND SHOULD NOT LEARN TO: it answers from a queue
    /// immediately, which is what every other test wants. A session is busy for exactly as long as it
    /// is INSIDE the model call, so holding that open needs a provider that does not return —
    /// SessionPluginClientTests keeps its own for the same reason.</para>
    /// </summary>
    private sealed class GatedProvider : ILlmProvider
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        /// <summary>How many requests reached the provider — the second is a turn reading what arrived.</summary>
        public int Calls => Volatile.Read(ref _calls);
        private int _calls;

        /// <summary>What the latest request carried.</summary>
        public List<ChatMessage>? LastMessages { get; private set; }

        public string ProviderId => "gated";
        public string ModelId => "gated-model";
        public string DisplayName => "Gated";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            LastMessages = [.. messages];
            // A DEADLINE, so a test that forgets to release fails rather than hanging the suite.
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return new LlmResponse { Text = "released", StopReason = "end_turn" };
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    /// <summary>A child agent with a context of its own, not kept until a test keeps it.</summary>
    private static SubAgent Child() => Child(out _);

    /// <summary>
    /// The same child, handing back the provider IT answers from.
    ///
    /// <para>FOR A TEST THAT PROVES THE CHILD RAN. A child's run is a round trip on its own provider,
    /// and <c>Wired</c>'s provider belongs to the SESSION — asserting on that one would pass against
    /// a delivery that woke the parent instead, which is the exact defect this port exists to fix.</para>
    /// </summary>
    private static SubAgent Child(out MockLlmProvider provider)
    {
        provider = new MockLlmProvider();
        return new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider,
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
            Tools = new AgentTools(CxAgent.Core.Jobs.JobRegistry.CreateWithBuiltins()),
        }).Create();
    }

    /// <summary>Waits for a turn nobody holds to finish.</summary>
    private async Task Settled()
    {
        for (var i = 0; i < 200 && _session!.IsBusy; i++) await Task.Delay(20);
    }

    /// <summary>
    /// AN EXIT NOTICE IS NOT THE USER SPEAKING. It wakes an idle session and the turn it starts is
    /// drawn as a System note — the transcript must not attribute to the user words they never typed.
    /// </summary>
    [Fact]
    public async Task ASystemWake_IsDrawnAsANote_NotAsTheUsersTurn()
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);
        provider.EnqueueResponse(new LlmResponse { Text = "noted", StopReason = "end_turn" });

        Assert.Equal(DeliveryOutcome.Woke, delivery.Tell(sessionAgentId, "the build finished"));
        await Settled();

        Assert.Contains("the build finished", _sink!.Notices);
        Assert.DoesNotContain("> the build finished", _sink.Transcript);
    }

    [Fact]
    public async Task AUserSubmit_IsStillTheUsersTurn()
    {
        Wired(out var provider, out _);
        provider.EnqueueResponse(new LlmResponse { Text = "hi", StopReason = "end_turn" });

        _session!.Submit("hello there");
        await Settled();

        Assert.Contains("> hello there", _sink!.Transcript);
    }

    /// <summary>Finished job rows clear on THIS, so it must mean the user — not a notice, not a plugin.</summary>
    [Fact]
    public async Task UserSpoke_IsRaisedForTheUser_AndNotForANoticeAPluginOrACommand()
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);
        for (var i = 0; i < 3; i++)
            provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var spoke = 0;
        _session!.UserSpoke += () => Interlocked.Increment(ref spoke);

        delivery.Tell(sessionAgentId, "the build finished");
        await Settled();
        _session.Submit("a plugin's goal", origin: TurnOriginator.Plugin("triggers"));
        await Settled();
        _session.Submit("/stats");
        Assert.Equal(0, spoke);

        _session.Submit("me, typing");
        Assert.Equal(1, spoke);
        await Settled();
    }

    /// <summary>A steer counts when it is SENT — the agent may take it minutes later, or never.</summary>
    [Fact]
    public async Task UserSpoke_IsRaisedForASteer_WhenItIsSent()
    {
        var gated = new GatedProvider();
        var (delivery, _, sessionAgentId) = WiredOn(gated);
        delivery.Tell(sessionAgentId, "start something");
        for (var i = 0; i < 200 && !_session!.IsBusy; i++) await Task.Delay(20);
        var spoke = 0;
        _session!.UserSpoke += () => Interlocked.Increment(ref spoke);

        _session.Submit("and also this");

        Assert.Equal(1, spoke);
        gated.Release();
        await Settled();
    }

    /// <summary>
    /// QUEUED TEXT WAITS FOR THE USER. Copy-to-transcript queues output "for the next message"; an exit
    /// notice waking the session must not carry it off, hidden behind the notice's echo.
    /// </summary>
    [Fact]
    public async Task InjectedText_WaitsForTheUser_NotForASystemWake()
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);
        provider.EnqueueResponse(new LlmResponse { Text = "noted", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "thanks", StopReason = "end_turn" });
        _session!.Inject("COPIED OUTPUT");

        delivery.Tell(sessionAgentId, "the build finished");
        await Settled();
        Assert.DoesNotContain("COPIED OUTPUT",
            string.Join("\n", provider.LastMessages!.Select(m => m.Content)));

        _session.Submit("look at this");
        await Settled();
        Assert.Contains("COPIED OUTPUT",
            string.Join("\n", provider.LastMessages!.Select(m => m.Content)));
    }

    /// <summary>Starts a turn held open by a gated provider, so a submit afterwards is a steer.</summary>
    private async Task<GatedProvider> Busy()
    {
        var gated = new GatedProvider();
        var (delivery, _, sessionAgentId) = WiredOn(gated);
        delivery.Tell(sessionAgentId, "start something");
        for (var i = 0; i < 200 && !_session!.IsBusy; i++) await Task.Delay(20);
        Assert.True(_session!.IsBusy, "the wake did not make the session busy");
        return gated;
    }

    /// <summary>
    /// A STEER SHOWS WHAT THE USER TYPED, NOTHING MORE. Queued Copy text goes to the model with it,
    /// but the queued block on screen — and what Escape hands back to the composer — is the user's
    /// own words; a log pasted into their message bar is words they did not type.
    /// </summary>
    [Fact]
    public async Task ASteer_IsShownAsTheUsersWordsOnly_WhileInjectedTextWaits()
    {
        var gated = await Busy();
        _session!.Inject("COPIED OUTPUT");

        _session.Submit("look at this");

        Assert.Equal("look at this", _session.PendingSteer);
        gated.Release();
        await Settled();
    }

    /// <summary>Escape takes back what the user typed; the copied text stays queued for their next message.</summary>
    [Fact]
    public async Task CancellingASteer_ReturnsOnlyTheUsersWords_AndKeepsTheCopyQueued()
    {
        var gated = await Busy();
        _session!.Inject("COPIED OUTPUT");
        _session.Submit("look at this");
        string? returned = null;
        _session.Cancelled += (text, _) => returned = text;

        _session.CancelPending();

        Assert.Equal("look at this", returned);
        Assert.Equal("COPIED OUTPUT", _session.TakeInjected());
        gated.Release();
        await Settled();
    }

    /// <summary>
    /// WHEN THE STEER IS DELIVERED the model receives the copy with it, and the transcript announces
    /// only the user's words.
    /// </summary>
    [Fact]
    public async Task ADeliveredSteer_CarriesTheCopyToTheModel_ButAnnouncesOnlyTheUsersWords()
    {
        var gated = await Busy();
        _session!.Inject("COPIED OUTPUT");
        _session.Submit("look at this");

        gated.Release();
        await Settled();

        Assert.Contains("> look at this", _sink!.Transcript);
        Assert.DoesNotContain("COPIED OUTPUT", _sink.Transcript);
        Assert.Contains(_session.Host!.Context.Messages,
            m => m.Content?.Contains("COPIED OUTPUT") == true && m.Content.Contains("look at this"));
        Assert.Null(_session.TakeInjected());
    }

    /// <summary>A kept sub-agent's jobs are the session's to show — but it is not the session's agent.</summary>
    [Fact]
    public void AKeptSubAgent_IsOwned_ButIsNotTheSessionAgent()
    {
        Wired(out _, out _);
        var child = Child();
        _session!.SubAgents.Keep(child, "find thing");

        Assert.True(_session.Owns(child.Agent.Id));
        Assert.False(_session.IsSessionAgent(child.Agent.Id));
    }

    [Fact]
    public void AnUnknownId_IsNotDelivered()
    {
        var (delivery, _, _) = Wired();

        Assert.Equal(DeliveryOutcome.Unknown, delivery.Tell("no-such-agent", "hello"));
    }

    /// <summary>
    /// A RUNNING SUB-AGENT IS INJECTED INTO. Its loop drains the mailbox at the top of each lap, so
    /// text handed over while it works is read on the next one.
    /// </summary>
    [Fact]
    public void ARunningSubAgent_IsInjected()
    {
        var (delivery, store, child) = Wired();
        var name = store.Keep(child, "find thing");
        Assert.True(store.ClaimAndAnnounce(name));       // busy == a loop is turning

        Assert.Equal(DeliveryOutcome.Injected, delivery.Tell(child.Agent.Id, "also check the loader"));
        Assert.Equal(["also check the loader"], child.Agent.Mailbox.Drain());
    }

    /// <summary>
    /// THE SESSION AGENT GOES THROUGH <c>Session.Submit</c>, whichever state it is in. Submit already
    /// decides between joining the running turn and starting a new one, and it is the method that
    /// owns the originator <c>UnwirePluginAsync</c>'s sever check reads — reaching past it to
    /// <c>Steer</c> would let a delivery inherit the running turn's.
    ///
    /// <para>PROVED BY THE PROVIDER BEING ASKED, and by what it was asked. An idle session is woken,
    /// so a round trip happens that would not otherwise, and the delivered text is in the messages
    /// that went with it — which together say the branch was reached AND carried the text, where an
    /// outcome alone would be satisfied by a method that returned <c>Woke</c> and did nothing.</para>
    /// </summary>
    [Fact]
    public void TheSessionAgent_GoesThroughSubmit()
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);
        provider.EnqueueResponse(new LlmResponse { Text = "noted", StopReason = "end_turn" });

        var outcome = delivery.Tell(sessionAgentId, "the build finished");

        // WOKE, NOT MERELY ONE OF THE TWO. The session was idle when this was told to it, so a turn
        // happened that would not otherwise have — and an assertion accepting either outcome would
        // survive the two arms being swapped, which is the one thing the wasBusy read exists to get
        // right. `Injected` is pinned separately, below.
        Assert.Equal(DeliveryOutcome.Woke, outcome);
        Assert.True(Reached(provider));
        Assert.Contains("the build finished",
            string.Join("\n", provider.LastMessages!.Select(m => m.Content)));
    }

    /// <summary>
    /// A BUSY SESSION IS INJECTED INTO, NOT WOKEN — the other arm of the <c>wasBusy</c> read.
    ///
    /// <para>WITHOUT THIS THE TWO ARMS COULD BE SWAPPED AND EVERY TEST WOULD STILL PASS. The wake case
    /// above pins <c>Woke</c>; this pins <c>Injected</c>, and only the pair makes the read that
    /// distinguishes them load-bearing.</para>
    ///
    /// <para>THE SESSION IS HELD BUSY BY ITS OWN PROVIDER, not by a flag a test sets: a turn is
    /// started and blocked inside the model call, which is what being busy IS. Reading the state some
    /// other way would prove the outcome against a condition the code does not consult.</para>
    /// </summary>
    [Fact]
    public async Task ABusySession_IsInjected()
    {
        var gated = new GatedProvider();
        var (delivery, _, sessionAgentId) = WiredOn(gated);

        // BUSY IS OBSERVED, NOT ASSUMED. The wake starts a turn on a task nobody here holds, so the
        // window opens asynchronously — asserting Injected before it opens would read the idle state
        // and fail for a reason that is not the behaviour under test. The gated provider is what keeps
        // the window open long enough to be read at all: MockLlmProvider answers instantly, so the
        // turn it starts is over before any poll can see it.
        Assert.Equal(DeliveryOutcome.Woke, delivery.Tell(sessionAgentId, "start something"));

        var session = _session!;
        for (var attempt = 0; attempt < 200 && !session.IsBusy; attempt++) await Task.Delay(20);
        Assert.True(session.IsBusy, "the wake did not make the session busy");

        Assert.Equal(DeliveryOutcome.Injected, delivery.Tell(sessionAgentId, "and also this"));

        gated.Release();
    }

    /// <summary>
    /// A DELIVERY THAT ARRIVES DURING A TURN'S LAST REQUEST IS STILL READ IN THAT TURN. The mailbox is
    /// drained before each request, so text landing while the final answer is being written misses
    /// every drain the turn has left — and the session, no longer busy, has nothing to wake it for.
    /// </summary>
    [Fact]
    public async Task ADeliveryDuringTheLastRequest_IsReadBeforeTheTurnEnds()
    {
        var gated = new GatedProvider();
        var (delivery, _, sessionAgentId) = WiredOn(gated);
        delivery.Tell(sessionAgentId, "start something");
        var session = _session!;
        for (var attempt = 0; attempt < 200 && gated.Calls == 0; attempt++) await Task.Delay(20);

        Assert.Equal(DeliveryOutcome.Injected, delivery.Tell(sessionAgentId, "the report arrived"));
        gated.Release();
        await Settled();

        Assert.Equal(2, gated.Calls);
        Assert.Contains("the report arrived",
            string.Join("\n", gated.LastMessages!.Select(m => m.Content)));
    }

    /// <summary>
    /// A DELIVERY THAT STARTED NO TURN IS REPORTED AS REFUSED rather than reported as delivered.
    ///
    /// <para>TEXT BEGINNING WITH A SLASH IS THE REACHABLE CASE. <c>Session.Submit</c> runs it as a
    /// command and starts nothing, so nothing was put where the agent will read it — and telling a
    /// caller its message landed when no agent will ever see it is the failure this pins.</para>
    ///
    /// <para>NO FULL-QUEUE REFUSAL IS ASSERTED ON THIS BRANCH because none exists on it: the port
    /// reaches <c>Session.Submit</c>, which steers into a running turn rather than into a bounded
    /// queue, so nothing here can be filled. A sub-agent'''s mailbox does refuse when full, and the
    /// child branch is where <see cref="DeliveryOutcome.Refused"/> is reachable that way.</para>
    /// </summary>
    [Fact]
    public void ADeliveryThatRanAsACommand_IsReported()
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);

        Assert.Equal(DeliveryOutcome.Refused, delivery.Tell(sessionAgentId, "/clear"));
        Assert.Equal(0, provider.ChatCallCount);
    }

    /// <summary>
    /// AN EMPTY MESSAGE IS REFUSED BEFORE ANYTHING IS ADDRESSED, and no turn is started for it.
    ///
    /// <para>A blank arrival would reach a model as a user message saying nothing, which costs a turn
    /// to read and answers no question.</para>
    ///
    /// <para>ASSERTED ON THE PROVIDER TOO, not only the outcome: the refusal has to happen before the
    /// submit, or an empty turn is started and merely reported as refused. <c>Reached</c> is given
    /// the chance to see a round trip and must not — an outcome-only assertion would pass against a
    /// port that woke the model first and returned Refused afterwards.</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyMessage_IsRefusedAndNothingIsSubmitted(string text)
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);

        Assert.Equal(DeliveryOutcome.Refused, delivery.Tell(sessionAgentId, text));
        Assert.False(Reached(provider));
    }

    /// <summary>
    /// A tool that hands its context to a callback and answers trivially.
    ///
    /// <para>THE CALLBACK TAKES THE CONCRETE <see cref="JobContext"/>, because that is where
    /// <c>Delivery</c> lives: <see cref="IJobContext"/> ships in CxAgent.Plugins.Abstractions, a
    /// package with no reference to Core at all, so a Core type cannot appear on it without changing
    /// the published plugin contract. An in-process executor casts, exactly as this does.</para>
    /// </summary>
    private sealed class RecordingTool(Action<JobContext> seen) : CxAgent.Core.Jobs.IAgentTool
    {
        public const string Name = "record_context";

        public ToolDefinition Definition { get; } = new(Name, "records the context it was called with",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context,
            CancellationToken ct)
        {
            seen((JobContext)context);
            return Task.FromResult(new JobResult { Success = true, Output = { ["content"] = "ok" } });
        }
    }

    /// <summary>A port that accepts anything, so a test can assert on identity rather than effect.</summary>
    private sealed class FakeDelivery : IAgentDelivery
    {
        public DeliveryOutcome Tell(string agentId, string text) => DeliveryOutcome.Woke;
    }

    /// <summary>
    /// A TOOL CALL CARRIES THE PORT, which is the only way an executor can reach back to the agent
    /// that called it: a JobContext holds an agent id, and an id alone addresses nothing.
    ///
    /// <para>ASSERTED ON THE CONTEXT A REAL TURN BUILDS, not on a hand-made one — the defect this
    /// guards is the wiring being absent, and a context constructed by the test would carry whatever
    /// the test put in it.</para>
    /// </summary>
    [Fact]
    public async Task AToolCall_CarriesTheDeliveryPort()
    {
        IAgentDelivery? seen = null;
        var tool = new RecordingTool(ctx => seen = ctx.Delivery);
        var delivery = new FakeDelivery();

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "c1", Name = RecordingTool.Name, Arguments = default }],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        var agent = new Agent(provider, CxAgent.Core.Jobs.JobRegistry.CreateWithBuiltins(),
            new TokenLedger(), new BufferedChatSink(), new BufferedJobPanel(), logs: null,
            maxTurns: 5, agentTools: [tool], delivery: delivery);

        await agent.SendAsync("call the tool", CancellationToken.None);

        Assert.Same(delivery, seen);
    }

    /// <summary>
    /// A SETTLED CHILD IS RUN, NOT LEFT QUEUED. A background command it started has finished, and a
    /// message nobody reads is the same as a result that was lost — the child is the only place that
    /// result exists, so waiting for somebody to resume it is waiting for nobody.
    ///
    /// <para>THE EMPTY MAILBOX IS THE PROOF, not the outcome word. A run drains the mailbox into the
    /// child's context on its first lap, so an outcome of <c>Woke</c> beside a mailbox still holding
    /// the text would be a method that named a wake and enqueued instead.</para>
    /// </summary>
    [Fact]
    public async Task ASettledSubAgent_IsRun()
    {
        var (delivery, store, child) = WiredWithChild(out var childProvider);
        childProvider.EnqueueResponse(new LlmResponse { Text = "noted", StopReason = "end_turn" });
        var name = store.Keep(child, "find thing");   // kept, never claimed: settled

        var outcome = delivery.Tell(child.Agent.Id, "the build finished, exit 1");

        Assert.Equal(DeliveryOutcome.Woke, outcome);

        // THE RUN IS NOT AWAITED BY Tell — see the port's own doc for why a caller in a `finally`
        // must not be made to block on somebody else's turn — so the drain happens on another
        // thread and is polled for, exactly as Reached polls for a session's round trip.
        for (var attempt = 0; attempt < 100 && store.IsBusy(name); attempt++) await Task.Delay(20);

        Assert.Empty(child.Agent.Mailbox.Drain());

        // AND THE CHILD'S OWN PROVIDER IS WHAT WAS ASKED, carrying the text. An empty mailbox alone
        // would be satisfied by a drain that threw the message away; this says where it went, and
        // says it about the CHILD rather than the session — the agent the old code woke instead.
        Assert.True(Reached(childProvider));
        Assert.Contains("the build finished, exit 1",
            string.Join("\n", childProvider.LastMessages!.Select(m => m.Content)));

        // AND THE CLAIM WAS GIVEN BACK. A run that held it forever would leave the child
        // permanently unreachable, with a delivery confirmation as the only symptom.
        Assert.False(store.IsBusy(name));
    }

    /// <summary>
    /// A WOKEN CHILD'S ANSWER REACHES THE SESSION. The parent took the child's final report long ago,
    /// so whatever the child does with a late message — a command it started finishing after it did —
    /// is known to nobody unless it is told onward; and the notice that woke it travels too, because
    /// the parent never saw that either.
    /// </summary>
    [Fact]
    public async Task ASettledSubAgentsAnswer_IsToldToTheSession()
    {
        var (delivery, store, child) = Wired(out var provider, out _, out var childProvider);
        childProvider.EnqueueResponse(new LlmResponse { Text = "tests now pass", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        store.Keep(child, "fix the build");

        delivery.Tell(child.Agent.Id, "the build finished, exit 1");

        Assert.True(Reached(provider));
        var told = string.Join("\n", provider.LastMessages!.Select(m => m.Content));
        Assert.Contains("tests now pass", told);
        Assert.Contains("fix-the-build", told);
        Assert.Contains("the build finished, exit 1", told);
        await Settled();
    }

    /// <summary>
    /// A SYSTEM PROMPT TO A BUSY SESSION IS INJECTED, NOT STEERED. Steer joins the running turn, is
    /// announced as a user turn and is drawn as the user's own queued block — all three wrong for
    /// text nobody typed. A system fact gets the agent's next lap instead.
    /// </summary>
    [Fact]
    public async Task ASystemPromptToABusySession_DoesNotJoinTheSteerQueue()
    {
        var gated = new GatedProvider();
        var (delivery, _, sessionAgentId) = WiredOn(gated);

        Assert.Equal(DeliveryOutcome.Woke, delivery.Tell(sessionAgentId, "start something"));
        var session = _session!;
        for (var attempt = 0; attempt < 200 && !session.IsBusy; attempt++) await Task.Delay(20);
        Assert.True(session.IsBusy, "the wake did not make the session busy");

        Assert.Equal(DeliveryOutcome.Injected, delivery.Tell(sessionAgentId, "the build finished"));

        // THE STEER QUEUE IS THE OBSERVATION, because it is the thing that must NOT have happened:
        // PendingSteer is what a front end draws as the user's queued block, so anything in it is a
        // system message about to be attributed to the user.
        Assert.Null(session.PendingSteer);

        gated.Release();
    }

    // ---- notices: a short line for the transcript, and a note that does not wake --------------------

    /// <summary>
    /// A NOTICE IS DRAWN AS ITS SHORT LINE, AND THE MODEL READS THE WHOLE OF IT. A background agent's
    /// report is pages long; the transcript gets one line saying it arrived.
    /// </summary>
    [Fact]
    public async Task ANoticeWake_DrawsTheShortLine_AndSendsTheWholeText()
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);
        provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });

        Assert.Equal(DeliveryOutcome.Woke, delivery.Tell(sessionAgentId,
            new AgentNotice(Shown: "agent x finished", ForModel: "the whole report, pages of it")));
        await Settled();

        Assert.Contains("agent x finished", _sink!.Notices);
        Assert.DoesNotContain(_sink.Notices, n => n.Contains("pages of it"));
        Assert.Contains("the whole report, pages of it", provider.Text());
    }

    /// <summary>INJECTED MID-TURN, THE SHORT LINE IS DRAWN THEN — the moment it reaches the agent.</summary>
    [Fact]
    public async Task ANoticeToABusySession_IsDrawnWhenItArrives()
    {
        var gated = new GatedProvider();
        var (delivery, _, sessionAgentId) = WiredOn(gated);
        delivery.Tell(sessionAgentId, "start something");
        Assert.True(await Poll.Until(() => gated.Calls == 1));

        Assert.Equal(DeliveryOutcome.Injected, delivery.Tell(sessionAgentId,
            new AgentNotice(Shown: "agent x finished", ForModel: "report")));

        Assert.Contains("agent x finished", _sink!.Notices);
        gated.Release();
        await Settled();
    }

    /// <summary>A SHELL'S EXIT NOTICE, which passes no short line, still draws nothing mid-turn.</summary>
    [Fact]
    public async Task APlainSystemDeliveryToABusySession_DrawsNothing()
    {
        var gated = new GatedProvider();
        var (delivery, _, sessionAgentId) = WiredOn(gated);
        delivery.Tell(sessionAgentId, "start something");
        Assert.True(await Poll.Until(() => gated.Calls == 1));
        var before = _sink!.Notices.Count;

        delivery.Tell(sessionAgentId, "The background command `x` finished with exit code 0");

        Assert.Equal(before, _sink.Notices.Count);
        gated.Release();
        await Settled();
    }

    /// <summary>
    /// LEFT, NOT WOKEN. A stop's report waits for the next turn and starts nothing — the user stopped the
    /// work, and a request spent hearing about it is a cost they did not ask for.
    /// </summary>
    [Fact]
    public async Task ALeftNotice_StartsNoTurn_DrawsItsLine_AndIsReadByTheNextTurn()
    {
        var (delivery, _, _) = Wired(out var provider, out var sessionAgentId);

        Assert.Equal(DeliveryOutcome.Queued, delivery.Leave(sessionAgentId,
            new AgentNotice(Shown: "agent x stopped", ForModel: "agent x was stopped")));
        Assert.False(Reached(provider));
        Assert.Contains("agent x stopped", _sink!.Notices);

        provider.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        await Assert.IsType<Session.SubmitOutcome.Started>(_session!.Submit("next")).Turn;
        Assert.Contains("agent x was stopped", provider.Text());
    }

    /// <summary>A LEFT NOTICE FOR A CHILD GOES TO ITS MAILBOX, and runs nothing.</summary>
    [Fact]
    public void ALeftNoticeForAChild_IsQueuedInItsMailbox_AndRunsNothing()
    {
        var (delivery, store, child) = WiredWithChild(out var childProvider);
        store.Keep(child, "find thing");

        Assert.Equal(DeliveryOutcome.Queued, delivery.Leave(child.Agent.Id,
            new AgentNotice(Shown: "shown", ForModel: "note")));
        Assert.False(Reached(childProvider));
        Assert.Equal(["note"], child.Agent.Mailbox.Drain());
    }

    [Fact]
    public void ALeftNoticeForAnUnknownId_IsUnknown()
    {
        var (delivery, _, _) = Wired();
        Assert.Equal(DeliveryOutcome.Unknown, delivery.Leave("nobody", new AgentNotice(Shown: "s", ForModel: "m")));
    }

    [Fact]
    public void AnEmptyNotice_IsRefused_BothWays()
    {
        var (delivery, _, _) = Wired(out _, out var id);
        Assert.Equal(DeliveryOutcome.Refused, delivery.Tell(id, new AgentNotice(Shown: "s", ForModel: " ")));
        Assert.Equal(DeliveryOutcome.Refused, delivery.Leave(id, new AgentNotice(Shown: "s", ForModel: "")));
    }

    /// <summary>A FULL MAILBOX REFUSES A LEFT NOTICE rather than claiming it was left.</summary>
    [Fact]
    public void ALeftNotice_ToAFullMailbox_IsRefused()
    {
        var (delivery, _, _) = Wired(out _, out var id);
        for (var i = 0; i < AgentMailbox.MaxDepth; i++) Assert.True(_session!.Host!.TryLeave($"n{i}"));

        Assert.Equal(DeliveryOutcome.Refused, delivery.Leave(id, new AgentNotice(Shown: "s", ForModel: "one more")));
    }

    /// <summary>A REPORT STILL REACHES A PARENT THAT SWITCHED TO SINGLE MODE while its child ran.</summary>
    [Fact]
    public async Task AReport_ReachesAParentThatSwitchedToSingle()
    {
        var parentModel = new MockLlmProvider("parent");
        parentModel.EnqueueResponse(new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [Calls.Spawn(id: "s1")] });
        parentModel.EnqueueResponse(new LlmResponse { Text = "started it", StopReason = "end_turn" });
        parentModel.EnqueueResponse(new LlmResponse { Text = "it found it", StopReason = "end_turn" });
        var childModel = new HeldProvider("found it");

        // TWO INSTANCES, THE CHILD'S TYPE ROUTED TO ITS OWN — a shared mock would interleave parent and child
        // requests and make the order the test depends on a race.
        var registry = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["local"] = parentModel, ["kid"] = childModel }, "local");
        var config = new ResolvedConfig(registry.Use("local"), new ProviderCatalog(Instances: registry,
            AgentTypes: new Dictionary<string, AgentTypeConfig> { ["general"] = new AgentTypeConfig("", Provider: "kid") }), []);

        _manager = SessionManager.Create(new AppPaths(_dir));
        var session = _session = _manager.Open(_dir, config,
            new SessionPorts { Observer = _sink = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.FanOut);

        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go")).Turn;
        session.SetMode(session.Mode with { Agent = AgentMode.Single });
        childModel.Release();

        Assert.True(await Poll.Until(() => parentModel.ChatCallCount == 3));
        Assert.Contains("found it", parentModel.Text());
    }

    /// <summary>A background run for a kept child, stopped by the user — as /tasks stop leaves it.</summary>
    private static BackgroundRun UserStopped(SubAgentStore store, string name)
    {
        var run = store.BeginBackground(new BackgroundStart(name, "parent", "do it", "find thing", IsResume: false),
            new ResultHandoff(_ => true));
        Assert.True(store.Stop(name, byUser: true));
        return run;
    }

    /// <summary>
    /// A SHELL'S EXIT NOTICE NEVER WAKES AN AGENT THE USER STOPPED. Waking it runs a turn nobody asked for,
    /// and its answer reaches the parent as "it acted on it and answered … completed" — the parent then
    /// tells the user that the agent they stopped finished its work.
    ///
    /// <para>BOTH HALVES ARE ASSERTED: the child's provider is never asked (no resume), and the session's is
    /// never asked either (no report reaches the parent). The notice is kept as a note, so a resume the user
    /// asks for later still learns how its command ended.</para>
    /// </summary>
    [Fact]
    public async Task AnExitNotice_ToAUserStoppedChild_NeitherWakesItNorReachesTheParent()
    {
        var (delivery, store, child) = Wired(out var provider, out _, out var childProvider);
        childProvider.EnqueueResponse(new LlmResponse { Text = "tests pass", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "it completed", StopReason = "end_turn" });
        var name = store.Keep(child, "find thing");
        UserStopped(store, name).Finish(BackgroundState.Stopped);

        var outcome = delivery.Tell(child.Agent.Id, "The background command `sleep 120` finished with exit code 0.");

        Assert.NotEqual(DeliveryOutcome.Woke, outcome);
        Assert.False(Reached(childProvider));
        Assert.Equal(0, provider.ChatCallCount);
        Assert.False(store.IsBusy(name));
        Assert.False(child.Agent.Mailbox.HasWaking);
        await Settled();
    }

    /// <summary>
    /// THE SAME WHEN THE STOP RACES THE EXIT: the run has been stopped but is still unwinding, so the claim is
    /// still held. A notice injected as a waking message would be read on the next lap of a loop the user
    /// ended; it is left as a note instead.
    /// </summary>
    [Fact]
    public void AnExitNotice_ToAUserStoppedChildStillUnwinding_IsLeftNotInjected()
    {
        var (delivery, store, child) = WiredWithChild(out var childProvider);
        var name = store.Keep(child, "find thing");
        Assert.True(store.TryClaim(name));
        UserStopped(store, name);

        var outcome = delivery.Tell(child.Agent.Id, "The background command `sleep 120` finished with exit code 0.");

        Assert.Equal(DeliveryOutcome.Queued, outcome);
        Assert.False(child.Agent.Mailbox.HasWaking);
        Assert.Equal(0, childProvider.ChatCallCount);
        store.Release(name);
    }
    /// <summary>
    /// NOTHING WAKES A CHILD OF A SESSION THAT HAS ENDED. Closing a tab kills the shells its children started,
    /// but an exit can race that sweep; a finished child — never stopped, so the user-stop guard does not hold
    /// it — would otherwise be resumed into a closed conversation and its answer submitted to a session whose
    /// host is going away.
    /// </summary>
    [Fact]
    public async Task AnExitNotice_ToAFinishedChildOfAnEndedSession_NeitherWakesItNorReachesTheParent()
    {
        var (delivery, store, child) = Wired(out var provider, out _, out var childProvider);
        childProvider.EnqueueResponse(new LlmResponse { Text = "tests pass", StopReason = "end_turn" });
        provider.EnqueueResponse(new LlmResponse { Text = "it completed", StopReason = "end_turn" });
        var name = store.Keep(child, "find thing");
        store.StopAll();

        var outcome = delivery.Tell(child.Agent.Id, "The background command `sleep 120` finished with exit code 0.");

        Assert.NotEqual(DeliveryOutcome.Woke, outcome);
        Assert.False(Reached(childProvider));
        Assert.Equal(0, provider.ChatCallCount);
        Assert.False(store.IsBusy(name));
        Assert.False(child.Agent.Mailbox.HasWaking);
        await Settled();
    }
}
