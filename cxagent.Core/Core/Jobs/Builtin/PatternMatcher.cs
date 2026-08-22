namespace CxAgent.Core.Jobs.Builtin;

/// <summary>
/// Where a replace pattern occurs in a file, and how it was found.
/// </summary>
/// <param name="Start">Character offset of the match.</param>
/// <param name="Length">Length of the matched span.</param>
/// <param name="WholeLines">
/// True when the span covers complete lines, false when it is a fragment inside one.
///
/// <para>This distinction is the whole reason the result is a record rather than a tuple. A
/// whole-line match has indentation to correct; a fragment (`a + b` inside `int t = a + b;`) does
/// not, and prepending one splices whitespace into the middle of a statement. Callers were deriving
/// this by re-examining the text around the offset and getting it wrong — the matcher already knows,
/// so it says.</para>
/// </param>
public readonly record struct PatternMatch(int Start, int Length, bool WholeLines);

/// <summary>
/// Finds a replace pattern in a file's text.
///
/// <para>A PURE FUNCTION over two strings, for the same reason <see cref="IndentShift"/> is: the
/// previous version lived inside the executor, and every attempt to change it produced failures that
/// could have come from the matcher, the span it returned, the shift applied afterwards, or a stale
/// build. Two strings in, a list of matches out — a wrong answer has one place to be.</para>
///
/// <para>TOLERANCE IS A SEARCH KEY, NOT A TRANSFORM. Matching may ignore whitespace to LOCATE the
/// target; what gets written is the model's own text, shifted at most by one uniform prefix.
/// Matchers of increasing tolerance may all feed the same verbatim splice. Tolerance in the matcher
/// costs nothing; tolerance in the writer is how a tool silently reshapes code.</para>
/// </summary>
public static class PatternMatcher
{
    /// <summary>
    /// Every place <paramref name="pattern"/> occurs, in the order they appear.
    ///
    /// <para>Two passes, and the order matters. An EXACT substring match is what the caller literally
    /// asked for and is returned alone when it exists — the tolerant pass below would also match
    /// formatting variants the caller did not mean, and mixing the two would make an unambiguous
    /// request look ambiguous.</para>
    ///
    /// <para>Only when nothing matches exactly does the WHITESPACE-TOLERANT pass run, because a
    /// model cannot know a file's exact leading bytes. That pass is line-oriented: it compares whole
    /// lines with their whitespace normalised, which is why its matches are marked
    /// <see cref="PatternMatch.WholeLines"/>.</para>
    /// </summary>
    public static IReadOnlyList<PatternMatch> FindAll(string text, string pattern)
    {
        var found = new List<PatternMatch>();
        if (pattern.Length == 0) return found;

        // EXACT FIRST, and it can land mid-line — `a + b` inside `int t = a + b;`. The tolerant pass
        // below compares whole lines only, so without this a fragment reported "not found even
        // ignoring indentation": indentation blamed for a substring the matcher could not express.
        for (var at = text.IndexOf(pattern, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(pattern, at + 1, StringComparison.Ordinal))
        {
            var whole = CoversWholeLines(text, at, pattern.Length);

            // A WHOLE-LINE SPAN ALWAYS STARTS AT THE LINE, whichever pass found it. The exact pass
            // begins at the pattern's first character, so a pattern sent without indentation
            // produced a span that omitted the file's — while the tolerant pass, which slices whole
            // lines, produced one that included it. That asymmetry is invisible in the type and
            // pushed the correction onto every caller: extend both alike and the tolerant match is
            // double-counted, extend neither and the exact one is never corrected. Normalising here
            // means a span means the same thing regardless of how it was found.
            var start = at;
            var length = pattern.Length;
            if (whole)
            {
                var lineStart = text.LastIndexOf('\n', Math.Max(0, Math.Min(at, text.Length - 1))) + 1;
                length += at - lineStart;
                start = lineStart;
            }

            // A ZERO-LENGTH SPAN IS NOT A MATCH, wherever it comes from. The exact pass produces one
            // for a pattern that is a single newline at the end of the file: nothing to replace, and
            // counting it makes an otherwise unambiguous edit "appear 2 times".
            if (length > 0) found.Add(new PatternMatch(start, length, whole));
        }

        // The exact pass does NOT short-circuit. Tempting, and wrong: a file holding both
        // `if (x) {` and `if (x)  {` gives the first an exact hit, and returning it alone edits one
        // of two indistinguishable targets. A model cannot see which spacing a file uses — that is
        // the whole reason tolerance exists — so it cannot have MEANT one variant over the other,
        // and picking silently is exactly the "wrong line changed in a file nobody is watching"
        // failure this tool exists to prevent.
        //
        // Running both passes and reporting every hit lets the caller refuse. Duplicates are removed
        // below, so an exact match is not counted twice for also matching tolerantly.
        var patternLines = Split(pattern);
        var textLines = Split(text);

        // Offsets computed once: recomputing a prefix sum inside the loop made this quadratic on a
        // large file, for a value that only ever moves forward.
        var lineOffsets = new int[textLines.Length];
        var running = 0;
        for (var i = 0; i < textLines.Length; i++)
        {
            lineOffsets[i] = running;
            running += textLines[i].Length + 1;
        }

        for (var i = 0; i + patternLines.Length <= textLines.Length; i++)
        {
            var all = true;
            for (var j = 0; j < patternLines.Length && all; j++)
                all = Squash(textLines[i + j]) == Squash(patternLines[j]);
            if (!all) continue;

            var start = lineOffsets[i];
            var length = 0;
            for (var k = 0; k < patternLines.Length; k++) length += textLines[i + k].Length + 1;

            // CLAMPED AT ZERO, not just at the remaining text. `text.Length - start` goes NEGATIVE
            // when a pattern's trailing newline puts the span's start past the end of the file, and
            // PatternMatch then reached Substring with length -1: `replace` with pattern "\n" came
            // back to the model as "length ('-1') must be a non-negative value. (Parameter
            // 'length')" — an internal argument name, describing nothing it could act on.
            var available = Math.Max(0, text.Length - start);
            var span = new PatternMatch(start, Math.Min(length - 1, available), WholeLines: true);

            // A ZERO-LENGTH SPAN IS NOT A MATCH. It arises from a pattern that squashes to nothing
            // against a blank line, and counting it makes an otherwise unambiguous edit "appear 2
            // times" — a refusal caused entirely by an empty span nobody could have meant.
            if (span.Length == 0) continue;

            // An exact whole-line hit was already normalised to this same span, so it would appear
            // twice and turn a single unambiguous edit into a refusal.
            if (!found.Any(f => f.Start == span.Start && f.Length == span.Length)) found.Add(span);
        }

        return found.OrderBy(f => f.Start).ToList();
    }

    /// <summary>
    /// Whether a span begins at a line start and ends at a line end — the condition for having
    /// indentation worth correcting.
    /// </summary>
    private static bool CoversWholeLines(string text, int start, int length)
    {
        // NEVER PAST `start`. LastIndexOf searches AT the index too, so a match that begins on a
        // newline finds that same newline and yields lineStart = start + 1 — and the slice below
        // became text[start+1..start], which throws "length ('-1') must be a non-negative value".
        // A model that sent pattern "\n" got that sentence back as its tool result.
        var lineStart = Math.Min(
            text.LastIndexOf('\n', Math.Max(0, Math.Min(start, text.Length - 1))) + 1,
            start);

        // Only whitespace before it on its line: the span starts the line's content.
        if (text[lineStart..start].Trim().Length > 0) return false;

        // And nothing but the line ending after it.
        var end = start + length;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return end >= text.Length || text[end] == '\n' || text[end] == '\r';
    }

    private static string[] Split(string s) => s.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// A line reduced to what a whitespace-tolerant comparison should see: outer whitespace gone,
    /// interior runs collapsed to a single space.
    ///
    /// <para>WHITESPACE SURVIVES ONLY BETWEEN TWO WORD CHARACTERS, which is the line between the
    /// two things that must not be confused:</para>
    /// <list type="bullet">
    ///   <item>`Estimate (int n)` and `Estimate(int n)` are the SAME code in different house styles,
    ///     and a model writing standard C# against a file that spaces before the paren must still
    ///     match it. Removing every whitespace character was how that worked.</item>
    ///   <item>`foo bar` and `foobar` are two DIFFERENT identifiers, and removing every whitespace
    ///     character made them equal — so a replace aimed at one could silently edit the other.</item>
    /// </list>
    /// <para>A space between `)` and `{`, or before a paren, is formatting. A space between `o` and
    /// `b` is a token boundary. Keeping only the latter tolerates style while preserving meaning.</para>
    /// </summary>
    private static string Squash(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        var pending = false;
        foreach (var c in line.Trim())
        {
            if (char.IsWhiteSpace(c)) { pending = sb.Length > 0; continue; }

            // Only when it separates two word characters does the gap carry meaning.
            if (pending && sb.Length > 0 && IsWord(sb[^1]) && IsWord(c)) sb.Append(' ');
            pending = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
