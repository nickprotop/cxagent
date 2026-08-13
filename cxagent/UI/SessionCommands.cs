using CxAgent.Core.Models;

namespace CxAgent.UI;

/// <summary>
/// Slash commands that manage the shared conversation directly, WITHOUT going through AgentHost —
/// no goal, no provider call, no tokens spent. Deliberately UI-free (takes the raw conversation list,
/// returns a reply string) so it's testable without ConsoleWindowSystem; AppBootstrap does the
/// displaying.
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
            new("login", "authorise a server that needs OAuth"),
            // NOT COMPLETABLE: the value is a name from the user's config, and the angle brackets are
            // notation for a reader rather than something to type. Listing live servers here would
            // couple this static table to session state — `/mcp` bare is what enumerates them.
            new("<server>", "inspect one server by name", Completes: false),
        ]),
        // NeedsWindow like /mcp: discovery reads the session's working directory, and this type
        // holds nothing but the conversation. NO SUBCOMMANDS — it lists, it does not load, and it is
        // not a refresh: skills are re-read every turn, so an edited one is already live.
        new("/skills", "list available skills, and any SKILL.md that was skipped",
            CommandOutcome.NeedsWindow),
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
                Completes: false),
            new("all", "every folder, not just this one"),
        ]),
        // NeedsWindow for the same reason /mcp is: the live mode belongs to the session's agent, and
        // this type deliberately holds nothing but the conversation.
        // THE AXIS IS NAMED, so this one command can grow. Delegation is the only axis today; file
        // editing and a build/plan mode are coming, and each would otherwise have wanted a command
        // of its own — three entries in this list where one will do, and no single place that shows
        // the whole picture. `/mode` bare reports every axis for exactly that reason.
        new("/mode", "show or set how this session works", CommandOutcome.NeedsWindow,
        [
            new("agent single", "this agent works alone; the spawn tool is withdrawn"),
            new("agent fan-out", "this agent can spawn sub-agents"),
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
    public static IReadOnlyList<CommandArgument> ArgumentsFor(string input)
    {
        var space = input.IndexOf(' ');
        if (space < 0) return [];

        // A SECOND SPACE MEANS THE ARGUMENT IS TYPED and the user has moved past choosing one —
        // "/mcp login serv" is naming a server, not still picking between login and reload.
        //
        // ...UNLESS SOMETHING CAN SUPPLY THE VALUES. `/sessions resume ` is a second space whose
        // right answer is a short list the app already has on hand, and offering nothing there sends
        // the user back to read a number off a listing they have scrolled past. See
        // <see cref="ValueSupplier"/> for why this is one hook rather than a general mechanism.
        var rest = input[(space + 1)..];
        if (rest.Contains(' ')) return Values(input[..space], rest);

        var command = All.FirstOrDefault(c =>
            string.Equals(c.Name, input[..space], StringComparison.OrdinalIgnoreCase));
        if (!command.TakesArguments) return [];

        if (rest.Length == 0) return command.Args;

        return [.. command.Args.Where(a =>
            a.Name.StartsWith(rest, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Live values for an argument that names something the app knows about, or null for the great
    /// majority that name nothing.
    ///
    /// <para>THE TABLE IS STATIC AND STAYS STATIC. <c>/mcp &lt;server&gt;</c> documents the reason:
    /// coupling this list to session state makes a description of the commands into a view of the
    /// world. This hook does not change that — the table still declares <c>resume &lt;number|id&gt;</c>
    /// as a shape, and the composition root, which owns the store, is what fills it in. Nothing here
    /// reads a session.</para>
    ///
    /// <para>ONE HOOK RATHER THAN AN INTERFACE PER COMMAND, because there is one command that needs
    /// it. A second — <c>/mcp login</c> offering live servers — would use the same field and the same
    /// shape, and a third is when this is worth generalising.</para>
    /// </summary>
    public static Func<string, IReadOnlyList<CommandArgument>>? ValueSupplier { get; set; }

    /// <summary>
    /// The values offered once a subcommand is typed and a space follows it — filtered by whatever
    /// has been typed since, so <c>/sessions resume 7f</c> narrows rather than listing everything.
    /// </summary>
    private static IReadOnlyList<CommandArgument> Values(string name, string rest)
    {
        if (ValueSupplier is null) return [];

        var cut = rest.IndexOf(' ');
        var sub = rest[..cut];
        var typed = rest[(cut + 1)..];

        // ONLY THE LAST WORD MAY BE INCOMPLETE. "/sessions resume 3 extra" is past choosing.
        if (typed.Contains(' ')) return [];

        var values = ValueSupplier($"{name} {sub}");
        if (typed.Length == 0) return values;

        return [.. values.Where(v => v.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))];
    }

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

        // A SERVER NAME IS THE OTHER NATURAL GUESS. "/mcp context7" reads as "tell me about
        // context7", so it means that rather than being an unknown subcommand.
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

    /// <summary>Every server, one line each: the summary <c>/mcp</c> opens with.</summary>
    private static string List(IReadOnlyList<Core.Mcp.McpServerStatus> servers)
    {
        if (servers.Count == 0)
            return "No MCP servers configured. Add one in Settings (F5), or in the \"mcp\" block of "
                 + "config.json.";

        var sb = new System.Text.StringBuilder();
        foreach (var server in servers)
        {
            sb.Append(server.Name).Append(" — ");
            if (!server.Enabled) sb.Append("disabled");
            // NEEDS AUTH IS NOT A FAILURE, and reads differently: nothing is broken, the server is
            // waiting to be logged in to. Saying "failed" would send someone to check their config.
            else if (server.NeedsAuth) sb.Append("not logged in — run /mcp login ").Append(server.Name);
            else if (server.Error is { } error) sb.Append("failed: ").Append(error);
            else if (server.ToolCount == 0) sb.Append("connected, but offers no tools");
            else sb.Append(server.ToolCount).Append(server.ToolCount == 1 ? " tool" : " tools");
            sb.Append('\n');
        }
        sb.Append("\n/mcp <server> for its tools · /mcp reload to re-read config");
        return sb.ToString().TrimEnd('\n');
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

    /// <summary>The command list as help text, one indented line each.</summary>
    /// <summary>
    /// The command list, as a table.
    ///
    /// <para>DRAWN, NOT MARKDOWN. <c>ChatRole.System</c> sets <c>Markdown = false</c> — a pipe table
    /// would render as literal pipes — and flipping that role would change every system message in
    /// the app, banner and errors included. Box characters go through the markup renderer untouched.
    /// </para>
    ///
    /// <para>A TABLE SUITS THIS AND NOT EVERY LIST. Command names and summaries are short and
    /// uniform, which is what columns are for; a skill's description is a paragraph and belongs in
    /// indented rows, so <c>/skills</c> keeps that shape.</para>
    ///
    /// <para>WIDTH IS BOUNDED BY THE CONTENT, not by the terminal: the name column is as wide as the
    /// widest name, so the table cannot push the summary off a narrow pane on its own.</para>
    /// </summary>
    public static string HelpLines(string markupColor)
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

        var width = rows.Max(r => r.Name.Length + (r.IsArg ? 2 : 0));
        var muted = ColorScheme.MutedMarkup;

        var lines = new List<string>
        {
            $"  [{muted}]┌─{new string('─', width)}─┬─{new string('─', rows.Max(r => r.Summary.Length))}─┐[/]",
        };

        foreach (var (name, summary, isArg) in rows)
        {
            var label = isArg ? "  " + name : name;
            var colour = isArg ? muted : markupColor;
            lines.Add($"  [{muted}]│[/] [{colour}]{label}[/]{new string(' ', width - label.Length)} "
                    + $"[{muted}]│[/] {(isArg ? $"[{muted}]{summary}[/]" : summary)}"
                    + $"{new string(' ', rows.Max(r => r.Summary.Length) - summary.Length)} [{muted}]│[/]");
        }

        lines.Add($"  [{muted}]└─{new string('─', width)}─┴─{new string('─', rows.Max(r => r.Summary.Length))}─┘[/]");
        return string.Join('\n', lines);
    }

}
