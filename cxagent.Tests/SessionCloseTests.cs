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

    /// <summary>
    /// CLOSE RETURNS PROMPTLY, whatever the plugins are doing.
    ///
    /// <para>Unwiring awaits each plugin's Stop with a ten-second timeout and a session can hold
    /// several — a language server among them. Run on the caller's thread that is fine at process
    /// shutdown, where no loop is left to starve, and fatal for `/exit` closing one session of
    /// two: the app froze with "UI UNRESPONSIVE" while three plugins stopped, and the watchdog
    /// logged `phase Input`. Drive-verified before and after.</para>
    ///
    /// <para>THE BOUND IS GENEROUS ON PURPOSE. This asserts Close does not WAIT for the teardown,
    /// not that it is fast — a threshold tight enough to measure speed would be a flake on a loaded
    /// machine, and the failure being caught is a ten-second block.</para>
    /// </summary>
    [Fact]
    public void ClosingDoesNotWaitForPluginTeardown()
    {
        var (manager, session) = Wired();
        using var _ = manager;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(manager.Close(session));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2),
            $"Close took {clock.Elapsed.TotalSeconds:F1}s — it is waiting for the teardown again.");
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
