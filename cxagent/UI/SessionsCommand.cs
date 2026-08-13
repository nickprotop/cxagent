using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>What <c>/sessions</c> decided to do, and the line to show for it.</summary>
/// <param name="ResumeUid">The session to restore, or null when nothing is being restored.</param>
/// <param name="Reply">The message for the transcript. Never empty.</param>
public readonly record struct SessionsCommandResult(string? ResumeUid, string Reply);

/// <summary>
/// <c>/sessions</c> — every conversation recorded in this folder, and a way back into one.
///
/// <para>WHAT IT REPLACES. The app knew about exactly one earlier session and offered it once, at
/// startup, before the user had typed anything — a decision about last time asked at the moment
/// someone sits down to do something else. Everything older was not hidden but UNREACHABLE: no
/// command in the app could name it.</para>
///
/// <para>THE DECISION IS SEPARATED FROM THE WIRING, like <see cref="ModeCommand"/>: rendering a list
/// and choosing what a number means are worth testing and need no window, no store and no agent.
/// </para>
/// </summary>
public static class SessionsCommand
{
    /// <summary>
    /// How many sessions are listed before the rest are summarised.
    ///
    /// <para>A folder worked in for months has hundreds. The list is a way back into recent work,
    /// not an archive index — and a screen of rows nobody reads is how a user stops reading the
    /// list at all.</para>
    /// </summary>
    public const int MaxRows = 20;

    /// <summary>
    /// Decides what <c>/sessions</c>, <c>/sessions all</c> or <c>/sessions resume X</c> should do.
    /// </summary>
    /// <param name="argument">Everything after the command word.</param>
    /// <param name="sessions">The rows to work with, newest first, already scoped by the caller.</param>
    /// <param name="retention">
    /// How long a finished session survives. STATED IN THE OUTPUT rather than left to be discovered:
    /// a user who loses a month of conversations to a policy nobody mentioned has a grievance.
    /// </param>
    public static SessionsCommandResult Decide(
        string argument, IReadOnlyList<SessionInfo> sessions, TimeSpan retention, bool all = false)
    {
        var words = argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length >= 1 && words[0].Equals("resume", StringComparison.OrdinalIgnoreCase))
            return Resume(words.Length > 1 ? words[1] : null, sessions);

