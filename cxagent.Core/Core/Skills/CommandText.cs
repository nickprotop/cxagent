using System.Text;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Skills;

/// <summary>What the user typed after a command's name: the whole of it, and the words as a shell
/// would split them. One value, because <c>$ARGUMENTS</c> and <c>$1</c>… are two views of the same
/// text and passing them apart lets the two disagree.</summary>
public sealed record TypedArguments(string All, IReadOnlyList<string> Words)
{
    public static TypedArguments From(string all) => new(all.Trim(), CommandText.SplitArguments(all));
}

/// <summary>A piece of a command body, found in the FILE's text before any argument is filled in.</summary>
public abstract record Segment
{
    public sealed record Literal(string Text) : Segment;

    /// <summary>A <c>!`command`</c> line, run before the prompt is sent.</summary>
    public sealed record Shell(string Command) : Segment;

    /// <summary>An <c>@path</c> token: what was written, and the path it names.</summary>
    public sealed record At(string Token, string Path) : Segment;
}

/// <summary>
/// The text half of expanding a command: splitting what was typed, quoting it for a shell, and finding
/// what in a body is a shell line or a file.
///
/// <para>SEGMENTS COME FROM THE FILE BEFORE ARGUMENTS GO IN. That order is the whole safety of the
/// feature: something the user typed can fill a placeholder, but it can never become a shell line or a
/// file include, because those were found before it was there.</para>
/// </summary>
public static partial class CommandText
{
    /// <summary>Words as a shell would split them: whitespace separates, quotes group.</summary>
    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var inWord = false;

        foreach (var c in arguments)
        {
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'') { quote = c; inWord = true; continue; }

            if (char.IsWhiteSpace(c))
            {
                if (inWord) { words.Add(current.ToString()); current.Clear(); inWord = false; }
                continue;
            }

            current.Append(c);
            inWord = true;
        }

        if (inWord) words.Add(current.ToString());
        return words;
    }

    /// <summary>
    /// One word <c>/bin/sh</c> reads literally: single quotes, with each embedded quote closed,
    /// escaped and reopened. Nothing inside single quotes is special to POSIX sh — not <c>;</c>, not
    /// <c>$</c>, not a backtick — so an argument can never end the command it was put into.
    /// </summary>
    public static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    [GeneratedRegex(@"!`([^`\n]+)`")]
    private static partial Regex ShellToken();

    // AN @ OPENS A TOKEN ONLY AT THE START OR AFTER WHITESPACE — the composer's own rule — so
    // an address like me@x.com is never a path.
    [GeneratedRegex(@"(?<=^|\s)@(\S+)")]
    private static partial Regex AtToken();

    [GeneratedRegex(@"\$(ARGUMENTS|[1-9])")]
    private static partial Regex Placeholder();

    /// <summary>The body split into literal text, shell lines and <c>@</c> tokens, in order.</summary>
    public static IReadOnlyList<Segment> Segments(string body)
    {
        var parts = new List<Segment>();
        var at = 0;

        foreach (Match shell in ShellToken().Matches(body))
        {
            AddLiteralAndFiles(parts, body[at..shell.Index]);
            parts.Add(new Segment.Shell(shell.Groups[1].Value));
            at = shell.Index + shell.Length;
        }

        AddLiteralAndFiles(parts, body[at..]);
        return parts;
    }

    private static void AddLiteralAndFiles(List<Segment> parts, string text)
    {
        var at = 0;
        foreach (Match token in AtToken().Matches(text))
        {
            // SENTENCE PUNCTUATION IS NOT PART OF A PATH. "Read @README.md." names README.md.
            var path = token.Groups[1].Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            if (path.Length == 0) continue;

            if (token.Index > at) parts.Add(new Segment.Literal(text[at..token.Index]));
            parts.Add(new Segment.At("@" + path, path));
            at = token.Index + 1 + path.Length;
        }

        if (at < text.Length) parts.Add(new Segment.Literal(text[at..]));
    }

    public static bool HasPlaceholder(string text) => Placeholder().IsMatch(text);

    /// <summary>
    /// Fills <c>$ARGUMENTS</c> and <c>$1</c>…<c>$9</c>. A missing positional becomes empty.
    /// <paramref name="quote"/> is for a shell line: each value becomes inert text for <c>/bin/sh</c>.
    /// </summary>
    public static string Substitute(string text, TypedArguments arguments, bool quote) =>
        quote ? SubstituteForShell(text, arguments)
              : Placeholder().Replace(text, m => ValueOf(m, arguments));

    /// <summary>
    /// A typed line split into the command's name and what follows it. ANY WHITESPACE ends the name —
    /// a tab is a separator in a terminal as much as a space is.
    /// </summary>
    public static (string Name, string Arguments) SplitLine(string line)
    {
        var text = line.Trim().TrimStart('/');
        var end = text.IndexOfAny([' ', '\t']);
        return end < 0 ? (text, "") : (text[..end], text[(end + 1)..].Trim());
    }

    private static string ValueOf(Match m, TypedArguments arguments) =>
        m.Groups[1].Value == "ARGUMENTS"
            ? arguments.All
            : int.Parse(m.Groups[1].Value) is var n && n <= arguments.Words.Count ? arguments.Words[n - 1] : "";

    /// <summary>
    /// Shell substitution that respects the quotes the file's AUTHOR wrote around a placeholder.
    ///
    /// <para>ONE ENCODING PER CONTEXT, because one is wrong in the others. A bare <c>$1</c> becomes a
    /// single-quoted word. Inside the author's <c>'…'</c> the value goes in with each <c>'</c> closed,
    /// escaped and reopened — wrapping it in quotes of its own would CLOSE the author's and leave the
    /// value bare, which is how <c>'$1'</c> with <c>x; echo hi</c> ran <c>echo hi</c>. Inside
    /// <c>"…"</c> the four characters still special there — <c>\ " $ `</c> — are backslashed.
    /// Claude Code files quote placeholders as often as not, so all three are ordinary input.</para>
    /// </summary>
    private static string SubstituteForShell(string text, TypedArguments arguments)
    {
        var sb = new StringBuilder();
        var state = '\0';          // '\0' outside quotes, else the quote character we are inside
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '$' && Placeholder().Match(text, i) is { Success: true } m && m.Index == i)
            {
                var value = ValueOf(m, arguments);
                sb.Append(state switch
                {
                    '\'' => value.Replace("'", "'\\''"),
                    '"' => DoubleQuoted(value),
                    _ => PosixQuote(value),
                });
                i += m.Length - 1;
                continue;
            }

            sb.Append(c);

            // A BACKSLASH OUTSIDE SINGLE QUOTES ESCAPES THE NEXT CHARACTER, so an escaped quote does
            // not open or close anything — copied through as written.
            if (c == '\\' && state != '\'' && i + 1 < text.Length)
            {
                sb.Append(text[++i]);
                continue;
            }

            if (state == '\0' && c is '\'' or '"') state = c;
            else if (state != '\0' && c == state) state = '\0';
        }

        return sb.ToString();
    }

    private static string DoubleQuoted(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '"' or '$' or '`') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
