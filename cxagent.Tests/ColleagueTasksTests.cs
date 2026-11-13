using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Colleagues in the Tasks panel: a section of their own, first, with a card that says what makes one a colleague —
/// its task, what it knows, its version, whether it is learning — and none of a worker's type or "finished" (user,
/// 2026-11-08). The terminal's F7 and the browser's Tasks pane draw the same facts.
/// </summary>
public sealed class ColleagueTasksTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleague-tasks-config-").FullName;
    private readonly string _project = TestProject.New("colleague-tasks-proj-");

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private async Task<Session> WithCharting(string? task = null, params LlmResponse[] before)
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "explore", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with
        {
            ExperienceVersion = 6,
            Workplaces = [new Workplace("cxgpu-12345678", "/home/dev/source/cxgpu", 1, DateTimeOffset.UtcNow),
                new Workplace("invoice-12345678", "/home/dev/source/invoice", 1, DateTimeOffset.UtcNow.AddDays(-1))],
        });
        var model = new MockLlmProvider();
        foreach (var response in before) model.EnqueueResponse(response);
        model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        var resolution = ResolvedConfig.ForTesting(model) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        await session.Colleagues!.BringAsync(new BringOrder("Charting", task ?? "", null, false, session.SessionId!), CancellationToken.None);
        await session.Colleagues.Idle();
        return session;
    }

    [Fact]
    public async Task The_card_facts_say_what_it_knows_its_version_and_its_task()
    {
        var session = await WithCharting("verify the FlowChrome usings\nand the rest");

        var card = session.Colleagues!.CardOf("charting")!;

        // WHERE IT HAS JUST WORKED IS KNOWN TOO, and comes first.
        Assert.Equal([Path.GetFileName(_project), "cxgpu", "invoice"], card.Knows);
        Assert.Equal(6, card.Version);
        Assert.False(card.Learning);
        Assert.Equal("verify the FlowChrome usings", card.Task);
    }

    /// <summary>
    /// The task is what it was asked, never what the app told it on the way: a live card read "task: You have called grep
    /// with the same arguments 3 times…" — the repeat warning, a user-role message added mid-run.
    /// </summary>
    [Fact]
    public async Task The_task_is_what_it_was_asked_not_a_warning_it_got_on_the_way()
    {
        File.WriteAllText(Path.Combine(_project, "a.txt"), "x");
        LlmResponse Same(int i) => new()
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = $"g{i}", Name = "glob", Arguments = System.Text.Json.JsonDocument.Parse("""{"pattern":"*.txt"}""").RootElement }],
        };

        var session = await WithCharting("chart the panes", Same(1), Same(2), Same(3));

        var kept = session.SubAgents.Find("charting")!;
        Assert.Contains(kept.Agent.Agent.Context.Messages, m => m.Content.StartsWith("You have called glob", StringComparison.Ordinal));
        Assert.Equal("chart the panes", session.Colleagues!.CardOf("charting")!.Task);
    }

    /// <summary>
    /// What a worker was asked is recorded as its run begins, on the run's own thread: the card read it from the worker's
    /// live conversation on the UI clock instead, a list the worker's own loop appends to and compacts in place.
    /// </summary>
    [Fact]
    public async Task What_it_was_asked_is_recorded_when_its_run_begins()
    {
        var session = await WithCharting("chart the panes\nwith the legend");

        Assert.Equal("chart the panes\nwith the legend", session.SubAgents.AskedOf("charting"));
    }

    [Theory]
    [InlineData(AgentRowState.Running, false, "working")]
    [InlineData(AgentRowState.Queued, false, "queued")]
    [InlineData(AgentRowState.WaitingForPermission, false, "waiting for permission")]
    [InlineData(AgentRowState.Done, false, "idle")]
    [InlineData(AgentRowState.Stopped, false, "idle")]
    [InlineData(AgentRowState.Failed, false, "idle · last task failed")]
    [InlineData(AgentRowState.Done, true, "learning")]
    [InlineData(AgentRowState.Running, true, "working")]
    public void A_colleague_s_state_is_never_finished(AgentRowState state, bool learning, string word) =>
        Assert.Equal(word, AgentRows.ColleagueWord(state, learning));

    [Fact]
    public async Task F7_has_a_Colleagues_section_first_with_the_card_and_no_type()
    {
        var session = await WithCharting("chart the panes");
        var panel = new JobsPanel();
        var kept = session.SubAgents.Find("charting")!;
        var row = new JobsPanel.AgentRow(AgentRows.Describe(session.SubAgents, kept, DateTimeOffset.UtcNow) ?? AgentRows.Idle(kept))
        {
            Colleague = session.Colleagues!.CardOf("charting"),
        };

        panel.Refresh(new JobsPanel.JobsShown([row], 0), 40);
        var text = panel.RenderedText;

        Assert.Contains("Colleagues", text);
        Assert.Contains("◆ Charting", text);
        Assert.Contains("task: chart the panes", text);
        Assert.Contains("v6 · knows", text);
        Assert.Contains("idle", text);
        Assert.DoesNotContain("explore", text);
        Assert.DoesNotContain("Finished", text);
        // NO ⏹ ON AN IDLE COLLEAGUE: a drive showed the card's default button on one that had never had a control.
        Assert.False(panel.ControlForTest(row.Key)!.Visible);
    }

    /// <summary>
    /// Here and available are told apart (user, 2026-11-08): the ones attached to this session get cards, and the rest
    /// of the library is listed under Available with Bring — read from the session in front, so it follows a switch.
    /// </summary>
    [Fact]
    public async Task F7_lists_the_ones_not_here_as_available_with_bring()
    {
        Library.Create(new ColleagueRecord { Name = "Parser", Type = "general", Created = DateTimeOffset.UtcNow.AddDays(-2), Description = "reads invoices" });
        var session = await WithCharting();
        var panel = new JobsPanel();
        var brought = new List<string>();
        panel.BringChosen += brought.Add;

        panel.Refresh(new JobsPanel.JobsShown([], 0) { Available = JobsPanel.AvailableOf(session.Colleagues!.Panel()) }, 40);
        var text = panel.RenderedText;
        panel.FollowAvailableForTest(0);

        Assert.Contains("Available", text);
        Assert.Contains("Parser", text);
        Assert.Contains("Bring", text);
        Assert.DoesNotContain("Charting", text.Split("Available")[1]);
        Assert.Equal(["Parser"], brought);
    }

    [Fact]
    public async Task One_that_joined_idle_and_never_worked_is_still_shown()
    {
        var session = await WithCharting();
        var kept = session.SubAgents.Find("charting")!;

        Assert.Null(AgentRows.Describe(session.SubAgents, kept, DateTimeOffset.UtcNow));
        Assert.Equal(AgentRowState.Done, AgentRows.Idle(kept).State);
    }

    [Fact]
    public async Task Its_tab_header_names_no_type()
    {
        var session = await WithCharting("chart it");
        var kept = session.SubAgents.Find("charting")!;

        var facts = AgentTab.FactsFor(kept, AgentRows.Describe(session.SubAgents, kept, DateTimeOffset.UtcNow),
            session.SubAgents.BadgeOf(kept.Name), card: null);

        Assert.StartsWith("colleague · charting", facts);
        Assert.Contains("idle", facts);
        Assert.DoesNotContain("explore", facts);
        Assert.DoesNotContain("done", facts);
    }

    [Fact]
    public async Task The_browser_gets_the_same_card()
    {
        var session = await WithCharting("verify the FlowChrome usings");

        var json = CxAgent.Web.WebPanel.ColleagueCard(session.Colleagues!.CardOf("charting"), "idle")!;

        Assert.Equal("verify the FlowChrome usings", json["task"]!.GetValue<string>());
        Assert.Equal(6, json["version"]!.GetValue<long>());
        Assert.Equal("idle", json["state"]!.GetValue<string>());
        Assert.True(CxAgent.Web.Http.StaticAssets.TryGet("/app.js", out var body, out _));
        var js = System.Text.Encoding.UTF8.GetString(body.Span);
        Assert.Contains("drawColleagueCards", js);
    }

    [Fact]
    public void The_library_names_no_type()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "explore", Created = DateTimeOffset.UtcNow, Instance = "local" });

        var tab = new ColleagueLibraryTab(Library, new ColleagueLibraryTab.Actions(_ => { }, _ => { }, _ => { }, _ => { }));

        Assert.DoesNotContain("explore", tab.DetailTextForTest);
        Assert.Contains("on local", tab.DetailTextForTest);
    }

    /// <summary>
    /// RELEASE ON AN IDLE COLLEAGUE'S CARD (user, 2026-11-11), beside Open and Ask… — and not while it works, when ⏹ is
    /// the card's control: release is refused then anyway, and a link that only refuses is noise.
    /// </summary>
    [Fact]
    public async Task An_idle_colleague_s_card_offers_release()
    {
        var session = await WithCharting();
        var panel = new JobsPanel();
        var kept = session.SubAgents.Find("charting")!;
        var row = new JobsPanel.AgentRow(AgentRows.Describe(session.SubAgents, kept, DateTimeOffset.UtcNow) ?? AgentRows.Idle(kept))
        {
            Colleague = session.Colleagues!.CardOf("charting"),
        };
        var chosen = new List<JobsPanel.RowChoice>();
        panel.RowChosen += chosen.Add;

        panel.Refresh(new JobsPanel.JobsShown([row], 0), 40);
        Assert.Contains("Release", panel.RenderedText);
        panel.FollowLinkForTest(row.Key, "release");
        panel.FollowLinkForTest(row.Key, "open");

        Assert.Equal([JobsPanel.RowAction.Release, JobsPanel.RowAction.Open], chosen.Select(c => c.Action));

        var working = row with { View = row.View with { State = AgentRowState.Running } };
        panel.Refresh(new JobsPanel.JobsShown([working], 0), 40);
        Assert.DoesNotContain("Release", panel.RenderedText);
    }
    // ---- Ask and Talk: one card, two tabs (user, 2026-11-11) ---------------------------------------------------------

    /// <summary>A COLLEAGUE'S TWO DOORS on its card: Ask… opens its Ask tab, Talk › its Talk tab — never Open.</summary>
    [Fact]
    public async Task The_card_offers_ask_and_talk()
    {
        var session = await WithCharting();
        var panel = new JobsPanel();
        var kept = session.SubAgents.Find("charting")!;
        var row = new JobsPanel.AgentRow(AgentRows.Idle(kept) with { Badge = session.SubAgents.BadgeOf("charting") })
        {
            Colleague = session.Colleagues!.CardOf("charting"),
        };
        var chosen = new List<JobsPanel.RowChoice>();
        panel.RowChosen += chosen.Add;

        panel.Refresh(new JobsPanel.JobsShown([row], 0), 40);
        panel.FollowLinkForTest(row.Key, "ask");
        panel.FollowLinkForTest(row.Key, "talk");

        Assert.Contains("Ask…", panel.RenderedText);
        Assert.Contains("Talk ›", panel.RenderedText);
        Assert.DoesNotContain("Open ›", panel.RenderedText);
        Assert.Equal([JobsPanel.RowAction.Open, JobsPanel.RowAction.Talk], chosen.Select(c => c.Action));
    }

    /// <summary>ITS TWO WORKERS ARE ONE CARD: the Talk worker folds into the Ask worker's row, which names it.</summary>
    [Fact]
    public async Task Ask_and_talk_are_one_card()
    {
        var session = await WithCharting();
        var talk = (await session.Colleagues!.OpenTalkAsync("Charting", session.SessionId!, CancellationToken.None)).Handle!;

        var rows = AgentRows.For(session.SubAgents, DateTimeOffset.UtcNow);

        var card = Assert.Single(rows, r => r.Badge is not null);
        Assert.Equal("charting", card.Kept.Name);
        Assert.Equal(session.SubAgents.Find(talk)!.Agent.Agent.Id, card.TalkAgentId);
        Assert.False(card.Talking);
    }

    /// <summary>
    /// THE CONVERSATION AT WORK ON THE CARD acts as its Talk worker — Stop and the task line reach that run — while the
    /// card stays the Ask worker's, with its own state kept for its tab. Stopping the card stopped the Ask worker at rest
    /// ("nothing to stop") while the conversation ran on.
    /// </summary>
    [Fact]
    public async Task A_card_showing_its_conversation_acts_as_the_talk_worker()
    {
        var session = await WithCharting();
        var talk = (await session.Colleagues!.OpenTalkAsync("Charting", session.SessionId!, CancellationToken.None)).Handle!;
        session.SubAgents.TryClaim(talk);
        session.SubAgents.AnnounceBegin(talk);

        var card = Assert.Single(AgentRows.For(session.SubAgents, DateTimeOffset.UtcNow), r => r.Badge is not null);

        Assert.True(card.Talking);
        Assert.Equal("charting", card.Kept.Name);
        Assert.Equal(talk, card.Acting.Name);
        Assert.False(card.Own!.Running);
    }

    /// <summary>A KEPT WORKER'S CONVERSATION WITH NO ASK WORKER YET stands alone, as the colleague's card.</summary>
    [Fact]
    public async Task A_talk_worker_alone_is_the_card()
    {
        var session = await WithCharting();
        session.SubAgents.KeepConversation("charting");

        var card = Assert.Single(AgentRows.For(session.SubAgents, DateTimeOffset.UtcNow), r => r.Badge is not null);

        Assert.Equal(card.Kept.Agent.Agent.Id, card.TalkAgentId);
    }

    /// <summary>EACH TAB SAYS WHICH DOOR IT IS; any other worker's tab says nothing.</summary>
    [Fact]
    public async Task Each_tab_names_its_door()
    {
        var session = await WithCharting();
        var talk = (await session.Colleagues!.OpenTalkAsync("Charting", session.SessionId!, CancellationToken.None)).Handle!;

        Assert.Equal(" · ask", AgentTab.Door(session.SubAgents, session.SubAgents.Find("charting")!));
        Assert.Equal(" · talk", AgentTab.Door(session.SubAgents, session.SubAgents.Find(talk)!));
    }

    /// <summary>AT WORK, THE CARD SAYS WHICH WORK: an ask, or the conversation with the user.</summary>
    [Theory]
    [InlineData(false, "answering an ask")]
    [InlineData(true, "talking with you")]
    public async Task The_card_says_which_work(bool talking, string word)
    {
        var session = await WithCharting();
        var kept = session.SubAgents.Find("charting")!;
        var row = AgentRows.Idle(kept) with { State = AgentRowState.Running, Talking = talking };

        Assert.Equal(word, AgentRows.ColleagueWord(row, session.Colleagues!.CardOf("charting")!));
    }
    /// <summary>
    /// THE STATE LINE NAMES THE DOOR TOO (user, 2026-11-11): "ASK" or "TALK" before the state, where the eye already is;
    /// any other worker's line is its state alone.
    /// </summary>
    [Fact]
    public async Task The_state_line_names_the_door()
    {
        var session = await WithCharting();
        var talk = (await session.Colleagues!.OpenTalkAsync("Charting", session.SessionId!, CancellationToken.None)).Handle!;

        Assert.Equal("ASK", AgentTab.DoorWord(session.SubAgents, session.SubAgents.Find("charting")!));
        Assert.Equal("TALK", AgentTab.DoorWord(session.SubAgents, session.SubAgents.Find(talk)!));
    }
    /// <summary>THE TAB SAYS WHAT THE CARD SAYS: "answering an ask" or "talking with you", never a bare "working".</summary>
    [Theory]
    [InlineData(false, "ANSWERING AN ASK")]
    [InlineData(true, "TALKING WITH YOU")]
    public async Task The_tab_s_state_word_is_the_card_s(bool talking, string word)
    {
        var session = await WithCharting();
        var kept = session.SubAgents.Find("charting")!;
        var row = AgentRows.Idle(kept) with { State = AgentRowState.Running, Talking = talking };

        Assert.Equal(word, AgentTab.StateWord(row, session.SubAgents.BadgeOf("charting"), session.Colleagues!.CardOf("charting"), ended: false));
    }
}
