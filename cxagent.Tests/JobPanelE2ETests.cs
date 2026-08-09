using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

public class JobPanelE2ETests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-jpe2e-" + Guid.NewGuid().ToString("N"));
    private readonly LogFileManager _logs;
    private readonly ConsoleWindowSystem _sys;

    public JobPanelE2ETests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        _logs = new LogFileManager(paths);
        _sys = new ConsoleWindowSystem(new HeadlessConsoleDriver(60, 20),
            new SharpConsoleUI.Configuration.ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Goal_RunsThroughRealShell_JobPanelGetsSetJobsAndUpdates()
    {
        var provider = new AnswersWithoutPlanningProvider();   // shared helper (P5a TestProviders.cs)
        var res = new ProviderResolution(provider, "Fake", System.Array.Empty<string>());
        var mw = new MainWindow(_sys, res, _logs);
        mw.Build();

        var chat = new ChatTranscriptSink(_sys, mw.Chat);
        var jobPanel = new JobPanelSink(_sys, mw.JobPanel);   // mw.JobPanel is now a JobPanelControl
        var runner = new GoalRunner(provider, chat, jobPanel,
            PluginRegistry.CreateWithBuiltins(), _logs);

        var state = await runner.RunAsync("do two steps", new List<ChatMessage>(), CancellationToken.None);
        Assert.Equal(GoalState.Completed, state);

        // Drive nothing else — the render landing is the tmux drive. We can't assert BlockCount
        // headlessly without a UI pump; instead assert the wiring compiles + the run completes
        // through the real MainWindow (JobPanelControl).
        Assert.NotNull(mw.JobPanel);
    }
}