        return new(null, Render(sessions, retention, all));
    }

    /// <summary>
    /// Turns what the user typed into a uid.
    ///
    /// <para>TWO WAYS TO NAME A SESSION, AND THEY ARE DIFFERENT PROMISES. A number belongs to the
    /// listing on screen right now — renumbered every time, meaningless in a script, ideal at a
    /// prompt. A uid is the session itself: stable, quotable, and the only form that works from the
    /// command line. Offering both is not redundancy.</para>
    /// </summary>
    private static SessionsCommandResult Resume(string? what, IReadOnlyList<SessionInfo> sessions)
    {
        if (string.IsNullOrWhiteSpace(what))
            return new(null, "[yellow]Which one? /sessions resume <number> or <id>.[/]");

        // A NUMBER IS A POSITION IN THE LIST THE USER JUST READ.
        if (int.TryParse(what, out var index))
        {
            if (index < 1 || index > sessions.Count)
                return new(null, $"[yellow]No session {index}. The list has {sessions.Count}.[/]");

            return new(sessions[index - 1].Uid, "");
        }

        // ...ANYTHING ELSE NAMES A UID — the short form from the listing, or a whole one pasted from
        // elsewhere. Resolved against the same list, so the ambiguity message can NAME the candidates
        // rather than only reporting that there were some.
        var matches = sessions.Where(s => MatchesShort(s.Uid, what)).ToList();

        if (matches.Count == 0)
            return new(null, $"[yellow]No session matches '{what}'.[/]");

        // AMBIGUITY IS REPORTED, NEVER RESOLVED. Picking the newest silently is how someone restores
        // the wrong conversation and does not find out for ten minutes.
        if (matches.Count > 1)
            return new(null, $"[yellow]'{what}' matches {matches.Count} sessions: "
                           + $"{string.Join(", ", matches.Take(4).Select(m => Short(m.Uid)))}"
                           + $"{(matches.Count > 4 ? ", …" : "")}. Use more characters.[/]");

        return new(matches[0].Uid, "");
    }

    private static string Render(IReadOnlyList<SessionInfo> sessions, TimeSpan retention, bool all)
    {
        var accent = ColorScheme.AccentMarkup;
        var muted = ColorScheme.MutedMarkup;
        var lines = new List<string>();

        if (sessions.Count == 0)
        {
            lines.Add($"[{accent}]Sessions[/]");
            lines.Add($"  [{muted}]none recorded "
                    + $"{(all ? "anywhere yet" : "in this folder yet")}.[/]");
            return string.Join('\n', lines);
        }

        lines.Add($"[{accent}]Sessions[/] [{muted}]· {sessions.Count}"
                + $"{(all ? " across every folder" : " here")}[/]");
        lines.Add("");

        for (var i = 0; i < Math.Min(sessions.Count, MaxRows); i++)
        {
            var s = sessions[i];

            // THE NUMBER, THE UID, AND THEN WHAT IT WAS ABOUT. The first two are how you name it; the
            // title is how you recognise it, and a row without one is a row nobody can act on.
            lines.Add($"  [{muted}]{i + 1,2}[/]  [{accent}]{Short(s.Uid)}[/]  "
                    + $"[{muted}]{Age(s.UpdatedAt),-10}{Tokens(s),8}[/]  "
                    + $"{Escape(s.Title ?? "(no messages yet)")}");

            // THE FOLDER, only when it could be a different one. Repeating the current directory on
            // every row of a folder-scoped list is noise.
            if (all && !string.IsNullOrWhiteSpace(s.WorkingDir))
                lines.Add($"      [{muted}]{Escape(s.WorkingDir!)}[/]");
        }

        if (sessions.Count > MaxRows)
            lines.Add($"  [{muted}]… {sessions.Count - MaxRows} older[/]");

        lines.Add("");
        lines.Add($"  [{muted}]/sessions resume <number|id>"
                + $"{(all ? "" : "  ·  /sessions all")}[/]");

        // THE RETENTION WINDOW, SAID OUT LOUD. These rows used to be an invisible buffer, and
        // deleting an invisible thing costs nobody anything. They are visible now.
        lines.Add($"  [{muted}]finished sessions are removed after {(int)retention.TotalDays} days[/]");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The rows the palette offers after <c>/sessions resume </c> — the same sessions, in the same
    /// order, named the way the listing names them.
    ///
    /// <para>COMPLETES TO THE NUMBER, DESCRIBES BY THE TITLE. The number is what the listing shows
    /// and the shortest thing to type; the title is the only part a user recognises. A palette of
    /// bare uids would be six commands' worth of hex and no way to tell which conversation is which.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CommandArgument> Completions(IReadOnlyList<SessionInfo> sessions)
    {
        var rows = new List<CommandArgument>();

        for (var i = 0; i < Math.Min(sessions.Count, MaxRows); i++)
        {
            var s = sessions[i];
            rows.Add(new CommandArgument(
                (i + 1).ToString(),
                $"{Short(s.Uid)}  {Age(s.UpdatedAt)}  {s.Title ?? "(no messages yet)"}"));
        }

        return rows;
    }

    /// <summary>
    /// Six characters, like git — and like git, the FIRST six, because
    /// <see cref="Helpers.UlidGenerator"/> now puts the randomness there.
    ///
    /// <para>It did not always. Ids used to open with a timestamp, so sessions started minutes apart
    /// shared a prefix: three made during this feature's own drive all rendered as <c>01KZXC</c>, an
    /// identifier that identified nothing. That was fixed where it was caused — in the generator —
    /// rather than compensated for here by printing the tail, which would have left the same
    /// collision in every other place an id is shown.</para>
    /// </summary>
    public static string Short(string uid) => uid.Length <= 6 ? uid : uid[..6];

    /// <summary>
    /// Does what the user typed name this session?
    ///
    /// <para>BOTH ENDS, because ids minted before the layout changed still exist in the store, and
    /// theirs is the half at the tail. A user reading one of those off a listing types what they see;
    /// refusing it would be a rule the output never explained, on rows that look no different.</para>
    /// </summary>
    public static bool MatchesShort(string uid, string typed) =>
        uid.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
        || uid.EndsWith(typed, StringComparison.OrdinalIgnoreCase);

    private static string Tokens(SessionInfo s)
    {
        var total = s.InputTokens + s.OutputTokens;
        return total >= 1_000_000 ? $"{total / 1_000_000.0:0.0}M"
             : total >= 1_000 ? $"{total / 1_000}k"
             : total.ToString();
    }

    /// <summary>How long ago, in the largest unit that still says something useful.</summary>
    private static string Age(DateTimeOffset when)
    {
        var age = DateTimeOffset.UtcNow - when;
        return age.TotalMinutes < 1 ? "just now"
             : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
             : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
             : age.TotalDays < 2 ? "yesterday"
             : $"{(int)age.TotalDays}d ago";
    }

    private static string Escape(string text) => SharpConsoleUI.Parsing.MarkupParser.Escape(text);
}
