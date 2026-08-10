using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The command table is the ONE source: the dispatcher, the unknown-command reply, the help text and
/// (later) a palette all read it. Each of those used to carry its own copy — the reply hardcoded
/// "/clear, /compress" and would have gone stale on the next addition.
/// </summary>
public class SessionCommandTableTests
{
    [Fact]
    public void EveryCommandStartsWithASlashAndSaysWhatItDoes()
    {
        // A palette row is a name and a summary; a command with an empty summary is an empty row.
        Assert.NotEmpty(SessionCommands.All);
        Assert.All(SessionCommands.All, c =>
        {
            Assert.StartsWith("/", c.Name, System.StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(c.Summary), $"{c.Name} has no summary");
        });
    }

    [Fact]
    public void MatchIsCaseInsensitiveAndIgnoresArguments()
    {
        // "/clear now please" still matches — the first whitespace-delimited token is the command.
        Assert.Equal("/clear", SessionCommands.Match("/CLEAR")?.Name);
        Assert.Equal("/clear", SessionCommands.Match("/clear now please")?.Name);
    }

    [Fact]
    public void ProseIsNeverACommand()
    {
        // The rule that matters most: a false positive silently wipes a session instead of doing
        // what was asked. Only a LEADING slash counts.
        Assert.Null(SessionCommands.Match("clear the build output"));
        Assert.Null(SessionCommands.Match("what does /clear do?"));
    }

    [Fact]
    public void AnUnknownCommandListsTheRealOnes()
    {
        var handled = SessionCommands.TryHandle("/celar", new List<ChatMessage>(), out var reply);

        Assert.True(handled, "an unrecognised slash is a command attempt, not a goal");
        foreach (var c in SessionCommands.All)
            Assert.Contains(c.Name, reply, System.StringComparison.Ordinal);
    }

    [Fact]
    public void CompressIsTheProviderCommand()
    {
        // There was an IsCompress probe, called BEFORE TryHandle because compressing needs a
        // provider the sync handler cannot reach. The fact now lives ON the command, so the
        // dispatcher reads one outcome instead of running checks in a significant order.
        Assert.Equal(CommandOutcome.NeedsProvider, SessionCommands.Match("/compress")?.Outcome);
        Assert.Equal(CommandOutcome.Handled, SessionCommands.Match("/clear")?.Outcome);
    }

    [Fact]
    public void ExitIsTheOnlyQuitCommand()
    {
        Assert.Equal("/exit", Assert.Single(
            SessionCommands.All.Where(c => c.Outcome == CommandOutcome.Quit)).Name);
    }

    [Fact]
    public void MatchingFiltersByPrefix_ForCompletionAndAPalette()
    {
        // The seam a TabCompleter and a palette both consume.
        Assert.Equal(SessionCommands.All.Count, SessionCommands.Matching("/").Count);
        Assert.Equal("/clear", Assert.Single(SessionCommands.Matching("/cl")).Name);
        Assert.Empty(SessionCommands.Matching("/zzz"));
    }

    [Fact]
    public void HelpLinesCoverEveryCommand()
    {
        var help = SessionCommands.HelpLines("cyan");

        foreach (var c in SessionCommands.All)
        {
            Assert.Contains(c.Name, help, System.StringComparison.Ordinal);
            Assert.Contains(c.Summary, help, System.StringComparison.Ordinal);
        }
    }
    [Fact]
    public void EveryCommandHasARealOutcome()
    {
        // NotACommand is what Match returns for prose; a command in the TABLE carrying it would be
        // unreachable — recognised by the matcher and dispatched by nobody.
        Assert.All(SessionCommands.All,
            c => Assert.NotEqual(CommandOutcome.NotACommand, c.Outcome));
    }

    [Fact]
    public void EveryOutcomeInTheTableIsOneTheDispatcherHandles()
    {
        // THE INVARIANT A MENU RESTS ON. Dispatch is a switch over the outcome, so a command whose
        // outcome has no branch falls through and runs as a GOAL — the model receives "/model gpt-4"
        // as a task. This is the check that catches a new outcome added to the enum but not to the
        // switch, which is the one mistake this design makes easy.
        //
        // Kept as an explicit list rather than reflected off the enum: the point is to fail when the
        // two drift, and a test that derives both sides from one of them cannot.
        var dispatched = new[]
        {
            CommandOutcome.Handled,
            CommandOutcome.NeedsProvider,
            CommandOutcome.NeedsWindow,
            CommandOutcome.Quit,
        };

        Assert.All(SessionCommands.All, c => Assert.Contains(c.Outcome, dispatched));
    }

    // ---- /mcp ----------------------------------------------------------------------------------

    /// <summary>/mcp is a real command with a description, like the others — it joins the table
    /// rather than being special-cased, so help and any future palette list it for free.</summary>
    [Fact]
    public void McpIsInTheCommandTable()
    {
        var mcp = Assert.Single(SessionCommands.All.Where(c => c.Name == "/mcp"));
        Assert.False(string.IsNullOrWhiteSpace(mcp.Summary));
    }

    private static CxAgent.Core.Mcp.McpServerStatus Server(string name, bool enabled = true,
        int tools = 2, string? error = null) => new(name, enabled, tools, error);

    /// <summary>
    /// It reports each server's state and tool count — AND the error when one failed, which is the
    /// whole reason to have the command rather than reading the panel. A panel row is 24 columns and
    /// cannot carry "npx: command not found".
    /// </summary>
    [Fact]
    public void Mcp_ReportsEachServersStateAndError()
    {
        var text = SessionCommands.DescribeMcp([
            Server("context7", tools: 2),
            Server("broken", tools: 0, error: "npx: command not found"),
        ]);

        Assert.Contains("context7", text, StringComparison.Ordinal);
        Assert.Contains("2 tools", text, StringComparison.Ordinal);
        Assert.Contains("broken", text, StringComparison.Ordinal);
        Assert.Contains("npx: command not found", text, StringComparison.Ordinal);
    }

    /// <summary>A disabled server is reported as disabled rather than omitted. Unlike the panel,
    /// this command is what someone runs BECAUSE a tool is missing — "it is switched off" is the
    /// answer they came for, and omitting it sends them to read config for no reason.</summary>
    [Fact]
    public void Mcp_ReportsADisabledServerAsDisabled()
    {
        var text = SessionCommands.DescribeMcp([Server("off", enabled: false, tools: 0)]);

        Assert.Contains("off", text, StringComparison.Ordinal);
        Assert.Contains("disabled", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>With nothing configured it says so and points at Settings, rather than printing an
    /// empty list that reads like a bug.</summary>
    [Fact]
    public void Mcp_WithNoServers_SaysHowToAddOne()
    {
        var text = SessionCommands.DescribeMcp([]);

        Assert.Contains("no mcp servers", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("settings", text, StringComparison.OrdinalIgnoreCase);
    }
}
