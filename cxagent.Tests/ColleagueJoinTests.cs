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

    // ---- Talk: the conversation the user holds with a colleague here -------------------------------------------------

    private static string Sent(MockLlmProvider model) => string.Join("\n", model.LastMessages!.Select(m => m.Content));

    private static Task<string> FromUser(Session session, string to, string prompt) =>
        new AgentReachTools(session.SubAgents).SendAsync(
            new SendRequest(To: to, Prompt: prompt, Background: false, ParentAgentId: null, FromUser: true), CancellationToken.None);

    private static Task<string> FromLead(Session session, string to, string prompt) =>
        new AgentReachTools(session.SubAgents).SendAsync(
            new SendRequest(To: to, Prompt: prompt, Background: false, ParentAgentId: null, FromUser: false), CancellationToken.None);

    private async Task<(Session Session, MockLlmProvider Model, string Talk)> Talking()
    {
        Charting();
        var model = new MockLlmProvider();
        for (var i = 0; i < 6; i++) model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var session = Open(model);
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);
        var opened = await session.Colleagues.OpenTalkAsync("Charting", session.SessionId!, CancellationToken.None);
        Assert.Null(opened.Refusal);
        return (session, model, opened.Handle!);
    }

    /// <summary>
    /// TALK KEEPS ITS CONVERSATION; AN ASK STARTS FRESH (user, 2026-11-11). The user's second message to the Talk worker
    /// carries the first; the lead's ask to the Ask worker carries neither.
    /// </summary>
    [Fact]
    public async Task Talk_keeps_its_conversation_while_asks_start_fresh()
    {
        var (session, model, talk) = await Talking();
        Assert.NotEqual("charting", talk);
        Assert.Equal("charting", session.Colleagues!.HandleHere("Charting"));
        Assert.Equal(talk, session.Colleagues.TalkHere("Charting"));

        await FromUser(session, talk, "the word is PELICAN");
        await FromUser(session, talk, "which word?");
        Assert.Contains("PELICAN", Sent(model));
        Assert.Contains("talking with the user directly", Sent(model));

        await FromLead(session, "charting", "a question from the lead");
        Assert.DoesNotContain("PELICAN", Sent(model));
        Assert.DoesNotContain("talking with the user directly", Sent(model));
    }

    /// <summary>A LEAD'S SEND TO THE TALK WORKER IS ANSWERED BY THE ASK WORKER: the user's conversation is the user's.</summary>
    [Fact]
    public async Task A_leads_send_to_the_talk_worker_goes_to_the_ask_worker()
    {
        var (session, model, talk) = await Talking();

        await FromLead(session, talk, "LEAD-ASK");

        Assert.Contains(session.SubAgents.Find("charting")!.Agent.Agent.Context.Messages, m => m.Content.Contains("LEAD-ASK"));
        Assert.DoesNotContain(session.SubAgents.Find(talk)!.Agent.Agent.Context.Messages, m => m.Content.Contains("LEAD-ASK"));
    }

    /// <summary>GROWN MEANWHILE, the Talk worker is told the new version in one line; its conversation goes on.</summary>
    [Fact]
    public async Task Talk_is_told_when_the_experience_grew()
    {
        var (session, model, talk) = await Talking();
        await FromUser(session, talk, "first");

        Library.Update("Charting", r => r with { ExperienceVersion = r.ExperienceVersion + 3 });
        await FromUser(session, talk, "second");

        Assert.Contains(ColleagueDesk.VersionLine(Library.Find("Charting")!.ExperienceVersion), Sent(model));
        Assert.Contains("first", Sent(model));
    }

    /// <summary>RELEASE LETS BOTH GO: its Ask worker and its Talk worker.</summary>
    [Fact]
    public async Task Release_lets_both_workers_go()
    {
        var (session, _, talk) = await Talking();

        Assert.StartsWith("Charting has left this session.", session.Colleagues!.Release("Charting"));

        Assert.Null(session.SubAgents.Find("charting"));
        Assert.Null(session.SubAgents.Find(talk));
    }
    // ---- /colleagues ask and talk: the command's half, which both front ends call before opening the tab -------------

    /// <summary>The send is not awaited by the verb — its answer streams into the tab — so the test waits for it.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        Assert.True(done());
    }

    /// <summary>ASK, TYPED IN ANY CASE: its Ask worker is brought in, and the question goes to it from the user.</summary>
    [Fact]
    public async Task The_ask_verb_brings_the_ask_worker_and_asks_it()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var session = Open(model);

        var door = await session.OpenColleagueDoorAsync("charting which axis?", talk: false);

        Assert.Null(door.Refusal);
        Assert.Equal("charting", door.Handle);
        Assert.Null(session.Colleagues!.TalkHere("Charting"));
        await Until(() => model.LastMessages?.Any(m => m.Content.Contains("which axis?")) == true);
    }

    /// <summary>TALK OPENS ITS TALK WORKER, and a second talk goes on in the same conversation.</summary>
    [Fact]
    public async Task The_talk_verb_opens_the_talk_worker_and_goes_on()
    {
        Charting();
        var model = new MockLlmProvider();
        for (var i = 0; i < 2; i++) model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var session = Open(model);

        var first = await session.OpenColleagueDoorAsync("Charting the word is PELICAN", talk: true);
        await Until(() => model.ChatCallCount == 1 && !session.SubAgents.IsBusy(first.Handle!));
        var second = await session.OpenColleagueDoorAsync("Charting which word?", talk: true);
        await Until(() => model.ChatCallCount == 2);

        Assert.Equal(first.Handle, second.Handle);
        Assert.Equal(session.Colleagues!.TalkHere("Charting"), first.Handle);
        Assert.Contains("PELICAN", Sent(model));
    }

    /// <summary>NO TEXT, NOTHING SENT: the tab opens for the user to type in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_door_without_text_only_opens(bool talk)
    {
        Charting();
        var model = new MockLlmProvider();
        var session = Open(model);

        var door = await session.OpenColleagueDoorAsync("Charting", talk);

        Assert.NotNull(door.Handle);
        Assert.Equal(0, model.ChatCallCount);
    }

    /// <summary>A NAME NOBODY HAS is refused with the names there are, and nothing is brought in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_door_to_nobody_is_refused(bool talk)
    {
        Charting();
        var session = Open(new MockLlmProvider());

        var door = await session.OpenColleagueDoorAsync("Nobody hello", talk);

        Assert.Null(door.Handle);
        Assert.Contains("Nobody", door.Refusal);
        Assert.Null(session.Colleagues!.HandleHere("Charting"));
    }
    /// <summary>
    /// RELEASES AT ONCE LET IT GO ONCE: a held key on the tab's Release ran seven releases together, and every one said
    /// "has left". One says so; the rest find it gone.
    /// </summary>
    [Fact]
    public async Task Releases_at_once_let_it_go_once()
    {
        var (session, _, _) = await Talking();

        var said = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => session.Colleagues!.Release("Charting"))));

        Assert.Single(said, s => s.StartsWith("Charting has left this session.", StringComparison.Ordinal));
    }
    /// <summary>ONLY ITS TALK WORKER HERE, IT IS STILL HERE: the panel names that worker, and offers no Bring.</summary>
    [Fact]
    public async Task A_colleague_here_only_to_talk_is_not_available()
    {
        Charting();
        var session = Open(new MockLlmProvider());
        var talk = (await session.Colleagues!.OpenTalkAsync("Charting", session.SessionId!, CancellationToken.None)).Handle;

        var row = Assert.Single(session.Colleagues.Panel()!.Rows);

        Assert.Equal(talk, row.Handle);
        Assert.Empty(CxAgent.UI.JobsPanel.AvailableOf(session.Colleagues.Panel()));
    }
    // ---- A new conversation: the Talk worker starts over from what it has learned (user, 2026-11-11) -----------------

    /// <summary>A NEW CONVERSATION FORGETS THE OLD ONE: the next message carries neither it nor more than one briefing.</summary>
    [Fact]
    public async Task A_new_conversation_starts_the_talk_over()
    {
        var (session, model, talk) = await Talking();
        await FromUser(session, talk, "the word is PELICAN");

        Assert.Null(session.Colleagues!.NewConversation(talk));
        await FromUser(session, talk, "which word?");

        Assert.DoesNotContain("PELICAN", Sent(model));
        Assert.Contains("talking with the user directly", Sent(model));
    }

    /// <summary>ONLY A TALK HAS A CONVERSATION TO CLEAR: an ask starts fresh anyway, and says so.</summary>
    [Fact]
    public async Task An_ask_worker_has_no_conversation_to_clear()
    {
        var (session, _, _) = await Talking();

        Assert.Contains("starts fresh", session.Colleagues!.NewConversation("charting"));
    }

    /// <summary>/clear IN THE TALK TAB is the new conversation; it never reaches the model.</summary>
    [Fact]
    public async Task Clear_typed_in_the_talk_tab_starts_it_over()
    {
        var (session, model, talk) = await Talking();
        await FromUser(session, talk, "the word is PELICAN");
        var calls = model.ChatCallCount;

        Assert.Null(await session.SendFromAgentTab(talk, "/clear"));
        Assert.Equal(calls, model.ChatCallCount);
        await FromUser(session, talk, "which word?");

        Assert.DoesNotContain("PELICAN", Sent(model));
    }

    /// <summary>/clear in the Ask tab is answered, not sent: every ask starts fresh already.</summary>
    [Fact]
    public async Task Clear_typed_in_the_ask_tab_says_asks_start_fresh()
    {
        var (session, model, _) = await Talking();

        Assert.Contains("starts fresh", await session.SendFromAgentTab("charting", "/clear"));
        Assert.Equal(0, model.ChatCallCount);
    }

    /// <summary>/colleagues talk NAME --new opens the Talk tab on a new conversation.</summary>
    [Fact]
    public async Task The_talk_verb_with_new_starts_over()
    {
        var (session, model, talk) = await Talking();
        await FromUser(session, talk, "the word is PELICAN");

        var door = await session.OpenColleagueDoorAsync("Charting --new", talk: true);
        Assert.Equal(talk, door.Handle);
        await FromUser(session, talk, "which word?");

        Assert.DoesNotContain("PELICAN", Sent(model));
    }
}
