using System.Text;
using CxAgent.Core.Commands;

namespace CxAgent.Web;

/// <summary>
/// The slash commands a browser may run.
///
/// <para>AN ALLOW-LIST, NEVER "WHATEVER IS REGISTERED". The registry holds commands that open terminal screens, edit
/// config.json, manage plugins — a login must not reach any of them, and a command added later must not become
/// reachable from a browser just by existing. One is added here on purpose or not at all.</para>
/// </summary>
public static class WebCommands
{
    /// <summary>The commands, as typed.</summary>
    public static readonly IReadOnlyList<string> Allowed =
    [
        "/compress", "/clear", "/model", "/mode", "/think", "/btw", "/stats", "/sandbox", "/diff",
        "/agents", "/skills", "/commands", "/mcp", "/hooks", "/tasks", "/sessions", "/help",
    ];

    /// <summary>What the refusal says.</summary>
    public const string NotOnTheWeb = "not available on the web";

    /// <summary>
    /// Why <paramref name="text"/> may not run from a browser, or null when it may. Only meaningful for text that
    /// starts with <c>/</c>.
    /// </summary>
    public static string? Refusal(string text)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var args = parts.Length > 1 ? parts[1].Trim() : "";
        if (!Allowed.Contains(name)) return $"{name} is {NotOnTheWeb}.";

        // ONLY THE PARTS THAT LEAVE CONFIG ALONE. /sandbox on|off|edit write config.json; /stats clear deletes history
        // behind a terminal dialog.
        if (name == "/sandbox" && args.Length > 0 && !args.Equals("review", StringComparison.OrdinalIgnoreCase))
            return $"/sandbox {args} is {NotOnTheWeb} — only /sandbox and /sandbox review are.";
        if (name == "/stats" && args.StartsWith("clear", StringComparison.OrdinalIgnoreCase))
            return $"/stats clear is {NotOnTheWeb}.";
        if (name == "/think" && args.Contains("--save", StringComparison.OrdinalIgnoreCase))
            return $"/think --save is {NotOnTheWeb} — it writes config.json; /think without it sets this session.";

        // THE SIDEBAR RESUMES. A resume typed in a tab swaps the conversation under every other tab that has this
        // session open; the list itself, and "all", only read.
        if (name == "/sessions" && args.Length > 0 && !args.Equals("all", StringComparison.OrdinalIgnoreCase))
            return $"/sessions {args.Split(' ')[0]} is {NotOnTheWeb} — open a session from the list instead.";
        return null;
    }

    /// <summary>Whether this is the web's own <c>/help</c>, answered here rather than by the terminal's.</summary>
    public static bool IsHelp(string text)
    {
        var first = text.Trim().Split(' ', 2)[0];
        return first.Equals("/help", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The web's <c>/help</c>: the allowed commands and what each does. The terminal's own /help opens a window in the
    /// terminal, which is no answer to a browser.
    /// </summary>
    public static string Help()
    {
        var text = new StringBuilder("| Command | What it does |\n|---|---|\n");
        foreach (var name in Allowed)
            if (SessionCommands.All.FirstOrDefault(c => c.Name == name) is { } c)
                text.Append("| `").Append(c.Name).Append("` | ").Append(Md.EscapeCell(c.Summary)).Append(" |\n");
        return text.ToString();
    }
}
