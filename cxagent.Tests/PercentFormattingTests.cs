using System.Globalization;
using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A rate renders the same on every machine.
///
/// <para>FOUND BY A RELEASE, NOT BY A TEST RUN. Eight call sites formatted rates with <c>:P0</c>,
/// which uses the CURRENT CULTURE's percent pattern — and several cultures, including the invariant
/// one a CI runner uses, put a non-breaking space before the sign. The suite passed on a developer
/// machine and failed twice on the runner, looking for "94% cache" in text that said "94 %".</para>
///
/// <para>The assertions were right and the formatting was wrong: a user in one locale saw a
/// different panel from a user in another, and nothing said so.</para>
/// </summary>
public class PercentFormattingTests
{
    [Fact]
    public void ARateRendersWithNoSpaceBeforeTheSign()
    {
        Assert.Equal("94%", StatsDashboard.Percent(0.94));
        Assert.Equal("0%", StatsDashboard.Percent(0));
        Assert.Equal("100%", StatsDashboard.Percent(1));
    }

    [Theory]
    [InlineData("")]            // invariant — what a CI runner uses
    [InlineData("en-US")]
    [InlineData("de-DE")]       // comma decimal separator
    [InlineData("fr-FR")]       // narrow no-break space before % under :P0
    [InlineData("el-GR")]
    public void EveryCultureAgrees(string culture)
    {
        // THE CULTURE IS SET ON THIS THREAD, which is what :P0 reads. Restored in a finally so a
        // failure here cannot leak a culture into whatever xunit runs next on this thread.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("94%", StatsDashboard.Percent(0.94));
            Assert.DoesNotContain(" ", StatsDashboard.Percent(0.94));

            // NON-BREAKING AND NARROW NO-BREAK SPACES EXPLICITLY. They are what :P0 actually emitted,
            // and a plain " " check does not catch either — which is why this reached a release.
            Assert.DoesNotContain(" ", StatsDashboard.Percent(0.94));
            Assert.DoesNotContain(" ", StatsDashboard.Percent(0.94));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ItRoundsRatherThanTruncating()
    {
        // 0.876 is 87.6%, and a dashboard read for scale should say 88 rather than 87.
        Assert.Equal("88%", StatsDashboard.Percent(0.876));
        Assert.Equal("87%", StatsDashboard.Percent(0.871));
    }
}
