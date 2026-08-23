using CxAgent.Core.Commands;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The command table is the ONE source: the dispatcher, the unknown-command reply, the help text and
/// (later) a palette all read it. Give any of them its own copy — a reply hardcoding
/// "/clear, /compress" — and it goes stale on the next addition, silently.
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
    public void CompressIsTheProviderCommand()
    {
        // The fact lives ON the command as NeedsModel, so a caller reads one flag instead of
        // running an ordered probe before dispatch.
        Assert.True(SessionCommands.Match("/compress")?.NeedsModel);
        Assert.False(SessionCommands.Match("/clear")?.NeedsModel);
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
        var help = SessionCommands.HelpLines();

        foreach (var c in SessionCommands.All)
        {
            Assert.Contains(c.Name, help, System.StringComparison.Ordinal);
            Assert.Contains(c.Summary, help, System.StringComparison.Ordinal);
        }
    }
    // ---- /mcp ----------------------------------------------------------------------------------

    /// <summary>/mcp is a real command with a description, like the others — it joins the table
    /// rather than being special-cased, so help and any future palette list it for free.</summary>
    [Fact]
    public void McpIsInTheCommandTable()
    {
        var mcp = Assert.Single(SessionCommands.All, c => c.Name == "/mcp");
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
    // ---- arguments -----------------------------------------------------------------------------

    /// <summary>
    /// THE REST OF THE LINE IS AVAILABLE, not discarded.
    ///
    /// <para>Match splits on the first space and returns the command, so everything after it is easy
    /// to drop on the floor — invisibly, while no command takes an argument. That is this codebase's
    /// recurring rot pattern: a value that parses and is never read. <c>/mcp login &lt;server&gt;</c>
    /// needs it.</para>
    /// </summary>
    [Fact]
    public void Arguments_ReturnsTheRestOfTheLine()
    {
        Assert.Equal("context7", SessionCommands.Arguments("/mcp context7"));
        Assert.Equal("login context7", SessionCommands.Arguments("/mcp login context7"));
    }

    /// <summary>No argument is empty, never null — a caller that has to null-check before splitting
    /// is a caller that will forget once.</summary>
    [Fact]
    public void Arguments_WithNoArgument_IsEmpty()
    {
        Assert.Equal("", SessionCommands.Arguments("/mcp"));
        Assert.Equal("", SessionCommands.Arguments("  /mcp   "));
    }

    /// <summary>Surrounding whitespace is the user's typing, not their intent.</summary>
    [Fact]
    public void Arguments_AreTrimmed()
    {
        Assert.Equal("context7", SessionCommands.Arguments("/mcp    context7   "));
    }

    /// <summary>Split into words for a subcommand plus its target, with the empty run dropped so
    /// double spaces do not produce a phantom argument.</summary>
    [Fact]
    public void ArgumentWords_SplitsOnWhitespace()
    {
        Assert.Equal(["login", "context7"], SessionCommands.ArgumentWords("/mcp  login   context7"));
        Assert.Empty(SessionCommands.ArgumentWords("/mcp"));
    }
    // ---- /mcp subcommands ----------------------------------------------------------------------

    /// <summary>Bare /mcp still lists servers — the common case must not require a subcommand.</summary>
    [Fact]
    public void DescribeMcp_BareCommand_ListsServers()
    {
        var text = SessionCommands.DescribeMcp([Server("context7", tools: 2)], "");

        Assert.Contains("context7", text, StringComparison.Ordinal);
        Assert.Contains("2 tools", text, StringComparison.Ordinal);
    }

    /// <summary>`/mcp list` is the explicit form of the same thing, so someone who guesses at a
    /// subcommand is not told they are wrong for guessing.</summary>
    [Fact]
    public void DescribeMcp_List_IsTheSameAsBare()
    {
        Assert.Equal(SessionCommands.DescribeMcp([Server("c7")], ""),
                     SessionCommands.DescribeMcp([Server("c7")], "list"));
    }

    /// <summary>
    /// `/mcp <server>` shows ONE server in detail — its tools by name.
    ///
    /// <para>The list answers "is it running"; this answers "what can it actually do", which is the
    /// question behind "why did the model not use my server". A tool that never appears because its
    /// name collided is invisible in the summary and named here.</para>
    /// </summary>
    [Fact]
    public void DescribeMcp_WithAServerName_ShowsThatServersTools()
    {
        var text = SessionCommands.DescribeMcp(
            [Server("context7", tools: 2)], "context7",
            toolNames: ["context7_resolve-library-id", "context7_query-docs"]);

        Assert.Contains("context7_query-docs", text, StringComparison.Ordinal);
    }

    /// <summary>An unknown server says so and lists the real ones, rather than printing nothing.</summary>
    [Fact]
    public void DescribeMcp_WithAnUnknownServer_SaysSoAndListsTheRealOnes()
    {
        var text = SessionCommands.DescribeMcp([Server("context7")], "typo");

        Assert.Contains("typo", text, StringComparison.Ordinal);
        Assert.Contains("context7", text, StringComparison.Ordinal);
    }

    /// <summary>An unrecognised subcommand names what IS understood — a wrong guess should teach the
    /// right one, not just fail — and lists the real servers, since a mistyped server name is one of
    /// the likeliest ways to get here.</summary>
    [Fact]
    public void DescribeMcp_WithAnUnknownSubcommand_ShowsUsage()
    {
        var text = SessionCommands.DescribeMcp([Server("c7")], "frobnicate the thing");

        Assert.Contains("reload", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c7", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// `reload` reports the RESULT, not a second announcement.
    ///
    /// <para>The caller owns the servers, so it performs the reload and says "Reloading…" itself,
    /// then calls this to show what came back. Returning another "reloading" here printed the same
    /// sentence twice — caught on a live drive, where it read as the reload having run twice.</para>
    /// </summary>
    [Fact]
    public void DescribeMcp_Reload_ReportsTheResultRatherThanAnnouncingAgain()
    {
        var text = SessionCommands.DescribeMcp([Server("c7", tools: 2)], "reload");

        Assert.DoesNotContain("Reloading", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 tools", text, StringComparison.Ordinal);
    }
    /// <summary>
    /// NEEDS-AUTH IS NOT A FAILURE, and reads differently. Nothing is broken — the server is waiting
    /// to be logged in to — and calling it "failed" sends someone to check their config instead of
    /// typing the one command that fixes it. The line carries that command.
    /// </summary>
    [Fact]
    public void DescribeMcp_AServerNeedingAuth_SaysHowToLogIn()
    {
        var text = SessionCommands.DescribeMcp(
            [new CxAgent.Core.Mcp.McpServerStatus("remote", true, 0, "not authorized", NeedsAuth: true)], "");

        Assert.Contains("/mcp login remote", text, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The usage line offers login, so someone who mistypes learns it exists.</summary>
    [Fact]
    public void DescribeMcp_UsageMentionsLogin()
    {
        var text = SessionCommands.DescribeMcp([Server("c7")], "frobnicate");

        Assert.Contains("login", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- arguments -----------------------------------------------------------------------------

    /// <summary>
    /// EVERY SUBCOMMAND THE DISPATCHER ACCEPTS IS IN THE TABLE. They existed in the handlers and in
    /// no surface a user could find — /help rendered name-plus-summary, and the palette closed the
    /// moment a space was typed. This is the list those surfaces now read from, so a subcommand added
    /// to a handler and not to the table is a subcommand nobody will discover.
    /// </summary>
    [Theory]
    [InlineData("/mcp", "reload")]
    [InlineData("/mcp", "login")]
    [InlineData("/mcp", "show")]
    [InlineData("/mode", "agent")]
    [InlineData("/mode", "edits")]
    [InlineData("/stats", "clear")]
    [InlineData("/stats", "all")]
    public void TheTable_CarriesEverySubcommand(string command, string argument)
    {
        var c = SessionCommands.All.Single(x => x.Name == command);

        // MATCHED ON THE LEADING VERB, because a row's name may carry its placeholder —
        // `show <name>`, `resume <number|id>`, `agent single`. What this pins is that the dispatcher
        // and the table agree on which subcommands EXIST, not on how a row is worded.
        Assert.Contains(c.Args, a => a.Name == argument || a.Name.StartsWith(argument + " ", StringComparison.Ordinal));
    }

    /// <summary>A command that takes nothing says so, so the hint is meaningful when it appears.</summary>
    [Fact]
    public void ACommandWithoutArguments_HasNoHint()
    {
        var clear = SessionCommands.All.Single(c => c.Name == "/clear");

        Assert.False(clear.TakesArguments);
        Assert.Equal("", clear.Hint);
    }

    /// <summary>The hint is the shell's own convention for "optional, one of these", so a reader who
    /// has used any CLI already knows how to read it.</summary>
    [Fact]
    public void ACommandWithArguments_HintsThemCompactly()
    {
        var stats = SessionCommands.All.Single(c => c.Name == "/stats");

        Assert.Equal("[<days>|all|clear]", stats.Hint);
    }

    /// <summary>
    /// A PLACEHOLDER IS NOT COMPLETABLE. <c>&lt;server&gt;</c> names a shape, not a value; filling
    /// the composer with angle brackets would put text there that is not a command. The row still
    /// exists, because the argument does.
    /// </summary>
    [Fact]
    public void PlaceholderArguments_AreMarkedNonCompleting()
    {
        var mcp = SessionCommands.All.Single(c => c.Name == "/mcp");

        // `show <name>` CARRIES ITS PLACEHOLDER IN THE NAME, the same shape as
        // `/sessions resume <number|id>` — the row reads as the whole phrase and the live server
        // list fills the blank, so completing it would put "<name>" in the composer literally.
        Assert.False(mcp.Args.Single(a => a.Name == "show <name>").Completes);
        Assert.True(mcp.Args.Single(a => a.Name == "reload").Completes);
    }

    // A ROW CARRYING A PLACEHOLDER IS SELECTABLE, and completes to the verb in front of it. Leave it
    // display-only and the level behind it is unreachable except by typing the verb by hand: the
    // server list behind `show <name>` works, but the palette shows the word and refuses to insert
    // it. Completing the whole name would put "<name>" in the composer literally, which is what the
    // non-completing flag prevents; completing the prefix ends in a space, which is what makes the
    // palette open its next level.
    [Theory]
    [InlineData("/mcp ", "show <name>", "/mcp show ")]
    [InlineData("/mcp ", "login <name>", "/mcp login ")]
    [InlineData("/sessions ", "resume <number|id>", "/sessions resume ")]
    public void PlaceholderRows_CompleteToTheVerbBeforeThePlaceholder(
        string typed, string rowName, string expected)
    {
        var command = SessionCommands.All.Single(c => c.Name == typed.TrimEnd());
        var argument = command.Args.Single(a => a.Name == rowName);

        Assert.Equal(expected, CommandMenu.CompletionFor(argument, typed.TrimEnd()));
    }

    // A COMMAND THAT IS A PREFIX OF ANOTHER IS STILL REACHABLE. /mode is a strict prefix of /model,
    // and table order put the longer one first — so typing the whole of /mode offered /model above
    // it and Enter completed to the wrong command. The shorter one could not be selected at all
    // without arrowing past a name that already matched exactly.
    [Fact]
    public void Matching_PutsAnExactMatchFirst()
    {
        var hits = SessionCommands.Matching("/mode");

        Assert.Equal("/mode", hits[0].Name);
        Assert.Contains(hits, c => c.Name == "/model");
    }

    // AND EVERYTHING ELSE KEEPS THE TABLE'S ORDER, which is the order /help prints and the one a
    // reader has already learned.
    [Fact]
    public void Matching_WithNoExactMatch_KeepsTheTableOrder()
    {
        var hits = SessionCommands.Matching("/mod");

        Assert.Equal(
            SessionCommands.All.Where(c => c.Name.StartsWith("/mod", StringComparison.Ordinal))
                               .Select(c => c.Name),
            hits.Select(c => c.Name));
    }

    // ---- the palette's second level ------------------------------------------------------------

    /// <summary>After a command and a space, the palette offers that command's arguments — the
    /// gesture the user already learned, one level down.</summary>
    [Fact]
    public void ArgumentsFor_AfterASpace_OffersTheCommandsArguments()
    {
        var args = SessionCommands.ArgumentsFor("/mcp ");

        Assert.Contains(args, a => a.Name == "reload");
        Assert.Contains(args, a => a.Name == "login <name>");
    }

    [Fact]
    public void ArgumentsFor_NarrowsAsTheUserTypes()
    {
        var args = SessionCommands.ArgumentsFor("/mcp re");

        Assert.Equal("reload", Assert.Single(args).Name);
    }

    /// <summary>
    /// A SECOND SPACE ENDS IT. "/mcp login serv" is naming a server, not still choosing between
    /// login and reload — a palette that kept offering subcommands there would be answering a
    /// question the user has already moved past.
    /// </summary>
    [Fact]
    public void ArgumentsFor_StopsOnceTheArgumentIsTyped() =>
        Assert.Empty(SessionCommands.ArgumentsFor("/mcp login context7"));

    /// <summary>
    /// ...UNLESS SOMETHING CAN SUPPLY THE VALUES. `/sessions resume ` is a second space whose right
    /// answer is a short list the app has on hand, and the supplier is how the composition root
    /// offers it without this table learning what a session is.
    /// </summary>
    [Fact]
    public void ArgumentsFor_WithASupplier_OffersLiveValuesPastTheSecondSpace()
    {
        var supplied = new SuppliedValues(ValueSources.Sessions,
            [new("1", "7f3a2b  2h ago  add the arity table"), new("2", "01kzwz  yesterday  why")]);

        var values = SessionCommands.ArgumentsFor("/sessions resume ", supplied.Supplier);

        Assert.Equal(["1", "2"], values.Select(v => v.Name));
    }

    [Fact]
    public void ArgumentsFor_WithASupplier_NarrowsAsTheValueIsTyped()
    {
        var supplied = new SuppliedValues(ValueSources.Sessions,
            [new("1", "one"), new("2", "two"), new("12", "twelve")]);

        var values = SessionCommands.ArgumentsFor("/sessions resume 1", supplied.Supplier);

        Assert.Equal(["1", "12"], values.Select(v => v.Name));
    }

    /// <summary>The supplier is asked for the subcommand actually typed, not for the command.</summary>
    [Fact]
    public void ArgumentsFor_WithASupplier_AsksAboutTheSubcommandItIsUnder()
    {
        var supplied = new SuppliedValues(ValueSources.Sessions, [new("1", "one")]);

        Assert.Empty(SessionCommands.ArgumentsFor("/sessions all ", supplied.Supplier));
    }

    /// <summary>Three words in: the value is typed, and there is nothing left to choose.</summary>
    [Fact]
    public void ArgumentsFor_WithASupplier_StopsOnceTheValueIsTyped()
    {
        var supplied = new SuppliedValues(ValueSources.Sessions, [new("1", "one")]);

        Assert.Empty(SessionCommands.ArgumentsFor("/sessions resume 1 extra", supplied.Supplier));
    }

    [Fact]
    public void ArgumentsFor_WithoutASupplier_StillStopsAtTheSecondSpace() =>
        Assert.Empty(SessionCommands.ArgumentsFor("/sessions resume "));

    /// <summary>A value supplier for one source, handed to ArgumentsFor by the caller — the menu
    /// owns it per instance, so there is no static for a test to leak through.</summary>
    private sealed class SuppliedValues
    {
        private readonly string _source;
        private readonly IReadOnlyList<CommandArgument> _values;

        public SuppliedValues(string forSource, IReadOnlyList<CommandArgument> values) =>
            (_source, _values) = (forSource, values);

        /// <summary>The supplier a caller passes in, so nothing static needs resetting between
        /// tests.</summary>
        public Func<string, IReadOnlyList<CommandArgument>> Supplier =>
            source => source == _source ? _values : [];

    }

    [Fact]
    public void ArgumentsFor_ACommandWithoutArguments_OffersNothing() =>
        Assert.Empty(SessionCommands.ArgumentsFor("/clear "));

    [Fact]
    public void ArgumentsFor_WithoutASpace_OffersNothing() =>
        Assert.Empty(SessionCommands.ArgumentsFor("/mcp"));

    /// <summary>An unknown command has no arguments to offer — the unknown-command reply on submit
    /// is what tells the user they mistyped.</summary>
    [Fact]
    public void ArgumentsFor_AnUnknownCommand_OffersNothing() =>
        Assert.Empty(SessionCommands.ArgumentsFor("/frobnicate "));

    // ---- help ----------------------------------------------------------------------------------

    /// <summary>
    /// /help LISTS SUBCOMMANDS. This is the defect that started this: every subcommand was reachable
    /// and none was documented anywhere the app itself would show you.
    /// </summary>
    [Fact]
    public void HelpLines_ListEverySubcommand()
    {
        var help = SessionCommands.HelpLines();

        Assert.Contains("/mcp reload", help, StringComparison.Ordinal);
        Assert.Contains("/stats clear", help, StringComparison.Ordinal);
        Assert.Contains("/mode agent <mode>", help, StringComparison.Ordinal);
    }

    /// <summary>Each subcommand line carries its own summary, so the list explains rather than
    /// merely enumerates.</summary>
    [Fact]
    public void HelpLines_ExplainEachSubcommand()
    {
        var help = SessionCommands.HelpLines();

        Assert.Contains("re-read config.json", help, StringComparison.Ordinal);
        Assert.Contains("delete all usage history", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// /help IS A MARKDOWN TABLE. The System role renders markdown, so the box-drawn table it used
    /// to build would have reached the screen as its own literal markup — palette parameters and all.
    /// </summary>
    [Fact]
    public void HelpLines_IsAMarkdownTable()
    {
        var help = SessionCommands.HelpLines();

        Assert.Contains("| command | what it does |", help, StringComparison.Ordinal);
        Assert.Contains("|---", help, StringComparison.Ordinal);

        // No box drawing and no markup tag survives — either one is a rendering bug on a markdown row.
        Assert.DoesNotContain("┌", help, StringComparison.Ordinal);
        Assert.DoesNotContain("│", help, StringComparison.Ordinal);
        Assert.DoesNotContain("[/]", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NAME IS ESCAPED, A SUMMARY IS NOT — the asymmetry this table depends on.
    ///
    /// <para>A name is a literal spelling, so its underscores and asterisks must not become
    /// emphasis. It is escaped for PROSE, not for cells: the name sits inside a code span, which
    /// already hides its pipe from the table parser, and a cell escape's backslash would survive
    /// the span and show on screen. A summary is markdown authored in SessionCommands itself, so
    /// escaping it would turn its intended inline code into punctuation.</para>
    /// </summary>
    [Fact]
    public void HelpLines_EscapesNamesButLeavesSummariesAsMarkdown()
    {
        var help = SessionCommands.HelpLines();

        Assert.Contains("`/sessions resume <number|id>`", help, StringComparison.Ordinal);
        Assert.Contains("a name from `providers` in config", help, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\`", help, StringComparison.Ordinal);
    }
}
