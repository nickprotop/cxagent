using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Decision 5′: a finished command stays on the panel when the user speaks — clear-on-speak is gone.</summary>
public sealed class PanelClearRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clear-rule-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager;
    private readonly DetachedProcessRegistry _registry = new();

    public PanelClearRuleTests()
    {
        Directory.CreateDirectory(_dir);
        _manager = SessionManager.Create(new AppPaths(_dir));
    }

    public void Dispose()
    {
        _registry.ReapAll();
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public async Task TheUserSpeaking_LeavesAFinishedCommandOnThePanel()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var board = new BackgroundJobBoard(_registry);
        var window = new MainWindow(new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
                new ConsoleWindowSystemOptions(InstallSynchronizationContext: true)),
            ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths)) { Jobs = board };
        window.Build();
        var front = _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        window.NoteFirstSession(front);

        var done = ProcessRunner.DetachAsync(
            new ProcessSpec("/bin/sh", ["-c", "exit 0"], new RunOptions(WorkingDir: TestProcesses.WorkingDir,
                SpillDir: Path.Combine(_dir, "spill"))),
            new CollectingContext(), _registry).GetAwaiter().GetResult();
        _registry.Describe(done, new BackgroundJob(done.Pid, front.SessionId!, "exit 0", DateTimeOffset.UtcNow,
            done.OutputPath));
        Assert.True(await Poll.Until(() => board.For(front.Owns).All(e => e.Finished)));

        front.Submit("hello");

        Assert.Single(board.For(front.Owns));
    }
}
