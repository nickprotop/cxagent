using System.Text.Json;
using CxAgent.Core.Export;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A restored session's workers come back: kept under the handle the model knows, with the id everything already
/// names them by, and the conversation their history ends with.
/// </summary>
public sealed class WorkerRevivalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("revive-").FullName;
    private readonly AppPaths _paths;
    private readonly string _work;

    public WorkerRevivalTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "cfg"));
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ChatMessage M(string role, string text) => new() { Role = role, Content = text };

    private static ExportEntry E(ChatMessage m, string kind = HistoryLine.Kinds.Message) =>
        new(DateTimeOffset.UnixEpoch, kind, m, null, null, "dir");

    [Fact]
    public void A_worker_that_never_compacted_comes_back_with_its_whole_conversation()
    {
        var context = WorkerRevival.ContextOf([E(M("system", "s")), E(M("user", "brief")), E(M("assistant", "done"))]);
        Assert.Equal(["s", "brief", "done"], context.Select(m => m.Content));
    }

    [Fact]
    public void A_compacted_worker_comes_back_with_its_summary_and_what_followed_under_its_system_prompt()
    {
        var summary = M("assistant", Core.Sessions.SessionCompressor.SummaryMarker + "earlier work]");
        var context = WorkerRevival.ContextOf(
        [
            E(M("system", "s")), E(M("user", "brief")), E(M("assistant", "old")),
            E(M("assistant", "x"), HistoryLine.Kinds.Compacted), E(summary), E(M("user", "more")),
        ]);
        Assert.Equal(["s", summary.Content, "more"], context.Select(m => m.Content));
    }

    [Fact]
    public void A_restored_session_s_worker_is_kept_under_its_handle_and_id_with_its_conversation()
    {
        // THE PARENT SPAWNED "counter"; the child's own history lives in a folder named for it, under the parent's.
        var parent = new SessionFolder(_paths, "OLD").Dir;
        Directory.CreateDirectory(parent);
        var spawnedWith = new List<ChatMessage>
        {
            M("user", "count"),
            new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "c1", Name = "agent",
                Arguments = JsonDocument.Parse("""{"type":"general","description":"counter","prompt":"count files"}""").RootElement.Clone() }] },
            new() { Role = "tool", ToolCallId = "c1", Content = """<sub_agent id="CHILD01" name="counter" state="completed">2</sub_agent>""" },
            M("assistant", "2 files"),
        };
        new HistoryRecorder(Path.Combine(parent, "history.jsonl")).Record(spawnedWith);
        Directory.CreateDirectory(Path.Combine(parent, "CHILD01"));
        var t0 = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
        var childHistory = new HistoryRecorder(Path.Combine(parent, "CHILD01", "history.jsonl"));
        var brief = M("user", "count files") with { Timestamp = t0 };
        var again = M("user", "count again") with { Timestamp = t0.AddMinutes(16) };
        childHistory.Label(brief, Incoming.Brief);
        childHistory.Label(again, Incoming.Parent);
        childHistory.Record(
        [
            M("system", "child prompt") with { Timestamp = t0 },
            brief,
            new ChatMessage { Role = "assistant", Content = "", Timestamp = t0, ToolCalls = [new ToolCall { Id = "k1", Name = "run_shell",
                Arguments = JsonDocument.Parse("""{"command":"ls"}""").RootElement.Clone() }] },
            new ChatMessage { Role = "tool", ToolCallId = "k1", Content = "a.txt b.txt", Timestamp = t0 },
            M("assistant", "2") with { Timestamp = t0.AddSeconds(5) },
            again,
            M("assistant", "2") with { Timestamp = t0.AddMinutes(16).AddSeconds(3) },
        ]);

        var session = new Core.Sessions.Session(_work);
        session.PendResume(new SessionSnapshot("OLD", spawnedWith, 0, 0, DateTimeOffset.UtcNow, Edits: null, WorkingDir: _work));
        Core.Sessions.SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(new MockLlmProvider()),
            new Core.Sessions.SharedServices { Logs = new LogFileManager(_paths) },
            new Core.Sessions.SessionPorts { Observer = new Core.Sessions.BufferedChatSink(), ToolObserver = new Core.Sessions.BufferedJobPanel() },
            AgentMode.Single);

        var kept = session.SubAgents.Find("counter");
        Assert.NotNull(kept);
        Assert.Equal("CHILD01", kept!.Agent.Agent.Id);
        Assert.Equal(["child prompt", "count files", "", "a.txt b.txt", "2", "count again", "2"],
            kept.Agent.Agent.Context.Messages.Select(m => m.Content));

        // ITS TAB SHOWS ITS PAST: what its parent sent it are its turns there, with the calls it made.
        var tab = new CxAgent.Web.SessionEventLog("T");
        using (kept.Agent.Feed!.Attach(new Core.Sessions.ChildView(tab, tab)))
        {
            var events = tab.Since(0).Events;
            Assert.Equal(["count files", "count again"], events.Where(e => e["kind"]!.GetValue<string>() == "user").Select(e => e["text"]!.GetValue<string>()));
            Assert.Single(events, e => e["kind"]!.GetValue<string>() == "toolEnded");
        }

        // LISTED AS A WORKER THAT HAS RUN: a list shows only those, and a revived one had not run in this process.
        var row = Assert.Single(CxAgent.UI.AgentRows.For(session.SubAgents, DateTimeOffset.UtcNow));
        Assert.Equal(CxAgent.UI.AgentRowState.Done, row.State);

        // ITS LAST RUN'S LENGTH, not the sixteen minutes its whole record spans.
        Assert.Equal(TimeSpan.FromSeconds(3), row.Elapsed);
    }

    [Fact]
    public void A_handle_already_taken_is_not_taken_again()
    {
        var store = new SubAgentStore();
        var first = AgentTabTests.Child(new MockLlmProvider());
        var name = store.Keep(first, "counter");

        Assert.False(store.Restore(name, AgentTabTests.Child(new MockLlmProvider()), "counter",
            new AgentEnd("completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        Assert.Same(first, store.Find(name)!.Agent);
    }
}
