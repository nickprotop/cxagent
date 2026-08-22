using CxAgent.Core.Models;

namespace CxAgent.Core.Commands;

/// <summary>
/// Slash commands that manage a session directly, WITHOUT going through AgentHost — no goal, no
/// provider call, no tokens spent.
///
/// <para>IN CORE, WHERE THE COMMANDS ARE ABOUT. It sat in the UI folder from the start and was
/// already UI-free in fact — a table, a parser and a help renderer, with one reference to the TUI's
/// palette as the only thing binding it. That is now a parameter, so a second front end renders the
/// same help in its own colours rather than carrying a second copy of the table.</para>
///
/// <para>MARKUP IS NOT A UI DEPENDENCY. The text here carries [yellow] and [grey50] the same way it
/// carries words: a format the reader renders, strips or logs. Asking a specific front end for its
/// palette IS a dependency, and that is the one that went.</para>
///
/// Only an exact leading-slash token counts as a command. "clear the build output" must fall through
/// to AgentHost as an ordinary goal — a false positive here would silently wipe a user's session
/// memory instead of doing what they asked.
/// </summary>
public static class SessionCommands
{
    /// <summary>
    /// Every command, in the order help and a palette should list them.
    ///
    /// <para>THE ONE SOURCE. The dispatcher, the unknown-command reply and the help text all read
    /// this; each used to carry its own copy, and the reply's hardcoded "/clear, /compress" would
    /// have gone stale on the next addition. A command palette is a filter over this list plus the
    /// dispatch below — no new mechanism, which is why the data lives here rather than in a switch.</para>
    /// </summary>
    public static readonly IReadOnlyList<SessionCommand> All =
    [
        new("/clear", "wipe the conversation", CommandOutcome.Handled),
        new("/compress", "summarise the conversation to free up room", CommandOutcome.NeedsProvider),
        // NeedsWindow, not Handled: the live server state belongs to the session, and this type
        // deliberately holds nothing but the conversation. The caller has the servers and formats
        // them through DescribeMcp below.
        new("/mcp", "list MCP servers, inspect one, or reload config", CommandOutcome.NeedsWindow,
        [
            new("reload", "re-read config.json and reconnect"),
            // VERB AND PLACEHOLDER IN ONE NAME, the same shape as `/sessions resume <number|id>`:
            // the row reads as the whole phrase, and the live server list fills the blank. Not
            // completable, because filling the composer with the literal "<name>" puts text there
            // that is not a command.
            new("login <name>", "authorise a server that needs OAuth", Completes: false,
                Values: ValueSources.McpServers),
            // COMPLETABLE NOW. This said listing servers "would couple this static table to session
            // state" — true when the composition root resolved every value by hand, and no longer
            // true: the table names a SET and the manager, which owns the toolset, answers for it.
            // The table still holds no state.
            //
            // THE LIVE SERVERS, not the names in config: one that failed to connect offers no tools,
            // and completing to it would send the user somewhere empty.
            new("show <name>", "inspect one server", Completes: false,
                Values: ValueSources.McpServers),
        ]),
        // NeedsWindow like /mcp: discovery reads the session's working directory, and this type
        // holds nothing but the conversation. NO SUBCOMMANDS — it lists, it does not load, and it is
        // not a refresh: skills are re-read every turn, so an edited one is already live.
        new("/skills", "list available skills, and any SKILL.md that was skipped",
            CommandOutcome.NeedsWindow),
        // NeedsWindow like /mcp and /skills: it answers from the catalog the session already built,
        // so it costs no turn and no tokens.
        //
        // IT EXISTS BECAUSE THE BRIEFINGS LEFT config.json. Reading what a type is told used to mean
        // opening your own config file; the shipped five are code now, and CONFIG.md is a poor
        // substitute when a drive has just gone wrong in front of you.
        new("/agents", "the sub-agent types this session can spawn, and what each one is told",
            CommandOutcome.NeedsWindow,
        [
            // COMPLETABLE NOW, for the reason /mcp's row is: this said the names "are session state,
            // and listing them in this static table would couple the two" — true when the
            // composition root resolved every value by hand, and no longer true. The table names a
            // SET; the session, which holds the catalog it was wired against, answers for it.
            new("show <name>", "the full briefing that type is given", Completes: false,
                Values: ValueSources.AgentTypes),
        ]),
        // NeedsTurn, alone among these: it costs tokens and takes time, because the agent has to go
        // and look at the project. Every other command here answers from state the app already holds.
        new("/init", "write the project instruction file this agent reads each session",
            CommandOutcome.NeedsTurn),
        // NeedsWindow: it shells out to git in the session's working directory, which this type
        // does not hold. FOR THE USER, NOT THE MODEL — the output goes to the transcript, and
        // whether the agent should be able to diff its own work is a separate, larger question.
        new("/diff", "what has changed in the working tree", CommandOutcome.NeedsWindow,
        [
            new("--staged", "what is staged, for people who stage as they go"),
            // NOT COMPLETABLE: any path in the repo, which this static table cannot enumerate and
            // the shell already completes better than a popup could.
            new("<path>", "just this file or folder", Completes: false),
        ]),
        // NeedsWindow like the rest: the store and the working directory belong to the composition
        // root, and this table deliberately holds nothing but the conversation.
        new("/sessions", "earlier conversations here, and a way back into one",
            CommandOutcome.NeedsWindow,
        [
            // NOT COMPLETABLE: the value is a number off a listing the user has to read first, or an
            // id. Neither is something this static table could offer.
            new("resume <number|id>", "restore one — the number from this list, or its id",
                Completes: false, Values: ValueSources.Sessions),
            new("all", "every folder, not just this one"),
        ]),
        // NeedsWindow: the registry of configured instances belongs to the composition root, and
        // this table holds nothing but the conversation. ONE ARGUMENT, and it is a live value — so
        // the palette offers the instances themselves rather than a placeholder to type over.
        new("/model", "show or switch which configured model this session uses",
            CommandOutcome.NeedsWindow,
        [
            new("<instance>", "a name from `providers` in config", Completes: false,
                Values: ValueSources.Providers),
        ]),
        // NeedsWindow for the same reason /mcp is: the live mode belongs to the session's agent, and
        // this type deliberately holds nothing but the conversation.
        // THE AXIS IS NAMED, so this one command can grow. Delegation is the only axis today; file
        // editing and a build/plan mode are coming, and each would otherwise have wanted a command
        // of its own — three entries in this list where one will do, and no single place that shows
        // the whole picture. `/mode` bare reports every axis for exactly that reason.
        new("/mode", "show or set how this session works", CommandOutcome.NeedsWindow,
        [
            // ONE ROW WITH A VALUE LIST, matching `edits <mode>` below. Two rows spelled the whole
            // command out — which worked, and meant the two axes of one command read as different
            // kinds of thing in the palette.
            new("agent <mode>", "whether this agent may spawn sub-agents", Completes: false,
                Values: ValueSources.AgentModes),

            // THE EDITS AXIS IS COMPLETABLE TOO. It was accepted by the command and simply not
            // declared here, so `/mode edits ` offered nothing while `/mode agent ` offered two
            // choices — the axis existed, was documented in /mode's own bare output, and had no way
            // to be discovered from the palette. The values are live because whether `auto` is among
            // them depends on this session's classifier.
            // THE SAME SHAPE AS `/mcp show <name>`: the placeholder rides in the name, so the row
            // completes to "edits " and the palette opens the mode list. Without it this was
            // unselectable AND offered nothing — the only way to reach the modes was to type the
            // word the palette was showing you.
            new("edits <mode>", "how file writes are approved", Completes: false,
                Values: ValueSources.EditModes),
        ]),
        // NeedsWindow again: history is a store the composition root owns, and this type holds
        // nothing but the conversation. `/stats 30` widens the window; the default is a week.
        new("/stats", "usage: tokens, projects, agent types, what fills the context",
            CommandOutcome.NeedsWindow,
        [
            new("<days>", "how far back to look — default 7", Completes: false),
            new("all", "every session ever recorded"),
            new("clear", "delete all usage history, after confirming"),
        ]),
        new("/help", "show keys and commands", CommandOutcome.NeedsWindow),
        new("/exit", "quit cxagent", CommandOutcome.Quit),
    ];

