using System.Globalization;
using CxAgent.Core.Helpers;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Every number the panels show renders the same on every machine.
///
/// <para>THE SAME BUG <see cref="PercentFormattingTests"/> DOCUMENTS, in the formats that fix did
/// not cover. <c>Percent</c> was written after a release failed on <c>:P0</c>, but the token counts,
/// magnitudes, durations and money beside it still used <c>:N0</c>, <c>:0.0</c> and <c>:0.#</c> —
/// all of which read the current culture. Under fr-FR the suite failed on assertions looking for
/// "153.1k", "4,441" and "9,007" in text that had rendered them with the other separators.</para>
///
/// <para>THE ASSERTIONS WERE RIGHT AND THE FORMATTING WAS WRONG, the same conclusion as last time:
/// these figures get compared against the README's screenshots, against a provider's documented
/// context window, and against another developer's terminal in an issue report. A separator that
/// changes by machine makes every one of those comparisons quietly wrong.</para>
///
/// <para>NOTE FOR ANYONE RUNNING THESE: <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c> makes every
/// culture invariant, so it would make this file pass no matter what the code did. Use <c>LANG</c>
/// to check a real locale.</para>
/// </summary>
public class NumberFormattingTests
{
    // The cultures that actually break things, rather than a broad sample: fr-FR groups with a
    // narrow no-break space and decimalises with a comma, de-DE swaps the two ASCII separators
    // outright, and the invariant one is what a CI runner uses.
    public static TheoryData<string> Cultures => new() { "", "en-US", "de-DE", "fr-FR", "el-GR" };

    /// <summary>Runs <paramref name="render"/> under a culture, restoring the thread's own in a
    /// finally so a failure cannot leak one into whatever xunit schedules next on this thread.</summary>
    private static string Under(string culture, Func<string> render)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return render();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void GroupedUsesACommaEverywhere(string culture)
    {
        Assert.Equal("4,441", Under(culture, () => DisplayNumber.Grouped(4441)));
        Assert.Equal("9,007", Under(culture, () => DisplayNumber.Grouped(9007)));
        Assert.Equal("0", Under(culture, () => DisplayNumber.Grouped(0)));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void CompactUsesAPointEverywhere(string culture)
    {
        // 153_100 is the reading from the status bar this was found on — "153.1k" or "153,1k"
        // depending on the machine, and the comma form reads as a GROUP separator to anyone
        // expecting the other, which is a number a thousand times too large at a glance.
        Assert.Equal("153.1k", Under(culture, () => DisplayNumber.Compact(153_100)));
        Assert.Equal("6.9k", Under(culture, () => DisplayNumber.Compact(6_900)));
        Assert.Equal("2.5M", Under(culture, () => DisplayNumber.Compact(2_500_000)));
        Assert.Equal("999", Under(culture, () => DisplayNumber.Compact(999)));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void FixedAndTrimmedUseAPointEverywhere(string culture)
    {
        Assert.Equal("2.0", Under(culture, () => DisplayNumber.Fixed(2.0, 1)));       // a duration
        Assert.Equal("0.0147", Under(culture, () => DisplayNumber.Fixed(0.0147m, 4))); // money
        Assert.Equal("1.5", Under(culture, () => DisplayNumber.Trimmed(1.5)));
        Assert.Equal("1", Under(culture, () => DisplayNumber.Trimmed(1.0)));           // no trailing .0
    }

    /// <summary>
    /// THE END-TO-END CHECK, and the one that would have caught this. The helpers above can be
    /// correct while a call site still formats its own number — which is exactly the state this
    /// codebase was in after the percent fix, and how a locale bug survives a green suite.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void TheStatusBarRendersIdenticallyUnderEveryCulture(string culture)
    {
        var label = Under(culture, () => MainWindow.ContextLabelForTest(
            used: 40_000, spent: 223_000, window: 208_000, input: 153_100, output: 6_900));

        Assert.Contains("19%", label);
        Assert.Contains("40,000/208,000", label);
        Assert.Contains("223,000 spent", label);
        Assert.Contains("↑153.1k", label);
        Assert.Contains("↓6.9k", label);

        // THE SEPARATORS THE OTHER CULTURES ACTUALLY EMIT, named explicitly. fr-FR groups with a
        // NARROW NO-BREAK SPACE and de-DE with a period, and neither is caught by looking for a
        // plain " " — the same blind spot that let the percent bug reach a release.
        Assert.DoesNotContain("40 000", label);
        Assert.DoesNotContain("40 000", label);
        Assert.DoesNotContain("40.000", label);
        Assert.DoesNotContain("153,1k", label);
    }
}
