using CxAgent.Core.Helpers;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Commands;

/// <summary>
/// <c>/stats</c> — reads the window, queries history, renders the dashboard.
///
/// <para>THE SEAM BETWEEN THREE PURE PIECES. <see cref="UsageHistoryStore"/> does SQL,
/// <see cref="StatsQuery"/> does arithmetic, <see cref="StatsDashboard"/> does markdown; this decides
/// how far back to look and joins them. Split that way because only the first touches a database, so
/// everything downstream of it is testable without one.</para>
/// </summary>
public static class StatsCommand
{
    /// <summary>A week. Long enough to show a rhythm, short enough that a quiet day is visible in
    /// the sparkline rather than compressed into a single column.</summary>
    public const int DefaultDays = 7;

    /// <summary>
    /// Reads the day count from the argument.
    ///
    /// <para>OUT OF RANGE IS CLAMPED, NOT REJECTED. "/stats 9999" is a user asking for everything,
    /// and answering with a usage message rather than their history would be pedantry — the window
    /// is an appetite, not a constraint the app has to defend.</para>
    ///
    /// <para>NULL FOR WHAT IT CANNOT READ, rather than the default window. A silent fallback
    /// answers a question the user did not ask, and the caller is what knows whether an
    /// unreadable word is a verb this process services or simply a mistake.</para>
    /// </summary>
    public static int? ParseDays(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return DefaultDays;

        var text = argument.Trim();
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase)) return 3650;

        // "30d" is what someone types who has used any other tool with a time window.
        if (text.EndsWith("d", StringComparison.OrdinalIgnoreCase)) text = text[..^1];

        // A negative count is not a small window, it is nonsense with a minus sign in front of it —
        // clamping it to 1 would answer "yesterday" to someone who typed a mistake, not a request.
        if (!int.TryParse(text, out var days) || days < 0) return null;

        return Math.Clamp(days, 1, 3650);
    }

    /// <summary>
    /// The confirmation text for <c>/stats clear</c>, naming what is at stake.
    ///
    /// <para>THE ROW COUNT IS THE POINT. "Clear history?" is a question nobody can answer well; "this
    /// deletes 1,284 records covering 11 sessions" is. A confirmation that does not say what will be
    /// lost is a speed bump, not a safeguard.</para>
    /// </summary>
    /// <remarks>
    /// NOT FENCED, unlike the dashboard this file's other half renders. Nothing here is drawn — it
    /// is a question and the two facts needed to answer it, and a confirmation set in a monospace
    /// box reads as output rather than as something being asked of the reader.
    ///
    /// <para>The pipes in a code span keep a table parser off them.</para>
    /// </remarks>
    public static string ConfirmText(int rows, int sessions) =>
        rows == 0
            ? "There is no usage history to clear."
            : "## Clear usage history?\n\n"
            + $"This deletes **{DisplayNumber.Grouped(rows)}** records"
            + (sessions > 0 ? $" covering **{sessions}** session{(sessions == 1 ? "" : "s")}" : "")
            + ".\n\n"
            + "Sessions, sub-agent runs, tool calls, compactions and "
            + "permission decisions. This cannot be undone.";

    public static Message Render(UsageHistoryStore history, string? argument)
    {
        if (ParseDays(argument) is not { } days)
            // NAMED, like the unknown-command reply: "bogus" and "clear" are the same failure to
            // Core, and a user who cannot see what was accepted cannot fix what they typed.
            return new Message(
                $"'{Md.Escape(argument ?? "")}' is not a window /stats understands. "
                + "Use a day count, \"Nd\", or \"all\".", Severity.Warning);

        var since = DateTimeOffset.UtcNow.AddDays(-days);

        var sessions = history.SessionsSince(since);
        var runs = history.RunsSince(since);
        var calls = history.ToolCallsSince(since);
        var compactions = history.CompactionsSince(since);
        var permissions = history.PermissionsSince(since);

        var today = DateOnly.FromDateTime(DateTime.Now);

        return StatsDashboard.Render(new StatsDashboard.StatsView
        {
            Days = days,
            Totals = StatsQuery.Totals(sessions),
            Projects = StatsQuery.ByProject(sessions),
            Models = StatsQuery.ByModel(sessions),
            Types = StatsQuery.ByType(runs),
            Tools = StatsQuery.ByTool(calls),

            // THE SPARKLINE IS CAPPED AT 30 COLUMNS regardless of the window. "/stats all" over a
            // year would otherwise emit a line hundreds of characters wide that wraps into noise.
            Daily = StatsQuery.ByDay(sessions, today.AddDays(-Math.Min(days, 30) + 1), today),

            Compaction = StatsQuery.Compaction(compactions),
            Permissions = StatsQuery.Permissions(permissions),
        });
    }
}
