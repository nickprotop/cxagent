using CxAgent.Core.Commands;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The arithmetic and the rendering, both pure. Neither needs a database, a session or a terminal —
/// which is the reason the store, the query and the dashboard are three types rather than one.
/// </summary>
public class StatsTests
{
    private static DateTimeOffset Ago(int h) => DateTimeOffset.UtcNow.AddHours(-h);

    private static SessionRecord S(string id, int input, int output, int sub = 0,
        string? dir = "/home/nick/source/cxgpu", string model = "qwen3.6-35b.gguf", int turns = 5) =>
        new(id, dir, model, "fan-out", input, output, sub, turns, Ago(2), Ago(1));

    private static RunRecord R(string id, string type, int input, int output, int turns,
        string outcome = "completed") =>
        new(id, "parent", type, "qwen3.6-35b.gguf", input, output, turns, 4, outcome,
            Ago(1), 60_000, "/home/nick/source/cxgpu");

    private static ToolCallRecord C(string id, string tool, int chars, string outcome = "succeeded") =>
        new(id, "a1", tool, "file", outcome, 20, chars, Ago(1), "/home/nick/source/cxgpu");

    // ---- totals --------------------------------------------------------------------------------

    [Fact]
    public void Totals_SumsEverySession()
    {
        var t = StatsQuery.Totals([S("a", 1000, 100), S("b", 2000, 200)]);

        Assert.Equal(2, t.Sessions);
        Assert.Equal(3300, t.TotalTokens);
        Assert.Equal(3000, t.InputTokens);
    }

    /// <summary>
    /// THE WORKER SHARE IS NULL WHEN NOTHING WAS SPENT, not zero. Zero renders as "0% to workers",
    /// which reads as "delegation was tried and did nothing" rather than "there is no data" — the
    /// same distinction occupancy makes when a provider has not yet reported usage.
    /// </summary>
    [Fact]
    public void WorkerShare_IsNull_WhenNothingWasSpent()
    {
        Assert.Null(StatsQuery.Totals([S("a", 0, 0)]).WorkerShare);
        Assert.Equal(0.8, StatsQuery.Totals([S("a", 900, 100, sub: 800)]).WorkerShare!.Value, 3);
    }

    // ---- grouping ------------------------------------------------------------------------------

    /// <summary>A session with no working directory is still spend. Dropping it would make the
    /// per-project rows stop summing to the headline, which is worse than an "(unknown)" row.</summary>
    [Fact]
    public void ByProject_KeepsUnattributedSessions()
    {
        var rows = StatsQuery.ByProject([S("a", 100, 0), S("b", 50, 0, dir: null)]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Project == "(unknown)" && r.Tokens == 50);
    }

    [Fact]
    public void ByProject_OrdersByTokensDescending()
    {
        var rows = StatsQuery.ByProject([
            S("a", 100, 0, dir: "/small"),
            S("b", 9000, 0, dir: "/big"),
        ]);

        Assert.Equal("/big", rows[0].Project);
    }

    [Fact]
    public void ByModel_SplitsInputAndOutput()
    {
        var rows = StatsQuery.ByModel([S("a", 900, 100), S("b", 100, 20)]);

        var row = Assert.Single(rows);
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(120, row.OutputTokens);
        Assert.Equal(2, row.Sessions);
    }

    // ---- agent types ---------------------------------------------------------------------------

    /// <summary>
    /// CAPPED IS COUNTED SEPARATELY FROM FAILED. Seen live: an explore child spent all 30 of its
    /// turns hunting a JSON schema that is not published anywhere, then filled its last turns
    /// re-reading local files. It did not fail — it ran out of room, which is a fact about the
    /// briefing rather than about the work, and folding it into "completed" hides the one run worth
    /// finding later.
    /// </summary>
    [Fact]
    public void ByType_CountsCappedApartFromFailed()
    {
        var rows = StatsQuery.ByType([
            R("r1", "explore", 100, 10, 12),
            R("r2", "explore", 900, 90, 30, outcome: "capped"),
            R("r3", "explore", 50, 5, 3, outcome: "failed"),
        ]);

        var explore = Assert.Single(rows);
        Assert.Equal(3, explore.Runs);
        Assert.Equal(1, explore.Capped);
        Assert.Equal(1, explore.Failed);
        Assert.Equal(15.0, explore.AvgTurns, 1);
    }

