using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That resuming a conversation takes the session to the folder it came from.
///
/// <para>THE FOLDER WAS ALWAYS STORED and never restored: `agent_sessions.working_dir` is what
/// scopes the listing, but `SessionSnapshot` dropped it. So `/sessions resume &lt;id&gt; all` left a
/// session remembering one project while its tools acted on another — `/open`, `@file`, the plugin
/// search and the skill catalogue all resolving against the folder the user happened to be standing
/// in.</para>
///
/// <para>RESUMING MAKES THIS SESSION BECOME THE STORED ONE, so it goes where that one was.</para>
/// </summary>
public class ResumeMovesFolderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cxagent-move-" + Guid.NewGuid().ToString("N"));

    private readonly string _here;
    private readonly string _there;

    public ResumeMovesFolderTests()
    {
        _here = Path.Combine(_root, "here");
        _there = Path.Combine(_root, "there");
        Directory.CreateDirectory(_here);
        Directory.CreateDirectory(_there);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (SessionManager Manager, Session Session) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_root));
        var session = manager.Open(_here, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session);
    }

    /// <summary>THE STORE HANDS BACK THE FOLDER, which it did not before.</summary>
    [Fact]
    public void ASnapshotCarriesTheFolderItWasSavedIn()
    {
        var store = new SqliteSessionStore(new AppPaths(_root));
        store.SaveTurn("01THERE0000000000000000000", [], 1, 1, workingDir: _there);

        var found = store.LoadByUid("01THERE0000000000000000000");

        Assert.Equal(_there, found.Session?.WorkingDir);
    }

    /// <summary>AND RESUMING MOVES THE SESSION TO IT.</summary>
    [Fact]
    public void ResumingACrossFolderConversationMovesTheSession()
    {
        var (manager, session) = Wired();
        using var _ = manager;
        manager.Rewire = () => { };   // a host that re-wires without rebuilding anything

        Assert.Equal(_here, session.WorkingDirectory);

        manager.Resume(session, new SessionSnapshot(
            "agent-1", [], 0, 0, DateTimeOffset.UtcNow, Edits: null, WorkingDir: _there));

        Assert.Equal(_there, session.WorkingDirectory);
    }

    /// <summary>
    /// AN OLDER ROW LEAVES IT WHERE IT IS. A row written before the column was carried says nothing
    /// about where it ran, and guessing would change which files a turn may touch.
    /// </summary>
    [Fact]
    public void ASnapshotWithNoFolderLeavesTheSessionWhereItIs()
    {
        var (manager, session) = Wired();
        using var _ = manager;
        manager.Rewire = () => { };

        manager.Resume(session, new SessionSnapshot(
            "agent-1", [], 0, 0, DateTimeOffset.UtcNow, Edits: null, WorkingDir: null));

        Assert.Equal(_here, session.WorkingDirectory);
    }

    /// <summary>
    /// AND A BUSY SESSION IS NOT MOVED — Resume refuses first, so the folder cannot change under a
    /// turn whose tools are resolving paths against the old one.
    /// </summary>
    [Fact]
    public void ABusySessionKeepsItsFolder()
    {
        var (manager, session) = Wired();
        using var _ = manager;
        manager.Rewire = () => { };

        using (session.PretendBusyForTesting())
        {
            manager.Resume(session, new SessionSnapshot(
                "agent-1", [], 0, 0, DateTimeOffset.UtcNow, Edits: null, WorkingDir: _there));
        }

        Assert.Equal(_here, session.WorkingDirectory);
    }
}
