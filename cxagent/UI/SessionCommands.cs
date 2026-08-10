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
        new("/mcp", "list MCP servers, inspect one, or reload config", CommandOutcome.NeedsWindow),
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
    public static string HelpLines(string markupColor) =>
        string.Join('\n', All.Select(c => $"  [{markupColor}]{c.Name}[/]".PadRight(28) + c.Summary));

}
