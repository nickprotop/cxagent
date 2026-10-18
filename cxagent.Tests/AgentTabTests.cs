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

    /// <summary>F12 EXPORTS THE AGENT IN FRONT, and its tab's rule says so as a session's does.</summary>
    [Fact]
    public void TheTabsComposerRule_CarriesTheExportKey()
    {
        _front.SubAgents.Keep(Child(Says("found it")), "finder");
        using var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(new ChatTranscriptControl()));

        Assert.Contains(MainWindow.ExportHint, tab.ComposerRule.Title);
        Assert.Equal(SharpConsoleUI.Layout.TextJustification.Right, tab.ComposerRule.TitleAlignment);
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

    /// <summary>Ctrl+B spec decision 11: a foreground child can be stopped alone, and moved — both from its tab.</summary>
    [Fact]
    public void AForegroundChild_CanBeStoppedAndMoved()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");
        _front.SubAgents.TryClaim("finder");
        _front.SubAgents.AnnounceBegin("finder");
        using var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(new ChatTranscriptControl()));

        Assert.True(tab.CanStop);
        Assert.True(tab.CanMove);
        _front.SubAgents.Release("finder");
    }

    [Fact]
    public async Task TheComposer_SendsToTheChild_AndClears()
    {
        _front.SubAgents.Keep(Child(Says("ok")), "finder");
        var chat = new ChatTranscriptControl();
        using var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(chat));

        await tab.Submit("look again");

        Assert.Contains(_parent.Notices, n => n.Contains("(from its tab): look again", StringComparison.Ordinal));
        Assert.Contains(ChatRole.User, Roles(chat));
    }

    [Fact]
    public void AForegroundChild_HasNoComposer()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");
        _front.SubAgents.TryClaim("finder");
        _front.SubAgents.AnnounceBegin("finder");
        using var tab = new AgentTab(_front, _front.SubAgents.Find("finder")!, Inline(new ChatTranscriptControl()));

        Assert.False(tab.CanType);
        _front.SubAgents.Release("finder");
    }

    private static IWindowControl APrompt() => SharpConsoleUI.Builders.Controls.Markup().Build();

    [Fact]
    public void APromptFromTheChildInFront_ShowsInItsTab_AndIsAnsweredOnce()
    {
        var child = Child(Says());
        _front.SubAgents.Keep(child, "finder");
        _window.OpenAgentTab(_front, "finder");
        var prompt = APrompt();
        var denied = 0;

        _window.ShowPermissionPrompt(prompt, () => denied++, child.Agent.Id);

        Assert.True(_window.ActiveAgentTab!.HasPrompt);
        Assert.Null(_window.SessionTabs[0].ActivePrompt);
        Assert.True(_window.TryDenyPermission());
        Assert.Equal(1, denied);

        _window.RestoreComposer(prompt);
        Assert.False(_window.ActiveAgentTab!.HasPrompt);
        Assert.False(_window.TryDenyPermission());
    }

    [Fact]
    public void APromptFromAChildNotInFront_ShowsInTheSessionTab()
    {
        var child = Child(Says());
        _front.SubAgents.Keep(child, "finder");
        _window.OpenAgentTab(_front, "finder");
        var tab = _window.ActiveAgentTab!;
        _window.Tabs.ActiveTabIndex = 0;
        var prompt = APrompt();

        _window.NotePromptTabBySessionId(_front.Id);
        _window.ShowPermissionPrompt(prompt, () => { }, child.Agent.Id);

        Assert.Same(prompt, _window.SessionTabs[0].ActivePrompt);
        Assert.False(tab.HasPrompt);
    }

    /// <summary>REVIEW FOCUS 5: a prompt must not vanish with its tab and leave the child waiting forever.</summary>
    [Fact]
    public void ClosingAnAgentTab_WithAPromptUp_MovesThePromptToTheSessionTab()
    {
        var child = Child(Says());
        _front.SubAgents.Keep(child, "finder");
        _window.OpenAgentTab(_front, "finder");
        var prompt = APrompt();
        _window.ShowPermissionPrompt(prompt, () => { }, child.Agent.Id);

        _window.CloseAgentTab(_window.ActiveAgentTab!);

        Assert.Same(prompt, _window.SessionTabs[0].ActivePrompt);
    }

    /// <summary>
    /// CLOSING THE SESSION DENIES ITS AGENT TAB'S PROMPT rather than moving it: the session's close ends the agent,
    /// and a moved prompt landed in whichever other session's composer was left in front.
    /// </summary>
    [Fact]
    public void ClosingTheSession_DeniesItsAgentTabsPrompt_RatherThanMovingIt()
    {
        var second = Open();
        _window.AddSessionTab(second);
        var child = Child(Says());
        _front.SubAgents.Keep(child, "finder");
        _window.Tabs.ActiveTabIndex = 0;
        _window.OpenAgentTab(_front, "finder");
        var prompt = APrompt();
        var denied = false;
        _window.ShowPermissionPrompt(prompt, () => denied = true, child.Agent.Id);

        _window.Tabs.ActiveTabIndex = 0;
        Assert.True(_window.CloseActiveSession(confirmed: true));

        Assert.True(denied);
        Assert.DoesNotContain(_window.SessionTabs, t => ReferenceEquals(t.ActivePrompt, prompt));
    }

    /// <summary>
    /// A MOVED PROMPT WAITS FOR THE ONE ALREADY UP on the session's tab, and Escape answers the one on screen. It was
    /// shown nowhere while taking over Escape, so Escape denied a prompt the user could not see.
    /// </summary>
    [Fact]
    public void AMovedPrompt_WaitsForTheOneAlreadyUp()
    {
        var child = Child(Says());
        _front.SubAgents.Keep(child, "finder");
        var visible = APrompt();
        var visibleDenied = false;
        _window.NotePromptTabBySessionId(_front.Id);
        _window.ShowPermissionPrompt(visible, () => visibleDenied = true);
        _window.OpenAgentTab(_front, "finder");
        var moved = APrompt();
        var movedDenied = false;
        _window.ShowPermissionPrompt(moved, () => movedDenied = true, child.Agent.Id);

        _window.CloseAgentTab(_window.ActiveAgentTab!);

        Assert.Same(visible, _window.SessionTabs[0].ActivePrompt);
        Assert.True(_window.TryDenyPermission());
        Assert.True(visibleDenied);
        Assert.False(movedDenied);

        _window.RestoreComposer(visible);

        Assert.Same(moved, _window.SessionTabs[0].ActivePrompt);
    }

    [Fact]
    public void AgentsOpen_OpensTheTab_ThenFocusesIt()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");

        _window.OpenAgentTabFor(_front, "open finder");
        var count = _window.Tabs.TabCount;
        _window.Tabs.ActiveTabIndex = 0;
        _window.OpenAgentTabFor(_front, "open finder");

        Assert.Equal(count, _window.Tabs.TabCount);
        Assert.Equal("finder", _window.ActiveAgentTab!.Kept.Name);
    }

    [Fact]
    public void AgentsOpen_WithNoName_SaysHowToUseIt()
    {
        _window.OpenAgentTabFor(_front, "open");

        Assert.Contains(_parent.Notices, n => n.Contains("/agents open <name>", StringComparison.Ordinal));
        Assert.Null(_window.ActiveAgentTab);
    }

    /// <summary>The palette offers this session's kept agents — the set /agents send already completes from — and
    /// not one the user forgot (decision 13).</summary>
    [Fact]
    public void AgentsOpen_CompletesFromTheKeptAgents_NotTheForgotten()
    {
        _front.SubAgents.Keep(Child(Says()), "finder");
        _front.SubAgents.Keep(Child(Says()), "gone");
        _front.SubAgents.TryClaim("gone");
        _front.SubAgents.AnnounceBegin("gone");
        _front.SubAgents.EndSend("gone", "completed");
        _front.SubAgents.Forget("gone");

        var offered = _front.Values(CompletionSets.SpawnedAgents);
        Assert.Contains(offered, v => v.Name == "finder");
        Assert.DoesNotContain(offered, v => v.Name == "gone");
    }
}
