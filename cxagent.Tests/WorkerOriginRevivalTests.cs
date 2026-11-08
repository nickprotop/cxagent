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

    /// <summary>
    /// A worker no report describes — a colleague the colleague tool brought, asked from its tab — comes back as its own
    /// history ended, not as failed: after a restart a live card read "idle · last task failed" for a colleague whose
    /// last run had answered.
    /// </summary>
    [Fact]
    public void A_worker_no_report_describes_comes_back_as_its_own_history_ended()
    {
        Parent().NoteWorker(new WorkerMark("CHILD01", "colleague:Charting") { Name = "charting", Type = "general", Description = "Charting" });

        var (store, _) = Revive(_ => new ChildAugment { Context = "EXP" });

        Assert.True(store.LastEnd("charting")!.Succeeded);
    }

    /// <summary>
    /// The later of its last report and its own last run decides how it comes back: a run asked from its tab after a
    /// report said "failed" answered, and the worker still came back failed.
    /// </summary>
    [Fact]
    public void A_later_run_of_its_own_beats_an_older_report()
    {
        var parent = Parent();
        parent.Record([M("user", "go"),
            new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "c1", Name = "agent",
                Arguments = JsonDocument.Parse("""{"type":"general","description":"charting","prompt":"work"}""").RootElement.Clone() }] },
            new() { Role = "tool", ToolCallId = "c1", Content = """<sub_agent id="CHILD01" name="charting" state="failed">broke</sub_agent>""" }]);
        Thread.Sleep(20);
        new HistoryRecorder(Path.Combine(_parent, "CHILD01", "history.jsonl")).Record([M("user", "try again"), M("assistant", "done now")]);

        var (store, _) = Revive(null);

        Assert.True(store.LastEnd("charting")!.Succeeded);
    }

    /// <summary>One that joined idle and never ran comes back with no end at all — never worked, not failed.</summary>
    [Fact]
    public void One_that_never_ran_comes_back_with_no_end()
    {
        Directory.CreateDirectory(Path.Combine(_parent, "IDLE01"));
        Parent().NoteWorker(new WorkerMark("IDLE01", "colleague:Parser") { Name = "parser", Type = "general", Description = "Parser" });

        var (store, _) = Revive(_ => new ChildAugment { Context = "EXP" });

        Assert.NotNull(store.Find("parser"));
        Assert.Null(store.LastEnd("parser"));
    }

    /// <summary>A run that stopped mid-tool, with no answer, is not called completed.</summary>
    [Fact]
    public void A_last_run_that_ended_without_an_answer_is_not_completed()
    {
        new HistoryRecorder(Path.Combine(_parent, "CHILD01", "history.jsonl")).Record([M("user", "again"),
            new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "t1", Name = "glob", Arguments = JsonDocument.Parse("{}").RootElement.Clone() }] }]);
        Parent().NoteWorker(new WorkerMark("CHILD01", "colleague:Charting") { Name = "charting", Type = "general", Description = "Charting" });

        var (store, _) = Revive(_ => new ChildAugment { Context = "EXP" });

        Assert.False(store.LastEnd("charting")!.Succeeded);
    }

    private sealed class Recall : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("recall", "past", JsonSerializer.SerializeToElement(new { type = "object" }));
        public CxAgent.Core.Permissions.PermissionRequest? Gate(JobParameters call) => null;
        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true, Output = new() { ["text"] = "x" } });
    }

    /// <summary>A resumed colleague gets its tools back through the same door: revival builds it through the factory.</summary>
    [Fact]
    public void Revival_brings_the_door_s_tools_back()
    {
        Parent().NoteWorker(new WorkerMark("CHILD01", "colleague:Charting") { Name = "charting", Type = "general", Description = "Charting" });

        var (store, _) = Revive(_ => new ChildAugment { Context = "EXP", ExtraTools = [new Recall()] });

        Assert.Contains("recall", store.Find("charting")!.Agent.Agent.OfferedToolNames());
    }

    /// <summary>
    /// A worker the user deleted stays deleted: a resumed session rebuilt it from the spawn in its parent's history, as if
    /// the delete had never happened.
    /// </summary>
    [Fact]
    public void A_deleted_worker_is_not_brought_back()
    {
        var parent = Parent();
        parent.Record(SpawnedByTool());
        parent.NoteWorker(new WorkerMark("CHILD01", null) { Deleted = true });

        var (store, report) = Revive(null);

        Assert.Null(store.Find("charting"));
        Assert.Empty(report.Left);
    }

    /// <summary>
    /// "Not this one" outlives a resume: the worker comes back turned down, and a declined line changes nothing else about
    /// it — not the origin a later line gave it.
    /// </summary>
    [Fact]
    public void A_declined_worker_comes_back_declined_and_otherwise_as_it_was()
    {
        var parent = Parent();
        parent.Record(SpawnedByTool());
        parent.NoteWorker(new WorkerMark("CHILD01", "colleague:Charting"));
        parent.NoteWorker(new WorkerMark("CHILD01", null) { Declined = true });

        var (store, _) = Revive(_ => new ChildAugment { Context = "EXP" });

        Assert.True(store.Declined("charting"));
        Assert.Equal("colleague:Charting", store.OriginOf("charting"));
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

        // THE MARK STAYS while the feature is merely off: switched back on, the next resume equips it again.
        Assert.Equal("colleague:Charting", store.OriginOf("charting"));

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

        // NO MARK LEFT: an ordinary worker now, which a keep may make a colleague again and no colleague's rounds reach.
        Assert.Null(store.OriginOf("charting"));
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