    [Fact]
    public void ByType_OrdersByTokens()
    {
        var rows = StatsQuery.ByType([
            R("r1", "explore", 100, 10, 4),
            R("r2", "planner", 40_000, 2_000, 7),
        ]);

        Assert.Equal("planner", rows[0].Type);
    }

    // ---- tools ---------------------------------------------------------------------------------

    /// <summary>
    /// TOOLS RANK BY CHARACTERS RETURNED, NOT BY CALL COUNT. A turn re-sends everything before it, so
    /// a tool returning large results costs its size again on every later turn — forty cheap calls
    /// are not the problem, one 16k result forty times is.
    /// </summary>
    [Fact]
    public void ByTool_RanksByContextCost_NotFrequency()
    {
        var calls = new List<ToolCallRecord>();
        for (var i = 0; i < 40; i++) calls.Add(C($"l{i}", "list_files", 80));
        calls.Add(C("h1", "http_request", 60_000));

        var rows = StatsQuery.ByTool(calls);

        Assert.Equal("http_request", rows[0].Tool);
        Assert.Equal(40, rows[1].Calls);            // more calls, less cost, ranked lower
    }

    [Fact]
    public void ByTool_CountsFailures()
    {
        var rows = StatsQuery.ByTool([
            C("a", "read_file", 100),
            C("b", "read_file", 0, outcome: "failed"),
        ]);

        Assert.Equal(1, Assert.Single(rows).Failed);
    }

    // ---- daily ---------------------------------------------------------------------------------

    /// <summary>
    /// QUIET DAYS ARE ZEROES, NOT GAPS. A sparkline that omits empty days compresses the timeline and
    /// makes a week with one busy day look like a week of constant work.
    /// </summary>
    [Fact]
    public void ByDay_FillsEmptyDays()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var days = StatsQuery.ByDay([S("a", 500, 0)], today.AddDays(-6), today);

