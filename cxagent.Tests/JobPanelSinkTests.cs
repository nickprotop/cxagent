using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

public class JobPanelSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-jps-" + Guid.NewGuid().ToString("N"));
    private readonly ConsoleWindowSystem _sys;
    private readonly LogFileManager _logs;
    public JobPanelSinkTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir); paths.EnsureCreated();
        _logs = new LogFileManager(paths);
        _sys = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new SharpConsoleUI.Configuration.ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static Job J(string id) => new()
    { Id = id, AgentId = "g", PluginType = "shell", DisplayName = id, State = JobState.Queued, CreatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task SetJobs_FromBackgroundThread_IsDeferred_NotAppliedSynchronously()
    {
        var panel = new JobPanelControl(_sys, _logs);
        var sink = new JobPanelSink(_sys, panel);

        await Task.Run(() => sink.ToolsChanged(new[] { J("a"), J("b") }));

        // Marshalled: nothing applied synchronously — the panel has no blocks yet (the ToolsChanged was enqueued).
        Assert.Equal(0, panel.BlockCount);
    }

    [Fact]
    public void Sink_DoesNotThrow_WhenCalledOffUiThread()
    {
        var panel = new JobPanelControl(_sys, _logs);
        var sink = new JobPanelSink(_sys, panel);
        var ex = Record.Exception(() =>
        {
            sink.ToolsChanged(new[] { J("a") });
            sink.ToolUpdated(J("a"));
        });
        Assert.Null(ex);   // enqueue-only, no synchronous mutation, no throw
    }
}
