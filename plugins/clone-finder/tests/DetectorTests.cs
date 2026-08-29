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
            [Source("a.cs", Block), Source("b.cs", Block)], minLines: 5);

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
            [Source("a.cs", Block), Source("b.cs", renamed)], minLines: 5);

        Assert.Single(clones);
    }

    /// <summary>A CHANGED LITERAL BREAKS THE MATCH, because literals are kept.</summary>
    [Fact]
    public void AChangedLiteralIsNotTheSameBlock()
    {
        var altered = Block.Replace("!= 0", "!= 1");

        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", altered)], minLines: 6);

        Assert.Empty(clones);
    }

    [Fact]
    public void BlocksShorterThanTheMinimumAreNotReported()
    {
        var clones = Detector.Find(
            [Source("a.cs", "x = 1;"), Source("b.cs", "y = 1;")], minLines: 6);

        Assert.Empty(clones);
    }

    /// <summary>Three copies are ONE clone with three places, not three pairwise findings — a
    /// report listing the same block three times is three times the context for one fact.</summary>
    [Fact]
    public void ThreeCopiesAreOneCloneWithThreePlaces()
    {
        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", Block), Source("c.cs", Block)], minLines: 5);

        var clone = Assert.Single(clones);
        Assert.Equal(3, clone.Places.Count);
    }

    /// <summary>Overlapping windows merge into one maximal block rather than many shifted
    /// near-duplicates of the same finding.</summary>
    [Fact]
    public void OverlappingMatchesMergeIntoOneBlock()
    {
        var clones = Detector.Find(
            [Source("a.cs", Block), Source("b.cs", Block)], minLines: 5);

        var clone = Assert.Single(clones);
        Assert.All(clone.Places, p => Assert.True(p.EndLine > p.StartLine));
    }

    /// <summary>A clone found twice inside ONE file is still a clone.</summary>
    [Fact]
    public void DuplicationWithinASingleFileIsFound()
    {
        var clones = Detector.Find([Source("a.cs", Block + "\n" + Block)], minLines: 5);

        var clone = Assert.Single(clones);
        Assert.Equal(2, clone.Places.Count);
    }
}
