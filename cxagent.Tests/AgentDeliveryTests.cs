using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
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

    public AgentDeliveryTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Removes the temp root, tolerating a write that is still landing in it.
    ///
    /// <para>A WAKE STARTS A TURN NOBODY WAITS FOR, which is the behaviour under test: delivery to an
    /// idle session agent submits without awaiting, so that turn is still writing its log when
    /// teardown arrives and a write landing between the recursive delete's scan and its rmdir makes
    /// the directory non-empty again.</para>
    ///
    /// <para>RETRIED, THEN IGNORED, because this is cleanup of a temp folder the OS will reap anyway.
    /// A test that FAILS on its own teardown reports a defect that does not exist.</para>
    /// </summary>
    public void Dispose()
    {
        _manager?.Dispose();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>A client that records what it was told and answers as instructed.</summary>
    private sealed class FakeClient : IPluginClient
    {
        public List<string> Submitted { get; } = [];
        public bool Accept { get; set; } = true;
        public bool Busy { get; set; }

        public Task<SubmitResult> Submit(string goal, bool wantResult = false,
            CancellationToken ct = default)
        {
            Submitted.Add(goal);
            return Task.FromResult(Accept
                ? new SubmitResult(true, null, null)
                : new SubmitResult(false, null, "queue is full"));
        }
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

    private (SessionAgentDelivery Delivery, SubAgentStore Store, SubAgent Child) Wired(
        out FakeClient client, out string sessionAgentId)
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        var session = _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        client = new FakeClient();
        sessionAgentId = session.SessionId!;
        var store = new SubAgentStore();
        return (new SessionAgentDelivery(session, store, client), store, Child());
    }

    /// <summary>A child agent with a context of its own, not kept until a test keeps it.</summary>
    private static SubAgent Child() =>
        new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(),
            Executors = CxAgent.Core.Jobs.JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            ContextWindow = 200_000,
        }).Create();

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
        Assert.True(store.TryBeginSend(name));       // busy == a loop is turning

        Assert.Equal(DeliveryOutcome.Injected, delivery.Tell(child.Agent.Id, "also check the loader"));
        Assert.Equal(["also check the loader"], child.Agent.Mailbox.Drain());
    }

    /// <summary>
    /// A SETTLED SUB-AGENT IS QUEUED, NOT WOKEN — see DeliveryOutcome.Queued for why. The text still
    /// lands in the mailbox, which is what makes it readable on the next resume.
    /// </summary>
    [Fact]
    public void ASettledSubAgent_IsQueued()
    {
        var (delivery, store, child) = Wired();
        store.Keep(child, "find thing");             // kept, never claimed: settled

        Assert.Equal(DeliveryOutcome.Queued, delivery.Tell(child.Agent.Id, "the build finished"));
        Assert.Equal(["the build finished"], child.Agent.Mailbox.Drain());
    }

    /// <summary>
    /// THE SESSION AGENT GOES THROUGH THE CLIENT, whichever state it is in. The client already decides
    /// between joining the running turn and starting a new one, and it is the only path that carries
    /// the plugin originator the sever check depends on.
    /// </summary>
    [Fact]
    public void TheSessionAgent_GoesThroughTheClient()
    {
        var (delivery, _, _) = Wired(out var client, out var sessionAgentId);

        var outcome = delivery.Tell(sessionAgentId, "the build finished");

        Assert.Equal(["the build finished"], client.Submitted);
        Assert.True(outcome is DeliveryOutcome.Injected or DeliveryOutcome.Woke);
    }

    /// <summary>A full queue is the only refusal, and it is reported rather than swallowed.</summary>
    [Fact]
    public void ARefusedSubmit_IsReported()
    {
        var (delivery, _, _) = Wired(out var client, out var sessionAgentId);
        client.Accept = false;

        Assert.Equal(DeliveryOutcome.Refused, delivery.Tell(sessionAgentId, "the build finished"));
    }
}
