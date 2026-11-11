using System.Text.Json;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A colleague brought in with no task joins idle: equipped, marked, kept and recorded for resume, and not run — work
/// reaches it as it reaches any worker (user, 2026-11-08: "why can't attach one without a task?").
/// </summary>
public sealed class ColleagueJoinTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("colleague-join-").FullName;
    private readonly string _config;
    private readonly string _project;
    private readonly BufferedChatSink _seen = new();

    public ColleagueJoinTests()
    {
        _config = Path.Combine(_root, "cfg");
        _project = Path.Combine(_root, "work");
        Directory.CreateDirectory(_config);
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private Session Open(MockLlmProvider model, SharedServices? shared = null)
    {
        var resolution = ResolvedConfig.ForTesting(model) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, shared ?? new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = _seen, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    private void Charting() =>
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });

    [Fact]
    public async Task Bring_without_a_task_joins_idle_and_marked()
    {
        Charting();
        var model = new MockLlmProvider();
        var session = Open(model);

        var answer = await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);

        Assert.Contains("idle", answer);
        Assert.Equal(0, model.ChatCallCount);
        var kept = session.SubAgents.Find("charting")!;
        Assert.Equal("colleague:Charting", session.SubAgents.OriginOf("charting"));
        Assert.Contains("You are Charting", kept.Agent.Agent.Setup.Brief.CallerContext);
        Assert.Empty(kept.Agent.Agent.Context.Messages.Where(m => m.Role != "system"));
    }

    [Fact]
    public async Task A_joined_colleague_takes_work_by_agent_send_and_it_is_a_round()
    {
        Charting();
        var model = new MockLlmProvider();
        var session = Open(model);
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);
        model.EnqueueResponse(new LlmResponse { Text = "charted the sales by month, three panes", StopReason = "end_turn" });

        await new AgentReachTools(session.SubAgents).SendAsync(
            new SendRequest(To: "charting", Prompt: "chart the sales", Background: false, ParentAgentId: null, FromUser: true), CancellationToken.None);
        await session.Colleagues.Idle();

        var round = Assert.Single(Library.Past("Charting"));
        Assert.Contains(round.Messages, m => m.Content == "chart the sales");
    }

    [Fact]
    public async Task A_joined_colleague_comes_back_on_resume()
    {
        Charting();
        var paths = new AppPaths(_config);
        var shared = new SharedServices { GlobalInstructionsDir = _config, Logs = new LogFileManager(paths) };
        var model = new MockLlmProvider();
        var session = Open(model, shared);
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);
        var parent = new SessionFolder(paths, session.SessionId!).Dir;

        var source = new Core.Export.ExportSource(new Core.Export.ExportHeader(session.SessionId!, null, _project, null, null, 0, 0),
            Core.Export.ExportSource.EntriesOf(HistoryRecorder.Read(Path.Combine(parent, "history.jsonl")), parent), true, parent);
        var store = new SubAgentStore();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = model, Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000, WorkingDir = _project,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()), Augment = session.Colleagues.Augment,
            // THE SESSION'S OWN ENTRY, as a wired session resolves it: a bring names the entry it was checked against.
            Instances = name => name == "test" ? new TypeRouting(model, 200_000, "test") : null,
        });

        var report = WorkerRevival.Revive(source, "NEW", new Revival(factory,
            new AgentTypeCatalog(new Dictionary<string, AgentTypeConfig>(), null), store));

        Assert.Empty(report.Left);
        Assert.Equal("colleague:Charting", store.OriginOf("charting"));
        Assert.Contains("You are Charting", store.Find("charting")!.Agent.Agent.Setup.Brief.CallerContext);
    }

    [Theory]
    [InlineData("Charting")]
    [InlineData("\"Charting\"")]
    public async Task Command_alone_joins(string typed)
    {
        Charting();
        var session = Open(new MockLlmProvider());

        await session.BringColleagueAsync(typed);

        Assert.NotNull(session.SubAgents.Find("charting"));
        Assert.Contains(_seen.Notices, n => n.Contains("joined as charting", StringComparison.Ordinal));
        Assert.Contains("idle", session.TakeColleagueNote());
    }

    [Fact]
    public async Task Tool_without_task_joins()
    {
        Charting();
        var session = Open(new MockLlmProvider());
        var tool = new ColleagueTool(session.Colleagues!, null, () => session.SessionId!);

        var answer = await tool.TryInvokeAsync(new ToolCall
        {
            Name = "colleague", Id = "c", Arguments = JsonSerializer.SerializeToElement(new { action = "bring", name = "Charting" }),
        }, CancellationToken.None);

        Assert.Equal("charting", SubAgentEnvelope.NameOf(answer));
        Assert.Contains("idle", answer);
    }

    [Fact]
    public async Task An_ordinary_spawn_still_needs_a_prompt()
    {
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5, Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }), store: store);

        var answer = await spawner.TryInvokeAsync(new ToolCall
        {
            Name = "agent", Id = "c", Arguments = JsonSerializer.SerializeToElement(new { description = "x" }),
        }, null, CancellationToken.None);

        Assert.StartsWith("error: 'prompt' is required", answer);
        Assert.Empty(store.All());
    }

    /// <summary>
    /// THE LISTING SAYS WHO IS HERE AND HOW TO REACH THEM (user, 2026-11-11): a lead that listed the library after
    /// bringing one in saw only display names, sent to "Explore SharpConsoleUI API", and was refused — the handle was
    /// said once, in the bring note, and nowhere it looked again.
    /// </summary>
    [Fact]
    public async Task The_list_marks_a_colleague_in_this_session_with_its_handle()
    {
        Charting();
        Library.Create(new ColleagueRecord { Name = "Tables", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open(new MockLlmProvider());
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);

        var list = await new ColleagueTool(session.Colleagues, null, () => "lead").TryInvokeAsync(
            new ToolCall { Id = "c", Name = ColleagueTool.ToolName, Arguments = JsonSerializer.SerializeToElement(new { action = "list" }) },
            CancellationToken.None);

        var lines = list!.Split('\n');
        Assert.Contains("here as charting — reach it with agent_send", Assert.Single(lines, l => l.StartsWith("Charting", StringComparison.Ordinal)));
        Assert.DoesNotContain("here as", Assert.Single(lines, l => l.StartsWith("Tables", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A SEND TO A NAME NOBODY HOLDS NAMES THE ONES THAT EXIST, a colleague's with its own name beside it — not a tool
    /// to look them up with, which the lead in that session did not have.
    /// </summary>
    [Fact]
    public async Task A_send_to_a_wrong_name_lists_the_handles_there_are()
    {
        Charting();
        var session = Open(new MockLlmProvider());
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);

        var refused = await new AgentReachTools(session.SubAgents).SendAsync(
            new SendRequest(To: "Charting Expert", Prompt: "chart", Background: false, ParentAgentId: null, FromUser: false), CancellationToken.None);

        Assert.StartsWith("error: no sub-agent named 'Charting Expert' in this session.", refused, StringComparison.Ordinal);
        Assert.Contains("charting (the colleague Charting)", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("task_list", refused, StringComparison.Ordinal);
    }

    /// <summary>
    /// RELEASE (user, 2026-11-11: a colleague brought in had no way out short of closing the session). It leaves the
    /// session — not offered, not resumed — with every round it finished here already filed, and can be brought again.
    /// </summary>
    [Fact]
    public async Task Releasing_an_idle_colleague_takes_it_out_of_the_session()
    {
        Charting();
        var session = Open(new MockLlmProvider());
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);

        var said = session.Colleagues.Release("Charting");

        Assert.Equal("Charting has left this session. Its rounds here are kept; /colleagues bring Charting brings it back.", said);
        Assert.Null(session.SubAgents.Find("charting"));
        Assert.Null(session.Colleagues.HandleHere("Charting"));
        Assert.Equal("Charting is not in this session.", session.Colleagues.Release("Charting"));
        Assert.Contains("idle", await session.Colleagues.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None));
    }

    /// <summary>A colleague at work is not released from under its task: stopping it is a separate, deliberate step.</summary>
    [Fact]
    public async Task Releasing_a_working_colleague_is_refused()
    {
        Charting();
        var session = Open(new MockLlmProvider());
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);
        Assert.True(session.SubAgents.TryClaim("charting"));

        var said = session.Colleagues.Release("Charting");

        Assert.Equal("Charting is working — stop it first (/tasks stop charting), then release it.", said);
        Assert.NotNull(session.SubAgents.Find("charting"));
        session.SubAgents.Release("charting");
    }

    [Fact]
    public async Task The_command_and_the_leads_tool_both_release()
    {
        Charting();
        Library.Create(new ColleagueRecord { Name = "Tables", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open(new MockLlmProvider());
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);
        await session.Colleagues.BringAsync(new BringOrder("Tables", "", null, true, session.SessionId!), CancellationToken.None);

        session.ColleaguesCommand("release Charting");
        var tool = await new ColleagueTool(session.Colleagues, null, () => "lead").TryInvokeAsync(
            new ToolCall { Id = "c", Name = ColleagueTool.ToolName, Arguments = JsonSerializer.SerializeToElement(new { action = "release", name = "Tables" }) },
            CancellationToken.None);

        Assert.Null(session.SubAgents.Find("charting"));
        Assert.Null(session.SubAgents.Find("tables"));
        Assert.StartsWith("Tables has left this session.", tool);
    }

    /// <summary>Release in the colleague's own tab, at rest — the terminal's door beside its card.</summary>
    [Fact]
    public async Task A_colleague_s_tab_offers_release_at_rest()
    {
        Charting();
        var session = Open(new MockLlmProvider());
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);

        using var tab = new CxAgent.UI.AgentTab(session, session.SubAgents.Find("charting")!,
            AgentTabTests.Inline(new SharpConsoleUI.Controls.ChatTranscriptControl()));

        Assert.True(tab.ReleaseForTest.Visible);
    }
}
