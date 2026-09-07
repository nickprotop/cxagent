using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What closing a session actually destroys.
///
/// <para>NOTHING CALLED THIS FOR A SECOND SESSION. `/exit` removed the TAB and left the conversation
/// open in the manager: its plugins stayed wired with their child processes running for the life of
/// the app, and the store still offered it to `--resume` as unfinished.</para>
/// </summary>
public class SessionCloseTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-close-" + Guid.NewGuid().ToString("N"));

    public SessionCloseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A manager holding one real session.
    ///
    /// <para>A RESOLVED CONFIG, because Open without one wires against a null catalog — and close
    /// reaches the session's host, so it has to be a session that actually has one.</para>
    /// </summary>
    private (SessionManager Manager, Session Session) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir,
            ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session);
    }

    /// <summary>A CLOSED SESSION LEAVES THE MANAGER, so nothing later iterates it.</summary>
    [Fact]
    public void ClosingRemovesTheSessionFromTheManager()
    {
        var (manager, session) = Wired();
        using var _ = manager;

        Assert.True(manager.Close(session));

        Assert.DoesNotContain(session, manager.Sessions);
    }

    /// <summary>
    /// A BUSY SESSION IS REFUSED AND SURVIVES. Disposing the host mid-turn kills the provider call
    /// and unwires plugins the turn is still calling into — an ObjectDisposedException surfacing on
    /// a scheduler thread where no caller can catch it.
    /// </summary>
    [Fact]
    public void ClosingRefusesWhileATurnIsRunning()
    {
        var (manager, session) = Wired();
        using var _ = manager;

        using (session.PretendBusyForTesting())
        {
            Assert.False(manager.Close(session));
            Assert.Contains(session, manager.Sessions);
        }
    }

    /// <summary>AND IT CLOSES ONCE THE TURN ENDS — the refusal is a wait, not a permanent no.</summary>
    [Fact]
    public void ClosingSucceedsAfterTheTurnEnds()
    {
        var (manager, session) = Wired();
        using var _ = manager;

        using (session.PretendBusyForTesting()) Assert.False(manager.Close(session));

        Assert.True(manager.Close(session));
        Assert.DoesNotContain(session, manager.Sessions);
    }
}
