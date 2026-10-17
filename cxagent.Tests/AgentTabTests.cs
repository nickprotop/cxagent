using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The agent tab: what the child did before it opened, then what it does live; one tab per child; and a tab that
/// closes with its session.
/// </summary>
public sealed class AgentTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-tab-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager;
    private readonly MainWindow _window;
    private readonly BufferedChatSink _parent = new();
    private readonly Session _front;

    public AgentTabTests()
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
        _front = Open(_parent);
        _window.NoteFirstSession(_front);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private Session Open(BufferedChatSink? sink = null) => _manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
        new SessionPorts { Observer = sink ?? new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);

    internal static SubAgent Child(ILlmProvider provider) => new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
    {
        Provider = provider, Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
        MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
    }).Create();

    internal static MockLlmProvider Says(params string[] answers)
    {
        var provider = new MockLlmProvider();
        foreach (var a in answers) provider.EnqueueResponse(new LlmResponse { Text = a, StopReason = "end_turn" });
        return provider;
    }

    /// <summary>Views that write inline — a headless test has no UI loop to drain the real sinks' enqueues.</summary>
    internal static AgentTabViews Inline(ChatTranscriptControl chat) =>
        new(chat, ChatTranscriptSink.ForTest(chat), new BufferedJobPanel(), null, () => { }, action => action());

    private static Task Ask(Session session, string prompt) =>
        new AgentReachTools(session.SubAgents).SendAsync(
            new SendRequest("finder", prompt, Background: false, ParentAgentId: null, FromUser: true), CancellationToken.None);

    private static List<ChatRole> Roles(ChatTranscriptControl chat) => [.. chat.MessageIds.Select(chat.GetRole)];

    [Fact]
    public async Task TheTab_ReplaysWhatTheChildDidBeforeItOpened()
    {
        _front.SubAgents.Keep(Child(Says("found it")), "finder");
        await Ask(_front, "look");

        var chat = new ChatTranscriptControl();
        using var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(chat));

        var roles = Roles(chat);
        Assert.True(roles.IndexOf(ChatRole.User) >= 0 && roles.IndexOf(ChatRole.User) < roles.LastIndexOf(ChatRole.Assistant));
    }

    [Fact]
    public async Task TheTab_FollowsWhatTheChildDoesAfterItOpened()
    {
        _front.SubAgents.Keep(Child(Says("found it")), "finder");
        var chat = new ChatTranscriptControl();
        using var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(chat));
        Assert.Empty(chat.MessageIds);

        await Ask(_front, "look");

        Assert.Contains(ChatRole.Assistant, Roles(chat));
    }

    [Fact]
    public void ClosingTheTab_DetachesItsView()
    {
        var child = Child(Says());
        _front.SubAgents.Keep(child, "finder");
        var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(new ChatTranscriptControl()));
        Assert.Equal(1, child.Feed.Watchers);

        tab.Dispose();

        Assert.Equal(0, child.Feed.Watchers);
    }

    [Fact]
    public void OpeningTwice_FocusesTheTabAlreadyOpen()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");

        Assert.Null(_window.OpenAgentTab(_front, "finder"));
        var count = _window.Tabs.TabCount;
        var at = _window.Tabs.ActiveTabIndex;
        _window.Tabs.ActiveTabIndex = 0;
        Assert.Null(_window.OpenAgentTab(_front, "finder"));

        Assert.Equal(count, _window.Tabs.TabCount);
        Assert.Equal(at, _window.Tabs.ActiveTabIndex);
        Assert.Equal("finder", _window.ActiveAgentTab!.Kept.Name);
    }

    [Fact]
    public void ANameThatIsNotKept_IsRefused()
    {
        var refusal = _window.OpenAgentTab(_front, "ghost");
        Assert.Contains("no sub-agent named 'ghost'", refusal, StringComparison.Ordinal);
    }

    /// <summary>Decision 13: a forgotten agent cannot be opened again; the refusal says why.</summary>
    [Fact]
    public void AForgottenAgent_IsRefused_AsDismissed()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");
        _front.SubAgents.TryClaim("finder");
        _front.SubAgents.AnnounceBegin("finder");
        _front.SubAgents.EndSend("finder", "completed");
        _front.SubAgents.Forget("finder");

        Assert.Contains("was dismissed", _window.OpenAgentTab(_front, "finder"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndedSession_IsRefused()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");
        var count = _window.Tabs.TabCount;
        _front.EndBackgroundWork();

        Assert.Contains("ended", _window.OpenAgentTab(_front, "finder"), StringComparison.Ordinal);
        Assert.Equal(count, _window.Tabs.TabCount);
    }

    /// <summary>Core decision 32 ends a closed tab's agents, so their tabs have nothing left to show.</summary>
    [Fact]
    public void ClosingTheSessionTab_ClosesItsAgentTabs()
    {
        var second = Open();
        _window.AddSessionTab(second);
        second.SubAgents.Keep(Child(Says()), "finder");
        _window.OpenAgentTab(second, "finder");
        var count = _window.Tabs.TabCount;

        _window.Tabs.ActiveTabIndex = 1;
        Assert.True(_window.CloseActiveSession(confirmed: true));

        Assert.Equal(count - 2, _window.Tabs.TabCount);
        Assert.Null(_window.ActiveAgentTab);
    }

    [Fact]
    public void AForegroundChild_HasNoStop()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");
        _front.SubAgents.TryClaim("finder");
        _front.SubAgents.AnnounceBegin("finder");
        using var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(new ChatTranscriptControl()));

        Assert.False(tab.CanStop);
        _front.SubAgents.Release("finder");
    }
}
