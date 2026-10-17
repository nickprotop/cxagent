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
/// </summary>
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
}
