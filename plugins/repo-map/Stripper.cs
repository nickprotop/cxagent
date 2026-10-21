using System.Text;

namespace CxAgent.Plugins.RepoMap;

/// <summary>
/// A file's text twice over, both the same length line for line: <see cref="Code"/> with comments
/// and the insides of strings blanked, and <see cref="Display"/> with only the comments blanked.
/// </summary>
/// <param name="Code">
/// What the outliner READS. A brace inside a string or a commented-out method is not code, and
/// counting it would close a class early or report a method nobody can call.
/// </param>
/// <param name="Display">
/// What a signature is QUOTED from, so a default argument of <c>", "</c> shows as written rather
/// than as two spaces.
/// </param>
public sealed record Stripped(string[] Code, string[] Display);

/// <summary>
/// Blanks comments and string contents, keeping every other character where it was.
///
/// <para>SPACES, NOT DELETION. The outliner reads a declaration from <see cref="Stripped.Code"/>
/// and quotes it from <see cref="Stripped.Display"/> at the same columns; removing a comment would
/// shift one against the other, and a signature would be cut in the wrong place.</para>
///
/// <para>WRONG IS LOCAL. A string this does not recognise — a C# interpolation with quotes nested
/// inside it, say — ends at the line, not at the end of the file: an ordinary string cannot span
/// lines in any language here, so a newline inside one is taken as the end of it. One bad line
/// costs one declaration, not the rest of the file.</para>
/// </summary>
public static class Stripper
{
    private enum State { Code, LineComment, BlockComment, Text }

    public static Stripped Strip(string text, Language language)
    {
        // ONE LINE ENDING BEFORE ANYTHING ELSE: a \r inside a string is blanked in one output and
        // kept in the other, and the two would stop lining up at the first Windows line ending.
        text = text.Replace("\r\n", "\n");
        var code = new StringBuilder(text.Length);
        var display = new StringBuilder(text.Length);
        var state = State.Code;

        // What closes the string being read, and how it escapes. Set when a string opens.
        var closer = "";
        var escapes = true;
        var spansLines = false;

        void Keep(char c) { code.Append(c); display.Append(c); }
        void Blank(char c) { var b = c == '\n' ? '\n' : ' '; code.Append(b); display.Append(b); }
        void Quote(char c) { code.Append(c == '\n' ? '\n' : ' '); display.Append(c); }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            switch (state)
            {
                case State.LineComment:
                    if (c == '\n') { Keep(c); state = State.Code; }
                    else Blank(c);
                    continue;

                case State.BlockComment:
                    if (c == '*' && next == '/') { Blank(c); Blank(next); i++; state = State.Code; }
                    else Blank(c);
                    continue;

                case State.Text:
                    if (c == '\n' && !spansLines) { Keep(c); state = State.Code; continue; }
                    if (escapes && c == '\\' && i + 1 < text.Length && text[i + 1] != '\n')
                    {
                        Quote(c); Quote(text[i + 1]); i++;
                        continue;
                    }
                    if (string.CompareOrdinal(text, i, closer, 0, closer.Length) == 0)
                    {
                        // A VERBATIM STRING ESCAPES A QUOTE BY DOUBLING IT, so "" inside @"…" is
                        // one character of the string, not its end and a new string's start.
                        if (!escapes && closer == "\"" && next == '"' && language.VerbatimStrings)
                        {
                            Quote(c); Quote(next); i++;
                            continue;
                        }
                        foreach (var d in closer) Keep(d);
                        i += closer.Length - 1;
                        state = State.Code;
                        continue;
                    }
                    Quote(c);
                    continue;
            }

            // State.Code from here on.
            if (language.SlashComments && c == '/' && next == '/') { state = State.LineComment; Blank(c); continue; }
            if (language.SlashComments && c == '/' && next == '*') { state = State.BlockComment; Blank(c); Blank(next); i++; continue; }
            if (language.HashComments && c == '#' && HashStartsComment(text, i, language))
            {
                state = State.LineComment; Blank(c);
                continue;
            }

            if (language.RegexLiterals && c == '/' && RegexMayStart(text, i) && RegexEnd(text, i) is > 0 and var end)
            {
                Keep(c);
                for (var j = i + 1; j < end; j++) Quote(text[j]);
                Keep(text[end]);
                i = end;
                continue;
            }

            if (language.TripleQuotes && (c == '"' || c == '\'') && next == c && i + 2 < text.Length && text[i + 2] == c
                && (c == '"' || language.Family == Family.Python))
            {
                closer = new string(c, 3);
                escapes = language.Family == Family.Python;
                spansLines = true;
                Open(closer);
                i += 2;
                continue;
            }

            if (language.VerbatimStrings && c == '@' && next == '"')
            {
                closer = "\""; escapes = false; spansLines = true;
                Keep(c); Keep(next); i++;
                state = State.Text;
                continue;
            }

            if (c == '"' || (c == '\'' && !IsLifetime(text, i, language)) || (c == '`' && language.Backticks))
            {
                closer = c.ToString();
                escapes = !(c == '`' && language.Family == Family.Go) && !(c == '\'' && language.Family == Family.Shell);
                spansLines = c == '`' || (c == '\'' && language.Family == Family.Shell);
                Open(closer);
                continue;
            }

            Keep(c);
        }