    /// <summary>The command matching this input's first token, or null.</summary>
    public static SessionCommand? Match(string input)
    {
        var token = FirstToken(input);
        if (token.Length == 0) return null;

        foreach (var c in All)
            if (token.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
                return c;

        return null;
    }

    /// <summary>
    /// Commands whose name starts with <paramref name="prefix"/> — for tab completion and, later, a
    /// palette. An empty or bare-slash prefix offers everything.
    /// </summary>
    public static IReadOnlyList<SessionCommand> Matching(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix == "/") return All;

        var hits = new List<SessionCommand>();
        foreach (var c in All)
            if (c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                hits.Add(c);

        // AN EXACT MATCH SORTS FIRST, because a command that is a strict PREFIX of another can never
        // be reached otherwise: typing /mode offered /model above it — table order decided, and
        // /model is declared first — so Enter completed to the longer name and the shorter command
        // was unreachable from the palette. Observed, and it had nothing to do with either command.
        //
        // STABLE OTHERWISE: everything else keeps the table's order, which is the order /help prints
        // and the one a reader has already learned.
        var exact = hits.FindIndex(c => c.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase));
        if (exact > 0)
        {
            var winner = hits[exact];
            hits.RemoveAt(exact);
            hits.Insert(0, winner);
        }

        return hits;
    }

