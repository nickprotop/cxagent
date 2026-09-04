using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a stall leaves behind. The framework detects an unresponsive main loop and shows a banner;
/// without this line the diagnosis it computed — the phase, and a label for the callback that was
/// executing — is displayed and discarded, and a hang leaves a session log that simply stops.
/// </summary>
public class WatchdogLineTests
{
    private static readonly DateTime When = new(2026, 9, 4, 11, 32, 07, DateTimeKind.Utc);

    [Fact]
    public void ItNamesTheStallAndWhereItWas()
    {
        var line = AppBootstrap.WatchdogLine(When, TimeSpan.FromSeconds(8.4), "Render", "TabControl.Paint");

        Assert.Contains("2026-09-04 11:32:07Z", line);
        Assert.Contains("8.4s", line);
        Assert.Contains("phase Render", line);
        Assert.Contains("in TabControl.Paint", line);
    }

    // A MISSING LABEL IS A FINDING, NOT AN OMISSION: it says the loop stalled outside any callback
    // the framework could name, which points somewhere different than a stall inside a named one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnknownFrameSaysSo(string? blockedIn)
        => Assert.Contains("no frame label",
            AppBootstrap.WatchdogLine(When, TimeSpan.FromSeconds(3), "Idle", blockedIn));

    // THE SEPARATOR IS NOT THE MACHINE'S. A bare :F1 renders "8,4s" under a comma-decimal culture,
    // which reads as a group separator to anyone expecting the other.
    [Fact]
    public void TheDurationDoesNotFollowTheCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            Assert.Contains("8.4s",
                AppBootstrap.WatchdogLine(When, TimeSpan.FromSeconds(8.4), "Render", null));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // ONE LINE, so a log of several stalls reads as several stalls.
    [Fact]
    public void ItIsASingleLine()
        => Assert.DoesNotContain('\n',
            AppBootstrap.WatchdogLine(When, TimeSpan.FromSeconds(2), "Input", "x"));
}
