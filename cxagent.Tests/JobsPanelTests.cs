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
            TimeSpan.FromSeconds(74), began, ended ? new AgentEnd("error", began, began.AddSeconds(74)) : null));
    }

    /// <summary>THE PANEL'S TEXT AS SHOWN. RenderedText is markup source, where a literal bracket is
    /// doubled — asserting on it would test the escaping rather than what a reader sees.</summary>
    private static string Plain(JobsPanel panel) => MarkupParser.Remove(panel.RenderedText);

    /// <summary>A panel showing these rows, with the count the window would give.</summary>
    private static JobsPanel Panel(int width, params JobsPanel.PanelRow[] rows)
    {
        var panel = new JobsPanel();
        panel.Refresh(new JobsPanel.JobsShown(JobsPanel.Order(rows), rows.Count(r => r.Running)), width);
        return panel;
    }

    private static string Render(params JobsPanel.PanelRow[] rows) =>
        Plain(Panel(SessionPanel.MaxWidth, rows));

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
        Assert.True(text.IndexOf("Running", StringComparison.Ordinal) < text.IndexOf("find-loader", StringComparison.Ordinal));
        Assert.True(text.IndexOf("npm run dev", StringComparison.Ordinal) < finished);
        Assert.True(text.IndexOf("find-loader", StringComparison.Ordinal) < finished);
        Assert.Contains("✓ map-auth", text, StringComparison.Ordinal);
        Assert.Contains("✗ capped-one", text, StringComparison.Ordinal);
        Assert.Contains("Agent · failed", text, StringComparison.Ordinal);
        Assert.Contains("✗ old build", text, StringComparison.Ordinal);
        Assert.Contains("Shell · exit 1", text, StringComparison.Ordinal);
    }

    /// <summary>Decision 33′: both sections are plain headings, and a finished card is shown without opening anything.</summary>
    [Fact]
    public void TheSections_ArePlainHeadings_AndFinishedCardsAreShown()
    {
        var text = Render(Agent("map-auth", AgentRowState.Done), Agent("find-loader", AgentRowState.Running));

        Assert.Contains("\nRunning\n", text, StringComparison.Ordinal);
        Assert.Contains("\nFinished\n", text, StringComparison.Ordinal);
        Assert.Contains("map-auth", text, StringComparison.Ordinal);
        Assert.DoesNotContain("▸", text, StringComparison.Ordinal);
        Assert.DoesNotContain("▾", text, StringComparison.Ordinal);
    }

    /// <summary>Decision 33′: one control per card, at the same edge — ⏹ while it runs, ✕ once it has ended.</summary>
    [Fact]
    public void AFinishedCard_CarriesDelete_WhereARunningCardCarriesStop()
    {
        var text = Render(Agent("map-auth", AgentRowState.Done), Agent("find-loader", AgentRowState.Running));
        var lines = text.Split('\n');

        var running = Assert.Single(lines, l => l.Contains("find-loader", StringComparison.Ordinal));
        var finished = Assert.Single(lines, l => l.Contains("map-auth", StringComparison.Ordinal));
        Assert.EndsWith("⏹", running.TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("✕", finished.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(running.TrimEnd().Length, finished.TrimEnd().Length);
    }

    [Fact]
    public void AFinishedCardsDelete_IsClickable()
    {
        var row = Agent("a", AgentRowState.Done);
        var panel = Panel(SessionPanel.MaxWidth, row);
        JobsPanel.RowChoice? chosen = null;
        panel.RowChosen += c => chosen = c;

        panel.Follow($"delete:agent:{row.View.AgentId}");

        Assert.Equal(new JobsPanel.RowChoice(row, JobsPanel.RowAction.StopOrForget), chosen);
    }


    [Fact]
    public void WithNothingFinished_ThereIsNoFinishedSection() =>
        Assert.DoesNotContain("Finished", Render(Agent("a", AgentRowState.Running)), StringComparison.Ordinal);

    /// <summary>Decision 32: ⏹ stops, as x does; "View …" opens, as Enter does.</summary>
    [Fact]
    public void StopAndView_AreClickable()
    {
        var row = Agent("a", AgentRowState.Running);
        var panel = Panel(SessionPanel.MaxWidth, row);
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
        var panel = Panel(SessionPanel.MaxWidth, row);
        JobsPanel.RowChoice? chosen = null;
        panel.RowChosen += c => chosen = c;

        Assert.True(panel.ActOnSelected());

        Assert.Equal(raised ? new JobsPanel.RowChoice(row, JobsPanel.RowAction.StopOrForget) : null, chosen);
    }

    [Fact]
    public void Enter_OnAnAgentRow_OpensIt()
    {
        var row = Agent("a", AgentRowState.Running, foreground: true);
        var panel = Panel(SessionPanel.MaxWidth, row);
        JobsPanel.RowChoice? chosen = null;
        panel.RowChosen += c => chosen = c;

        panel.ChooseSelected();

        Assert.Equal(new JobsPanel.RowChoice(row, JobsPanel.RowAction.Open), chosen);
    }

    /// <summary>↓ and Enter open the second row; the selection stops at the ends rather than wrapping.</summary>
    [Fact]
    public void Selection_MovesAndClamps_AndEnterChoosesTheSelectedRow()
    {
        var panel = Panel(SessionPanel.MaxWidth,
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

    /// <summary>With only finished cards, the keys act on them — nothing has to be opened first.</summary>
    [Fact]
    public void Selection_ReachesFinishedCards_WithNothingRunning()
    {
        var row = Agent("done-one", AgentRowState.Done);
        var panel = Panel(SessionPanel.MaxWidth, row);
        JobsPanel.RowChoice? chosen = null;
        panel.RowChosen += c => chosen = c;

        Assert.True(panel.ActOnSelected());
        Assert.Equal(new JobsPanel.RowChoice(row, JobsPanel.RowAction.StopOrForget), chosen);
    }

    /// <summary>With nothing listed, the keys are not the panel's to swallow.</summary>
    [Fact]
    public void Selection_DeclinesTheKeys_WithNoRows()
    {
        var panel = Panel(SessionPanel.MaxWidth);

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
        var text = Plain(Panel(SessionPanel.MinWidth,
            Row("for i in $(seq 1 200); do echo tick $i; sleep 1; done", BackgroundJobState.Running, agent: "explore-auth"),
            Agent(new string('h', 40), AgentRowState.WaitingForPermission,
                description: "a very long description of what this agent was asked to do")));

        // MARGINS AND A SCROLLBAR: two columns of margin, and the two the scroll panel reserves for its bar.
        foreach (var line in text.Split('\n').Skip(1))
            Assert.True(line.Length <= SessionPanel.MinWidth - 4, $"too wide for the column: '{line}'");
        Assert.Contains("View output", text, StringComparison.Ordinal);
        Assert.Contains("View transcript", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CLICK ON ⏹ WHERE IT IS DRAWN STOPS THE CARD. The cards stop short of the list's right edge — the panel keeps
    /// columns for its scrollbar — so a hit-test at the list's edge never met the glyph, and a real click did nothing.
    /// A click in the same column one line down, on the card's facts, is not the control.
    /// </summary>
    [Fact]
    public void AClickOnTheDrawnStop_StopsTheCard_AndOnlyOnItsLine()
    {
        var system = new SharpConsoleUI.ConsoleWindowSystem(new SharpConsoleUI.Drivers.HeadlessConsoleDriver(60, 40));
        CxAgentTheme.Install(system, null);
        var panel = new JobsPanel();
        var window = new SharpConsoleUI.Window(system) { Left = 0, Top = 0, Width = SessionPanel.MaxWidth + 2, Height = 40 };
        window.AddControl(panel.Control);
        system.AddWindow(window);
        var agent = Agent("worker", AgentRowState.Running, description: "Summarise prices.py");
        panel.Refresh(new JobsPanel.JobsShown([agent], 1), SessionPanel.MaxWidth);
        var painted = window.RenderAndGetVisibleContent()
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, "\u001b\\[[0-9;]*m", "")).ToList();
        var row = painted.FindIndex(l => l.Contains('⏹'));
        var col = painted[row].IndexOf('⏹');
        var list = panel.RunningListForTest;
        var chosen = new List<JobsPanel.RowChoice>();
        panel.RowChosen += chosen.Add;

        Click(list, col - list.ActualX, row + 1 - list.ActualY);
        Assert.Empty(chosen);
        Click(list, col - list.ActualX, row - list.ActualY);

        Assert.Equal([new JobsPanel.RowChoice(agent, JobsPanel.RowAction.StopOrForget)], chosen);
    }

    private static void Click(SharpConsoleUI.Controls.ListControl list, int x, int y)
    {
        var at = new System.Drawing.Point(x, y);
        list.ProcessMouseEvent(new SharpConsoleUI.Events.MouseEventArgs(
            [SharpConsoleUI.Drivers.MouseFlags.Button1Clicked], at, at, at));
        Thread.Sleep(600);   // past the list's double-click window, so the next click is a click of its own
    }

    /// <summary>
    /// WIDTHS IN COLUMNS, NOT CHARACTERS. A wide (CJK or emoji) title counted one column a character pushed the card's
    /// control out of line with every other card's, or wrapped the title line.
    /// </summary>
    [Theory]
    [InlineData("整理价格文件并总结结果给我看一下")]
    [InlineData("📦📦📦📦📦📦📦📦📦📦📦📦📦📦📦📦📦📦")]
    public void AWideTitle_KeepsTheControlInItsColumn(string title)
    {
        var text = Render(Agent("plain", AgentRowState.Running, description: "a plain title"),
            Agent("wide", AgentRowState.Running, description: title));
        var lines = text.Split('\n');
        var plain = Assert.Single(lines, l => l.Contains("a plain title", StringComparison.Ordinal)).TrimEnd();
        var wide = Assert.Single(lines, l => l.EndsWith("⏹", StringComparison.Ordinal) && !l.Contains("plain")).TrimEnd();

        Assert.Equal(SharpConsoleUI.Helpers.UnicodeWidth.GetStringWidth(plain),
            SharpConsoleUI.Helpers.UnicodeWidth.GetStringWidth(wide));
    }

    /// <summary>
    /// ↓ PAST THE LAST RUNNING CARD GOES ON INTO FINISHED, and ↑ comes back: the column reads as one list, and the
    /// keys stopped dead at a section's end with more cards visible below.
    /// </summary>
    [Fact]
    public void TheArrows_CrossBetweenSections()
    {
        var system = new SharpConsoleUI.ConsoleWindowSystem(new SharpConsoleUI.Drivers.HeadlessConsoleDriver(60, 40));
        CxAgentTheme.Install(system, null);
        var panel = new JobsPanel();
        var window = new SharpConsoleUI.Window(system) { Left = 0, Top = 0, Width = 44, Height = 40 };
        window.AddControl(panel.Control);
        system.AddWindow(window);
        var running = Agent("worker", AgentRowState.Running);
        var done = Agent("old-done", AgentRowState.Done);
        panel.Refresh(new JobsPanel.JobsShown(JobsPanel.Order([running, done]), 1), SessionPanel.MaxWidth);
        window.RenderAndGetVisibleContent();
        var chosen = new List<JobsPanel.RowChoice>();
        panel.RowChosen += chosen.Add;

        Assert.True(panel.MoveSelection(down: true));
        panel.ChooseSelected();
        Assert.True(panel.MoveSelection(down: false));
        panel.ChooseSelected();

        Assert.Equal([done, running], chosen.Select(c => c.Row));
    }

    /// <summary>
    /// x THEN x AGAIN ACTS ON ONE CARD (decision 13). Stopping the last running card moves it to Finished; the
    /// keyboard goes with it, so the second x deletes that card — not whichever finished card was selected before,
    /// with no highlight to say so.
    /// </summary>
    [Fact]
    public void StoppingTheLastRunningCard_TakesTheKeyboardWithIt()
    {
        var system = new SharpConsoleUI.ConsoleWindowSystem(new SharpConsoleUI.Drivers.HeadlessConsoleDriver(60, 40));
        CxAgentTheme.Install(system, null);
        var panel = new JobsPanel();
        var window = new SharpConsoleUI.Window(system) { Left = 0, Top = 0, Width = 44, Height = 40 };
        window.AddControl(panel.Control);
        system.AddWindow(window);
        var old = Agent("old-done", AgentRowState.Done);
        var worker = Agent("worker", AgentRowState.Running);
        panel.Refresh(new JobsPanel.JobsShown(JobsPanel.Order([worker, old]), 1), SessionPanel.MaxWidth);
        window.RenderAndGetVisibleContent();
        Assert.True(panel.RowsHaveFocus);

        var stopped = new JobsPanel.AgentRow(worker.View with
        {
            State = AgentRowState.Stopped, End = new AgentEnd("stopped", worker.View.Began, DateTimeOffset.UtcNow),
        });
        panel.Refresh(new JobsPanel.JobsShown(JobsPanel.Order([stopped, old]), 0), SessionPanel.MaxWidth);
        window.RenderAndGetVisibleContent();
        JobsPanel.RowChoice? chosen = null;
        panel.RowChosen += c => chosen = c;

        Assert.True(panel.RowsHaveFocus);
        Assert.True(panel.ActOnSelected());
        Assert.Equal("worker", Assert.IsType<JobsPanel.AgentRow>(chosen!.Row).View.Kept.Name);
    }

    /// <summary>
    /// WHAT IS PAINTED, NOT ONLY WHAT THE MODEL HOLDS: a second running card that arrives after the first must be
    /// drawn too. Measured live, the Running list stayed one card tall and scrolled to its selection, so the shell a
    /// sub-agent started had a row in the panel's model and no line on screen.
    /// </summary>
    [Fact]
    public void ASecondRunningCard_ThatArrivesLater_IsPainted()
    {
        var system = new SharpConsoleUI.ConsoleWindowSystem(new SharpConsoleUI.Drivers.HeadlessConsoleDriver(60, 40));
        CxAgentTheme.Install(system, null);
        var panel = new JobsPanel();
        var window = new SharpConsoleUI.Window(system) { Left = 0, Top = 0, Width = 44, Height = 40 };
        window.AddControl(panel.Control);
        system.AddWindow(window);
        var agent = Agent("worker", AgentRowState.Running, description: "Summarise prices.py");
        panel.Refresh(new JobsPanel.JobsShown([agent], 1), SessionPanel.MaxWidth);
        window.RenderAndGetVisibleContent();
        window.RenderAndGetVisibleContent();
        // WITH THE KEYBOARD ON THE CARDS — the window gives it to the only focusable control. Handing it to the
        // replacement list redraws the panel mid-update, which once put the one-card list back.
        Assert.True(panel.RowsHaveFocus);

        var shell = Row("sleep 120", BackgroundJobState.Running, pid: 7);
        panel.Refresh(new JobsPanel.JobsShown(JobsPanel.Order([shell, agent]), 2), SessionPanel.MaxWidth);
        window.RenderAndGetVisibleContent();

        var painted = System.Text.RegularExpressions.Regex.Replace(
            string.Join("\n", window.RenderAndGetVisibleContent()), "\u001b\\[[0-9;]*m", "");
        var shown = string.Join(" | ", painted.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        Assert.True(painted.Contains("sleep 120", StringComparison.Ordinal), shown);
        Assert.Contains("Summarise prices.py", painted, StringComparison.Ordinal);
    }
}
