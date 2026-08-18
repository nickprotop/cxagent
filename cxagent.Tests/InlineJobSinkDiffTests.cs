using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That a rendered diff row is treated as an ANSWER rather than as a tool echo.
///
/// <para>WHY THIS FILE EXISTS AT ALL: the plan for this feature named ONE collapse site and would
/// have shipped a diff that renders and then collapses itself, with every other test still green.
/// The second site is guarded by <c>IsCompactRow</c>, which returned true for everything that was
/// not llm_agent or todo — so show_diff qualified as a compact row and took
/// <c>SetExpanded(id, false)</c> on the branch for a compact row WITH a non-empty body, which is
/// precisely what a rendered diff is.</para>
///
/// <para>These assert through <c>IsCompactRowForTest</c> because the collapse itself is invisible
/// from outside: that seam exists because the rendering "is only observable through a UI queue the
/// tests cannot drain".</para>
/// </summary>
public class InlineJobSinkDiffTests
{
    private static Job DiffJob(JobState state = JobState.Succeeded, string body = "") =>
        new()
        {
            Id = "d1",
            AgentId = "g1",
            PluginType = "show_diff",
            DisplayName = "show_diff",
            State = state,
            Result = new JobResult
            {
                Success = state == JobState.Succeeded,
                Output = body.Length > 0 ? new() { ["content"] = body } : new(),
                Duration = TimeSpan.Zero,
            },
        };

    private static Job ToolJob(string type = "file", string body = "some output") =>
        new()
        {
            Id = "t1",
            AgentId = "g1",
            PluginType = type,
            DisplayName = "read",
            State = JobState.Succeeded,
            Result = new JobResult
            {
                Success = true,
                Output = new() { ["content"] = body },
                Duration = TimeSpan.Zero,
            },
        };

    [Fact]
    public void ADiffRowIsNotACompactRow()
    {
        // THE ONE THAT CATCHES THE BUG. A compact row with a non-empty body is collapsed on
        // completion, so qualifying here is what silently folds the diff away.
        Assert.False(InlineJobSink.IsCompactRowForTest(
            DiffJob(body: "[#7ee787]+ 32[/] some added line")));
    }

    [Fact]
    public void ADiffRowWithSubstantialOutputStaysExpandable()
    {
        // Forty lines of diff must not be summarised as "40 lines, 2,100 chars" — that measures the
        // answer instead of showing it, which is the tool-vs-worker distinction this file encodes.
        var big = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i}"));

        Assert.False(InlineJobSink.IsCompactRowForTest(DiffJob(body: big)));
    }

    [Fact]
    public void OrdinaryToolRowsStillCompact()
    {
        // No collateral damage: the exemption must not widen to every tool, or every read_file
        // pastes a file back at the user — the case the compact rule was written for.
        Assert.True(InlineJobSink.IsCompactRowForTest(ToolJob()));
        Assert.True(InlineJobSink.IsCompactRowForTest(ToolJob("shell")));
    }

    [Fact]
    public void AFailedDiffIsStillNotCompacted()
    {
        // A failure's body is its reason, and the reason for a failed show_diff is what the user
        // needs in order to know the diff they were shown is not the diff that exists.
        Assert.False(InlineJobSink.IsCompactRowForTest(
            DiffJob(JobState.Failed, body: "not a git repository")));
    }
}
