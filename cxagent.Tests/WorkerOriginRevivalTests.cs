using System.Text.Json;
using CxAgent.Core.Export;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Where a worker came from is written in its parent's history, so a resume brings it back through the factory's door
/// — re-equipped by the module that knows its origin, or plain, with its conversation, when nothing does.
/// </summary>
public sealed class WorkerOriginRevivalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("origin-revive-").FullName;
    private readonly string _parent;

    public WorkerOriginRevivalTests()
    {
        _parent = Path.Combine(_root, "OLD");
        Directory.CreateDirectory(Path.Combine(_parent, "CHILD01"));
        new HistoryRecorder(Path.Combine(_parent, "CHILD01", "history.jsonl"))
            .Record([M("system", "p"), M("user", "work"), M("assistant", "worked")]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ChatMessage M(string role, string text) => new() { Role = role, Content = text };

    private HistoryRecorder Parent() => new(Path.Combine(_parent, "history.jsonl"));

    /// <summary>The parent's own conversation, spawning CHILD01 through the agent tool.</summary>
    private static List<ChatMessage> SpawnedByTool() =>
    [
        M("user", "go"),
        new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "c1", Name = "agent",
            Arguments = JsonDocument.Parse("""{"type":"general","description":"charting","prompt":"work"}""").RootElement.Clone() }] },
        new() { Role = "tool", ToolCallId = "c1", Content = """<sub_agent id="CHILD01" name="charting" state="completed">worked</sub_agent>""" },
    ];

    private (SubAgentStore Store, RevivalReport Report) Revive(Func<string, ChildAugment?>? augment)
    {
        var store = new SubAgentStore();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()), WorkingDir = _root, Augment = augment,
        });
        var source = new ExportSource(new ExportHeader("OLD", null, _root, null, null, 0, 0),
            ExportSource.EntriesOf(HistoryRecorder.Read(Path.Combine(_parent, "history.jsonl")), _parent), true, _parent);
        var report = WorkerRevival.Revive(source, "NEW", new Revival(factory,
            new AgentTypeCatalog(new Dictionary<string, AgentTypeConfig>(), null), store));
        return (store, report);
    }

    [Fact]
    public void A_spawn_mark_is_revived_with_its_origin()
    {
        var parent = Parent();
        parent.Record([M("user", "bring charting in")]);
        parent.NoteWorker(new WorkerMark("CHILD01", "colleague:Charting") { Name = "charting", Type = "general", Description = "Charting" });
        string? asked = null;

        var (store, report) = Revive(o => { asked = o; return new ChildAugment { Context = "EXP" }; });

        Assert.Empty(report.Left);
        Assert.Empty(report.Plain);
        Assert.Equal("colleague:Charting", asked);
        Assert.Equal("colleague:Charting", store.OriginOf("charting"));
        var kept = store.Find("charting")!;
        Assert.Equal("CHILD01", kept.Agent.Agent.Id);
        Assert.Equal("EXP", kept.Agent.Agent.Setup.Brief.CallerContext);
        Assert.Equal(["work", "worked"], kept.Agent.Agent.Context.Messages.Select(m => m.Content));
    }

    [Fact]
    public void A_bare_mark_after_an_agent_spawn_sets_the_origin()
    {
        var parent = Parent();
        parent.Record(SpawnedByTool());
        parent.NoteWorker(new WorkerMark("CHILD01", "colleague:Charting"));

        var (store, _) = Revive(_ => new ChildAugment { Context = "EXP" });

        Assert.Equal("colleague:Charting", store.OriginOf("charting"));
        Assert.Equal("EXP", store.Find("charting")!.Agent.Agent.Setup.Brief.CallerContext);
    }

    [Fact]
    public void A_mark_with_null_origin_clears_it()
    {
        var parent = Parent();
        parent.Record(SpawnedByTool());
        parent.NoteWorker(new WorkerMark("CHILD01", "colleague:Charting"));
        parent.NoteWorker(new WorkerMark("CHILD01", null));
        var asked = false;

        var (store, _) = Revive(_ => { asked = true; return null; });

        Assert.False(asked);
        Assert.Null(store.OriginOf("charting"));
        Assert.NotNull(store.Find("charting"));
    }

    [Fact]
    public void Without_a_hook_the_worker_comes_back_plain_with_its_conversation()
    {
        var parent = Parent();
        parent.Record(SpawnedByTool());
        parent.NoteWorker(new WorkerMark("CHILD01", "colleague:Charting"));

        var (store, report) = Revive(augment: null);

        var kept = store.Find("charting")!;
        Assert.Equal(["work", "worked"], kept.Agent.Agent.Context.Messages.Select(m => m.Content));
        Assert.Empty(report.Left);
        Assert.Contains("charting (what it came from is switched off here)", report.Plain);
    }

    [Fact]
    public void A_failing_hook_brings_it_back_plain_and_says_why()
    {
        var parent = Parent();
        parent.Record(SpawnedByTool());
        parent.NoteWorker(new WorkerMark("CHILD01", "colleague:Charting"));

        var (store, report) = Revive(_ => throw new InvalidOperationException("no such colleague"));

        Assert.NotNull(store.Find("charting"));
        Assert.Contains(report.Plain, l => l.Contains("no such colleague", StringComparison.Ordinal));
    }

    [Fact]
    public void Old_histories_without_marks_revive_exactly_as_before()
    {
        Parent().Record(SpawnedByTool());
        var asked = false;

        var (store, report) = Revive(_ => { asked = true; return null; });

        Assert.Empty(report.Left);
        Assert.Empty(report.Plain);
        Assert.False(asked);
        Assert.Null(store.OriginOf("charting"));
        Assert.Null(store.Find("charting")!.Agent.Agent.Setup.Brief.CallerContext);
    }
}
