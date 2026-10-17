using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.UI;
using SharpConsoleUI.Parsing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What F7's Tasks panel shows: a card per background shell and sub-agent — running first, then a Finished section
/// — and what a key or a click on a card does (spec decisions 4′, 13, 16, 32, 33).
/// </summary>
public class JobsPanelTests
{
    private static JobsPanel.JobRow Row(string command, BackgroundJobState state, int exit = 0,
        string? agent = null, int pid = 100, params string[] tail) =>
        new(new JobView(new BackgroundJob(pid, "A", command, DateTimeOffset.UtcNow.AddSeconds(-74), null),
            state, exit, TimeSpan.FromSeconds(74)), tail, agent);

    private static JobsPanel.AgentRow Agent(string handle, AgentRowState state, bool foreground = false,
        string? description = null)
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5,
        }).Create();
        var began = DateTimeOffset.UtcNow.AddSeconds(-74);
        var ended = state is AgentRowState.Done or AgentRowState.Stopped or AgentRowState.Failed;
        return new(new AgentRowView(new StoredAgent(handle, "explore", description, began, child), state, foreground,
            TimeSpan.FromSeconds(74), null, began, ended ? new AgentEnd("error", began, began.AddSeconds(74)) : null));
    }

    /// <summary>THE PANEL'S TEXT AS SHOWN. RenderedText is markup source, where a literal bracket is
    /// doubled — asserting on it would test the escaping rather than what a reader sees.</summary>
    private static string Plain(JobsPanel panel) => MarkupParser.Remove(panel.RenderedText);

    /// <summary>A panel showing these rows, with the count the window would give and Finished opened when asked.</summary>
    private static JobsPanel Panel(int width, bool finishedOpen, params JobsPanel.PanelRow[] rows)
    {
        var panel = new JobsPanel();
        panel.Refresh(new JobsPanel.JobsShown(JobsPanel.Order(rows), rows.Count(r => r.Running)), width);
        if (finishedOpen) panel.ToggleFinished();
        return panel;
    }

    private static string Render(params JobsPanel.PanelRow[] rows) =>
        Plain(Panel(SessionPanel.MaxWidth, finishedOpen: true, rows));

    /// <summary>An empty panel says what would appear there, so the key that opened it is learned.</summary>
    [Fact]
    public void NoTasks_SaysWhatWouldAppear() =>
        Assert.Contains("appears here", Render(), StringComparison.Ordinal);

    /// <summary>Decision 32: a running shell's card — its command and ⏹, then "Shell · how long", then "View output".</summary>
    [Fact]
    public void ARunningShell_IsACard_WithStopAndViewOutput()
    {
        var text = Render(Row("npm run dev", BackgroundJobState.Running, tail: ["compiled"]));

        Assert.Contains("Tasks · 1 running", text, StringComparison.Ordinal);
        Assert.Contains("▭ npm run dev", text, StringComparison.Ordinal);
        Assert.Contains("⏹", text, StringComparison.Ordinal);
        Assert.Contains("Shell · 1m 14s\n  View output", text, StringComparison.Ordinal);
        Assert.DoesNotContain("compiled", text, StringComparison.Ordinal);   // two lines per card; the tab has the output
    }

    /// <summary>Decision 32: an agent's card is titled by what it was asked to do, and views its transcript.</summary>
    [Fact]
    public void ARunningAgent_IsACard_TitledByItsDescription()
    {
        var text = Render(Agent("find-loader", AgentRowState.Running, description: "Find the config loader"));

        Assert.Contains("◇ Find the config loader", text, StringComparison.Ordinal);
        Assert.Contains("Agent · 1m 14s\n  View transcript", text, StringComparison.Ordinal);
        Assert.Contains("⏹", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentWithNoDescription_IsTitledByItsHandle() =>
        Assert.Contains("◇ find-loader", Render(Agent("find-loader", AgentRowState.Running)), StringComparison.Ordinal);

    /// <summary>A state other than plain running is named, so a queued or waiting card does not read as working.</summary>
    [Theory]
    [InlineData(AgentRowState.Queued, "Agent · queued")]
    [InlineData(AgentRowState.WaitingForPermission, "Agent · waiting for permission")]
    public void AnAgentNotPlainlyRunning_SaysHowItStands(AgentRowState state, string expected) =>
        Assert.Contains(expected, Render(Agent("a", state)), StringComparison.Ordinal);

    /// <summary>Decision 9: a foreground agent's card has no ⏹ and says the parent is waiting on it.</summary>
    [Fact]
    public void AForegroundAgent_HasNoStop()
    {
        var text = Render(Agent("a", AgentRowState.Running, foreground: true));

        Assert.DoesNotContain("⏹", text, StringComparison.Ordinal);
        Assert.Contains("foreground", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningCardsComeFirst_ThenAFinishedSection_WithTheirMarks()
    {
        var text = Render(
            Row("old build", BackgroundJobState.Exited, exit: 1, pid: 1),
            Agent("map-auth", AgentRowState.Done),
            Agent("find-loader", AgentRowState.Running),
            Agent("capped-one", AgentRowState.Failed),
            Row("npm run dev", BackgroundJobState.Running, pid: 2));

        var finished = text.IndexOf("Finished", StringComparison.Ordinal);
        Assert.True(text.IndexOf("▾ Running", StringComparison.Ordinal) < text.IndexOf("find-loader", StringComparison.Ordinal));
        Assert.True(text.IndexOf("npm run dev", StringComparison.Ordinal) < finished);
        Assert.True(text.IndexOf("find-loader", StringComparison.Ordinal) < finished);
        Assert.Contains("✓ map-auth", text, StringComparison.Ordinal);
        Assert.Contains("✗ capped-one", text, StringComparison.Ordinal);
        Assert.Contains("Agent · failed", text, StringComparison.Ordinal);
        Assert.Contains("✗ old build", text, StringComparison.Ordinal);
        Assert.Contains("Shell · exit 1", text, StringComparison.Ordinal);
    }

    /// <summary>Decision 33: Finished starts closed, says how many it holds, and opens on a click or f.</summary>
    [Fact]
    public void TheFinishedSection_StartsClosed_AndOpensOnAClick()
    {
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: false,
            Agent("map-auth", AgentRowState.Done), Agent("find-loader", AgentRowState.Running));
        Assert.Contains("▸ Finished (1)", Plain(panel), StringComparison.Ordinal);
        Assert.DoesNotContain("map-auth", Plain(panel), StringComparison.Ordinal);

        panel.Follow("section:finished");

        Assert.Contains("▾ Finished", Plain(panel), StringComparison.Ordinal);
        Assert.Contains("map-auth", Plain(panel), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunningSection_ClosesOnAClick()
    {
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: false, Agent("find-loader", AgentRowState.Running));

        panel.Follow("section:running");

        Assert.Contains("▸ Running (1)", Plain(panel), StringComparison.Ordinal);
        Assert.DoesNotContain("find-loader", Plain(panel), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNothingFinished_ThereIsNoFinishedSection() =>
        Assert.DoesNotContain("Finished", Render(Agent("a", AgentRowState.Running)), StringComparison.Ordinal);

    /// <summary>Decision 32: ⏹ stops, as x does; "View …" opens, as Enter does.</summary>
    [Fact]
    public void StopAndView_AreClickable()
    {
        var row = Agent("a", AgentRowState.Running);
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: false, row);
        var chosen = new List<JobsPanel.RowChoice>();
        panel.RowChosen += chosen.Add;

        panel.Follow($"stop:agent:{row.View.AgentId}");
        panel.Follow($"agent:{row.View.AgentId}");

        Assert.Equal([new JobsPanel.RowChoice(row, JobsPanel.RowAction.StopOrForget),
                      new JobsPanel.RowChoice(row, JobsPanel.RowAction.Open)], chosen);
    }

    [Theory]
    [InlineData(AgentRowState.Running, false, true)]
    [InlineData(AgentRowState.Failed, false, true)]
    [InlineData(AgentRowState.Running, true, false)]
    public void X_StopsOrForgets_ButNotAForegroundAgent(AgentRowState state, bool foreground, bool raised)
    {
        var row = Agent("a", state, foreground);
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: true, row);
        JobsPanel.RowChoice? chosen = null;
        panel.RowChosen += c => chosen = c;

        Assert.True(panel.ActOnSelected());

        Assert.Equal(raised ? new JobsPanel.RowChoice(row, JobsPanel.RowAction.StopOrForget) : null, chosen);
    }

    [Fact]
    public void Enter_OnAnAgentRow_OpensIt()
    {
        var row = Agent("a", AgentRowState.Running, foreground: true);
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: false, row);
        JobsPanel.RowChoice? chosen = null;
        panel.RowChosen += c => chosen = c;

        panel.ChooseSelected();

        Assert.Equal(new JobsPanel.RowChoice(row, JobsPanel.RowAction.Open), chosen);
    }

    /// <summary>↓ and Enter open the second row; the selection stops at the ends rather than wrapping.</summary>
    [Fact]
    public void Selection_MovesAndClamps_AndEnterChoosesTheSelectedRow()
    {
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: false,
            Row("a", BackgroundJobState.Running, pid: 11), Row("b", BackgroundJobState.Running, pid: 22));
        int? chosen = null;
        panel.RowChosen += c => chosen = ((JobsPanel.JobRow)c.Row).View.Job.Pid;
        var order = Plain(panel);
        var (first, second) = order.IndexOf("▭ a", StringComparison.Ordinal) < order.IndexOf("▭ b", StringComparison.Ordinal)
            ? (11, 22) : (22, 11);

        Assert.True(panel.MoveSelection(down: true));
        Assert.True(panel.MoveSelection(down: true));   // already last: stays
        Assert.True(panel.ChooseSelected());
        Assert.Equal(second, chosen);

        panel.MoveSelection(down: false);
        panel.ChooseSelected();
        Assert.Equal(first, chosen);
    }

    /// <summary>A closed section's rows are not selectable — the keys act only on what is shown.</summary>
    [Fact]
    public void Selection_SkipsAClosedSection()
    {
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: false, Agent("done-one", AgentRowState.Done));

        Assert.False(panel.MoveSelection(down: true));
        Assert.False(panel.ChooseSelected());
        Assert.False(panel.ActOnSelected());
    }

    /// <summary>With nothing listed, the keys are not the panel's to swallow.</summary>
    [Fact]
    public void Selection_DeclinesTheKeys_WithNoRows()
    {
        var panel = Panel(SessionPanel.MaxWidth, finishedOpen: false);

        Assert.False(panel.MoveSelection(down: true));
        Assert.False(panel.ChooseSelected());
    }

    /// <summary>REVIEW FOCUS 1: a command, a handle and a description are text, not markup.</summary>
    [Fact]
    public void MarkupInATitle_RendersLiterally()
    {
        var text = Render(Row("echo [red]x", BackgroundJobState.Running),
            Agent("a", AgentRowState.Running, description: "grep [bold]y[/]"));

        Assert.Contains("echo [red]x", text, StringComparison.Ordinal);
        Assert.Contains("grep [bold]y[/]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// REVIEW FOCUS 2: EVERY LINE FITS THE NARROWEST COLUMN, SCROLLBAR INCLUDED — and "View …" survives the cut.
    /// </summary>
    [Fact]
    public void Cards_FitTheNarrowestColumn()
    {
        var text = Plain(Panel(SessionPanel.MinWidth, finishedOpen: true,
            Row("for i in $(seq 1 200); do echo tick $i; sleep 1; done", BackgroundJobState.Running, agent: "explore-auth"),
            Agent(new string('h', 40), AgentRowState.WaitingForPermission,
                description: "a very long description of what this agent was asked to do")));

        // MARGINS AND A SCROLLBAR: two columns of margin, and the two the scroll panel reserves for its bar.
        foreach (var line in text.Split('\n').Skip(1))
            Assert.True(line.Length <= SessionPanel.MinWidth - 4, $"too wide for the column: '{line}'");
        Assert.Contains("View output", text, StringComparison.Ordinal);
        Assert.Contains("View transcript", text, StringComparison.Ordinal);
    }
}
