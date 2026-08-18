using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Tools;

/// <summary>
/// A parsed diff as native SharpConsoleUI markup.
///
/// <para>A PURE FUNCTION, which is why it is its own type: everything hard about rendering a diff —
/// escaping, the cut, which ground goes under which run — is decided here and can be tested without
/// a terminal, a repository or a running session.</para>
///
/// <para>NATIVE MARKUP, NOT MARKDOWN. The transcript's Tool role is markdown by default, which
/// escapes <c>[</c> and would print <c>[#7ee787]</c> literally. InlineJobSink turns that off for
/// this one plugin type; what this produces is meaningless without that.</para>
/// </summary>
public static class DiffRenderer
{
    /// <summary>
    /// The minus sign in the header count, U+2212 — not a hyphen.
    ///
    /// <para>Named because it is otherwise invisible in a diff of this file, and a hyphen typed over
    /// it would look identical in most editors while changing what the header reads as.</para>
    /// </summary>
    public const string Minus = "−";

    /// <summary>
    /// How many body lines reach the transcript.
    ///
    /// <para>DiffCommand's figure, and its reasoning applies unchanged: a five-thousand-line diff is
    /// not review material, it is scrollback. One file rarely reaches this, which is the point of
    /// the per-file scope — but a generated file can, and the cut must exist before it does.</para>
    /// </summary>
    public const int MaxLines = 400;

    public static string Render(FileDiff diff)
    {
        var sb = new System.Text.StringBuilder();

        // THE HEADER IS ALWAYS THE SAME SHAPE, whatever the status. It is what the row collapses to,
        // so a reader scanning the transcript sees the filename and the size of the change on every
        // one of these rows without expanding any of them.
        sb.Append($"[{ColorScheme.AccentMarkup}]{MarkupParser.Escape(diff.Path)}[/]");

        if (diff.Status == DiffStatus.Changed)
            sb.Append($"  [{ColorScheme.DiffAddedMarkup}]+{diff.Added}[/] "
                + $"[{ColorScheme.DiffRemovedMarkup}]{Minus}{diff.Removed}[/]");

        sb.Append('\n');

        // ONE LINE, NOT AN EMPTY BODY, for every status that has nothing to draw. An empty box reads
        // as a rendering bug; "no changes" reads as the answer it is.
        switch (diff.Status)
        {
            case DiffStatus.NoChanges:
                sb.Append($"\n[{ColorScheme.MutedMarkup}]no changes[/]");
                return sb.ToString();

            case DiffStatus.Binary:
                sb.Append($"\n[{ColorScheme.MutedMarkup}]binary file — nothing to show[/]");
                return sb.ToString();

            case DiffStatus.NotARepository:
                sb.Append($"\n[{ColorScheme.MutedMarkup}]not a git repository[/]");
                return sb.ToString();
        }

        var written = 0;
        var truncated = false;

        foreach (var hunk in diff.Hunks)
        {
            if (written >= MaxLines) { truncated = true; break; }

            // GIT'S OWN HEADER, kept for the enclosing function it appends after the @@ ranges —
            // "record UsageView" is the orientation a reader wants, and the ranges themselves are
            // noise we would otherwise have to reproduce.
            sb.Append($"\n[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(Context(hunk.Header))}[/]\n");

            foreach (var line in hunk.Lines)
            {
                if (written >= MaxLines) { truncated = true; break; }

                sb.Append(RenderLine(line));
                sb.Append('\n');
                written++;
            }
        }

        // STATED, NEVER SILENT. DiffCommand's rule, and the reason is worth repeating: a diff that
        // simply stops is one someone will read as complete, and "everything after this is fine" is
        // the worst possible thing to imply by accident.
        if (truncated)
            sb.Append($"\n[{ColorScheme.MutedMarkup}]truncated at {MaxLines} lines "
                + $"— run git diff for the rest[/]");

        return sb.ToString();
    }

    /// <summary>The part of a hunk header after the closing <c>@@</c>: the enclosing function git
    /// supplies. Empty when git had nothing to say, in which case the ranges are dropped rather than
    /// shown, since they orient nobody.</summary>
    private static string Context(string header)
    {
        var close = header.LastIndexOf("@@", StringComparison.Ordinal);
        return close < 0 || close + 2 >= header.Length ? "" : header[(close + 2)..].Trim();
    }

    private static string RenderLine(DiffLine line)
    {
        var (marker, row, span, text) = line.Kind switch
        {
            LineKind.Added => ('+', ColorScheme.DiffAddedRow, ColorScheme.DiffAddedSpan, ColorScheme.DiffAddedMarkup),
            LineKind.Removed => ('-', ColorScheme.DiffRemovedRow, ColorScheme.DiffRemovedSpan, ColorScheme.DiffRemovedMarkup),
            _ => (' ', "", "", ""),
        };

        var sb = new System.Text.StringBuilder();

        // THE GUTTER IS DIMMED so the eye lands on the change rather than on the numbers. It shows
        // the line's own side: an added line has no old number and a removed line has no new one,
        // and printing a placeholder for the missing half implies a correspondence that is not there.
        var number = line.Numbers.New ?? line.Numbers.Old;
        sb.Append($"[{ColorScheme.MutedMarkup}]{number,4}[/] ");

        if (line.Kind == LineKind.Context)
        {
            // No ground at all for context. A surface behind every line would make the whole block
            // one colour and leave the changed rows with nothing to stand out against.
            sb.Append($"  {MarkupParser.Escape(Text(line))}");
            return sb.ToString();
        }

        sb.Append($"[{text} on {row}]{marker} [/]");

        foreach (var s in line.Spans)
        {
            var ground = s.Changed ? span : row;
            sb.Append($"[{text} on {ground}]{MarkupParser.Escape(s.Text)}[/]");
        }

        return sb.ToString();
    }

    private static string Text(DiffLine line) => string.Concat(line.Spans.Select(s => s.Text));
}