    /// <summary>
    /// The arguments offered for an input that has already named a command and a space —
    /// <c>"/mcp "</c>, <c>"/mcp re"</c> — or empty when the input is not in that shape.
    ///
    /// <para>ONE LEVEL DOWN, SAME GESTURE. The palette closed the moment a space was typed, which is
    /// exactly when a user has committed to a command and needs to know what follows it. Arguments
    /// are NOT flattened into the top-level list: nine commands would become twenty rows, and
    /// <c>/clear</c> would sit among <c>/mcp login</c> and <c>/stats clear</c> with nothing to say
    /// which is a command and which is a modifier. The hierarchy is real, so the palette shows it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CommandArgument> ArgumentsFor(
        string input, Func<string, IReadOnlyList<CommandArgument>>? values = null)
    {
        var space = input.IndexOf(' ');
        if (space < 0) return [];

        // A SECOND SPACE MEANS THE ARGUMENT IS TYPED and the user has moved past choosing one —
        // "/mcp login serv" is naming a server, not still picking between login and reload.
        //
        // ...UNLESS THE ARGUMENT NAMES A SOURCE. `/sessions resume ` is a second space whose right
        // answer is a short list the app has on hand, and offering nothing there sends the user back
        // to read a number off a listing they have scrolled past.
        var rest = input[(space + 1)..];
        var name = input[..space];

        var command = All.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!command.TakesArguments) return [];

        if (rest.Contains(' '))
        {
            // Two words in: the first named a subcommand, so the values belong to THAT argument.
            var cut = rest.IndexOf(' ');
            var sub = rest[..cut];
            var typed = rest[(cut + 1)..];

            // ONLY THE LAST WORD MAY BE INCOMPLETE. "/sessions resume 3 extra" is past choosing.
            if (typed.Contains(' ')) return [];

            var under = command.Args.FirstOrDefault(a =>
                a.Name.StartsWith(sub, StringComparison.OrdinalIgnoreCase));

            return Narrow(Supplied(values, under.Values), typed);
        }

        // ONE WORD IN. Offer the declared arguments — and, for a command whose SOLE argument is a
        // live value (`/model <instance>`), the values themselves: there is nothing else to pick.
        var live = command.Args.Count == 1 ? Supplied(values, command.Args[0].Values) : [];
        if (live.Count > 0) return Narrow(live, rest);

        if (rest.Length == 0) return command.Args;

        return [.. command.Args.Where(a =>
            a.Name.StartsWith(rest, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Rows from a named source, or empty when nothing supplies it.</summary>
    private static IReadOnlyList<CommandArgument> Supplied(
        Func<string, IReadOnlyList<CommandArgument>>? values, string? source) =>
        source is null || values is null ? [] : values(source);

    /// <summary>Filtered by what has been typed so far, so a long list narrows as you go.</summary>
    private static IReadOnlyList<CommandArgument> Narrow(
        IReadOnlyList<CommandArgument> rows, string typed) =>
        typed.Length == 0
            ? rows
            : [.. rows.Where(r => r.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Everything after the command name, trimmed — empty when there is nothing.
    ///
    /// <para><see cref="Match"/> splits on the first space and returns the command; the remainder used
    /// to be dropped on the floor. That was invisible while no command took an argument, and it is
    /// this codebase's recurring rot pattern: a value that parses and is never read.</para>
    ///
    /// <para>Empty rather than null on purpose. A caller that must null-check before splitting is a
    /// caller that will forget once, and the forgetting produces a NullReferenceException in a
    /// command handler rather than an unrecognised subcommand.</para>
    /// </summary>
    public static string Arguments(string input)
    {
        var trimmed = input.Trim();
        var end = trimmed.IndexOf(' ');
        return end < 0 ? "" : trimmed[(end + 1)..].Trim();
    }

    /// <summary>The arguments as words — a subcommand and its target. Empty runs are dropped, so
    /// double spaces do not produce a phantom argument.</summary>
    public static IReadOnlyList<string> ArgumentWords(string input) =>
        Arguments(input).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string FirstToken(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/')) return "";
        var end = trimmed.IndexOf(' ');
        return end < 0 ? trimmed : trimmed[..end];
    }

    /// <summary>
    /// True when <paramref name="input"/> was a recognized (or unrecognized) slash command — either
    /// way, the caller must NOT treat it as a goal. <paramref name="reply"/> is the chat message to
    /// display.
    /// </summary>
    public static bool TryHandle(string input, out string reply)
    {
        var trimmed = input.Trim();

        // Only a leading slash makes something a command — "what does /clear do?" is a question
        // about the command, not the command itself.
        if (!trimmed.StartsWith('/'))
        {
            reply = "";
            return false;
        }

        // First whitespace-delimited token only, so "/clear now please" still matches "/clear".
        var end = trimmed.IndexOf(' ');
        var token = end < 0 ? trimmed : trimmed[..end];

        if (Match(trimmed) is not { } command)
        {
            // An unrecognised slash is still a COMMAND ATTEMPT, never a goal: sending "/celar" to the
            // model as a task is worse than saying it does not exist.
            reply = $"Unknown command '{token}'. Available: {string.Join(", ", All.Select(c => c.Name))}.";
            return true;
        }

        switch (command.Name)
        {
            case "/clear":
                // THE CALLER CLEARS THE CONTEXT. This type deliberately holds no session state — it
                // used to be handed a List<ChatMessage> to empty here, but nothing ever read that
                // list, so the clear was a no-op dressed as the command's whole purpose.
                reply = "Conversation cleared.";
                return true;

            default:
                // /compress, /help and /exit are all serviced by the CALLER: they need a provider, the
                // window, or the process — none of which this type has, and deliberately so. It takes
                // the raw conversation and returns a string, which is what makes it testable without a
                // ConsoleWindowSystem. The outcome on the command says which.
                reply = "";
                return true;
        }
    }

    /// <summary>
    /// The reply for <c>/mcp</c>: every configured server, its state, and the ERROR when it failed.
    ///
    /// <para>THE ERROR IS THE POINT. The session panel is ~24 columns and can only say a server
    /// failed; this is the surface that says "npx: command not found" — the difference between a
    /// user who fixes it in ten seconds and one who assumes the feature is broken.</para>
    ///
    /// <para>A DISABLED server is listed here, unlike in the panel. This is what someone runs
    /// BECAUSE a tool they expected is missing, so "it is switched off" is the answer they came
    /// for; omitting it sends them to read config for nothing.</para>
    /// </summary>
    public static string DescribeMcp(
        IReadOnlyList<Core.Mcp.McpServerStatus> servers,
        string arguments = "",
        IReadOnlyList<string>? toolNames = null)
    {
        var words = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // BARE AND `list` ARE THE SAME. Someone who guesses at a subcommand should not be told they
        // guessed wrong for using the obvious word.
        if (words.Length == 0 || words[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            return List(servers);

        // `show <name>` IS THE SPELLED-OUT FORM, and it exists because the others are verbs. With
        // only a bare name, `/mcp reload` and `/mcp context7` were parsed at different levels — one
        // a subcommand, the other a fallthrough — which is why the palette could offer the verbs and
        // the names but never both as one list.
        if (words[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            if (words.Length < 2) return "Name a server: `/mcp show <name>`.";

            var shown = servers.FirstOrDefault(s =>
                s.Name.Equals(words[1], StringComparison.OrdinalIgnoreCase));

            return shown is not null
                ? Detail(shown, toolNames)
                : $"No MCP server called '{words[1]}'.\n\n" + List(servers);
        }

        // A BARE NAME STILL WORKS. "/mcp context7" reads as "tell me about context7", and refusing
        // the reading someone naturally reaches for teaches nothing — `show` is what the palette
        // offers and what the help lists; this is the shortcut for people who skip both.
        var named = servers.FirstOrDefault(s =>
            s.Name.Equals(words[0], StringComparison.OrdinalIgnoreCase));
        if (named is not null) return Detail(named, toolNames);

        // `reload` falls through to the LIST. The caller performs the reload — it owns the servers
        // and the config — and then calls this to report the result, so returning a second
        // "reloading…" here would echo the same announcement twice.
        if (words[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
            return List(servers);

        // Naming what IS understood, including the servers: a wrong guess should teach the right
        // one. Two of the four cases here are a mistyped server name.
        var known = servers.Count == 0 ? "(none configured)" : string.Join(", ", servers.Select(s => s.Name));
        return $"Unknown: '{words[0]}'.\n"
             + "Usage: /mcp [list | reload | login <server> | <server>]\n"
             + $"Servers: {known}";
    }

    /// <summary>Every server, one row each: the summary <c>/mcp</c> opens with.</summary>
    private static string List(IReadOnlyList<Core.Mcp.McpServerStatus> servers)
    {
        if (servers.Count == 0)
            return "No MCP servers configured. Add one in Settings (F5), or in the \"mcp\" block of "
                 + "config.json.";

        // A TABLE, NOT A DASH-SEPARATED LINE PER SERVER. Server and status are two columns of data,
        // not one sentence — the same call this task makes for /sessions and /model.
        //
        // Md.EscapeCell, NOT Md.Escape — Ruling 16. Every value below lands inside a `|`-delimited
        // row: an unescaped pipe in a server name or a connection error would split the row into
        // more cells than the header declares, and Markdig drops the overflow silently.
        var lines = new List<string> { "| server | status |", "|---|---|" };
        foreach (var server in servers)
        {
            string status;
            if (!server.Enabled) status = "disabled";
            // NEEDS AUTH IS NOT A FAILURE, and reads differently: nothing is broken, the server is
            // waiting to be logged in to. Saying "failed" would send someone to check their config.
            else if (server.NeedsAuth)
                status = $"not logged in — run `/mcp login {Md.EscapeCell(server.Name)}`";
            else if (server.Error is { } error) status = $"failed: {Md.EscapeCell(error)}";
            else if (server.ToolCount == 0) status = "connected, but offers no tools";
            else status = $"{server.ToolCount} {(server.ToolCount == 1 ? "tool" : "tools")}";

            lines.Add($"| `{Md.EscapeCell(server.Name)}` | {status} |");
        }
        lines.Add("");
        lines.Add("`/mcp <server>` for its tools · `/mcp reload` to re-read config");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// One server in detail: its state and the tools it actually contributed.
    ///
    /// <para>The tool NAMES, not just a count, because "why did the model not use my server" is
    /// usually answered by seeing what it was offered — a tool dropped for a name collision is
    /// invisible in the summary and absent here, which is the same fact stated where it can be
    /// noticed.</para>
    /// </summary>
    private static string Detail(Core.Mcp.McpServerStatus server, IReadOnlyList<string>? toolNames)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(server.Name).Append(" — ");
        if (!server.Enabled) sb.Append("disabled in config");
        else if (server.NeedsAuth) sb.Append("not logged in — run /mcp login ").Append(server.Name);
        else if (server.Error is { } error) sb.Append("failed: ").Append(error);
        else sb.Append("connected");
        sb.Append('\n');

        var mine = (toolNames ?? [])
            .Where(n => n.StartsWith(Sanitize(server.Name) + "_", StringComparison.Ordinal))
            .ToList();

        if (mine.Count == 0)
            sb.Append(server.Enabled && server.Error is null
                ? "  no tools reached the model"
                : "  no tools (the server is not running)");
        else
            foreach (var name in mine) sb.Append("  ").Append(name).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The same sanitisation the toolset applies when composing a tool name, so a server
    /// whose name needed cleaning still matches its own tools.</summary>
    private static string Sanitize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// The command list, as a markdown table.
    ///
    /// <para>A TABLE SUITS THIS AND NOT EVERY LIST. Command names and summaries are short and
    /// uniform, which is what columns are for; a skill's description is a paragraph and belongs in
    /// indented rows, so <c>/skills</c> keeps that shape.</para>
    ///
    /// <para>NO PALETTE, AND NO WIDTHS. Both were this file's last bindings to a front end: colour
    /// arrived as parameters so no literal tag appeared here, and the column widths were computed
    /// from character counts, which is only a layout in a monospace terminal at a width Core
    /// guessed. Markdown says "these are columns" and lets whatever renders it decide.</para>
    /// </summary>
    /// <returns>A markdown table of every command and subcommand.</returns>
    public static string HelpLines()
    {
        // ARGUMENTS ARE ROWS, INDENTED UNDER THEIR COMMAND. /help rendered name-plus-summary only,
        // so `/mcp reload`, `/stats clear` and `/mode fan-out` existed in the dispatcher and in no
        // surface a user could find. The indent carries the relationship: these are modifiers of the
        // row above, not commands of their own.
        var rows = new List<(string Name, string Summary, bool IsArg)>();
        foreach (var c in All)
        {
            rows.Add((c.Name, c.Summary, false));
            foreach (var a in c.Args)
                rows.Add(($"{c.Name} {a.Name}", a.Summary, true));
        }

        var lines = new List<string>
        {
            "| command | what it does |",
            "|---------|--------------|",
        };

        // A NON-BREAKING SPACE FOR THE INDENT. Markdown collapses leading spaces inside a cell, so a
        // plain two-space indent renders flush against the command above it and the subcommand rows
        // stop reading as modifiers of anything.
        //
        // THE NAME IS ESCAPED AND THE SUMMARY IS NOT, which is not an oversight. A name is a literal
        // spelling — `/sessions resume <number|id>` carries a pipe that would split the row into
        // more cells than the header declares. A summary is markdown this file AUTHORS, backticks
        // included ("a name from `providers` in config"); escaping it puts backslashes on screen and
        // turns intended inline code into punctuation.
        foreach (var (name, summary, isArg) in rows)
            lines.Add($"| {(isArg ? "\u00a0\u00a0" : "")}`{Md.EscapeCell(name)}` | {summary} |");

        return string.Join('\n', lines);
    }
}
