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
    private const string ClearCommand = "/clear";
    private const string CompressCommand = "/compress";

    /// <summary>
    /// True when <paramref name="input"/> was a recognized (or unrecognized) slash command — either
    /// way, the caller must NOT treat it as a goal. <paramref name="reply"/> is the chat message to
    /// display.
    /// </summary>
    /// <summary>
    /// True when <paramref name="input"/> is the /compress command, which the CALLER must service
    /// asynchronously via <see cref="SessionCompressor.CompressAsync"/>. Split out because
    /// <see cref="TryHandle"/> is sync (a key handler calls it) and summarising needs a provider call.
    /// </summary>
    public static bool IsCompress(string input) =>
        FirstToken(input).Equals(CompressCommand, StringComparison.OrdinalIgnoreCase);

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

        switch (token.ToLowerInvariant())
        {
            case ClearCommand:
                conversation.Clear();
                reply = "Conversation cleared.";
                return true;

            case CompressCommand:
                // Handled by the CALLER, asynchronously: /compress means COMPRESS — it summarises
                // through the model like auto-compression does. Truncation is only the fallback when
                // that call fails. TryHandle is synchronous (it is called from a key handler), so it
                // reports the intent and lets the caller await SessionCompressor.
                reply = "";
                return true;

            default:
                reply = $"Unknown command '{token}'. Available: {ClearCommand}, {CompressCommand}.";
                return true;
        }
    }

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
