using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What distilling costs: every call to the distiller is counted against its colleague — in the colleague's own record, so
/// the library's total covers every process, and as a run in the usage history, so <c>/stats</c> shows it per colleague,
/// apart from the agent types.
/// </summary>
public sealed class ColleagueSpendTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("spend-").FullName;
    private readonly string _project;
    private readonly MockLlmProvider _model = new("distil-model");

    public ColleagueSpendTests()
    {
        _project = Path.Combine(_config, "invoice");
        Directory.CreateDirectory(_project);
        Library.Create(new ColleagueRecord { Name = "Charting", Created = DateTimeOffset.UtcNow });
    }

    public void Dispose()
    {
        try { Directory.Delete(_config, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private void Round() =>
        Library.Append("Charting", new PastRound(0, "S", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = new string('c', 600) }]));

    private void Answer(string text, int input, int output, string stop = "end_turn") =>
        _model.EnqueueResponse(new LlmResponse { Text = text, StopReason = stop, Usage = new LlmUsage { InputTokens = input, OutputTokens = output } });

    private async Task<List<DistillSpend>> Drain()
    {
        var spent = new List<DistillSpend>();
        var distiller = new Distiller(Library);
        distiller.Spent += spent.Add;
        await distiller.DrainAllAsync(_ => _ => _model, CancellationToken.None);
        return spent;
    }

    [Fact]
    public async Task A_distilling_call_raises_its_spend()
    {
        Round();
        Answer("<core>\n## Lessons\nPanes first.\n</core>", 1200, 300);

        var spent = await Drain();

        var one = Assert.Single(spent);
        Assert.Equal(("Charting", "distil-model", 1200, 300), (one.Colleague, one.ModelId, one.InputTokens, one.OutputTokens));
        Assert.Equal(new DistillTotals { Calls = 1, InputTokens = 1200, OutputTokens = 300 }, Library.Find("Charting")!.Spend);
    }

    [Fact]
    public async Task A_shorten_ask_is_a_second_call()
    {
        Round();
        Answer("<core>\n" + new string('x', (ExperienceBudget.CoreCap + 500) * 4) + "\n</core>", 1000, 9000);
        Answer("<core>\n## Lessons\nShort.\n</core>", 1500, 100);

        await Drain();

        Assert.Equal(new DistillTotals { Calls = 2, InputTokens = 2500, OutputTokens = 9100 }, Library.Find("Charting")!.Spend);
    }

    /// <summary>A cut-off answer is refused whole — and was still paid for, at its full length.</summary>
    [Fact]
    public async Task A_cut_off_answer_still_counts()
    {
        Round();
        Answer("<core>\n## Lessons\nPanes", 800, 4096, stop: "max_tokens");

        var spent = await Drain();

        Assert.Equal(4096, Assert.Single(spent).OutputTokens);
        Assert.Equal(1, Library.Find("Charting")!.Spend.Calls);
    }

    [Fact]
    public void Distilling_is_its_own_stats_section()
    {
        var at = DateTimeOffset.UtcNow;
        RunRecord Run(string type, string model, int tokens) => new(Guid.NewGuid().ToString("N"), "s", type, model, tokens, 0, 1, 0, "completed", at, 10, null);
        var runs = new List<RunRecord> { Run("explore", "m", 500), Run("distill:Charting", "d", 1000), Run("distill:Charting", "d", 300) };

        Assert.DoesNotContain(StatsQuery.ByType(runs), t => t.Type.StartsWith(StatsQuery.DistillPrefix, StringComparison.Ordinal));
        var charting = Assert.Single(StatsQuery.Distilling(runs));
        Assert.Equal(("Charting", 2, 1300L), (charting.Colleague, charting.Calls, charting.Tokens));

        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7,
            Totals = new StatsTotals(1, 10, 10, 0, 0, 0, 0, 0),
            Projects = [], Models = [], Types = StatsQuery.ByType(runs), Tools = [], Daily = [],
            Distilling = StatsQuery.Distilling(runs),
        });
        Assert.Contains("Distilling", text);
        Assert.Contains("Charting", text);
    }

    [Fact]
    public async Task The_library_shows_the_total()
    {
        Round();
        Answer("<core>\n## Lessons\nPanes first.\n</core>", 1200, 300);
        await Drain();

        var facts = Assert.Single(LibraryView.Build(Library)).Facts;

        Assert.Equal(1, facts.Spend.Calls);
        Assert.Contains("distilling: 1 call", LibraryView.FactsLine(facts));
    }

    [Fact]
    public async Task The_desk_saves_a_run_per_call()
    {
        Round();
        Answer("<core>\n## Lessons\nPanes first.\n</core>", 1200, 300);
        var saved = new List<RunRecord>();
        using var desk = new ColleagueDesk(new ColleagueDeskParts
        {
            Library = Library,
            Workers = new SubAgentStore(),
            Folder = _project,
            SessionId = () => "session-1",
            NoteWorker = _ => { },
            Models = _ => new ModelChoice(_model, "main:distil-model", 200_000, "main"),
            SaveRun = saved.Add,
        });

        await desk.Idle();

        var run = Assert.Single(saved);
        Assert.Equal(("distill:Charting", "main:distil-model", 1200, 300, "session-1"),
            (run.TypeName, run.ModelId, run.InputTokens, run.OutputTokens, run.ParentAgentId));
    }

    /// <summary>A long name keeps its gap: "SharpConsoleUI Guide" ran into its call count.</summary>
    [Fact]
    public void A_long_colleague_name_does_not_run_into_its_count()
    {
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 1,
            Totals = new StatsTotals(1, 10, 10, 0, 0, 0, 0, 0),
            Projects = [], Models = [], Types = [], Tools = [], Daily = [],
            Distilling = [new DistillStat("SharpConsoleUI Guide", 5, 35_500, []),
                new DistillStat("An Unusually Long Colleague Name For Layouts", 1, 100, [])],
        });

        Assert.Matches(@"SharpConsoleUI Guide +5 ", text);
        Assert.DoesNotContain("An Unusually Long Colleague Name For Layouts", text);
        Assert.Contains("…", text);
    }
}
