using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A colleague's one-to-one: a session of its own, the colleague as its agent, read-only, kept out of every list a
/// session is offered from, every turn a round — and the same conversation each time it is opened.
/// </summary>
public sealed class OneToOneTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("one-to-one-").FullName;
    private readonly string _config;
    private readonly string _project;
    private readonly AppPaths _paths;

    public OneToOneTests()
    {
        _config = Path.Combine(_root, "cfg");
        _project = Path.Combine(_root, "cxgpu");
        Directory.CreateDirectory(_config);
        Directory.CreateDirectory(_project);
        _paths = new AppPaths(_config);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);
    private FolderSessionStore Store => new(_paths);

    private void Charting()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with { Workplaces = [new Workplace(Codebase.Key(_project), _project, 1, DateTimeOffset.UtcNow)] });
        Library.WriteCore("Charting", "## Lessons\nPanes first.");
    }

    private Session Wire(Session session, ILlmProvider model, Core.Mcp.McpToolset? mcp = null)
    {
        var resolution = ResolvedConfig.ForTesting(model) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        SessionFactory.Wire(session, resolution,
            new SharedServices { GlobalInstructionsDir = _config, Resume = Store, Logs = new LogFileManager(_paths), Mcp = mcp },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    /// <summary>A lead session, to ask its desk for the one-to-one, then the one-to-one itself as a front end opens it.</summary>
    private (Session OneToOne, OneToOneOpening Opening) Opened(ILlmProvider model)
    {
        var lead = Wire(new Session(_project), model);
        var opening = lead.Colleagues!.OneToOne("Charting", out var refusal)!;
        Assert.Null(refusal);
        var session = new Session(opening.Folder);
        session.BecomeOneToOne(opening.Setup);
        if (opening.Resume is { } resume) session.PendResume(resume);
        return (Wire(session, model), opening);
    }

    [Fact]
    public void A_tagged_session_survives_every_turn_and_is_never_offered()
    {
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("ONE", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("ONE", [new ChatMessage { Role = "user", Content = "a" }, new ChatMessage { Role = "assistant", Content = "b" }], 2, 2, _project) { Tag = "colleague:Charting" });
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("PLAIN", [new ChatMessage { Role = "user", Content = "c" }], 1, 1, _project));

        Assert.Equal(["PLAIN"], Store.List(all: true).Select(i => i.Uid));
        Assert.Equal(["PLAIN"], Core.Commands.SessionsCommand.Offered(Store.List(_project)).Select(i => i.Uid));
        Assert.Equal("PLAIN", Store.LoadLatestUnfinished(_project)?.AgentId);
        Assert.Equal("PLAIN", Store.LoadLatestUncontinued(_project)?.AgentId);
        Assert.Equal(["ONE"], Store.ListTagged("colleague:Charting").Select(i => i.Uid));
    }

    /// <summary>
    /// A one-to-one named by its id (-r, a browser's resume) is known for what it is, so it opens as the one-to-one —
    /// resumed as an ordinary session it had every tool, and its tag fell off at its next save.
    /// </summary>
    [Fact]
    public void A_saved_one_to_one_is_known_by_its_tag()
    {
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("TAGGED", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("PLAIN2", [new ChatMessage { Role = "user", Content = "b" }], 1, 1, _project));

        Assert.Equal("Charting", ColleagueDesk.OneToOneOf(Store.LoadById("TAGGED")));
        Assert.Null(ColleagueDesk.OneToOneOf(Store.LoadById("PLAIN2")));
    }

    [Fact]
    public void Its_tools_are_read_only()
    {
        Charting();

        var (session, _) = Opened(new MockLlmProvider());

        var tools = session.OfferedToolNames();
        Assert.Contains(Tool.ReadFile, tools);
        Assert.Contains(Tool.Grep, tools);
        foreach (var never in new[] { Tool.WriteFile, Tool.ReplaceInFile, Tool.RunShell, Tool.Agent, Tool.Colleague, Tool.HttpRequest })
            Assert.DoesNotContain(never, tools);
    }

    private sealed class WritingServer : Core.Mcp.IMcpServer
    {
        public string Name => "files";
        public string? Instructions => null;
        public string? Error => null;
        public IReadOnlyList<Core.Mcp.McpToolDef> Tools { get; } =
            [new("write_file", "writes", System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone())];
        public Task<string> CallToolAsync(string tool, System.Text.Json.JsonElement args, CancellationToken ct) => Task.FromResult("written");
    }

    /// <summary>
    /// No MCP tool reaches a one-to-one: MCP is exempt from a tool selection by design, so a server the user enabled
    /// offered its write tools to a conversation that "cannot change anything".
    /// </summary>
    [Fact]
    public void No_MCP_tool_reaches_it()
    {
        Charting();
        var lead = Wire(new Session(_project), new MockLlmProvider());
        var opening = lead.Colleagues!.OneToOne("Charting", out _)!;
        var session = new Session(opening.Folder);
        session.BecomeOneToOne(opening.Setup);
        var mcp = new Core.Mcp.McpToolset([new WritingServer()]);

        Wire(session, new MockLlmProvider(), mcp);

        Assert.DoesNotContain(session.OfferedToolNames(), n => n.Contains("write_file", StringComparison.Ordinal));
        Assert.Contains(Wire(new Session(_project), new MockLlmProvider(), mcp).OfferedToolNames(), n => n.Contains("write_file", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing is brought in or kept inside a one-to-one, by any path: a worker brought there would report into the
    /// conversation, and its work would be distilled as the colleague's own — how a colleague is poisoned.
    /// </summary>
    [Fact]
    public async Task Nothing_is_brought_in_or_kept_there()
    {
        Charting();
        Library.Create(new ColleagueRecord { Name = "Builder", Type = "general", Created = DateTimeOffset.UtcNow });
        var (session, _) = Opened(new MockLlmProvider());

        var bring = await session.Colleagues!.BringAsync(new BringOrder("Builder", "fix it", null, true, session.SessionId ?? "x"), CancellationToken.None);
        var keep = session.Colleagues.Keep(new KeepOrder("anyone", null));

        Assert.Contains("one-to-one", bring);
        Assert.Contains("one-to-one", keep);
        Assert.Empty(session.SubAgents.All());
    }

    [Fact]
    public async Task Its_briefing_heads_the_prompt_and_every_turn_is_a_round()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "Panes first, because the legend follows the series.", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });   // the distiller
        var (session, _) = Opened(model);

        await session.SendAndWait("why do you lay charts out in panes?");
        await session.Colleagues!.Idle();

        var past = Library.Past("Charting");
        var round = Assert.Single(past);
        Assert.Contains(round.Messages, m => m.Content == "why do you lay charts out in panes?");
        Assert.Contains(round.Messages, m => m.Content.StartsWith("Panes first", StringComparison.Ordinal));
    }

    [Fact]
    public void The_briefing_says_it_is_talking_with_the_user()
    {
        Charting();
        var lead = Wire(new Session(_project), new MockLlmProvider());

        var opening = lead.Colleagues!.OneToOne("Charting", out _)!;

        Assert.Contains("You are Charting", opening.Setup.Briefing);
        Assert.Contains("Panes first.", opening.Setup.Briefing);
        Assert.Contains("talking with the user directly", opening.Setup.Briefing);
        Assert.Equal("with Charting", opening.Setup.Title);
        Assert.Equal(_project, opening.Folder);
    }

    [Fact]
    public async Task Open_again_returns_to_the_same_conversation()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "hello", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        var (first, opening) = Opened(model);
        Assert.Null(opening.Resume);
        await first.SendAndWait("hi");
        await first.Colleagues!.Idle();
        var uid = first.SessionId;

        var again = Wire(new Session(_project), model).Colleagues!.OneToOne("Charting", out _)!;

        Assert.Equal(uid, again.Resume?.AgentId);
    }

    /// <summary>One writer per conversation: a one-to-one open in another process is refused, as /sessions refuses one.</summary>
    [Fact]
    public void A_one_to_one_open_elsewhere_is_refused()
    {
        Charting();
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("HELD121", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        using var held = SessionLock.TryAcquire(_paths.LocksDir, "HELD121", LockHolder.Web);
        Assert.NotNull(held);
        var lead = Wire(new Session(_project), new MockLlmProvider());

        Assert.Null(lead.Colleagues!.OneToOne("Charting", out var refusal));
        Assert.Contains("open on the web", refusal);
    }

    /// <summary>
    /// A one-to-one lasts as long as its colleague: pruning took one left untouched for 30 days, and Open then started a
    /// new conversation.
    /// </summary>
    [Fact]
    public void Pruning_never_takes_a_one_to_one()
    {
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("OLD121", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("OLDPLAIN", [new ChatMessage { Role = "user", Content = "b" }], 1, 1, _project));
        Store.MarkFinished("OLD121");
        Store.MarkFinished("OLDPLAIN");

        Store.Prune(TimeSpan.FromDays(-1));   // everything finished is past its retention

        Assert.Equal(["OLD121"], Store.ListTagged("colleague:Charting").Select(i => i.Uid));
        Assert.DoesNotContain(Store.List(all: true), i => i.Uid == "OLDPLAIN");
    }

    /// <summary>Opening a one-to-one again retires the copy it continued, so only one copy of the conversation is live.</summary>
    [Fact]
    public async Task Open_again_retires_the_copy_it_continued()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "hello", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "again", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        var (first, _) = Opened(model);
        await first.SendAndWait("hi");
        await first.Colleagues!.Idle();
        var (again, _) = Opened(model);
        await again.SendAndWait("more");
        await again.Colleagues!.Idle();

        Assert.Equal([again.SessionId], Store.ListTagged("colleague:Charting").Select(i => i.Uid));
    }

    /// <summary>
    /// After a restart the conversation is resumed, and only what is said from then on is a new round: a review found the
    /// whole earlier conversation appended again on the first exchange after a resume, and distilled a second time.
    /// </summary>
    [Fact]
    public async Task After_a_restart_only_the_new_exchange_is_a_round()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "hello", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "panes first", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        var (first, _) = Opened(model);
        await first.SendAndWait("hi");
        await first.Colleagues!.Idle();

        var (again, opening) = Opened(model);   // a new process: a new desk, the conversation resumed from disk
        Assert.NotNull(opening.Resume);
        await again.SendAndWait("why panes?");
        await again.Colleagues!.Idle();

        var past = Library.Past("Charting");
        Assert.Equal(2, past.Count);
        Assert.DoesNotContain(past[1].Messages, m => m.Content == "hi");
        Assert.Contains(past[1].Messages, m => m.Content == "why panes?");
    }

    /// <summary>
    /// Inside its own one-to-one a colleague is not offered to itself: a live drive showed the fan-out warning, the
    /// opening "Colleagues: Charting…" line, and the panel's Bring for Charting — in the conversation with Charting.
    /// </summary>
    [Fact]
    public void A_one_to_one_says_nothing_about_colleagues_or_fan_out()
    {
        Charting();
        var lead = Wire(new Session(_project), new MockLlmProvider());
        var opening = lead.Colleagues!.OneToOne("Charting", out _)!;
        var seen = new BufferedChatSink();
        var session = new Session(opening.Folder);
        session.BecomeOneToOne(opening.Setup);
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        SessionFactory.Wire(session, resolution,
            new SharedServices { GlobalInstructionsDir = _config, Resume = Store, Logs = new LogFileManager(_paths) },
            new SessionPorts { Observer = seen, ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);

        Assert.DoesNotContain(seen.Notices, n => n.Contains("fan-out", StringComparison.Ordinal));
        Assert.DoesNotContain(seen.Notices, n => n.StartsWith("Colleagues:", StringComparison.Ordinal));
        Assert.Null(session.Colleagues!.Panel());
    }

    [Fact]
    public void An_unknown_colleague_is_refused()
    {
        var lead = Wire(new Session(_project), new MockLlmProvider());

        Assert.Null(lead.Colleagues!.OneToOne("Nobody", out var refusal));
        Assert.Contains("no colleague called 'Nobody'", refusal);
    }

    [Fact]
    public void The_command_table_offers_open()
    {
        Assert.Contains(Core.Commands.SessionCommands.ArgumentsFor("/colleagues "), a => a.Name.StartsWith("open", StringComparison.Ordinal));
    }
}