        Assert.Equal(7, days.Count);
        Assert.Equal(today, days[^1].Day);          // oldest first, today last
        Assert.Contains(days, d => d.Tokens == 0);
    }

    // ---- housekeeping --------------------------------------------------------------------------

    [Fact]
    public void Compaction_SumsReclaimedAndCountsManual()
    {
        var (runs, reclaimed, manual) = StatsQuery.Compaction([
            new("a1", Ago(2), 190_000, 40_000, "pressure", null),
            new("a1", Ago(1), 90_000, 30_000, "manual", null),
        ]);

        Assert.Equal(2, runs);
        Assert.Equal(210_000, reclaimed);
        Assert.Equal(1, manual);
    }

    /// <summary>Silent allows are not "asked". A session of stored rules must not look like a session
    /// of decisions the user actually reviewed.</summary>
    [Fact]
    public void Permissions_KeepSilentOutOfTheAskedCount()
    {
        var (asked, allowed, denied, silent) = StatsQuery.Permissions([
            new("a", Ago(1), "Shell", "silent", null, null),
            new("a", Ago(1), "Shell", "allowed", null, null),
            new("a", Ago(1), "Http", "denied", null, null),
        ]);

        Assert.Equal(2, asked);
        Assert.Equal(1, allowed);
        Assert.Equal(1, denied);
        Assert.Equal(1, silent);
    }

    // ---- rendering -----------------------------------------------------------------------------

    /// <summary>
    /// NOTHING RECORDED IS ITS OWN MESSAGE. An empty dashboard reads as "you have done nothing";
    /// history that starts at this version is the actual state, and saying so stops a user
    /// concluding the feature is broken on their first run.
    /// </summary>
    [Fact]
    public void Render_WithNoHistory_SaysSo_RatherThanShowingEmptySections()
    {
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7, Totals = StatsQuery.Totals([]),
            Projects = [], Models = [], Types = [], Tools = [], Daily = [],
        });

        Assert.Contains("No sessions recorded", text, StringComparison.Ordinal);
        Assert.DoesNotContain("By project", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ShowsTheHeadlineAndTheWorkerShare()
    {
        var sessions = new[] { S("a", 900_000, 20_000, sub: 800_000) };
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7,
            Totals = StatsQuery.Totals(sessions),
            Projects = StatsQuery.ByProject(sessions),
            Models = StatsQuery.ByModel(sessions),
            Types = [], Tools = [], Daily = [],
        });

        Assert.Contains("920,000", text, StringComparison.Ordinal);
        Assert.Contains("to workers", text, StringComparison.Ordinal);
        Assert.Contains("87%", text, StringComparison.Ordinal);
    }

    /// <summary>The dashboard is markup, and every tag it opens it closes — an unbalanced tag leaks
    /// its colour into the rest of the transcript.</summary>
    [Fact]
    public void Render_ProducesBalancedMarkup()
    {
        var sessions = new[] { S("a", 900, 100, sub: 400) };
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7,
            Totals = StatsQuery.Totals(sessions),
            Projects = StatsQuery.ByProject(sessions),
            Models = StatsQuery.ByModel(sessions),
            Types = StatsQuery.ByType([R("r1", "planner", 400, 40, 7)]),
            Tools = StatsQuery.ByTool([C("c1", "read_file", 5_000)]),
            Daily = StatsQuery.ByDay(sessions, DateOnly.FromDateTime(DateTime.Now).AddDays(-6),
                DateOnly.FromDateTime(DateTime.Now)),
            Compaction = (1, 150_000, 0),
            Permissions = (2, 1, 1, 3),
        });

        var opens = text.Split('[').Length - 1;
        var closes = text.Split("[/]").Length - 1;

        // Every opening tag has a matching [/]; the closers are themselves counted as opens above.
        Assert.Equal(closes * 2, opens);
    }

    [Fact]
    public void Bar_IsProportional()
    {
        Assert.DoesNotContain('█', StatsDashboard.Bar(0, 10));
        Assert.Contains(new string('█', 5), StatsDashboard.Bar(0.5, 10), StringComparison.Ordinal);
        Assert.Contains(new string('█', 10), StatsDashboard.Bar(1, 10), StringComparison.Ordinal);
    }

    /// <summary>
    /// A BAR NEVER OVERFLOWS ITS WIDTH, whatever it is handed. Fractions come from division, and a
    /// dashboard that crashes — or emits a 400-character line — because a denominator was zero would
    /// be a statistics feature breaking the session it reports on.
    /// </summary>
    [Theory]
    [InlineData(5.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Bar_ClampsAnythingItIsGiven(double fraction)
    {
        var bar = StatsDashboard.Bar(fraction, 10);

        // Ten cells, whatever they are: filled, half, or track.
        var cells = bar.Count(c => c is '█' or '▌' or '─');
        Assert.Equal(10, cells);
    }

    // ---- the command's window ------------------------------------------------------------------

    [Fact]
    public void ParseDays_DefaultsToAWeek()
    {
        Assert.Equal(7, StatsCommand.ParseDays(null));
        Assert.Equal(7, StatsCommand.ParseDays("   "));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("30d", 30)]
    [InlineData("1", 1)]
    public void ParseDays_ReadsANumber(string arg, int expected) =>
        Assert.Equal(expected, StatsCommand.ParseDays(arg));

    /// <summary>Out of range is CLAMPED rather than rejected: "/stats 9999" is a user asking for
    /// everything, and a usage message instead of their history would be pedantry.</summary>
    [Fact]
    public void ParseDays_ClampsRatherThanRejecting()
    {
        Assert.Equal(3650, StatsCommand.ParseDays("99999"));
        Assert.Equal(1, StatsCommand.ParseDays("0"));
        Assert.Equal(7, StatsCommand.ParseDays("nonsense"));
    }

    [Fact]
    public void ParseDays_UnderstandsAll() =>
        Assert.Equal(3650, StatsCommand.ParseDays("all"));

    /// <summary>
    /// WHAT IT ALL COST, across sessions. The per-session figure answers "this run"; /stats answers
    /// "this week", which is the question that decides whether a workflow is affordable.
    /// </summary>
    [Fact]
    public void Render_ShowsTheTotalCost_WhenSessionsReportedOne()
    {
        var totals = new StatsTotals(2, 900_000, 20_000, 0, 40,
            Cost: 0.0147m, CostReportingSessions: 2);

        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7, Totals = totals,
            Projects = [], Models = [], Types = [], Tools = [], Daily = [],
        });

        Assert.Contains("$0.0147", text, StringComparison.Ordinal);
    }

    /// <summary>NOTHING REPORTED, NOTHING SHOWN — local-only history must not read "$0.00".</summary>
    [Fact]
    public void Render_WithNoCostReported_ShowsNoCostLine()
    {
        var totals = new StatsTotals(2, 900_000, 20_000, 0, 40);

        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7, Totals = totals,
            Projects = [], Models = [], Types = [], Tools = [], Daily = [],
        });

        Assert.DoesNotContain("$", text, StringComparison.Ordinal);
    }
}
