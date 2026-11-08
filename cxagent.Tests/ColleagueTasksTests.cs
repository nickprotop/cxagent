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
    private readonly string _project = Directory.CreateTempSubdirectory("colleague-tasks-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private async Task<Session> WithCharting(string? task = null)
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "explore", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with
        {
            ExperienceVersion = 6,
            Workplaces = [new Workplace("cxgpu-12345678", "/home/dev/source/cxgpu", 1, DateTimeOffset.UtcNow),
                new Workplace("invoice-12345678", "/home/dev/source/invoice", 1, DateTimeOffset.UtcNow.AddDays(-1))],
        });
        var model = new MockLlmProvider();
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
            session.SubAgents.BadgeOf(kept.Name), learning: false);

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

        var tab = new ColleagueLibraryTab(Library, new ColleagueLibraryTab.Actions(_ => { }, _ => { }));

        Assert.DoesNotContain("explore", tab.DetailTextForTest);
        Assert.Contains("on local", tab.DetailTextForTest);
    }
}