        return new Stripped(Lines(code), Lines(display));

        void Open(string delimiter)
        {
            foreach (var d in delimiter) Keep(d);
            state = State.Text;
        }
    }

    /// <summary>
    /// Whether a <c>#</c> opens a comment.
    ///
    /// <para>IN SHELL, ONLY AFTER WHITESPACE OR AT THE START: <c>$#</c> is the argument count and
    /// <c>${#name}</c> a length, and treating either as a comment would blank the rest of a function
    /// header. Python, Ruby and PHP have no such use, so there a <c>#</c> anywhere outside a string
    /// starts one.</para>
    /// </summary>
    private static bool HashStartsComment(string text, int i, Language language)
    {
        if (language.Family != Family.Shell) return true;
        return i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] == ';';
    }

    /// <summary>
    /// A Rust lifetime (<c>'a</c>) rather than a character literal (<c>'a'</c>, <c>'\n'</c>).
    /// </summary>
    private static bool IsLifetime(string text, int i, Language language)
    {
        if (!language.Lifetimes) return false;
        if (i + 1 < text.Length && text[i + 1] == '\\') return false;
        return !(i + 2 < text.Length && text[i + 2] == '\'');
    }

    /// <summary>Words after which a value, and so a regex, can start.</summary>
    private static readonly HashSet<string> RegexAfterWords =
        ["return", "typeof", "case", "yield", "await", "in", "of", "void", "delete", "throw", "else", "do", "instanceof", "new"];

    /// <summary>
    /// Whether a <c>/</c> is where a value can start, which makes it a regex rather than a division.
    ///
    /// <para>AFTER AN OPERATOR, AN OPENING BRACKET, A KEYWORD OR NOTHING ON THE LINE. After a name, a
    /// number or a closing bracket a value has just ended, and a slash divides it. Never after
    /// <c>&lt;</c> or <c>&gt;</c>: in JSX <c>&lt;/div&gt;</c> closes a tag, and reading it as a regex
    /// would blank markup up to the next slash, braces and all.</para>
    /// </summary>
    private static bool RegexMayStart(string text, int i)
    {
        var k = i - 1;
        while (k >= 0 && text[k] is ' ' or '\t') k--;
        if (k < 0 || text[k] == '\n') return true;
        var before = text[k];
        if ("=(,:[!&|?{};+-*%~^".Contains(before)) return true;
        if (!(char.IsLetterOrDigit(before) || before is '_' or '$')) return false;

        var start = k;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$')) start--;
        return RegexAfterWords.Contains(text[start..(k + 1)]);
    }

    /// <summary>
    /// Where the regex opened at <paramref name="i"/> closes: the next <c>/</c> not escaped and not in
    /// a <c>[…]</c> class, on the same line. -1 when the line ends first — a regex cannot span lines,
    /// so that slash was not one after all, and it is left as code.
    /// </summary>
    private static int RegexEnd(string text, int i)
    {
        var inClass = false;
        for (var j = i + 1; j < text.Length; j++)
        {
            var c = text[j];
            if (c == '\n') return -1;
            if (c == '\\') { j++; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass) return j;
        }
        return -1;
    }

    private static string[] Lines(StringBuilder text) =>
        text.ToString().Split('\n');
}
