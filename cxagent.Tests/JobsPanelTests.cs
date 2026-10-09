using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.UI;
using SharpConsoleUI.Parsing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What F7's jobs panel shows: each job's state, command and running time, then what it last printed.
/// </summary>
public class JobsPanelTests
{
    private static JobsPanel.JobRow Row(string command, BackgroundJobState state, int exit = 0,
        string? agent = null, int pid = 100, params string[] tail) =>
        new(new JobView(new BackgroundJob(pid, "A", command, DateTimeOffset.UtcNow.AddSeconds(-74), null),
            state, exit, TimeSpan.FromSeconds(74)), tail, agent);

    /// <summary>THE PANEL'S TEXT AS SHOWN. RenderedText is markup source, where a literal bracket is
    /// doubled — asserting on it would test the escaping rather than what a reader sees.</summary>
    private static string Plain(JobsPanel panel) => MarkupParser.Remove(panel.RenderedText);

    private static string Render(int width, params JobsPanel.JobRow[] jobs)
    {
        var panel = new JobsPanel();
        panel.Refresh(jobs, width);
        return Plain(panel);
    }

    private static string Render(params JobsPanel.JobRow[] jobs) => Render(SessionPanel.MaxWidth, jobs);

    /// <summary>An empty panel says what would appear there, so the key that opened it is learned.</summary>
    [Fact]
    public void NoJobs_SaysWhatWouldAppear() =>
        Assert.Contains("background: true", Render(), StringComparison.Ordinal);

    [Fact]
    public void ARunningJob_ShowsItsCommandElapsedStateAndLastLines()
    {
        var text = Render(Row("npm run dev", BackgroundJobState.Running, tail: ["compiled", "ready on :3000"]));

        Assert.Contains("Jobs · 1 running", text, StringComparison.Ordinal);
        Assert.Contains("npm run dev", text, StringComparison.Ordinal);
        Assert.Contains("1:14", text, StringComparison.Ordinal);
        Assert.Contains("running", text, StringComparison.Ordinal);
        Assert.Contains("compiled", text, StringComparison.Ordinal);
        Assert.Contains("ready on :3000", text, StringComparison.Ordinal);
    }

    /// <summary>A finished job keeps its last lines — the error is the reason to look.</summary>
    [Fact]
    public void FinishedJobs_SayHowTheyEnded_AndKeepTheirLastLines()
    {
        var text = Render(Row("a", BackgroundJobState.Exited, exit: 1, pid: 1, tail: ["error: test failed"]),
                          Row("b", BackgroundJobState.Killed, pid: 2),
                          Row("c", BackgroundJobState.Exited, exit: 0, pid: 3));

        Assert.Contains("exit 1", text, StringComparison.Ordinal);
        Assert.Contains("error: test failed", text, StringComparison.Ordinal);
        Assert.Contains("killed", text, StringComparison.Ordinal);
        Assert.Contains("exit 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubAgentsJob_NamesTheAgent() =>
        Assert.Contains("running · explore-auth",
            Render(Row("x", BackgroundJobState.Running, agent: "explore-auth")), StringComparison.Ordinal);

    /// <summary>A command's own brackets are text, not markup.</summary>
    [Fact]
    public void MarkupInACommandOrLine_RendersLiterally()
    {
        var text = Render(Row("echo [red]x", BackgroundJobState.Running, tail: ["[bold]y[/]"]));

        Assert.Contains("echo [red]x", text, StringComparison.Ordinal);
        Assert.Contains("[bold]y[/]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY ROW FITS THE NARROWEST COLUMN, SCROLLBAR INCLUDED. The column is 24 wide on a terminal of
    /// 100 to 119 and grows a scrollbar once it is taller than the screen; a row sized without either
    /// wraps, and the clock lands on a line of its own.
    /// </summary>
    [Fact]
    public void Rows_FitTheNarrowestColumn()
    {
        var text = Render(SessionPanel.MinWidth,
            Row("for i in $(seq 1 200); do echo tick $i; sleep 1; done", BackgroundJobState.Running,
                tail: ["a very long line of output that would never fit in a narrow panel"]));

        // MARGINS AND A SCROLLBAR: two columns of margin, and the two the scroll panel reserves for its
        // bar. The empty-state prose is not a row and wraps by design, so only job rows are measured.
        foreach (var line in text.Split('\n').Skip(1))
            Assert.True(line.Length <= SessionPanel.MinWidth - 4, $"too wide for the column: '{line}'");
    }

    /// <summary>↓ and Enter open the second job; the selection stops at the ends rather than wrapping.</summary>
    [Fact]
    public void Selection_MovesAndClamps_AndEnterChoosesTheSelectedJob()
    {
        var panel = new JobsPanel();
        panel.Refresh([Row("a", BackgroundJobState.Running, pid: 11), Row("b", BackgroundJobState.Running, pid: 22)],
            SessionPanel.MaxWidth);
        int? chosen = null;
        panel.JobChosen += pid => chosen = pid;

        Assert.True(panel.MoveSelection(down: true));
        Assert.True(panel.MoveSelection(down: true));   // already last: stays
        Assert.True(panel.ChooseSelected());
        Assert.Equal(22, chosen);

        panel.MoveSelection(down: false);
        panel.ChooseSelected();
        Assert.Equal(11, chosen);
    }

    /// <summary>With nothing listed, the keys are not the panel's to swallow.</summary>
    [Fact]
    public void Selection_DeclinesTheKeys_WithNoJobs()
    {
        var panel = new JobsPanel();
        panel.Refresh([], SessionPanel.MaxWidth);

        Assert.False(panel.MoveSelection(down: true));
        Assert.False(panel.ChooseSelected());
    }
}
