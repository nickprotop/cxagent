using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>/tasks as the user types it: list, stop as the user, read without taking; and a closing tab.</summary>
public class SessionTasksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "session-tasks-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;
    private BufferedChatSink? _sink;
    public SessionTasksTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _manager?.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private Session Open()
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        return _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = _sink = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.FanOut);
    }

    private static SubAgent Child() => new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
    {
        Provider = new MockLlmProvider(), Ledger = new TokenLedger(),
        MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
        Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
    }).Create();

    private static BackgroundRun Run(Session s, string name)
    {
        s.SubAgents.Keep(Child(), name);
        return s.SubAgents.BeginBackground(new BackgroundStart(Name: name, ParentAgentId: s.SessionId!, Brief: "b",
            Description: name, IsResume: false), new ResultHandoff(_ => true));
    }

    [Fact]
    public void SlashTasks_ListsAKeptAgent()
    {
        var s = Open();
        Run(s, "finder");

        s.ListTasks();

        Assert.Contains(_sink!.Notices, n => n.Contains("finder"));
    }

    [Fact]
    public void SlashTasksStop_StopsAsTheUser()
    {
        var s = Open();
        var run = Run(s, "finder");

        s.StopTask("stop finder");

        Assert.True(run.StoppedByUser);
        Assert.Contains(_sink!.Notices, n => n.Contains("stopped 'finder'"));
    }

    [Fact]
    public void SlashTasksStop_AnUnknownTask_SaysSo()
    {
        var s = Open();
        s.StopTask("stop ghost");
        Assert.Contains(_sink!.Notices, n => n.Contains("no task 'ghost'"));
    }

    /// <summary>A PERSON READING A REPORT DOES NOT TAKE IT: the parent is still owed it.</summary>
    [Fact]
    public void SlashTasksOutput_DoesNotTakeTheParentsReport()
    {
        var s = Open();
        var run = Run(s, "finder");
        run.Finish(BackgroundState.Finished);
        run.Handoff.Complete("the report text", _ => false);   // undelivered

        s.ShowTaskOutput("output finder");

        Assert.Contains(_sink!.Notices, n => n.Contains("the report text"));
        Assert.True(run.Handoff.Undelivered);
    }

    [Fact]
    public void RunningBackgroundAgents_CountsQueuedAndRunningOnly()
    {
        var s = Open();
        Run(s, "queued");
        Run(s, "running").MarkRunning();
        Run(s, "done").Finish(BackgroundState.Finished);

        Assert.Equal(2, s.RunningBackgroundAgents);
    }

    /// <summary>A CLOSED TAB STOPS ITS AGENTS AND TELLS NOBODY — there is no conversation left to tell.</summary>
    [Fact]
    public void ClosingASession_StopsItsBackgroundAgents_AndTellsNobody()
    {
        var s = Open();
        var run = Run(s, "finder");
        var sent = 0;

        Assert.True(_manager!.Close(s));
        Assert.True(run.Token.IsCancellationRequested);
        run.Handoff.Complete("r", _ => { sent++; return true; });
        Assert.Equal(0, sent);
    }

    /// <summary>
    /// THE APP GOING AWAY STOPS A BUSY SESSION'S AGENTS TOO. Close refuses a busy session, and Dispose
    /// then tears it down by hand — a background run left going there would outlive its conversation and
    /// report into one that is closing.
    /// </summary>
    [Fact]
    public void DisposingTheManager_StopsABusySessionsBackgroundAgents_AndTellsNobody()
    {
        var s = Open();
        var run = Run(s, "finder");
        var sent = 0;

        using (s.PretendBusyForTesting())
        {
            _manager!.Dispose();
            _manager = null;
        }

        Assert.True(run.Token.IsCancellationRequested);
        run.Handoff.Complete("r", _ => { sent++; return true; });
        Assert.Equal(0, sent);
    }
}
