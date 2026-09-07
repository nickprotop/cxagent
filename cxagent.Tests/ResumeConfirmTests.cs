using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// When `/sessions resume` asks before discarding what a session is holding.
///
/// <para>RESUMING REPLACES THE CONVERSATION, and a session that has taken a turn has history the user
/// does not get back — it is not saved anywhere else. So the question is worth asking there, and
/// worth NOT asking in a session that has said nothing: a confirmation with nothing behind it is
/// friction that teaches people to dismiss the dialog unread, which is exactly when it stops
/// protecting anything.</para>
/// </summary>
public class ResumeConfirmTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-confirm-" + Guid.NewGuid().ToString("N"));

    public ResumeConfirmTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (SessionManager Manager, Session Session, BufferedChatSink Said) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var sink = new BufferedChatSink();
        var session = manager.Open(_dir,
            ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session, sink);
    }

    /// <summary>
    /// A SESSION THAT HAS SAID NOTHING RESUMES WITHOUT ASKING.
    ///
    /// <para>This is the common case straight after `/sessions new`, and the one where a
    /// confirmation costs something and protects nothing.</para>
    /// </summary>
    [Fact]
    public void AnEmptySessionIsNotAskedAboutReplacement()
    {
        var (manager, session, _) = Wired();
        using var _m = manager;

        var asked = false;
        manager.ConfirmReplace = _ => asked = true;

        Assert.False(session.HasSavedTurn);   // the precondition the branch turns on

        manager.Resume(session, new SessionSnapshot("agent-1", [], 0, 0, DateTimeOffset.UtcNow));

        Assert.False(asked);
    }

    /// <summary>
    /// AND A HOST THAT WIRED NO CONFIRMATION STILL RESUMES.
    ///
    /// <para>An embedder that never supplied a hook did not ask for a prompt it cannot render, and
    /// refusing the command instead would make resume look broken in a host that simply has no UI.
    /// </para>
    /// </summary>
    [Fact]
    public void AHostWithNoConfirmHookResumesAnyway()
    {
        var (manager, session, _) = Wired();
        using var _m = manager;

        manager.ConfirmReplace = null;
        var rewired = false;
        manager.RewireOne = _ => { rewired = true; return true; };

        manager.Resume(session, new SessionSnapshot("agent-1", [], 0, 0, DateTimeOffset.UtcNow));

        Assert.True(rewired);
    }

    /// <summary>THE HOOK IS REACHABLE THROUGH THE MANAGER, so a composition root can supply it
    /// without reaching into the shared services the manager builds for itself.</summary>
    [Fact]
    public void TheConfirmHookIsSettableOnTheManager()
    {
        var (manager, _, _) = Wired();
        using var _m = manager;

        Action<ReplaceConversation> hook = _ => { };
        manager.ConfirmReplace = hook;

        Assert.Same(hook, manager.ConfirmReplace);
    }
}
