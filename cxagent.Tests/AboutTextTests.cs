using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What <c>/about</c> says.
///
/// <para>THE RULE UNDER TEST IS THAT A LINE CARRIES A FACT OR IS ABSENT. Every "unknown", "n/a" or
/// bare zero in a diagnostic reads as breakage to the person who asked, and the cases that produce
/// them — no install date, an unreadable store, a fresh install with nothing to report — are exactly
/// the ones nobody sees while building it.</para>
/// </summary>
public class AboutTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static Installation Install(
        DateTimeOffset? firstSeen = null, int launches = 5, string? upgradedFrom = null) =>
        new(IsFirstRun: false,
            UpgradedFrom: upgradedFrom,
            Version: "0.9.14",
            CoreVersion: "0.9.14",
            FirstSeen: firstSeen ?? Now.AddDays(-90),
            LaunchCount: launches,
            Path: "/opt/cxagent",
            UiVersion: "2.6.5",
            Runtime: ".NET 10.0.9",
            Os: "Ubuntu 26.04",
            Architecture: "x64");

    private static string Render(Installation install, AboutText.Usage? usage = null,
                                 IReadOnlyList<string>? plugins = null) =>
        AboutText.Render(install, "/home/x/.config/cxagent", usage, plugins ?? [], Now);

    [Fact]
    public void ItLeadsWithTheVersion()
    {
        var text = Render(Install());

        Assert.StartsWith("## cxagent 0.9.14", text, StringComparison.Ordinal);
    }

    /// <summary>ALL THREE VERSIONS, including Core's when it matches the app's. The release stamps
    /// both from one tag, so a match is the healthy case being reported — and the day they differ is
    /// a development build against a stale Core, which is the thing worth seeing.</summary>
    [Fact]
    public void ItReportsEveryVersionItRunsOn()
    {
        var text = Render(Install());

        Assert.Contains("CxAgent.Core 0.9.14", text, StringComparison.Ordinal);
        Assert.Contains("SharpConsoleUI 2.6.5", text, StringComparison.Ordinal);
        Assert.Contains(".NET 10.0.9 on Ubuntu 26.04", text, StringComparison.Ordinal);
    }

    /// <summary>THE UPGRADE CLAUSE IS ABSENT WHEN THERE WAS NO UPGRADE. A fresh install saying
    /// "updated from" nothing would be a sentence with a hole in it.</summary>
    [Fact]
    public void ItMentionsAnUpgradeOnlyWhenThereWasOne()
    {
        Assert.DoesNotContain("updated from", Render(Install()), StringComparison.Ordinal);
        Assert.Contains("updated from 0.9.13",
            Render(Install(upgradedFrom: "0.9.13")), StringComparison.Ordinal);
    }

    /// <summary>A FIRST LAUNCH READS AS ONE, not "1 launches" — the line a brand new user sees is
    /// the one most likely to be read closely.</summary>
    [Fact]
    public void TheFirstLaunchReadsProperly()
    {
        Assert.Contains("1st launch", Render(Install(launches: 1)), StringComparison.Ordinal);
        Assert.Contains("47 launches", Render(Install(launches: 47)), StringComparison.Ordinal);
    }

    /// <summary>Dates are said the way a person would say them, with the calendar date kept for
    /// anything old enough that "3 months ago" invites checking.</summary>
    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(3, "3 days ago")]
    [InlineData(90, "3 months ago")]
    public void ItSaysWhenInWordsAPersonUses(int daysAgo, string expected)
    {
        var text = Render(Install(firstSeen: Now.AddDays(-daysAgo)));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    /// <summary>AN UNREADABLE STORE DROPS THE LINE. "0 sessions" would be a confident lie to someone
    /// whose history merely failed to open.</summary>
    [Fact]
    public void UnreadableUsageIsOmittedRatherThanZeroed()
    {
        var text = Render(Install(), usage: null);

        Assert.DoesNotContain("Sessions", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItReportsUsageWhenItHasIt()
    {
        var text = Render(Install(), usage: new AboutText.Usage(128, 2431));

        Assert.Contains("128 sessions", text, StringComparison.Ordinal);
        Assert.Contains("2,431 tool calls", text, StringComparison.Ordinal);
    }

    /// <summary>NO PLUGINS SAYS SO, unlike every other absence here: "are any running" is the
    /// question, and a missing row answers it with silence where a stated none answers it.</summary>
    [Fact]
    public void NoPluginsIsStatedRatherThanOmitted()
    {
        var text = Render(Install(), plugins: []);

        Assert.Contains("none loaded", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItNamesTheLoadedPlugins()
    {
        var text = Render(Install(), plugins: ["calculator", "clone-finder"]);

        Assert.Contains("2 loaded", text, StringComparison.Ordinal);
        Assert.Contains("calculator, clone-finder", text, StringComparison.Ordinal);
    }

    /// <summary>A LONG LIST IS CAPPED. The count already said how many; the names answer "which",
    /// which the first few do without turning the row into a paragraph.</summary>
    [Fact]
    public void ALongPluginListIsCapped()
    {
        var text = Render(Install(), plugins: ["a", "b", "c", "d", "e", "f"]);

        Assert.Contains("+2 more", text, StringComparison.Ordinal);
        Assert.DoesNotContain(", f", text, StringComparison.Ordinal);
    }

    /// <summary>Home is written as ~, because the full path is noise the reader has to skip past to
    /// see the part that says how the app was installed.</summary>
    [Fact]
    public void ItWritesHomeAsATilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = AboutText.Render(Install(), Path.Combine(home, ".config", "cxagent"),
            usage: null, plugins: [], Now);

        Assert.Contains("~", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(home, ".config"), text, StringComparison.Ordinal);
    }
}
