using Xunit;
using CxAgent.UI;

namespace CxAgent.Tests;

/// <summary>
/// The plugin-facing text a user reads: the startup notice, and the Plugins dialog's details pane.
/// Both were wrong in ways that only show up as clutter — repeated blocks, repeated words, ragged
/// lines — which is exactly what a string-level test can pin.
/// </summary>
public class PluginNoticeTests
{
    private static PluginDiscovery.UnconfiguredPlugin Plugin(string name) =>
        new(name, name + ".dll", "/home/me/.config/cxagent/plugins/" + name, 3);

    [Fact]
    public void NothingFoundSaysNothing()
    {
        Assert.Null(PluginDiscovery.FormatUnconfiguredNotice([]));
    }

    [Fact]
    public void OneFindKeepsItsExactConfigEntry()
    {
        var notice = PluginDiscovery.FormatUnconfiguredNotice([Plugin("csharp-lsp")])!;

        Assert.Contains("\"csharp-lsp\": { \"file\": \"csharp-lsp.dll\" }", notice);
        Assert.Contains("/plugin load csharp-lsp.dll", notice);
    }

    /// <summary>Four finds were four five-line blocks repeating one set of instructions.</summary>
    [Fact]
    public void SeveralFindsAreOneBlockWithTheHowToSaidOnce()
    {
        var notice = PluginDiscovery.FormatUnconfiguredNotice(
            [Plugin("a"), Plugin("b"), Plugin("c"), Plugin("d")])!;

        Assert.Equal(1, CountOf(notice, "config.json"));
        Assert.Equal(1, CountOf(notice, "/plugin load"));
        foreach (var name in new[] { "a", "b", "c", "d" })
            Assert.Contains($"    {name} — ", notice);
        // A header, one line each, and the two how-to lines.
        Assert.True(notice.Split('\n').Length <= 4 + 3, notice);
    }

    [Fact]
    public void ALongListIsCappedAndTheRestCounted()
    {
        var many = Enumerable.Range(0, 30).Select(i => Plugin("p" + i)).ToList();

        var notice = PluginDiscovery.FormatUnconfiguredNotice(many)!;

        Assert.Contains("and 24 more", notice);
        Assert.True(notice.Split('\n').Length <= 10, notice);
    }

    /// <summary>The line read "not in config · global · not in config".</summary>
    [Fact]
    public void AnUnconfiguredPluginSaysNotInConfigOnce()
    {
        var row = new PluginRow("found", PluginRowSection.Installed, "not in config", Folder: "global");

        var line = PluginManagerDialog.StateLineForTest(row);

        Assert.Equal(1, CountOf(line, "not in config"));
        Assert.Equal("not in config · global", line);
    }

    [Fact]
    public void AnUnconfiguredLoadedPluginSaysItOnceToo()
    {
        var row = new PluginRow("found", PluginRowSection.Installed, "active, not in config", Folder: "project");

        Assert.Equal(1, CountOf(PluginManagerDialog.StateLineForTest(row), "not in config"));
    }

    [Fact]
    public void AHandWrappedDescriptionBecomesOneParagraph()
    {
        var wrapped = "Lets a plan be spread over\nseveral turns and says exactly, in\none call.";

        Assert.Equal("Lets a plan be spread over several turns and says exactly, in one call.",
            PluginManagerDialog.Unwrap(wrapped));
    }

    [Fact]
    public void ParagraphBreaksAndListsSurviveUnwrapping()
    {
        var text = "First para\nstill first.\n\nSecond.\n- one\n- two";

        Assert.Equal("First para still first.\n\nSecond.\n- one\n- two", PluginManagerDialog.Unwrap(text));
    }

    [Fact]
    public void AnInstalledReadme_JoinsItsParagraphs_AndKeepsItsCodeAsWritten()
    {
        // The calculator README, the case that was reported: hand-wrapped prose, a heading, and a
        // fenced block whose two example lines would become one if joined.
        var readme = "# calculator\n\n"
            + "A language model approximates arithmetic, and a calculation spread over\n"
            + "several turns goes wrong — confidently. This does the same work exactly, in\n"
            + "one call.\n\n"
            + "## The one tool\n\n"
            + "```\n"
            + "(1847 * 0.0325) / 12   ->  5.00229166666667\n"
            + "sqrt(144) + 2^10       ->  1036\n"
            + "```\n\n"
            + "**Compose it into one expression.** Three calls is the\n"
            + "problem this solves.";

        var unwrapped = PluginManagerDialog.Unwrap(readme);

        Assert.Contains("a calculation spread over several turns goes wrong — confidently. "
            + "This does the same work exactly, in one call.", unwrapped);
        Assert.Contains("```\n(1847 * 0.0325) / 12   ->  5.00229166666667\nsqrt(144) + 2^10       ->  1036\n```",
            unwrapped);
        Assert.Contains("# calculator\n\n", unwrapped);
        Assert.Contains("## The one tool\n\n", unwrapped);
        Assert.Contains("Three calls is the problem this solves.", unwrapped);
    }

    [Fact]
    public void IndentedLinesHardBreaksAndTables_KeepTheirLines_AndAListItemsContinuationJoins()
    {
        var text = "- an item that wraps\n  onto an indented line\n"
                 + "- an item whose wrap\nis lazy\n\n"
                 + "    indented code\n    more code\n\n"
                 + "a hard break  \nafter it\n\n"
                 + "| a | b |\n|---|---|";

        var unwrapped = PluginManagerDialog.Unwrap(text);

        Assert.Contains("- an item that wraps\n  onto an indented line", unwrapped);
        Assert.Contains("- an item whose wrap is lazy", unwrapped);
        Assert.Contains("    indented code\n    more code", unwrapped);
        Assert.Contains("a hard break  \nafter it", unwrapped);
        Assert.Contains("| a | b |\n|---|---|", unwrapped);
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
