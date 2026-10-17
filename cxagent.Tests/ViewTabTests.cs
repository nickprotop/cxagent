using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A tab opened from the Tasks panel is a VIEW of a task, not its owner — its title shows the task's state, its
/// header says closing it stops nothing, closing it says where the task went, and it sits beside its session's tab
/// (spec decisions 28–31). Owner tabs — a session's, a manual shell's — are unchanged.
/// </summary>
public sealed class ViewTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "view-tab-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager;
    private readonly MainWindow _window;
    private readonly Session _front;

    public ViewTabTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        _manager = SessionManager.Create(paths);
        _window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        {
            CloseSession = (_, _) => true,
            Jobs = new BackgroundJobBoard(new CxAgent.Core.Execution.DetachedProcessRegistry()),
        };
        _window.Build();
        _front = Open();
        _window.NoteFirstSession(_front);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private Session Open() => _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
        new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);

    /// <summary>A kept child of this session running in the background.</summary>
    private static BackgroundRun Running(Session session, string name)
    {
        session.SubAgents.Keep(AgentTabTests.Child(AgentTabTests.Says()), name);
        var run = session.SubAgents.BeginBackground(new BackgroundStart(name, "session", "b", name, IsResume: false),
            new ResultHandoff(_ => true));
        run.MarkRunning();
        return run;
    }

    /// <summary>Decision 28: the title leads with the task's state, and follows it.</summary>
    [Fact]
    public void AnAgentTabsTitle_LeadsWithItsState_AndFollowsIt()
    {
        var run = Running(_front, "finder");
        _window.OpenAgentTab(_front, "finder");
        var tab = _window.ActiveAgentTab!;
        Assert.StartsWith("● ", tab.Title(), StringComparison.Ordinal);

        run.Finish(BackgroundState.Stopped);
        tab.Tick(DateTimeOffset.UtcNow);

        Assert.StartsWith("✗ ", tab.Title(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionTabsTitle_CarriesNoStateMark() =>
        Assert.DoesNotMatch("^[●✓✗○·] ", _window.Tabs.GetTab(0)!.Title);

    /// <summary>Decision 29: the tab says, in it, that closing it stops nothing — and how the task is stopped.</summary>
    [Fact]
    public void ARunningAgentTabsHeader_SaysClosingItKeepsTheAgentRunning()
    {
        Running(_front, "finder");
        _window.OpenAgentTab(_front, "finder");

        Assert.Contains("keeps running when you close this tab", _window.ActiveAgentTab!.HeaderText, StringComparison.Ordinal);
    }

    /// <summary>Decision 33′: a finished agent's tab names x by what the panel's ✕ calls it — delete.</summary>
    [Fact]
    public void AFinishedAgentTabsHeader_SaysXDeletesIt()
    {
        _front.SubAgents.Keep(AgentTabTests.Child(AgentTabTests.Says()), "finder");
        _front.SubAgents.TryClaim("finder");
        _front.SubAgents.AnnounceBegin("finder");
        _front.SubAgents.EndSend("finder", "completed");
        _window.OpenAgentTab(_front, "finder");

        Assert.Contains("x in Tasks (F7) deletes it", _window.ActiveAgentTab!.HeaderText, StringComparison.Ordinal);
    }

    /// <summary>Decision 30: closing a running view tab asks nothing, stops nothing, and says where the task went.</summary>
    [Fact]
    public void ClosingARunningAgentTab_LeavesItRunning_AndSaysWhereToFindIt()
    {
        var run = Running(_front, "finder");
        _window.OpenAgentTab(_front, "finder");

        _window.CloseAgentTab(_window.ActiveAgentTab!);

        Assert.Equal(BackgroundState.Running, run.State);
        Assert.Contains("'finder' keeps running — F7 shows it", _window.StatusNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NOTE TAKES THE READOUT'S PLACE for its few seconds, rather than joining it: beside the hints and the
    /// readout it overran the bar at 150 columns and was cut off mid-word.
    /// </summary>
    [Fact]
    public void TheNote_TakesTheReadoutsPlace_ThenGivesItBack()
    {
        _window.SetTokenTotal(24_000);
        var readout = _window.StatusBar.Readout;
        var items = _window.StatusBar.RightItems.Count;
        Running(_front, "finder");
        _window.OpenAgentTab(_front, "finder");

        _window.CloseAgentTab(_window.ActiveAgentTab!);
        _window.TickNoteForTest();

        Assert.Equal(items, _window.StatusBar.RightItems.Count);
        Assert.Same(readout, _window.StatusBar.Readout);
        Assert.Contains("keeps running", _window.StatusBar.Readout!.Label, StringComparison.Ordinal);

        _window.ExpireNoteForTest();
        _window.TickNoteForTest();

        Assert.Contains("spent", _window.StatusBar.Readout!.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// WHEN STOP GOES, THE KEYBOARD GOES TO THE COMPOSER. Clicked, Stop keeps the focus; hidden once the agent has
    /// stopped, it kept it still — what was typed next went nowhere, and every space pressed the hidden button again,
    /// one "nothing to stop" line in the session per press.
    /// </summary>
    [Fact]
    public void WhenStopHides_TheKeyboardMovesToTheComposer()
    {
        var run = Running(_front, "finder");
        _window.OpenAgentTab(_front, "finder");
        var tab = _window.ActiveAgentTab!;
        tab.Tick(DateTimeOffset.UtcNow);
        var window = SharpConsoleUI.Extensions.WindowControlExtensions.GetParentWindow(tab.StopForTest)!;
        window.FocusManager.SetFocus(tab.StopForTest, SharpConsoleUI.Controls.FocusReason.Mouse);
        Assert.True(tab.StopForTest.HasFocus);

        run.Finish(BackgroundState.Stopped);
        tab.Tick(DateTimeOffset.UtcNow);

        Assert.False(tab.StopForTest.Visible);
        Assert.Same(tab.MainControl, window.FocusManager.FocusedControl);
    }

    [Fact]
    public void ClosingAFinishedAgentTab_SaysNothing()
    {
        _front.SubAgents.Keep(AgentTabTests.Child(AgentTabTests.Says()), "finder");
        _window.OpenAgentTab(_front, "finder");

        _window.CloseAgentTab(_window.ActiveAgentTab!);

        Assert.Null(_window.StatusNote);
    }

    /// <summary>Decision 31: a view tab opens right after its session's tab and that session's other view tabs.</summary>
    [Fact]
    public void AnAgentTab_OpensBesideItsSessionsTab()
    {
        var second = Open();
        _window.AddSessionTab(second);
        Running(_front, "one");
        Running(_front, "two");

        _window.OpenAgentTab(_front, "one");
        Assert.Equal(1, _window.Tabs.ActiveTabIndex);
        _window.OpenAgentTab(_front, "two");
        Assert.Equal(2, _window.Tabs.ActiveTabIndex);

        // THE SECOND SESSION'S TAB MOVED ALONG, and still answers for its session.
        _window.Tabs.ActiveTabIndex = 3;
        Assert.Same(second, _window.ActiveSession);
    }
}
