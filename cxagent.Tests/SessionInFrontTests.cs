using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which session the tab in front belongs to — what F8 shows and edits. A shell or editor tab belongs to the
/// session it was opened from, not to whichever session tab happens to come first in the strip.
/// </summary>
public sealed class SessionInFrontTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "in-front-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager;
    private readonly MainWindow _window;

    public SessionInFrontTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        _manager = SessionManager.Create(paths);
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        _window = new MainWindow(system, ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        {
            CloseSession = (_, _) => true,
        };
        _window.Build();
        _window.NoteFirstSession(Open());
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private Session Open() => _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
        new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

    [Fact]
    public void A_session_tab_is_its_own_session()
    {
        var second = Open();
        _window.AddSessionTab(second);
        Assert.Same(second, _window.SessionInFront);
    }

    [Fact]
    public void A_tab_opened_from_the_second_session_belongs_to_it()
    {
        var second = Open();
        _window.AddSessionTab(second);
        _window.AddTab("shell", new MarkupControl(["$"]), owner: second);
        Assert.Same(second, _window.SessionInFront);
        // AND NOT TO THE FIRST, which is where a lookup by strip position lands.
        Assert.NotSame(_window.SessionTabs[0].Session, _window.SessionInFront);
    }

    [Fact]
    public void A_tab_with_no_owner_falls_back_to_the_first_session()
    {
        _window.AddTab("help", new MarkupControl(["?"]));
        Assert.Same(_window.SessionTabs[0].Session, _window.SessionInFront);
    }
}
