using CxAgent.Plugins.CloneFinder;
using Xunit;

namespace CxAgent.Plugins.CloneFinder.Tests;

public class DetectorTests
{
    private static CloneSource Source(string path, string text) =>
        new(path, Tokenizer.Normalise(text));

    private const string Block = """
        var raw = RunProcess(command, timeout);
        if (raw.ExitCode != 0) { return Empty; }
        var lines = raw.Output.Split('\n');
        foreach (var line in lines) { Parse(line); }
        Total = lines.Length;
        return Build(Total);
        """;

    /// <summary>The same block in two files is one clone with two places.</summary>
    [Fact]
    public void AnIdenticalBlockInTwoFilesIsOneClone()
    {
        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", Block)], new CloneQuery(MinLines: 5, MinTokens: 50));

        var clone = Assert.Single(clones);
        Assert.Equal(2, clone.Places.Count);
        Assert.Contains(clone.Places, p => p.Path == "a.cs");
        Assert.Contains(clone.Places, p => p.Path == "b.cs");
    }

    /// <summary>RENAMING DOES NOT HIDE IT — the case the tokeniser exists for, end to end.</summary>
    [Fact]
    public void RenamedIdentifiersStillMatch()
    {
        var renamed = Block.Replace("raw", "output").Replace("lines", "rows");

        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", renamed)], new CloneQuery(MinLines: 5, MinTokens: 50));

        Assert.Single(clones);
    }

    /// <summary>A CHANGED LITERAL BREAKS THE MATCH, because literals are kept.</summary>
    [Fact]
    public void AChangedLiteralIsNotTheSameBlock()
    {
        var altered = Block.Replace("!= 0", "!= 1");

        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", altered)], new CloneQuery(MinLines: 6, MinTokens: 50));

        Assert.Empty(clones);
    }

    [Fact]
    public void BlocksShorterThanTheMinimumAreNotReported()
    {
        var clones = Detector.Find(
            [Source("a.cs", "x = 1;"), Source("b.cs", "y = 1;")], new CloneQuery(MinLines: 6, MinTokens: 50));

        Assert.Empty(clones);
    }

    /// <summary>Three copies are ONE clone with three places, not three pairwise findings — a
    /// report listing the same block three times is three times the context for one fact.</summary>
    [Fact]
    public void ThreeCopiesAreOneCloneWithThreePlaces()
    {
        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", Block), Source("c.cs", Block)], new CloneQuery(MinLines: 5, MinTokens: 50));

        var clone = Assert.Single(clones);
        Assert.Equal(3, clone.Places.Count);
    }

    /// <summary>Overlapping windows merge into one maximal block rather than many shifted
    /// near-duplicates of the same finding.</summary>
    [Fact]
    public void OverlappingMatchesMergeIntoOneBlock()
    {
        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", Block)], new CloneQuery(MinLines: 5, MinTokens: 50));

        var clone = Assert.Single(clones);
        Assert.All(clone.Places, p => Assert.True(p.EndLine > p.StartLine));
    }

    /// <summary>A clone found twice inside ONE file is still a clone.</summary>
    [Fact]
    public void DuplicationWithinASingleFileIsFound()
    {
        var clones = Detector.Find([Source("a.cs", Block + "\n" + Block)], new CloneQuery(MinLines: 5, MinTokens: 50));

        var clone = Assert.Single(clones);
        Assert.Equal(2, clone.Places.Count);
    }

    [Fact]
    public void RepetitiveShortStatementsAreNotClones()
    {
        // The shape every real test file has: many short calls that normalise identically once
        // identifiers fold. Reporting these is what made a 62-file repo return 722 "clones".
        var repetitive = string.Join("\n", Enumerable.Range(0, 40)
            .Select(i => $"Assert.Equal(expected{i}, actual{i});"));

        var clones = Detector.Find(
            [Source("a.cs", repetitive), Source("b.cs", repetitive)],
            new CloneQuery(MinLines: 6, MinTokens: 50));

        Assert.Empty(clones);
    }

    /// <summary>Lines is the span the places actually cover — not a token count, not the span of
    /// one outlier place.</summary>
    [Fact]
    public void LinesReportsTheSpanThePlacesActuallyCover()
    {
        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", Block)], new CloneQuery(MinLines: 5, MinTokens: 50));

        // Block is six source lines, and every place covers all of them.
        var clone = Assert.Single(clones);
        Assert.Equal(6, clone.Lines);
        Assert.All(clone.Places, p => Assert.Equal(clone.Lines, p.EndLine - p.StartLine + 1));
    }

    /// <summary>When some copies carry a couple of extra shared lines, the region is still ONE
    /// finding — not one row for the short extent everywhere plus another for the long extent
    /// where the long copies live. The place counts differing between such rows is the tell that
    /// they restate each other.</summary>
    [Fact]
    public void ALongerVariantInSomeCopiesDoesNotSplitTheFinding()
    {
        var longer = Block + "\nGrandTotal = Total + 1;\nPublish(GrandTotal);";

        var clones = Detector.Find(
            [Source("a.cs", longer), Source("b.cs", longer),
             Source("c.cs", Block), Source("d.cs", Block), Source("e.cs", Block)],
            new CloneQuery(MinLines: 5, MinTokens: 50));

        var clone = Assert.Single(clones);
        Assert.Equal(5, clone.Places.Count);
    }

    /// <summary>A short block with its OWN population is a separate finding from a long block
    /// that happens to contain it: the extra places are information the long clone does not
    /// carry, so deduplication must keep both.</summary>
    [Fact]
    public void AShorterBlockWithManyMorePlacesIsItsOwnFinding()
    {
        var tail = string.Join("\n",
            "if (Total > limit) { Flush(buffer); }",
            "var report = Render(Total, width);",
            "Store(report, destination);",
            "if (report.Length == 0) { throw Fail(); }",
            "Publish(report, channel);",
            "buffer.Clear();",
            "Log(report);",
            "return report;");
        var longBlock = Block + "\n" + tail;

        var clones = Detector.Find(
            [Source("a.cs", longBlock), Source("b.cs", longBlock),
             Source("c.cs", Block), Source("d.cs", Block), Source("e.cs", Block),
             Source("f.cs", Block), Source("g.cs", Block), Source("h.cs", Block)],
            new CloneQuery(MinLines: 5, MinTokens: 50));

        Assert.Equal(2, clones.Count);
        Assert.Contains(clones, c => c.Places.Count == 8 && c.Lines == 6);
        Assert.Contains(clones, c => c.Places.Count == 2 && c.Lines == 14);
    }
}
