using CxAgent.Core.Agents;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
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
}
