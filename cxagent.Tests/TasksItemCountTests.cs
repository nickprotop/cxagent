using CxAgent.Core.Agents;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Parsing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The F7 status item counts every running task /tasks would list as going — background agents as well as
/// shells — in this tab and the others.
///
/// <para>COUNTING SHELLS ALONE SHOWED NOTHING while only agents ran, so the one readout a narrow terminal
/// has for background work said there was none.</para>
///
/// <para>IN THE DEFAULT-REGISTRY COLLECTION: the delete question is asked of a sub-agent's shell, which the session's
/// store reads from the process-wide registry — where another class's teardown would kill it mid-test.</para>
/// </summary>
[Collection(DefaultShellRegistryCollection.Name)]
public sealed class TasksItemCountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasks-item-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager;

    public TasksItemCountTests()
    {
        Directory.CreateDirectory(_dir);
        _manager = SessionManager.Create(new AppPaths(_dir));
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private Session Open() => _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
        new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

    /// <summary>A background run on this session, in the given state.</summary>
    private static void Run(Session session, string name, BackgroundState state)
    {
        session.SubAgents.Keep(AgentTabTests.Child(AgentTabTests.Says()), name);
        var run = session.SubAgents.BeginBackground(new BackgroundStart(name, "parent", "do it", name, IsResume: false),
            new ResultHandoff(_ => true));
        if (state != BackgroundState.Queued) run.MarkRunning();
        if (state is BackgroundState.Finished or BackgroundState.Stopped) run.Finish(state);
    }

    private static BackgroundJobBoard NoShells() => new(new DetachedProcessRegistry());

    [Fact]
    public void AgentsInThisTab_AreCounted()
    {
        var front = Open();
        Run(front, "a", BackgroundState.Running);
        Run(front, "b", BackgroundState.Queued);

        Assert.Equal("Tasks 2", MainWindow.JobsItemLabelFor(front, [front], NoShells()));
    }

    [Fact]
    public void AgentsInAnotherTab_CountElsewhere()
    {
        var front = Open();
        var other = Open();
        Run(other, "c", BackgroundState.Running);

        Assert.Equal("Tasks 0+1", MainWindow.JobsItemLabelFor(front, [front, other], NoShells()));
    }

    [Fact]
    public void EndedAgents_AreNotCounted()
    {
        var front = Open();
        Run(front, "done", BackgroundState.Finished);
        Run(front, "stopped", BackgroundState.Stopped);

        Assert.Equal("Tasks", MainWindow.JobsItemLabelFor(front, [front], NoShells()));
    }
    /// <summary>
    /// THE PANEL F7 OPENS COUNTS WHAT THE ITEM COUNTS. The heading counted shells alone, so with only agents
    /// going the item said "Tasks 2" and the panel it opened said nothing was running. A real window, so the
    /// heading is the one its own refresh paints.
    /// </summary>
    [Fact]
    public void ThePanelHeading_CountsTheSameRunningTasksAsTheItem()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        {
            // ITS OWN BOARD, not the process-wide one: other tests start real shells in the shared registry, and
            // the item counts every running shell — one from a parallel test would read as "Tasks 2+1".
            Jobs = NoShells(),
        };
        window.Build();
        var front = Open();
        window.NoteFirstSession(front);
        Run(front, "a", BackgroundState.Running);
        Run(front, "b", BackgroundState.Queued);
        Run(front, "done", BackgroundState.Finished);

        window.ToggleJobsPanel();

        Assert.Equal("Tasks 2", MainWindow.JobsItemLabelFor(front, [front], window.Jobs));
        var panel = MarkupParser.Remove(window.JobsPanel.RenderedText);
        Assert.Contains("Tasks · 2 running", panel, StringComparison.Ordinal);
        Assert.Contains("◇ a", panel, StringComparison.Ordinal);
        Assert.Contains("queued", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing running", panel, StringComparison.Ordinal);
    }

    /// <summary>
    /// WHAT CLOSING A TAB WOULD END, counted from the same sources as the status item: its queued and running
    /// agents, and its running shells. A finished shell is nothing to keep or stop.
    /// </summary>
    [Fact]
    public void ATabsWork_CountsItsAgentsAndItsRunningShells()
    {
        var front = Open();
        Run(front, "a", BackgroundState.Running);
        var registry = new DetachedProcessRegistry();
        var board = new BackgroundJobBoard(registry);
        var spill = Path.Combine(_dir, "spill");
        var running = ProcessRunner.DetachAsync(
            new ProcessSpec("/bin/sh", ["-c", "sleep 30"], new RunOptions(WorkingDir: TestProcesses.WorkingDir, SpillDir: spill)),
            new CollectingContext(), registry).GetAwaiter().GetResult();
        registry.Describe(running, new BackgroundJob(running.Pid, front.SessionId!, "sleep 30", DateTimeOffset.UtcNow, running.OutputPath));
        try
        {
            Assert.Equal(new TabWork(Agents: 1, Commands: 1), MainWindow.TabWorkFor(front, board));
        }
        finally
        {
            registry.ReapAll();
        }
    }

    /// <summary>Decision 22: a foreground child counts in F7, so the item says what the running rows show.</summary>
    [Fact]
    public void AForegroundChild_IsCountedInTheItem_ButNotInTheCloseQuestion()
    {
        var front = Open();
        front.SubAgents.Keep(AgentBookkeepingTests.ChildForTests(), "fg");
        front.SubAgents.TryClaim("fg");

        Assert.Equal("Tasks 1", MainWindow.JobsItemLabelFor(front, [front], NoShells()));
        Assert.Equal(new TabWork(Agents: 0, Commands: 0), MainWindow.TabWorkFor(front, NoShells()));
    }

    /// <summary>Q12: the user's own send is work a close ends, so the close question names it.</summary>
    [Fact]
    public void TheUsersOwnSend_IsInTheCloseQuestion()
    {
        var front = Open();
        front.SubAgents.Keep(AgentBookkeepingTests.ChildForTests(), "mine");
        front.SubAgents.BeginUserSend("mine");

        Assert.Equal(new TabWork(Agents: 1, Commands: 0), MainWindow.TabWorkFor(front, NoShells()));
    }

    [Fact]
    public void X_OnAFinishedShell_ForgetsIt()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var registry = new DetachedProcessRegistry();
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        { Jobs = new BackgroundJobBoard(registry), Shells = new FinishedShells() };
        window.Build();
        var front = Open();
        window.NoteFirstSession(front);
        var failing = ProcessRunner.DetachAsync(
            new ProcessSpec("/bin/sh", ["-c", "exit 3"], new RunOptions(WorkingDir: TestProcesses.WorkingDir,
                SpillDir: Path.Combine(_dir, "spill"))), new CollectingContext(), registry).GetAwaiter().GetResult();
        registry.Describe(failing, new BackgroundJob(failing.Pid, front.SessionId!, "exit 3", DateTimeOffset.UtcNow,
            failing.OutputPath));
        try
        {
            Assert.True(Poll.Until(() => window.Jobs.For(front.Owns).All(e => e.Finished)).GetAwaiter().GetResult());
            window.ToggleJobsPanel();

            Assert.True(window.JobsPanel.ActOnSelected());

            Assert.Empty(window.Jobs.For(front.Owns));
        }
        finally { registry.ReapAll(); }
    }

    /// <summary>Decision 13: x on a finished agent forgets it everywhere — and its open tab closes.</summary>
    [Fact]
    public void X_OnAFinishedAgent_ForgetsIt_AndClosesItsTab()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        { Jobs = NoShells(), Shells = new FinishedShells() };
        window.Build();
        var front = Open();
        window.NoteFirstSession(front);
        front.SubAgents.Keep(AgentTabTests.Child(AgentTabTests.Says()), "finder");
        front.SubAgents.TryClaim("finder");
        front.SubAgents.AnnounceBegin("finder");
        front.SubAgents.EndSend("finder", "completed");
        window.OpenAgentTab(front, "finder");
        var tabs = window.Tabs.TabCount;
        window.Tabs.ActiveTabIndex = 0;
        window.ToggleJobsPanel();

        Assert.True(window.JobsPanel.ActOnSelected());

        Assert.Null(front.SubAgents.Find("finder"));
        Assert.Equal(tabs - 1, window.Tabs.TabCount);
        Assert.DoesNotContain("finder", MarkupParser.Remove(window.JobsPanel.RenderedText), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ctrl+B spec decisions 6′ and 11: ON A FOREGROUND CARD, x STOPS THE AGENT ALONE AND b MOVES IT to the background —
    /// each reaching the session's own action.
    /// </summary>
    [Theory]
    [InlineData(false, ForegroundRelease.Stop)]
    [InlineData(true, ForegroundRelease.Move)]
    public void OnAForegroundCard_XStops_AndBMoves(bool move, ForegroundRelease expected)
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        { Jobs = NoShells(), Shells = new FinishedShells() };
        window.Build();
        var front = Open();
        window.NoteFirstSession(front);
        front.SubAgents.Keep(AgentTabTests.Child(AgentTabTests.Says()), "finder");
        front.SubAgents.TryClaim("finder");
        front.SubAgents.AnnounceBegin("finder");
        var run = front.SubAgents.BeginForeground("finder", CancellationToken.None);
        window.ToggleJobsPanel();

        Assert.True(move ? window.JobsPanel.BackgroundSelected() : window.JobsPanel.ActOnSelected());

        Assert.True(run.Released.IsCompletedSuccessfully);
        Assert.Equal(expected, run.Released.Result);
    }

    /// <summary>
    /// ENTER ON A CARD THAT CANNOT OPEN SAYS WHY, as <c>/agents open</c> does — here an agent deleted elsewhere
    /// before the panel's next refresh. Silence read as a key that did nothing.
    /// </summary>
    [Fact]
    public void Enter_OnACardThatCannotOpen_SaysWhy()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        { Jobs = NoShells(), Shells = new FinishedShells() };
        window.Build();
        var sink = new BufferedChatSink();
        var front = _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        window.NoteFirstSession(front);
        var child = AgentTabTests.Child(AgentTabTests.Says());
        front.SubAgents.Keep(child, "finder");
        front.SubAgents.TryClaim("finder");
        front.SubAgents.AnnounceBegin("finder");
        front.SubAgents.EndSend("finder", "completed");
        window.ToggleJobsPanel();
        front.SubAgents.Forget("finder");

        window.JobsPanel.Follow($"agent:{child.Agent.Id}");

        Assert.Contains(sink.Notices, n => n.Contains("dismissed", StringComparison.Ordinal));
    }

    /// <summary>
    /// DELETING AN AGENT WHOSE COMMANDS STILL RUN ASKS FIRST, as closing a tab does — Cancel keeps both, "stop them"
    /// ends both, "keep" leaves the commands running and on this session's panel.
    /// </summary>
    [Theory]
    [InlineData(AgentDelete.Cancel)]
    [InlineData(AgentDelete.StopCommands)]
    [InlineData(AgentDelete.KeepCommands)]
    public async Task X_OnAFinishedAgent_WithACommandRunning_AsksFirst(AgentDelete answer)
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        // THE DEFAULT REGISTRY, because the session's store reads its agents' shells there; the shell is killed by
        // hand below, never by ReapAll, which would reach other tests' processes.
        var registry = DetachedProcessRegistry.Default;
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        { Jobs = new BackgroundJobBoard(registry), Shells = new FinishedShells() };
        (string Name, int Commands)? asked = null;
        // THE DIALOG TAKES THE KEYBOARD while it is up, as the real one does; the cards must get it back after.
        window.AskDelete = (name, commands) =>
        {
            asked = (name, commands);
            window.FocusComposer();
            return Task.FromResult(answer);
        };
        window.Build();
        var front = Open();
        window.NoteFirstSession(front);
        var child = AgentTabTests.Child(AgentTabTests.Says());
        front.SubAgents.Keep(child, "finder");
        front.SubAgents.TryClaim("finder");
        front.SubAgents.AnnounceBegin("finder");
        front.SubAgents.EndSend("finder", "completed");
        var shell = ProcessRunner.DetachAsync(
            new ProcessSpec("/bin/sh", ["-c", "sleep 30"], new RunOptions(WorkingDir: TestProcesses.WorkingDir,
                SpillDir: Path.Combine(_dir, "spill"))), new CollectingContext(), registry).GetAwaiter().GetResult();
        registry.Describe(shell, new BackgroundJob(shell.Pid, child.Agent.Id, "sleep 30", DateTimeOffset.UtcNow,
            shell.OutputPath));
        try
        {
            window.ToggleJobsPanel();

            window.JobsPanel.Follow($"delete:agent:{child.Agent.Id}");

            Assert.True(await Poll.Until(() => asked is not null));
            Assert.Equal(("finder", 1), asked);
            var gone = answer != AgentDelete.Cancel;
            Assert.True(await Poll.Until(() => (front.SubAgents.Find("finder") is null) == gone));
            Assert.Equal(answer == AgentDelete.StopCommands, shell.Killed);
            Assert.Equal(answer != AgentDelete.StopCommands,
                window.Jobs.For(front.Owns).Any(e => e.Job.Pid == shell.Pid && !e.Finished));
            // AWAITED, as the delete is: it finishes on the pool, and the keyboard comes back after it.
            Assert.True(await Poll.Until(() => window.JobsRowsHaveFocus), "the keyboard did not come back to the cards");
        }
        finally { shell.Kill(); }
    }

    /// <summary>
    /// A SUB-AGENT'S SHELL GETS ITS OWN CARD beside the agent's: the heading counted both, and the panel showed only
    /// the agent.
    /// </summary>
    [Fact]
    public void ASubAgentsRunningShell_HasACardBesideTheAgent()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var registry = new DetachedProcessRegistry();
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        { Jobs = new BackgroundJobBoard(registry), Shells = new FinishedShells() };
        window.Build();
        var front = Open();
        window.NoteFirstSession(front);
        Run(front, "worker", BackgroundState.Running);
        var childId = front.SubAgents.Find("worker")!.Agent.Agent.Id;
        var shell = ProcessRunner.DetachAsync(
            new ProcessSpec("/bin/sh", ["-c", "sleep 30"], new RunOptions(WorkingDir: TestProcesses.WorkingDir,
                SpillDir: Path.Combine(_dir, "spill"))), new CollectingContext(), registry).GetAwaiter().GetResult();
        registry.Describe(shell, new BackgroundJob(shell.Pid, childId, "sleep 30", DateTimeOffset.UtcNow, shell.OutputPath));
        try
        {
            window.ToggleJobsPanel();

            var panel = MarkupParser.Remove(window.JobsPanel.RenderedText);
            Assert.Contains("Tasks · 2 running", panel, StringComparison.Ordinal);
            Assert.Contains("▭ sleep 30", panel, StringComparison.Ordinal);
            Assert.Contains("◇ worker", panel, StringComparison.Ordinal);
        }
        finally { registry.ReapAll(); }
    }
}
