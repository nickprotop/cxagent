using CxAgent.Core.Storage;
using CxAgent.Core.Models;
using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A session another process holds is listed as held and never resumed a second time — by
/// `/sessions resume`, by `--resume`, or by `-c`.</summary>
public sealed class HeldResumeTests : IDisposable
{
    private const string Held = "HELDAAAAAAAAAAAAAAAAAAAAAA";
    private readonly string _root = Directory.CreateTempSubdirectory("held-resume-").FullName;
    private readonly string _work;
    private readonly AppPaths _paths;

    public HeldResumeTests()
    {
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);
        _paths = new AppPaths(Path.Combine(_root, "cfg"));
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static List<ChatMessage> Talk(string text) => [new ChatMessage { Role = "user", Content = text }];

    [Fact]
    public void List_marks_superseded()
    {
        var store = new FolderSessionStore(_paths);
        store.SaveTurn(new FolderSessionStore.ResumeTurn("OLD", Talk("a"), 1, 1, _work));
        store.SaveTurn(new FolderSessionStore.ResumeTurn("NEW", Talk("b"), 1, 1, _work));
        store.MarkSuperseded("OLD");

        var rows = store.List(_work);

        Assert.True(rows.Single(r => r.Uid == "OLD").Superseded);
        Assert.False(rows.Single(r => r.Uid == "NEW").Superseded);
    }

    [Fact]
    public void List_reports_holder_of_a_live_session()
    {
        var store = new FolderSessionStore(_paths);
        store.SaveTurn(new FolderSessionStore.ResumeTurn(Held, Talk("a"), 1, 1, _work));
        store.SaveTurn(new FolderSessionStore.ResumeTurn("FREE", Talk("b"), 1, 1, _work));
        using var claim = SessionLock.TryAcquire(_paths.LocksDir, Held, LockHolder.Web);

        var rows = store.List(_work);

        Assert.Equal(LockHolder.Web, rows.Single(r => r.Uid == Held).Holder?.Kind);
        Assert.Null(rows.Single(r => r.Uid == "FREE").Holder);
    }

    [Fact]
    public void Sessions_resume_refuses_a_held_session_with_the_holder_named()
    {
        using var manager = SessionManager.Create(_paths);
        manager.Shared.Resume!.SaveTurn(new FolderSessionStore.ResumeTurn(Held, Talk("a"), 1, 1, _work));
        var said = new BufferedChatSink();
        var session = manager.Open(_work, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        var before = session.SessionId;

        using (var claim = SessionLock.TryAcquire(_paths.LocksDir, Held, LockHolder.Web))
            session.ListSessions("resume " + Held);

        Assert.Contains("open on the web", said.Transcript, StringComparison.Ordinal);
        Assert.Equal(before, session.SessionId);
    }

    [Fact]
    public void Resume_by_id_of_a_held_session_is_a_problem_naming_the_holder()
    {
        var store = new FolderSessionStore(_paths);
        store.SaveTurn(new FolderSessionStore.ResumeTurn(Held, Talk("a"), 1, 1, _work));
        using var claim = SessionLock.TryAcquire(_paths.LocksDir, Held, LockHolder.Terminal);

        var target = Startup.FindResumeTarget(store, _work, new ResumeRequest(ResumeKind.ById, Held));

        Assert.Null(target.Snapshot);
        Assert.Contains("open in terminal", target.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Continue_when_the_latest_is_held_is_a_problem()
    {
        var store = new FolderSessionStore(_paths);
        store.SaveTurn(new FolderSessionStore.ResumeTurn(Held, Talk("a"), 1, 1, _work));
        using var claim = SessionLock.TryAcquire(_paths.LocksDir, Held, LockHolder.Headless);

        var target = Startup.FindResumeTarget(store, _work, new ResumeRequest(ResumeKind.Continue, null));

        Assert.Null(target.Snapshot);
        Assert.Contains("open in another process", target.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LockHolder.Terminal, "open in terminal · run /web there to share it")]
    [InlineData(LockHolder.Web, "open on the web")]
    [InlineData(LockHolder.Headless, "open in another process")]
    public void Held_message_names_the_holder(LockHolder kind, string expected) =>
        Assert.Equal(expected, SessionLock.HeldMessage(new HeldBy(kind, 1)));
}
