using CxAgent.Plugins.CloneFinder;
using Xunit;

namespace CxAgent.Plugins.CloneFinder.Tests;

public class ReportTests
{
    private static Clone Clone(int lines, params string[] paths) =>
        new(lines, paths.Select(p => new Occurrence(p, 10, 10 + lines)).ToList(),
            ["var raw = RunProcess(command);", "if (raw.ExitCode != 0) return;"]);

    /// <summary>Biggest first, by lines x places: the order a person would fix them in.</summary>
    [Fact]
    public void ClonesAreRankedByLinesTimesPlaces()
    {
        var rendered = Report.Render(
            [Clone(10, "small.cs", "small2.cs"), Clone(40, "big.cs", "big2.cs")],
            maxResults: 20, belowMinimum: 0);

        Assert.True(rendered.IndexOf("big.cs") < rendered.IndexOf("small.cs"));
    }

    /// <summary>THE CAP ADMITS ITSELF. A list that hides its own truncation reads as complete.</summary>
    [Fact]
    public void TheReportSaysWhatItOmitted()
    {
        var many = Enumerable.Range(0, 30).Select(i => Clone(10, $"a{i}.cs", $"b{i}.cs")).ToList();

        var rendered = Report.Render(many, maxResults: 20, belowMinimum: 340);

        Assert.Contains("10", rendered);   // 30 - 20 omitted by the cap
        Assert.Contains("340", rendered);  // below the minimum size
    }

    /// <summary>Every place is named with its line range, because the point is to send the reader
    /// to the code rather than to reproduce it.</summary>
    [Fact]
    public void EveryPlaceIsNamedWithItsLines()
    {
        var rendered = Report.Render([Clone(12, "x.cs", "y.cs")], maxResults: 20, belowMinimum: 0);

        Assert.Contains("x.cs:10-22", rendered);
        Assert.Contains("y.cs:10-22", rendered);
    }

    /// <summary>THE FINGERPRINT IS SHORT ON PURPOSE. Printing whole blocks spends the context this
    /// plugin exists to preserve; printing nothing makes the model open files to find out what a
    /// hit is, which spends it too.</summary>
    [Fact]
    public void TheFingerprintIsAFewLinesNotTheWholeBlock()
    {
        var rendered = Report.Render([Clone(47, "x.cs", "y.cs")], maxResults: 20, belowMinimum: 0);

        Assert.Contains("var raw = RunProcess(command);", rendered);
        Assert.True(rendered.Split('\n').Length < 20);
    }

    /// <summary>THE PLACE LIST IS CAPPED. A 29-place finding needs to say it is everywhere and
    /// where to start, not name all 29 — listed in full, the top findings alone would spend the
    /// context the report exists to save.</summary>
    [Fact]
    public void APlaceListBeyondTheCapBecomesACount()
    {
        var paths = Enumerable.Range(0, 30).Select(i => $"p{i:00}.cs").ToArray();

        var rendered = Report.Render([Clone(15, paths)], maxResults: 20, belowMinimum: 0);

        Assert.Contains("p02.cs", rendered);        // the third place is still named...
        Assert.DoesNotContain("p03.cs", rendered);  // ...the fourth is not
        Assert.Contains("27 more places", rendered);
    }

    [Fact]
    public void NoClonesSaysSoPlainly()
    {
        var rendered = Report.Render([], maxResults: 20, belowMinimum: 0);
        Assert.Contains("no duplication", rendered, StringComparison.OrdinalIgnoreCase);
    }
}
