using CxAgent.Core.Sessions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The side panel's per-turn sparklines (user, 2026-11-11): the lead's tokens, the workers' on a row of their own, the
/// cache rate and the context, one point per lead response, each under a label with its latest value.
/// </summary>
public sealed class TurnChartsTests
{
    private static TurnPoint Point(int tokens, int workers, double? cache, int context) =>
        new(DateTimeOffset.UtcNow, tokens, workers, cache, context, 200_000);

    [Fact]
    public void Nothing_shows_before_the_first_turn()
    {
        var charts = new TurnCharts();
        charts.Update([], 30);

        Assert.False(charts.Control.Visible);
    }

    [Fact]
    public void Each_series_is_drawn_with_its_latest_value()
    {
        var charts = new TurnCharts();
        charts.Update([Point(10_500, 0, 0.8, 10_000), Point(12_400, 3_200, null, 12_000)], 30);

        Assert.True(charts.Control.Visible);
        Assert.Contains("tokens · last 12.4k", charts.Text);
        Assert.Contains("workers · last 3.2k", charts.Text);
        Assert.Contains("cache · last 80%", charts.Text);   // the latest REPORTED rate
        Assert.Contains("context · last 6%", charts.Text);
        Assert.Equal([10_500, 12_400], charts.DataFor("tokens"));
        Assert.Equal([0.8, 0], charts.DataFor("cache"));
        Assert.Equal([0.05, 0.06], charts.DataFor("context"));
    }

    /// <summary>NO WORKERS ROW until a worker spent something: an empty chart is a row saying nothing.</summary>
    [Fact]
    public void The_workers_row_waits_for_a_worker()
    {
        var charts = new TurnCharts();
        charts.Update([Point(1_000, 0, null, 900)], 30);

        Assert.DoesNotContain("workers", charts.Text);
    }

    /// <summary>A long session shows its newest turns, as many as the panel is wide.</summary>
    [Fact]
    public void Only_as_many_turns_as_fit_are_drawn()
    {
        var charts = new TurnCharts();
        charts.Update([.. Enumerable.Range(1, 100).Select(i => Point(i, 0, null, i))], 20);

        Assert.Equal(Enumerable.Range(81, 20).Select(i => (double)i), charts.DataFor("tokens"));
    }
}
