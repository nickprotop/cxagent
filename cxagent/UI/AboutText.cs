using System.Globalization;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>
/// What <c>/about</c> prints.
///
/// <para>SEPARATED FROM THE COMMAND so it can be tested: the handler needs a live session and a
/// transcript, where the text is a pure function of an <see cref="Installation"/> and a few counts.
/// Everything hard here is formatting, and formatting is exactly what a test can hold still.</para>
///
/// <para>A LINE EITHER CARRIES A FACT OR IS ABSENT. There are no "unknown" placeholders: an install
/// date that could not be read, a plugin list that is empty, a store that would not answer — each
/// drops its line rather than printing a word that reads as breakage. The exception is the plugin
/// count, where "none loaded" IS the answer someone is looking for.</para>
/// </summary>
public static class AboutText
{
    /// <summary>The counts that come from the history store, gathered by the caller.</summary>
    /// <param name="Sessions">How many sessions have been recorded, ever.</param>
    /// <param name="ToolCalls">How many tool calls, across all of them.</param>
    public readonly record struct Usage(int Sessions, int ToolCalls);

    /// <summary>
    /// Renders the message body.
    /// </summary>
    /// <param name="installation">This install, as read once at startup.</param>
    /// <param name="configDir">Where config and the stores live.</param>
    /// <param name="usage">Recorded totals, or null when the store could not be read.</param>
    /// <param name="plugins">The plugins loaded in this session, in the order the registry holds.</param>
    /// <param name="now">Today, for the "installed N ago" phrasing — passed so a test can hold it.</param>
    public static string Render(Installation installation, string configDir, Usage? usage,
                                IReadOnlyList<string> plugins, DateTimeOffset now)
    {
        var rows = new List<(string Label, string Value)>
        {
            ("Installed",  InstalledLine(installation, now)),
            ("Running",    $"{installation.Runtime} on {installation.Os} · {installation.Architecture}"),
            ("Built with", $"CxAgent.Core {installation.CoreVersion} · SharpConsoleUI {installation.UiVersion}"),
            ("Living in",  Tilde(installation.Path)),
            ("Config",     Tilde(configDir)),
        };

        if (usage is { } u)
            rows.Add(("Sessions", $"{Count(u.Sessions, "session")} · {Count(u.ToolCalls, "tool call")}"));

        // "NONE LOADED" RATHER THAN NO ROW, unlike every other absence here: someone asking about
        // plugins wants to know whether any are running, and a missing row answers that with silence
        // where a stated none answers it outright.
        rows.Add(("Plugins", plugins.Count == 0
            ? "none loaded"
            : $"{plugins.Count:N0} loaded — {Join(plugins)}"));

        var lines = new List<string>
        {
            // THE VERSION FIRST AND ALONE. It is the question /about is actually asked; everything
            // below is context for it.
            $"## cxagent {installation.Version}",
            "",
            "A coding agent that works in your checkout.",
            "",
            // A REAL TABLE, NOT A PADDED LABEL COLUMN. Hand-padding put a space before the closing
            // `**` — "**Installed  **" — which markdown will not close, so every label rendered with
            // its asterisks showing. The renderer measures columns; this file should not try to.
            //
            // AN EMPTY HEADER ROW, AND IT HAS TO BE THERE. Markdig needs a header line above the
            // `|---|` or it does not see a table at all and prints the pipes as text — verified by
            // removing it. Empty rather than named because "Label | Value" would caption rows that
            // already say what they are; the blank band it costs is the price of the table.
            "| | |",
            "|---|---|",
        };

        lines.AddRange(rows.Select(r => $"| **{r.Label}** | {r.Value} |"));

        lines.Add("");
        lines.Add("*MIT licensed · github.com/nickprotop/cxagent*");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The install line: when, how many launches, and what it came from.
    ///
    /// <para>THE UPGRADE CLAUSE ONLY WHEN THERE WAS ONE, and the date only when it is known. A fresh
    /// install reads "today · 1st launch" and says nothing about upgrading, because it has not.</para>
    /// </summary>
    private static string InstalledLine(Installation installation, DateTimeOffset now)
    {
        var parts = new List<string>();

        if (installation.FirstSeen is { } seen)
            parts.Add(Ago(seen, now));

        parts.Add(installation.LaunchCount == 1 ? "1st launch" : $"{installation.LaunchCount:N0} launches");

        if (installation.UpgradedFrom is { } from)
            parts.Add($"updated from {from}");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A date a person would say out loud.
    ///
    /// <para>NOT AN ISO TIMESTAMP. Someone asking how long they have had this wants "3 months ago",
    /// and the exact second is noise they then have to subtract today's date from. The date itself
    /// follows for anything older than a week, because "6 months ago" stops being useful the moment
    /// somebody wants to check it against something.</para>
    /// </summary>
    private static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var days = (int)(now - then).TotalDays;

        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 7 => $"{days} days ago",
            _ => $"{then.ToLocalTime():d MMM yyyy} ({Span(days)})",
        };
    }

    /// <summary>How long ago, in the largest unit that still says something.</summary>
    private static string Span(int days) => days switch
    {
        < 14 => $"{days / 7} week ago",
        < 60 => $"{days / 7} weeks ago",
        < 365 => $"{days / 30} months ago",
        < 730 => "a year ago",
        _ => $"{days / 365} years ago",
    };

    /// <summary>A count with its noun, pluralised and grouped.</summary>
    private static string Count(int n, string noun) =>
        $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// The plugin names, capped.
    ///
    /// <para>FOUR THEN A REMAINDER. A user with a dozen plugins gets a line rather than a paragraph,
    /// and the count above it already told them how many there are — the names are here to answer
    /// "which ones", which the first few do.</para>
    /// </summary>
    private static string Join(IReadOnlyList<string> names)
    {
        const int shown = 4;
        return names.Count <= shown
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(shown)) + $", +{names.Count - shown} more";
    }

    /// <summary>
    /// A path with the user's home written as <c>~</c>.
    ///
    /// <para>NOT TRUNCATED, unlike the session panel's version of this: that one fits a fixed column
    /// and this one has the transcript's full width, where an elided middle would hide the part of
    /// the path that says how the app was installed.</para>
    /// </summary>
    private static string Tilde(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }
}
