using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

public class JobPanelControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-jp-" + Guid.NewGuid().ToString("N"));
    private readonly ConsoleWindowSystem _sys;
    private readonly LogFileManager _logs;
    public JobPanelControlTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir); paths.EnsureCreated();
        _logs = new LogFileManager(paths);
        _sys = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new SharpConsoleUI.Configuration.ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static Job J(string id, JobState s) => new()
    { Id = id, AgentId = "g", PluginType = "shell", DisplayName = id, State = s, CreatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void SetJobs_CreatesOneBlockPerJob_KeyedById()
    {
        var panel = new JobPanelControl(_sys, _logs);
        panel.SetJobs(new[] { J("a", JobState.Queued), J("b", JobState.Queued) });
        Assert.Equal(2, panel.BlockCount);
        Assert.True(panel.TryGetBlock("a", out _));
        Assert.True(panel.TryGetBlock("b", out _));
    }

    [Fact]
    public void UpdateJob_UpdatesTheRightBlock()
    {
        var panel = new JobPanelControl(_sys, _logs);
        panel.SetJobs(new[] { J("a", JobState.Queued), J("b", JobState.Queued) });
        panel.UpdateJob(J("a", JobState.Running));
        Assert.True(panel.TryGetBlock("a", out var a));
        Assert.Equal(SharpConsoleUI.Themes.ColorRole.Info, a.ColorRole);
        // b unchanged
        Assert.True(panel.TryGetBlock("b", out var b));
        Assert.Equal(SharpConsoleUI.Themes.ColorRole.Default, b.ColorRole);
    }

    [Fact]
    public void UpdateJob_UnknownId_IsNoOp()
    {
        var panel = new JobPanelControl(_sys, _logs);
        panel.SetJobs(new[] { J("a", JobState.Queued) });
        var ex = Record.Exception(() => panel.UpdateJob(J("ghost", JobState.Running)));
        Assert.Null(ex);
        Assert.Equal(1, panel.BlockCount);
    }

    [Fact]
    public void SetJobs_Twice_ReplacesBlocks()
    {
        var panel = new JobPanelControl(_sys, _logs);
        panel.SetJobs(new[] { J("a", JobState.Queued) });
        panel.SetJobs(new[] { J("x", JobState.Queued), J("y", JobState.Queued) });
        Assert.Equal(2, panel.BlockCount);
        Assert.False(panel.TryGetBlock("a", out _));
        Assert.True(panel.TryGetBlock("x", out _));
    }

    // ------------------------------------------------------------------ P9 Task 2: draft-mode banner
}
