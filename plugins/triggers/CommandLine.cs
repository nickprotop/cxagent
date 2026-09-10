namespace CxAgent.Plugins.Triggers;

/// <summary>
/// What a person types, and what they read back.
///
/// <para>A COMMAND'S ARGUMENT IS ONE RAW STRING, so parity of CAPABILITY is not parity of SHAPE. A
/// tool gets named fields a schema validates; a command gets whatever was typed, and this is the
/// grammar that turns one into the other.</para>
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Splits `&lt;when&gt; &lt;prompt&gt;`, honouring quotes around a cron line.
    ///
    /// <para>THE FIRST WHITESPACE-DELIMITED TOKEN IS THE SCHEDULE, everything after it is the
    /// prompt verbatim — except a cron line, which is quoted because it is the one form of the
    /// three that itself contains spaces.</para>
    /// </summary>
    public static bool TrySplit(string arguments, out string? whenText, out string? prompt,
        out string? refusal)
    {
        whenText = null;
        prompt = null;

        var text = arguments.Trim();
        if (text.Length == 0)
        {
            refusal = "say when and what: `<when> <prompt>` — for example "
                    + "`20m check whether the deploy finished`.";
            return false;
        }

        int split;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
            {
                refusal = "the opening quote is never closed. A cron line is quoted because it is "
                        + "the one form with spaces in it: `\"0 9 * * 1-5\" post the reminder`.";
                return false;
            }
            whenText = text[1..close];
            split = close + 1;
        }
        else
        {
            split = text.IndexOf(' ');
            if (split < 0)
            {
                refusal = $"'{text}' says when but not what. Add the prompt after it.";
                return false;
            }
            whenText = text[..split];
        }

        prompt = text[split..].Trim();
        if (prompt.Length == 0)
        {
            refusal = $"'{whenText}' says when but not what. Add the prompt after it.";
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>
    /// Reads one typed token as a schedule, telling the three shapes apart.
    ///
    /// <para>SNIFFED HERE, AND THAT IS NOT WHAT THE TOOL REFUSED TO DO. trigger_wake will not guess
    /// between the three fields because a MODEL can name the one it means and a wrong guess is a
    /// silent mis-schedule. A PERSON typing at a palette cannot name a field, and the three shapes
    /// do not overlap — so an ambiguous token is refused on screen rather than picked between.</para>
    /// </summary>
    public static bool TryReadWhen(string text, out When? when, out string? refusal)
    {
        if (When.TryParseDuration(text, out _))
            return When.TryParse(text, null, null, out when, out refusal);

        // A DATETIME IS CHECKED BEFORE THE CRON HEURISTIC, because "2026-09-10 09:00" also
        // contains a space. THE PARSE ITSELF IS THE TEST — not a punctuation guess like Contains('-')
        // — because a cron line's day-of-week range ("1-5") carries the same characters a date does
        // and only an actual parse tells "0 9 * * 1-5" apart from "2026-09-10 09:00".
        if (DateTimeOffset.TryParse(text, out _))
            return When.TryParse(null, text, null, out when, out refusal);

        if (text.Contains(' ') || text.Contains('*'))
            return When.TryParse(null, null, text, out when, out refusal);

        when = null;
        refusal = $"'{text}' is not a schedule. Use a duration (20m, 2h), a time "
                + "(09:00 or 2026-09-10 09:00), or a quoted cron line (\"0 9 * * 1-5\").";
        return false;
    }

    /// <summary>
    /// The listing shown by both the <c>trigger_list</c> tool and the slash command.
    ///
    /// <para>NO LINE MAY START WITH A BARE <c>N.</c>. The slash command's text is rendered as
    /// markdown, and a leading "4." reads to that renderer as an ordered-list item, which it
    /// renumbers from 1 — so the id a user reads on screen would stop being the id they must
    /// retype into <c>/triggers-cancel</c>. <c>#4</c> carries the same digits but is not
    /// list-item syntax, so it survives both the tool's plain output and the command's markdown
    /// rendering unchanged.</para>
    /// </summary>
    public static string Render(IReadOnlyList<Trigger> triggers)
    {
        if (triggers.Count == 0) return "no triggers pending in this session.";

        return string.Join('\n', triggers.Select(t =>
            $"#{t.Id} {t.When.Describe()} — {Clip(FirstLine(t.Prompt), 60)}"));
    }

    private static string FirstLine(string prompt)
    {
        var newline = prompt.IndexOf('\n');
        if (newline < 0) return prompt;

        // TRIMEND THE '\r', not just the '\n': a CRLF prompt leaves one behind after splitting on
        // '\n' alone, and it renders as a stray box or space at the end of the listing line.
        return prompt[..newline].TrimEnd('\r');
    }

    // AN ELLIPSIS, NOT A HARD CUT, so a listing of long prompts still fits one line per trigger
    // without the reader mistaking a truncated line for the whole prompt.
    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
