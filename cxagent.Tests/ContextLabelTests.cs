using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The status bar's context readout.
///
/// <para>These exist because the readout was wrong in a way nobody could see from the code: it divided
/// the CUMULATIVE token total by the context window. That total sums input and output over every turn,
/// and every turn re-sends the whole conversation, so it grows quadratically — a real session showed
/// 107% of a window that was nowhere near full. Worse, a cumulative counter cannot decrease, so
/// compressing the context (the one operation whose purpose is to free it) moved the number not at
/// all, leaving the user unable to tell whether it had worked.</para>
/// </summary>
public class ContextLabelTests
{
    /// <summary>
    /// THE REGRESSION. Occupancy well under the window must never read as over it, however much the
    /// session has spent in total — these are the numbers from the reported bug.
    /// </summary>
    [Fact]
    public void PercentageIsOccupancyNotCumulativeSpend()
    {
        // 40k of a 208k window is 19%, while the session has spent 223k in total — which is what
        // used to be divided by the window to produce "107%".
        var label = MainWindow.ContextLabelForTest(used: 40_000, spent: 223_000, window: 208_000);

        Assert.Contains("19%", label);
        Assert.DoesNotContain("107%", label);
    }

    /// <summary>Cumulative spend is still reported — it is a real question, just not this percentage.</summary>
    [Fact]
    public void SpendIsShownSeparately()
    {
        var label = MainWindow.ContextLabelForTest(used: 40_000, spent: 223_000, window: 208_000);

        Assert.Contains("223,000 spent", label);
        Assert.Contains("40,000/208,000", label);
    }

    /// <summary>
    /// A compression must be VISIBLE. The true post-compression figure is unknowable until the next
    /// turn reports usage, so the last reading is marked approximate rather than silently kept.
    /// </summary>
    [Fact]
    public void StaleReadingIsMarkedApproximate()
    {
        var fresh = MainWindow.ContextLabelForTest(used: 40_000, spent: 223_000, window: 208_000);
        var stale = MainWindow.ContextLabelForTest(used: 40_000, spent: 223_000, window: 208_000, stale: true);

        Assert.DoesNotContain("~", fresh);
        Assert.Contains("~19%", stale);
    }

    /// <summary>
    /// A compression must say something MEANINGFUL, not just cast doubt on the old number — and now
    /// it can say both what happened AND where that leaves the context, because AgentContext scales
    /// its own reading by the character ratio compaction measured. The fraction is marked approximate,
    /// since it is arithmetic on a ratio rather than a measurement.
    /// </summary>
    [Fact]
    public void StaleReadingShowsWhatTheCompressionDidAndAnEstimate()
    {
        var label = MainWindow.ContextLabelForTest(used: 12_000, spent: 223_000, window: 208_000,
            stale: true, delta: "compressed −70%");

        Assert.Contains("compressed −70%", label);
        Assert.Contains("~12,000/208,000", label);
    }

    /// <summary>A stale reading with no delta still degrades to the fraction rather than showing nothing.</summary>
    [Fact]
    public void StaleWithoutDeltaKeepsTheFraction()
    {
        var label = MainWindow.ContextLabelForTest(used: 40_000, spent: 223_000, window: 208_000,
            stale: true);

        Assert.Contains("40,000/208,000", label);
    }

    /// <summary>
    /// A percentage needs a denominator, and a guessed one is worse than none: with no known window
    /// the raw figure is shown rather than a confident-looking fraction of an invented scale.
    /// </summary>
    [Fact]
    public void NoWindowMeansNoPercentage()
    {
        var label = MainWindow.ContextLabelForTest(used: 40_000, spent: 223_000, window: null);

        Assert.DoesNotContain("%", label);
        Assert.Contains("40,000", label);
    }

    /// <summary>Before any turn has reported usage there is no occupancy to show — only spend.</summary>
    [Fact]
    public void NoUsageYetShowsSpendOnly()
    {
        var label = MainWindow.ContextLabelForTest(used: null, spent: 1_200, window: 208_000);

        Assert.DoesNotContain("ctx", label);
        Assert.Contains("1,200 spent", label);
    }

    /// <summary>Nothing measured and nothing spent is an empty readout, not a row of zeroes.</summary>
    [Fact]
    public void NothingKnownRendersNothing()
    {
        Assert.Equal(string.Empty, MainWindow.ContextLabelForTest(used: null, spent: 0, window: 208_000));
    }
}
