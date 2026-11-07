using System.Text;
using CxAgent.Core.Commands;
using CxAgent.Core.Web;

namespace CxAgent.Web;

/// <summary>
/// The slash commands a browser may run.
///
/// <para>AN ALLOW-LIST, NEVER "WHATEVER IS REGISTERED". The registry holds commands that open terminal screens, edit
/// config.json, manage plugins — a login must not reach any of them unless <see cref="WebAccess"/> says so, and a
/// command added later must not become reachable from a browser just by existing. One is added here on purpose or not
/// at all.</para>
/// </summary>
public static class WebCommands
{
    /// <summary>The commands every browser may run, as typed.</summary>
    public static readonly IReadOnlyList<string> Allowed =
    [
        "/compress", "/clear", "/model", "/mode", "/think", "/btw", "/stats", "/sandbox", "/diff",
        "/agents", "/skills", "/commands", "/mcp", "/hooks", "/tasks", "/sessions", "/open", "/export", "/help",
    ];

    /// <summary>The commands behind a switch, and the switch: what each would reach beyond the conversation.</summary>
    private static readonly IReadOnlyList<(string Name, Func<WebAccess, bool> On, string Switch)> Gated =
    [
        ("/shell", a => a.Shell, "shell"),
        ("/plugin", a => a.Plugins, "plugins"),
        ("/trust", a => a.Config, "config"),
        ("/init", a => a.Config, "config"),
        ("/config", a => a.Config, "config"),
    ];

    /// <summary>What the refusal says.</summary>
    public const string NotOnTheWeb = "not available on the web";

    /// <summary>The commands this server's browsers may run, as the completion list offers them.</summary>
    public static IReadOnlyList<string> AllowedWith(WebAccess allow) =>
        [.. Allowed.Take(Allowed.Count - 1), .. Gated.Where(g => g.On(allow)).Select(g => g.Name), "/help"];

    /// <summary>
    /// Why <paramref name="text"/> may not run from a browser, or null when it may — with the switches at their
    /// defaults. Only meaningful for text that starts with <c>/</c>.
    /// </summary>
    public static string? Refusal(string text) => Refusal(text, WebAccess.Default);

    /// <summary>Why <paramref name="text"/> may not run from a browser under <paramref name="allow"/>, or null.</summary>
    public static string? Refusal(string text, WebAccess allow)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var args = parts.Length > 1 ? parts[1].Trim() : "";

        if (Gated.FirstOrDefault(g => g.Name == name) is { Name: not null } gated)
            return gated.On(allow) ? null : Off(name, gated.Switch);
        if (!Allowed.Contains(name)) return $"{name} is {NotOnTheWeb}.";

        // ONLY THE PARTS THAT LEAVE CONFIG ALONE. /sandbox on|off|edit write config.json — the picker under the message
        // box changes this session's; /stats clear deletes history behind a terminal dialog.
        if (name == "/sandbox" && args.Length > 0 && !args.Equals("review", StringComparison.OrdinalIgnoreCase))
            return $"/sandbox {args} is {NotOnTheWeb} — the sandbox picker under the message box changes this session; config.json is the terminal's.";
        if (name == "/stats" && args.StartsWith("clear", StringComparison.OrdinalIgnoreCase))
            return $"/stats clear is {NotOnTheWeb}.";
        if (name == "/think" && args.Contains("--save", StringComparison.OrdinalIgnoreCase) && !allow.Config)
            return Off("/think --save", "config") + " Without --save it sets this session.";

        // THE SIDEBAR RESUMES. A resume typed in a tab swaps the conversation under every other tab that has this
        // session open; the list itself, and "all", only read.
        if (name == "/sessions" && args.Length > 0 && !args.Equals("all", StringComparison.OrdinalIgnoreCase))
            return $"/sessions {args.Split(' ')[0]} is {NotOnTheWeb} — open a session from the list instead.";
        return null;
    }

    private static string Off(string what, string switchName) =>
        $"{what} is off for browsers. It is turned on in the terminal: F8 › Web › Browser access ({switchName}), or web.allow.{switchName} in config.json.";

    /// <summary>Whether this is the web's own <c>/help</c>, answered here rather than by the terminal's.</summary>
    public static bool IsHelp(string text)
    {
        var first = text.Trim().Split(' ', 2)[0];
        return first.Equals("/help", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The web's <c>/help</c>: the commands this browser may run and what each does. The terminal's own /help opens a
    /// window in the terminal, which is no answer to a browser.
    /// </summary>
    public static string Help(WebAccess allow)
    {
        var text = new StringBuilder("| Command | What it does |\n|---|---|\n");
        foreach (var name in AllowedWith(allow))
        {
            var summary = name switch
            {
                "/open" => "open a file in a tab beside the conversation",
                "/export" => "download this conversation as markdown",
                "/shell" => "a real terminal in a tab",
                "/config" => "edit config.json — not the browser's own access, users or folders",
                _ => SessionCommands.All.Where(c => c.Name == name).Select(c => (string?)c.Summary).FirstOrDefault(),
            };
            if (summary is not null) text.Append("| `").Append(name).Append("` | ").Append(Md.EscapeCell(summary)).Append(" |\n");
        }
        return text.ToString();
    }

    /// <summary>The help with the switches at their defaults.</summary>
    public static string Help() => Help(WebAccess.Default);
}
