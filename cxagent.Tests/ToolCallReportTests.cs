using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The output is kept so a later surface can show it. Nothing renders it yet, which is exactly why
/// it needs a test: a field nothing reads is a field nothing notices breaking.
/// </summary>
public class ToolCallReportTests
{
    private static ToolCallReport Report(string? output) =>
        new("c1", "a1", "read_file", "file", "succeeded", 12, output?.Length ?? 0,
            DateTimeOffset.UtcNow) { Output = output };

    [Fact]
    public void AShortOutputIsKeptWhole()
        => Assert.Equal("hello", Report("hello").Output);

    [Fact]
    public void NoOutputIsNull()
        => Assert.Null(Report(null).Output);

    // CAPPED, BECAUSE THE SINK HOLDS A SESSION'S WORTH. One dotnet build returning megabytes would
    // otherwise sit in a list that lives as long as the window.
    [Fact]
    public void ALongOutputIsCappedWithAMarker()
    {
        var kept = ToolCallReport.Cap(new string('x', ToolCallReport.OutputCap * 3));

        Assert.True(kept!.Length <= ToolCallReport.OutputCap + 40,
            $"expected roughly the cap, got {kept.Length}");
        Assert.Contains("truncated", kept, StringComparison.OrdinalIgnoreCase);
    }

    // THE TRUE LENGTH IS NOT LOST. ResultChars still records what actually came back, so capping
    // the text costs nothing about size.
    [Fact]
    public void TheCapDoesNotChangeResultChars()
    {
        var full = new string('x', ToolCallReport.OutputCap * 2);
        var report = new ToolCallReport("c", "a", "t", null, "succeeded", 1, full.Length,
            DateTimeOffset.UtcNow) { Output = ToolCallReport.Cap(full) };

        Assert.Equal(full.Length, report.ResultChars);
        Assert.True(report.Output!.Length < full.Length);
    }

    [Fact]
    public void CapLeavesAShortStringAlone()
        => Assert.Equal("short", ToolCallReport.Cap("short"));
}
