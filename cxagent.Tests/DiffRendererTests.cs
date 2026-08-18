using CxAgent.UI;
using CxAgent.UI.Tools;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Structure into native markup. A pure function, so these need no terminal and no repository —
/// which is the reason the parser and the renderer are separate types at all.
/// </summary>
public class DiffRendererTests
{
    private static DiffLine Line(LineKind kind, params Span[] spans) =>
        new(kind, spans, kind switch
        {
            LineKind.Removed => new LineNumbers(41, null),
            LineKind.Added => new LineNumbers(null, 41),
            _ => new LineNumbers(41, 41),
        });

    private static FileDiff Diff(params DiffLine[] lines) =>
        new("UsageView.cs", 12, 3, DiffStatus.Changed, [new Hunk("@@ -41 +41 @@ record UsageView", lines)]);

    [Fact]
    public void HeaderCarriesThePathAndCounts()
    {
        var markup = DiffRenderer.Render(Diff(Line(LineKind.Context, new Span("x", false))));

        Assert.Contains("UsageView.cs", markup);
        Assert.Contains("+12", markup);
        Assert.Contains(DiffRenderer.Minus + "3", markup);
    }

    [Fact]
    public void GivesAChangedSpanAStrongerBackgroundThanItsRow()
    {
        // THE WHOLE POINT of the word-diff. Without a distinct span ground the row is a coloured
        // block and the reader is back to comparing two lines by eye.
        var markup = DiffRenderer.Render(Diff(
            Line(LineKind.Added, new Span("int Top", false), new Span(", int? GpuTop", true))));

        Assert.Contains(ColorScheme.DiffAddedRow, markup);
        Assert.Contains(ColorScheme.DiffAddedSpan, markup);
    }

    [Fact]
    public void DimsLineNumbersSoTheEyeLandsOnTheChange()
    {
        var markup = DiffRenderer.Render(Diff(Line(LineKind.Context, new Span("unchanged", false))));

        Assert.Contains(ColorScheme.MutedMarkup, markup);
    }

    [Fact]
    public void EscapesLiteralBracketsInSourceCode()
    {
        // C# is full of [Attribute] and int[]. Unescaped, "[InlineData]" is parsed AS MARKUP and
        // vanishes from the output — a silent failure, which is why it gets its own test rather
        // than being trusted to a live drive.
        var markup = DiffRenderer.Render(Diff(
            Line(LineKind.Removed, new Span("[InlineData(\"0\")]", true))));

        Assert.Contains("[[InlineData", markup);      // escaped, not swallowed
    }

    [Fact]
    public void CutsAtMaxLinesAndSaysSo()
    {
        // DiffCommand's rule, and it is not optional: "a diff that simply stops is one someone will
        // read as complete, and 'everything after this is fine' is the worst possible thing to imply
        // by accident."
        var many = Enumerable.Range(0, DiffRenderer.MaxLines + 50)
            .Select(i => Line(LineKind.Added, new Span($"line {i}", true)))
            .ToArray();

        var markup = DiffRenderer.Render(Diff(many));

        Assert.Contains("truncated", markup, StringComparison.OrdinalIgnoreCase);
        Assert.True(markup.Split('\n').Length < DiffRenderer.MaxLines + 50);
    }

    [Fact]
    public void SaysSoRatherThanRenderingAnEmptyBodyWhenThereIsNoChange()
    {
        var markup = DiffRenderer.Render(new FileDiff("f.cs", 0, 0, DiffStatus.NoChanges, []));

        Assert.Contains("no changes", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaysBinaryRatherThanRenderingNothing()
    {
        var markup = DiffRenderer.Render(new FileDiff("b.bin", 0, 0, DiffStatus.Binary, []));

        Assert.Contains("binary", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaysNotARepositoryPlainly()
    {
        var markup = DiffRenderer.Render(new FileDiff("f.cs", 0, 0, DiffStatus.NotARepository, []));

        Assert.Contains("not a git repository", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovedAndAddedRowsAreDistinguishable()
    {
        var markup = DiffRenderer.Render(Diff(
            Line(LineKind.Removed, new Span("old", true)),
            Line(LineKind.Added, new Span("new", true))));

        Assert.Contains(ColorScheme.DiffRemovedRow, markup);
        Assert.Contains(ColorScheme.DiffAddedRow, markup);
    }
}
