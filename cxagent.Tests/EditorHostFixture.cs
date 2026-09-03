using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;

namespace CxAgent.Tests;

/// <summary>
/// Builds the three things a file tab needs from the app around it, and owns what has to be disposed
/// afterwards.
///
/// <para>SHARED RATHER THAN COPIED into each test class: this is setup that must agree between them,
/// and a class that built its own window would be testing a different app than its neighbour.</para>
/// </summary>
internal sealed class EditorHostFixture : IDisposable
{
    private readonly SessionManager _manager;
    private readonly string _dir;

    public EditorHost Host { get; }

    /// <summary>The session's working directory, for tests that need a real file on disk.</summary>
    public string WorkingDirectory => _dir;

    public EditorHostFixture()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));

        _dir = Path.Combine(Path.GetTempPath(), "cxagent-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();

        var config = ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock");
        var main = new MainWindow(system, config, new LogFileManager(paths));
        main.Build();

        _manager = SessionManager.Create(paths);
        var session = _manager.Open(_dir, config,
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        Host = new EditorHost(system, main, session);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
