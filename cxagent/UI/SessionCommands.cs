using CxAgent.Core.Models;

namespace CxAgent.UI;

/// <summary>
/// Slash commands that manage the shared conversation directly, WITHOUT going through GoalRunner —
/// no goal, no provider call, no tokens spent. Deliberately UI-free (takes the raw conversation list,
/// returns a reply string) so it's testable without ConsoleWindowSystem; AppBootstrap does the
/// displaying.
///
/// Only an exact leading-slash token counts as a command. "clear the build output" must fall through
/// to GoalRunner as an ordinary goal — a false positive here would silently wipe a user's session
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
    /// True when <paramref name="input"/> was a recognized (or unrecognized) slash command — either
    /// way, the caller must NOT treat it as a goal. <paramref name="reply"/> is the chat message to
    /// display.
    /// </summary>

    private static string FirstToken(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/')) return "";
        var end = trimmed.IndexOf(' ');
        return end < 0 ? trimmed : trimmed[..end];
    }

    public static bool TryHandle(string input, List<ChatMessage> conversation, out string reply)
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
                conversation.Clear();
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

    /// <summary>The command list as help text, one indented line each.</summary>
    public static string HelpLines(string markupColor) =>
        string.Join('\n', All.Select(c => $"  [{markupColor}]{c.Name}[/]".PadRight(28) + c.Summary));

    /// <summary>
    /// Halves the conversation, dropping the OLDEST messages first and keeping the newest — the
    /// referent of a follow-up ("now do X to that") is almost always recent. Reused as-is by Task 3's
    /// automatic compression, which calls this same routine under measured context pressure rather
    /// than a fixed size; the explicit `/compress` command is the only caller that halves
    /// unconditionally in response to a user request.
    ///
    /// Compression is LOSSY, so it must never fire on a conversation short enough not to need it —
    /// below this floor it's a costless no-op instead of throwing away history for nothing.
    ///
    /// Internal, not private: <see cref="SessionCompressor"/> (P11 Task 3) shares this exact floor so
    /// its own short-conversation no-op matches this one rather than drifting to a second constant.
    /// </summary>
    internal const int MinMessagesToCompress = 8;

    public static void Compress(List<ChatMessage> conversation)
    {
        if (conversation.Count < MinMessagesToCompress) return;

        var keep = conversation.Count / 2;
        conversation.RemoveRange(0, conversation.Count - keep);
    }
}
