using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A second turn queues instead of starting, and Core is what decides that.
///
/// <para>THE INVARIANT, in the composition root's own words before it moved: "Two SendAsync calls on
/// the same Agent append to ONE live Context.Messages from two loops — and worse, the Exchange below
/// would dispose the RUNNING turn's token, so the first loop throws ObjectDisposedException at its
/// next cancellation check instead of cancelling." Neither failure throws where the mistake is made;
/// the conversation is simply wrong afterwards.</para>
///
/// <para>IT WAS ENFORCED BY A BOOL LOCAL TO ONE FRONT END while AgentHost's own comment leaned on it
/// as though Core held the rule. A second front end, a headless driver, or a resume racing a
/// submission would each have had to rediscover it.</para>
/// </summary>
public class SendGuardTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "send-" + Guid.NewGuid().ToString("N"));

    public SendGuardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private Session Wired(out SessionManager manager)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        return manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() },
            AgentMode.Single);
    }

    // AN IDLE SESSION STARTS A TURN, and the caller runs it — Send says whether one MAY start, not
    // how to await it, so a front end keeps its continuation and a headless driver awaits the Task.
    [Fact]
    public void Send_WhenIdle_SaysStarted_AndQueuesNothing()
    {
        var session = Wired(out var manager);
        using var _ = manager;

        Assert.Equal(Session.SendOutcome.Started, session.Send("go"));
        Assert.Null(session.PendingSteer);
    }

    // NO HOST IS NOT "STARTED". A caller that cleared its composer on Started would lose the text.
    [Fact]
    public void Send_WithNoAgent_SaysSo()
    {
        var session = new Session(_dir);

        Assert.Equal(Session.SendOutcome.NoAgent, session.Send("go"));
        Assert.Null(session.PendingSteer);
    }

    // THE PENDING EVENT IS THE REPORT. Nothing is said beside it — the queued block IS the message,
    // and a line saying "queued" next to it would say it twice.
    [Fact]
    public void Send_Queued_RaisesPending_SoAWatcherCanDrawIt()
    {
        var session = Wired(out var manager);
        using var _ = manager;

        var seen = new List<string>();
        session.Pending += (whole, _) => seen.Add(whole);

        // Steer directly is what Send does when busy; asserting the event fires from that path is
        // the half a test can pin without holding a real turn open.
        session.Steer("a correction");

        Assert.Equal(["a correction"], seen);
    }
}
