using System.Text.Json;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A colleague at work is shown as one wherever a worker shows — the transcript row, F7, its tab, the panel, the
/// browser's card and view, the background report — from one badge both front ends read.
/// </summary>
public sealed class ColleagueMarkerTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("marker-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("marker-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private Session Open(ILlmProvider? model = null)
    {
        var resolution = ResolvedConfig.ForTesting(model ?? new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    private void Charting() =>
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });

    private async Task<(Session Session, StoredAgent Kept)> Brought()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "charted", StopReason = "end_turn" });
        var session = Open(model);
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "chart it", null, false, session.SessionId!), CancellationToken.None);
        return (session, session.SubAgents.Find("charting")!);
    }

    [Fact]
    public async Task The_store_badges_a_colleague_and_not_an_ordinary_worker()
    {
        var (session, _) = await Brought();
        var plain = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5, Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        var handle = session.SubAgents.Keep(plain, "plain");

        Assert.Equal(new WorkerBadge("Colleague", "Charting", "◆"), session.SubAgents.BadgeOf("charting"));
        Assert.Null(session.SubAgents.BadgeOf(handle));
    }

    [Fact]
    public async Task Rows_carry_the_badge()
    {
        var (session, _) = await Brought();

        var row = Assert.Single(AgentRows.For(session.SubAgents, DateTimeOffset.UtcNow));

        Assert.Equal("Charting", row.Badge?.Name);
    }

    [Fact]
    public void Transcript_row_of_a_bring_says_Colleague()
    {
        var job = new Job
        {
            Id = "j", PlanLocalId = Tool.Colleague, AgentId = "lead", JobType = "llm_agent", DisplayName = "colleague · Charting",
            State = JobState.Running,
        };

        Assert.Equal("Colleague", InlineJobSink.Author(job));
    }

    [Fact]
    public async Task Tab_title_and_card_name_the_colleague()
    {
        var (session, kept) = await Brought();
        var row = AgentRows.Describe(session.SubAgents, kept, DateTimeOffset.UtcNow);

        Assert.Contains("◆ Charting", AgentTab.TitleFor(kept, row, session.SubAgents.BadgeOf(kept.Name)));
        var card = JobsPanel.AgentCardFactsForTest(row!);
        Assert.Equal("Colleague", card[0]);
    }

    /// <summary>A colleague that joined idle has never run, so no row — its tab still names it (a live drive showed "· charting").</summary>
    [Fact]
    public async Task A_tab_names_a_colleague_that_has_not_run_yet()
    {
        Charting();
        var session = Open();
        await session.BringColleagueAsync("Charting");
        var kept = session.SubAgents.Find("charting")!;

        Assert.Null(AgentRows.Describe(session.SubAgents, kept, DateTimeOffset.UtcNow));
        Assert.Equal("◆ Charting", AgentTab.TitleFor(kept, null, session.SubAgents.BadgeOf(kept.Name)));
    }

    /// <summary>
    /// A BRING IS A SPAWN ROW: the lead's colleague call raises the session's spawn event with the child, as the agent
    /// tool does — the event the terminal's transcript and the browser's worker card are both built from.
    /// </summary>
    [Fact]
    public async Task A_bring_raises_the_spawn_event_on_a_worker_row()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse
        {
            StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "c1", Name = Tool.Colleague,
                Arguments = JsonSerializer.SerializeToElement(new { action = "bring", name = "Charting", task = "chart it", background = false }) }],
        });
        model.EnqueueResponse(new LlmResponse { Text = "charted", StopReason = "end_turn" });   // the colleague
        model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });      // the lead
        var session = Open(model);
        var spawned = new List<Core.Agents.SpawnedChild>();
        session.WorkerSpawned += spawned.Add;

        await session.SendAndWait("bring charting in");

        var child = Assert.Single(spawned);
        Assert.Equal("colleague:Charting", child.Child.Origin);
    }

    [Fact]
    public void The_background_report_names_the_colleague()
    {
        Assert.Equal("colleague Charting finished · completed · 4s",
            Core.Agents.BackgroundRunner.Who(new WorkerBadge("Colleague", "Charting", "◆"), "charting") + " finished · completed · 4s");
        Assert.Equal("background agent 'x'", Core.Agents.BackgroundRunner.Who(null, "x"));
    }

    [Fact]
    public void The_browser_draws_the_badge_and_the_row_author()
    {
        Assert.True(CxAgent.Web.Http.StaticAssets.TryGet("/app.js", out var body, out _));
        var js = System.Text.Encoding.UTF8.GetString(body.Span);

        Assert.Contains("m.author", js);
        Assert.Contains("badge", js);
    }
}
