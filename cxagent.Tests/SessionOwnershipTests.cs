using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which agent ids are this session's. A re-wire mints a new agent — and a new id — while the jobs
/// the old one started keep running and keep reporting to the id they were started under.
/// </summary>
public class SessionOwnershipTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "owns-" + Guid.NewGuid().ToString("N"));

    public SessionOwnershipTests() => Directory.CreateDirectory(_dir);

    /// <summary>RETRIED, THEN IGNORED: a woken turn may still be writing its log as this runs — see
    /// AgentDeliveryTests.Dispose, which meets the same race.</summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    private static SessionPorts Ports() =>
        new() { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() };

    private Session Open(SessionManager manager, ILlmProvider provider) =>
        manager.Open(_dir, ResolvedConfig.ForTesting(provider), Ports(), AgentMode.Single);

    [Fact]
    public void ASession_OwnsItsAgent_AndNotAStranger()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Open(manager, new MockLlmProvider());

        Assert.True(session.Owns(session.SessionId!));
        Assert.True(session.IsSessionAgent(session.SessionId!));
        Assert.False(session.Owns("01STRANGER"));
    }

    /// <summary>The id from before a re-wire is still this session's.</summary>
    [Fact]
    public void ARewire_KeepsTheOldAgentIdOwned()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Open(manager, new MockLlmProvider());
        var before = session.SessionId!;

        manager.Open(session, ResolvedConfig.ForTesting(new MockLlmProvider()), Ports());

        Assert.NotEqual(before, session.SessionId);
        Assert.True(session.Owns(before));
        Assert.True(session.IsSessionAgent(before));
    }

    /// <summary>
    /// A JOB OUTLIVES THE AGENT THAT STARTED IT. Its exit report is addressed to the id it was started
    /// under, and that id must still reach the session after a re-wire has replaced the agent.
    /// </summary>
    [Fact]
    public void AnExitAddressedToAReplacedAgent_StillWakesTheSession()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = Open(manager, new MockLlmProvider());
        var before = session.SessionId!;

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "noted", StopReason = "end_turn" });
        manager.Open(session, ResolvedConfig.ForTesting(provider), Ports());

        var outcome = new SessionAgentDelivery(session, session.SubAgents).Tell(before, "the build finished");

        Assert.Equal(DeliveryOutcome.Woke, outcome);
    }
}
