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
        var counts = StatsQuery.Permissions([
            new("a", Ago(1), "Shell", "silent", null, null),
            new("a", Ago(1), "Shell", "allowed", null, null),
            new("a", Ago(1), "Http", "denied", null, null),
        ]);

        Assert.Equal(2, counts.Asked);
        Assert.Equal(1, counts.Allowed);
        Assert.Equal(1, counts.Denied);
        Assert.Equal(1, counts.Silent);
    }

    [Fact]
    public void Permissions_CountsTheAutoDecisionsSeparately()
    {
        // THE BUG: auto-allowed and auto-refused are written by PermissionDecider and match none of
        // the four buckets, so they are stored and then dropped. A user cannot answer "is auto mode
        // helping?" from data the app already collects.
        var rows = new List<PermissionRecord>
        {
            new("a", DateTimeOffset.UtcNow, "Shell", "allowed", null),
            new("a", DateTimeOffset.UtcNow, "Shell", "denied", null),
            new("a", DateTimeOffset.UtcNow, "Shell", "silent", null),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-allowed", null),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-refused", null),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-denied", null),
        };

        var counts = StatsQuery.Permissions(rows);

        Assert.Equal(2, counts.Asked);          // allowed + denied — a human answered
        Assert.Equal(1, counts.Allowed);
        Assert.Equal(1, counts.Denied);
        Assert.Equal(1, counts.Silent);
        Assert.Equal(1, counts.AutoAllowed);
        Assert.Equal(1, counts.AutoRefused);
        Assert.Equal(1, counts.AutoDenied);
    }

    [Fact]
    public void Permissions_AutoDecisionsAreNotCountedAsAsked()
    {
        // A classifier decision is not a question the user answered. Collapsing them would make auto
        // mode look like a session of choices, which is the distinction the `silent` bucket already
        // exists to preserve.
        var rows = new List<PermissionRecord>
        {
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-allowed", null),
        };

        Assert.Equal(0, StatsQuery.Permissions(rows).Asked);
    }

    /// <summary>
    /// FLAGGED IS COUNTED ONLY OVER THE THREE AUTO DECISIONS, and only rows explicitly true — a
    /// human's own "allowed"/"denied" and a stored "silent" rule never touched the classifier and
    /// must not contribute to either side of the rate, even if some other bug ever set Flagged on
    /// one of them.
    /// </summary>
    [Fact]
    public void Permissions_CountsOnlyFlaggedAutoDecisions()
    {
        var rows = new List<PermissionRecord>
        {
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-allowed", null, Flagged: true),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-allowed", null, Flagged: false),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-refused", null, Flagged: true),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-denied", null, Flagged: false),
            new("a", DateTimeOffset.UtcNow, "Shell", "allowed", null),   // never touched the classifier
        };

        Assert.Equal(2, StatsQuery.Permissions(rows).Flagged);
    }

    /// <summary>
    /// A NULL FLAGGED — a row written before the column existed — MUST NOT COUNT AS FLAGGED. Treating
    /// "unknown" as "yes" would inflate the rate with history that predates the question.
    /// </summary>
    [Fact]
    public void Permissions_NullFlaggedDoesNotCountAsFlagged()
    {
        var rows = new List<PermissionRecord>
        {
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-allowed", null, Flagged: null),
        };

        Assert.Equal(0, StatsQuery.Permissions(rows).Flagged);
    }

    /// <summary>
    /// CLASSIFIED COUNTS ONLY NON-NULL FLAGGED AUTO ROWS — the same population Flagged draws its
    /// numerator from. A row that never touched the classifier, or an auto row that predates the
    /// Flagged column, must not appear in this denominator, or the resulting rate understates how
    /// often triage actually escalates within the rows that can answer the question.
    /// </summary>
    [Fact]
    public void Permissions_ClassifiedCountsOnlyNonNullFlaggedAutoRows()
    {
        var rows = new List<PermissionRecord>
        {
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-allowed", null, Flagged: true),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-allowed", null, Flagged: false),
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-refused", null, Flagged: null),   // legacy row
            new("a", DateTimeOffset.UtcNow, "Shell", "auto-denied", null, Flagged: null),    // legacy row
            new("a", DateTimeOffset.UtcNow, "Shell", "allowed", null),   // never touched the classifier
        };

        var counts = StatsQuery.Permissions(rows);

        Assert.Equal(1, counts.Flagged);
        Assert.Equal(2, counts.Classified);
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

    /// <summary>
    /// EVERY FENCE THE DASHBOARD OPENS IT CLOSES. An unterminated ``` does not merely tint what
    /// follows the way an unclosed markup tag would — it swallows it, and the whole transcript below
    /// the dashboard disappears into a code block.
    /// </summary>
    [Fact]
    public void Render_ClosesEveryFenceItOpens()
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
            Permissions = new(2, 1, 1, 3, 0, 0, 0, 0, 0),
        });

        var fences = text.Split("```").Length - 1;

        // Sections are drawn only when they have data, so the count varies with the view — what
        // cannot vary is that it is EVEN, and that a fence is actually present to check.
        Assert.True(fences > 0, "the dashboard draws bars, so it must fence them");
        Assert.Equal(0, fences % 2);

        // And no colour tag reaches the output. The guard test sweeps Core's SOURCE for these; this
        // one checks the RENDERED text, which is where a tag assembled from parts would show up.
        Assert.DoesNotContain("[/]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RATE, RENDERED WHERE THE READER IS ALREADY LOOKING — on the existing "auto review" line,
    /// because whether stage two is earning its cost is a property of the auto decisions already
    /// being reported there, not a new fact needing its own line.
    /// </summary>
    [Fact]
    public void Render_ShowsTheTriageFlagRateOnTheAutoReviewLine()
    {
        var sessions = new[] { S("a", 900, 100, sub: 0) };
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7, Totals = StatsQuery.Totals(sessions),
            Projects = StatsQuery.ByProject(sessions), Models = StatsQuery.ByModel(sessions),
            Types = [], Tools = [], Daily = [],
            // 4 auto decisions, all 4 classified (no legacy rows in this sample), 1 flagged — a
            // reader must be able to see 25% without doing the division themselves.
            Permissions = new(0, 0, 0, 0, 3, 1, 0, 1, 4),
        });

        Assert.Contains("auto review 4 decided", text, StringComparison.Ordinal);
        Assert.Contains("25% triage-flagged", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RATE'S DENOMINATOR IS THE CLASSIFIED POPULATION, NOT THE RAW AUTO TOTAL. A real database
    /// can hold auto decisions written before the Flagged column existed — their Flagged is null, and
    /// StatsQuery.Permissions already excludes them from the Flagged count. If the dashboard divided
    /// by the auto total instead, those same legacy rows would still dilute the denominator, reporting
    /// a rate lower than the classified sample actually shows. Found live against a real database: 33
    /// legacy auto rows with null Flagged, none contributing to the numerator but all inflating a
    /// naive denominator.
    /// </summary>
    [Fact]
    public void Render_TriageFlagRate_DividesByClassifiedNotByAutoTotal()
    {
        var sessions = new[] { S("a", 900, 100, sub: 0) };
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7, Totals = StatsQuery.Totals(sessions),
            Projects = StatsQuery.ByProject(sessions), Models = StatsQuery.ByModel(sessions),
            Types = [], Tools = [], Daily = [],
            // 10 auto decisions total, but only 2 are classified (Flagged non-null) and 1 of those
            // is flagged. Dividing by the auto total (10) would read 10%; the honest answer, over the
            // only population that can speak to it, is 50%.
            Permissions = new(0, 0, 0, 0, 8, 2, 0, 1, 2),
        });

        Assert.Contains("50% triage-flagged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10% triage-flagged", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// NOTHING MEASURED YET IS NOT THE SAME FACT AS "NEVER FLAGGED", so it must not render as 0% —
    /// that would claim triage screens out everything, when the true state is that every auto decision
    /// on record predates the column that would say so. This is the user's own database's shape today.
    /// </summary>
    [Fact]
    public void Render_WithNoClassifiedAutoDecisions_OmitsTheRateButKeepsTheCounts()
    {
        var sessions = new[] { S("a", 900, 100, sub: 0) };
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7, Totals = StatsQuery.Totals(sessions),
            Projects = StatsQuery.ByProject(sessions), Models = StatsQuery.ByModel(sessions),
            Types = [], Tools = [], Daily = [],
            // Auto decisions exist, but every one predates triage-flag telemetry — Classified is 0.
            Permissions = new(0, 0, 0, 0, 29, 4, 0, 0, 0),
        });

        Assert.Contains("auto review 33 decided", text, StringComparison.Ordinal);
        Assert.DoesNotContain("triage-flagged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONTROL: a session with zero auto decisions renders BYTE-IDENTICALLY whether or not this
    /// feature exists. Nothing about triage-flag telemetry may leak into a transcript for a user who
    /// never enabled auto mode.
    /// </summary>
    [Fact]
    public void Render_WithNoAutoDecisions_ShowsNoTriageFlagRate()
    {
        var sessions = new[] { S("a", 900, 100, sub: 0) };
        var text = StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = 7, Totals = StatsQuery.Totals(sessions),
            Projects = StatsQuery.ByProject(sessions), Models = StatsQuery.ByModel(sessions),
            Types = [], Tools = [], Daily = [],
            Permissions = new(2, 1, 1, 3, 0, 0, 0, 0, 0),
        });

        Assert.DoesNotContain("auto review", text, StringComparison.Ordinal);
        Assert.DoesNotContain("triage-flagged", text, StringComparison.Ordinal);
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
    }

    /// <summary>
    /// AN ARGUMENT NOBODY CAN READ IS NOT A WINDOW. Falling back to the default silently answers a
    /// question the user did not ask: "/stats clear" renders seven days of history to someone who
    /// asked to delete it, and nothing on screen says the deletion did not happen.
    /// </summary>
    [Theory]
    [InlineData("nonsense")]
    [InlineData("clear")]
    [InlineData("-3")]
    public void ParseDays_RefusesWhatItCannotRead(string argument) =>
        Assert.Null(StatsCommand.ParseDays(argument));

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
