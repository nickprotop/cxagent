namespace CxAgent.Core.Plugins.Builtin;

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
/// previous version lived inside the plugin, and every attempt to change it produced failures that
/// could have come from the matcher, the span it returned, the shift applied afterwards, or a stale
/// build. Two strings in, a list of matches out — a wrong answer has one place to be.</para>
///
/// <para>TOLERANCE IS A SEARCH KEY, NOT A TRANSFORM. Matching may ignore whitespace to LOCATE the
/// target; what gets written is the model's own text, shifted at most by one uniform prefix. opencode
/// makes the point most clearly — it runs nine matchers of increasing tolerance and every success
/// path is the same verbatim splice. Tolerance in the matcher costs nothing; tolerance in the writer
/// is how a tool silently reshapes code.</para>
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
            found.Add(new PatternMatch(at, pattern.Length, WholeLines: CoversWholeLines(text, at, pattern.Length)));

        if (found.Count > 0) return found;

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

            found.Add(new PatternMatch(start, Math.Min(length - 1, text.Length - start), WholeLines: true));
        }

        return found;
    }

    /// <summary>
    /// Whether a span begins at a line start and ends at a line end — the condition for having
    /// indentation worth correcting.
    /// </summary>
    private static bool CoversWholeLines(string text, int start, int length)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, Math.Min(start, text.Length - 1))) + 1;

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
    /// <para>NOT "every whitespace character removed", which is what this did. That made `foo bar`
    /// match `foobar` — two different identifiers — so a replace aimed at one could edit the other.
    /// Tolerating INDENTATION and house-style spacing is the point; erasing the distinction between
    /// separated and joined tokens is a far larger claim, and one nobody asked for.</para>
    /// </summary>
    private static string Squash(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        var pending = false;
        foreach (var c in line.Trim())
        {
            if (char.IsWhiteSpace(c)) { pending = sb.Length > 0; continue; }
            if (pending) { sb.Append(' '); pending = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
