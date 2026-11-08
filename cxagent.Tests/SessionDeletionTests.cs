using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Deleting a saved session for good — the rule both front ends ask through: what it removes (the whole resume chain),
/// what it refuses (a session open here or held elsewhere), and the question put first.
/// </summary>
public sealed class SessionDeletionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("session-delete-").FullName;
    private readonly AppPaths _paths;
    private readonly string _work;

    public SessionDeletionTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "cfg"));
        _paths.EnsureCreated();
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private FolderSessionStore Store => new(_paths);

    private void Saved(string id, string text) =>
        Store.SaveTurn(new FolderSessionStore.ResumeTurn(id, [new ChatMessage { Role = "user", Content = text }], 1, 1, _work));

    /// <summary>B continues A, as a resume makes it: B's history begins by naming A.</summary>
    private void Continues(string newer, string older)
    {
        Saved(newer, "carried on");
        File.WriteAllText(Path.Combine(new SessionFolder(_paths, newer).Dir, "history.jsonl"),
            $"{{\"q\":1,\"at\":\"2026-11-08T00:00:00+00:00\",\"k\":\"continues\",\"prev\":\"{older}\"}}\n");
        Store.MarkSuperseded(older);
    }

    [Fact]
    public void Delete_removes_the_whole_resume_chain()
    {
        Saved("OLDEST", "chart the sales");
        Continues("NEWEST", "OLDEST");

        using var manager = SessionManager.Create(_paths);
        var said = SessionDeletion.Delete(Store, manager, "NEWEST");

        Assert.StartsWith("Deleted", said);
        Assert.False(Directory.Exists(new SessionFolder(_paths, "NEWEST").Dir));
        Assert.False(Directory.Exists(new SessionFolder(_paths, "OLDEST").Dir));
        Assert.Empty(Store.List(all: true));
    }

    [Fact]
    public void Ask_names_the_session_and_says_it_cannot_be_brought_back()
    {
        Saved("SAVED", "chart the sales");
        using var manager = SessionManager.Create(_paths);

        var ask = SessionDeletion.Ask(Store, manager, "SAVED");

        Assert.Equal("SAVED", ask.Uid);
        Assert.Contains("chart the sales", ask.Text);
        Assert.Contains("cannot be brought back", ask.Text);
    }

    [Fact]
    public void A_session_open_here_is_refused_and_kept()
    {
        using var manager = SessionManager.Create(_paths);
        var open = manager.Open(_work, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() });
        var uid = open.SessionId!;
        Saved(uid, "in use");

        var ask = SessionDeletion.Ask(Store, manager, uid);
        var said = SessionDeletion.Delete(Store, manager, uid);

        Assert.Null(ask.Uid);
        Assert.Contains("open", ask.Text);
        Assert.DoesNotContain("Deleted", said);
        Assert.True(Directory.Exists(new SessionFolder(_paths, uid).Dir));
    }

    [Fact]
    public void An_unknown_session_is_said_not_thrown()
    {
        using var manager = SessionManager.Create(_paths);

        Assert.Null(SessionDeletion.Ask(Store, manager, "NOPE").Uid);
        Assert.Contains("No saved session", SessionDeletion.Delete(Store, manager, "NOPE"));
    }

    [Fact]
    public void The_terminal_names_one_by_its_number_in_this_folder_s_list()
    {
        Saved("FIRST", "chart the sales");
        using var manager = SessionManager.Create(_paths);
        var session = manager.Open(_work, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() });

        var ask = session.AskDeleteSession("1");

        Assert.Equal("FIRST", ask.Uid);
    }

    [Fact]
    public void The_command_table_offers_the_verb()
    {
        var verbs = CxAgent.Core.Commands.SessionCommands.ArgumentsFor("/sessions ");
        Assert.Contains(verbs, a => a.Name.StartsWith("delete", StringComparison.Ordinal));
    }
}
