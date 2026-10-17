using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the window shows once a session tab closes: the tab that becomes active, in full.
///
/// <para>THE CLOSED TAB'S COMPOSER HELD THE KEYBOARD, so with nothing moving it the next keys went
/// nowhere until F4; and the side panel kept painting the closed conversation's turns, tool calls and
/// spend beside the one now in front.</para>
/// </summary>
public sealed class TabCloseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tab-close-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager;
    private readonly MainWindow _window;

    public TabCloseTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        _manager = SessionManager.Create(paths);

        // WIDE ENOUGH THAT THE SIDE PANEL IS SHOWN, so what it paints is what a user would read.
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(200, 40),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));
        _window = new MainWindow(system, ResolvedConfig.ForTesting(new MockLlmProvider(), "Mock"), new LogFileManager(paths))
        {
            CloseSession = _ => true,
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
    public void ClosingATab_PutsTheKeyboardInTheComposerOfTheTabNowInFront()
    {
        var first = _window.SessionTabs[0];
        _window.AddSessionTab(Open());
        Assert.Same(_window.SessionTabs[1].Input, _window.Window!.FocusManager.FocusedControl);

        Assert.True(_window.CloseActiveSession(confirmed: true));

        Assert.Same(first, _window.ActiveTab);
        Assert.Same(first.Input, _window.Window!.FocusManager.FocusedControl);
    }

    [Fact]
    public void ClosingATab_ShowsTheRemainingTabsNumbersInTheSidePanel()
    {
        var first = _window.SessionTabs[0];
        first.Tally.Turns = 3;
        first.Tally.ToolCalls = 11;
        first.Spend = new MainWindow.SpendReading { ByInstance = new Dictionary<string, int> { ["frontinst:m"] = 1_200 } };
        var closing = Open();
        var second = _window.AddSessionTab(closing);
        second.Tally.Turns = 7;
        second.Tally.ToolCalls = 42;
        _window.SetSpend(closing, new MainWindow.SpendReading
        {
            ByInstance = new Dictionary<string, int> { ["closedinst:m"] = 9_000 },
            SubAgentTokens = 4_000,
        });
        Assert.Contains("7 turns", _window.SessionPanel.RenderedText);
        Assert.Contains("closedinst", _window.SessionPanel.RenderedText);

        _window.CloseActiveSession(confirmed: true);

        var shown = _window.SessionPanel.RenderedText;
        Assert.Contains("3 turns", shown);
        Assert.Contains("11 tool calls", shown);
        Assert.DoesNotContain("42 tool calls", shown);
        Assert.Contains("frontinst", shown);
        Assert.DoesNotContain("closedinst", shown);
    }
}
