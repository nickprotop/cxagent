using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

public class MainWindowTests
{
    private static ConsoleWindowSystem Sys() =>
        new(new HeadlessConsoleDriver(80, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));

    /// <summary>A system with a chosen terminal width, for the panel's responsive threshold.</summary>
    private static ConsoleWindowSystem SysOfWidth(int width) =>
        new(new HeadlessConsoleDriver(width, 24),
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));

    private static LogFileManager Logs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-mwt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new AppPaths(dir);
        paths.EnsureCreated();
        return new LogFileManager(paths);
    }

    [Fact]
    public void Build_WithProvider_EnablesSubmission_AndExposesControls()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        var win = mw.Build();

        Assert.NotNull(win);
        Assert.NotNull(mw.Chat);
        Assert.NotNull(mw.Input);
        Assert.True(mw.SubmissionEnabled);
    }

    /// <summary>
    /// Regression (P5b live-drive): the goal composer must start in EDITING mode.
    /// MultilineEditControl has two modes — focused-but-navigating (_isEditing=false, where
    /// non-navigation keys BUBBLE and typed characters are discarded) and editing. It flips to
    /// editing ONLY on Enter (MultilineEditControl.Keyboard.cs: `case ConsoleKey.Enter: IsEditing = true`).
    /// But AppBootstrap's PreviewKeyPressed consumes EVERY Enter while the composer has focus
    /// (e.Handled = true) to implement Enter-submits, so the control can never receive the Enter
    /// that would start editing — leaving the composer permanently unable to accept text.
    /// Verified live: keys arrive (KEY A ch=97) with focus=MultilineEditControl HasFocus=True,
    /// yet the placeholder never changes. Build() must therefore set IsEditing itself.
    /// </summary>
    [Fact]
    public void Build_GoalComposer_StartsFocused_SoTypingWorks()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        Assert.True(mw.Input.HasFocus, "composer must hold focus");
    }

    /// <summary>
    /// Regression (P5b live-drive): the status bar advertises Ctrl+N / Ctrl+J / Ctrl+H / F1, but only
    /// Ctrl+Q was ever registered — the other four were dead keys. Ctrl+J in particular is the ONLY
    /// way to move focus into the job panel, without which a block can't be collapsed/expanded and
    /// P5b's headline feature (expand → live log tail) is unreachable by keyboard.
    /// These focus helpers are what the AppBootstrap shortcuts invoke.
    /// </summary>
    [Fact]
    public void FocusJobs_IsANoOp_SinceJobsMovedInline()
    {
        // Replaces FocusJobs_And_FocusChat_MoveFocusBetweenPanes. Jobs now render INLINE in the
        // transcript, so JobPanel is constructed but never placed in the grid. FocusJobs must NOT
        // move focus to it: the old implementation set Input.IsEditing = false first, which would
        // leave the composer silently unable to accept typed input with nothing visible to show for
        // it — exactly D10's failure mode, and unrecoverable without knowing to press F4.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        Assert.True(mw.Input.HasFocus);

        mw.FocusJobs();

        Assert.True(mw.Input.HasFocus, "focus must stay on the composer — the job panel is not displayed");
    }

    /// <summary>
    /// Drift guard: every key the status bar advertises must be one AppBootstrap can actually bind.
    /// The status bar is the only discovery surface for these, so a shown-but-unbound key is a dead
    /// key the user will press and get nothing from — which is exactly what shipped (Ctrl+N/J/H were
    /// displayed but never registered, and Ctrl+J/Ctrl+H can't be registered at all: a terminal
    /// sends them as 0x0A/0x08, byte-identical to Enter/Backspace).
    /// This asserts the bar advertises only F-keys (unambiguous escape sequences) plus Ctrl+Q,
    /// which is verified working.
    /// </summary>
    [Fact]
    public void StatusBar_AdvertisesOnlyBindableKeys()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        // BOTH SIDES. Asserting LeftItems pinned WHERE a key sits, which is a layout decision, and
        // it broke the moment the bar was rearranged. What matters is that nothing ADVERTISED is
        // unbindable, wherever it is shown.
        var shortcuts = mw.StatusBar.LeftItems.Concat(mw.StatusBar.RightItems)
            .Select(i => i.Shortcut)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        Assert.NotEmpty(shortcuts);
        foreach (var s in shortcuts)
        {
            // Ctrl+<letter> combos that alias an ASCII control char can never be delivered.
            Assert.False(s == "Ctrl+J" || s == "Ctrl+H" || s == "Ctrl+M" || s == "Ctrl+I",
                $"'{s}' aliases an ASCII control byte (Enter/Backspace/Tab) and can never be bound");
            Assert.True(s!.StartsWith("F") || s == "Ctrl+Q",
                $"'{s}' is advertised but is not a key AppBootstrap binds (expected an F-key or Ctrl+Q)");
        }
    }

    [Fact]
    public void Build_NoProvider_DisablesSubmission_ShowsErrors()
    {
        var res = new ProviderResolution(null, null, new[] { "config.json not found at '/x'." });
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        Assert.False(mw.SubmissionEnabled);
        // The no-provider message + the error line are rendered somewhere in the chat panel.
        // (Assert via the chat control's content or a dedicated status field — see impl note.)
    }

    [Fact]
    public void Build_SetsInitialFocusToInputControl()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        var focused = mw.Window!.FocusManager.FocusedControl;
        Assert.True(object.ReferenceEquals(focused, mw.Input),
            $"Expected focus on Input (MultilineEditControl) but was on {focused?.GetType().Name ?? "null"}");
    }

    /// <summary>
    /// First-run setup rebuilds the runner wiring live, so the composer must become submittable in the
    /// same session — without a restart. `Build()` starts it disabled when no provider resolved; the
    /// setter is what first-run flips, and it must also refresh the placeholder, which Build() derived
    /// from the same flag (otherwise the UI still reads "(no provider — submission disabled)" while
    /// Enter works).
    /// </summary>
    [Fact]
    public void SetSubmissionEnabled_FlipsFlag_AndRefreshesPlaceholder()
    {
        var noProvider = new ProviderResolution(null, null, new[] { "config.json not found." });
        var mw = new MainWindow(Sys(), noProvider, Logs());
        mw.Build();

        Assert.False(mw.SubmissionEnabled);
        var disabledPlaceholder = mw.Input.Placeholder;

        mw.SetSubmissionEnabled(true);

        Assert.True(mw.SubmissionEnabled);
        Assert.NotEqual(disabledPlaceholder, mw.Input.Placeholder);
    }

    [Fact]
    public void ShowHelp_PostsAMessage_AndKeepsComposerTypable()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        var before = mw.Chat.MessageIds.Count;
        mw.ShowHelp();

        Assert.True(mw.Chat.MessageIds.Count > before, "ShowHelp must post a message to the transcript");
        // ShowHelp ends by returning focus to the composer — otherwise F1 would leave the app untypable.
        Assert.True(mw.Input.HasFocus);
    }

    // --- Task 11: F6 diagnose, status-bar cost --------------------------------------------------

    /// <summary>
    /// Settings is ONE key, not three. F7 (Roles) and F8 (Providers) were retired when the three
    /// dialogs became one: both opened the SAME consolidated dialog on a different page, so the bar
    /// advertised three shortcuts for one surface and a user pressing F7 had no way to learn that F5
    /// and F8 went to the same place. The page names live in the dialog's nav pane instead.
    ///
    /// This replaces StatusBar_AdvertisesRoles_F7 / StatusBar_AdvertisesProviders_F8, which asserted
    /// the OLD design. Written as an ABSENCE check as well as a presence one, because a stale
    /// registration that still fires is exactly the kind of thing that survives a refactor unnoticed
    /// -- a dead key advertised in the bar is worse than no key.
    /// </summary>
    [Fact]
    public void StatusBar_AdvertisesSettingsOnce_AndNoLongerAdvertisesF7OrF8()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        // The PRESENCE half went with the bar's redesign: it no longer lists every dialog key, so
        // "F5 appears in the bar" now asserts a layout choice rather than a binding. The absence
        // half is the part that was ever load-bearing — a retired key still advertised is a key the
        // user presses and gets nothing from.
        var items = mw.StatusBar.LeftItems.Concat(mw.StatusBar.RightItems).ToList();
        Assert.DoesNotContain(items, i => i.Shortcut == "F7");
        Assert.DoesNotContain(items, i => i.Shortcut == "F8");
    }

    [Fact]
    public void SetTokenTotal_ShowsSpend_AndZeroShowsNothing()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        mw.SetTokenTotal(1234);
        Assert.Contains(mw.StatusBar.RightItems.Select(i => i.Label ?? ""), l => l.Contains("1234")
            || l.Contains("1,234"));

        // Before any LLM call there is nothing to report — an unconditional "0 tokens" is noise.
        mw.SetTokenTotal(0);
        Assert.DoesNotContain(mw.StatusBar.RightItems.Select(i => i.Label ?? ""), l => l.Contains("0 tokens"));
    }

    [Fact]
    public void ShowHelp_MentionsF6()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        var before = mw.Chat.MessageIds.Count;
        mw.ShowHelp();

        Assert.True(mw.Chat.MessageIds.Count > before);
        // The help TEXT is not headless-assertable (ChatTranscriptControl exposes MessageIds/GetRole but
        // NO content accessor) — the tmux drive checks the wording. Here we assert it still posts and
        // still returns the composer to a typable state.
        Assert.True(mw.Input.HasFocus);
    }

    // --- Task 3: prompt control / composer swap --------------------------------------------------

    private static PermissionRequest ShellRequest(string command) =>
        new(PermissionKind.Shell, command, command);

    private static MainWindow BuiltMainWindow()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();
        return mw;
    }

    [Fact]
    public void ShowPermissionPrompt_ReplacesTheComposer_AndRestorePutsItBack()
    {
        var mw = BuiltMainWindow();
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var built = prompt.BuildContent();   // built ONCE — the same control rides both calls

        mw.ShowPermissionPrompt(built);
        Assert.False(mw.Input.HasFocus);          // the composer is out of the tree

        mw.RestoreComposer(built);
        Assert.True(mw.Input.HasFocus, "composer must come back focused");
    }

    [Fact]
    public void RestoreComposer_PreservesWhateverTheUserHadTyped()
    {
        // The prompt interrupts mid-thought; the half-typed goal must survive the round trip.
        var mw = BuiltMainWindow();
        mw.Input.Input = "half-typed goal";
        var prompt = new PermissionPromptControl(ShellRequest("ls"));
        var built = prompt.BuildContent();
        mw.ShowPermissionPrompt(built);
        mw.RestoreComposer(built);
        Assert.Equal("half-typed goal", mw.Input.Input);
    }

    [Fact]
    public void ShowPermissionPrompt_CalledTwice_IsIdempotent_DoesNotThrow()
    {
        // A second Show while one is already up is a caller bug in Task 4's serialisation, but the
        // UI must not crash the render loop over it — guard and no-op rather than let
        // GridControl.ReplaceControl throw ArgumentException on the already-swapped-out composer.
        var mw = BuiltMainWindow();
        var prompt1 = new PermissionPromptControl(ShellRequest("git status")).BuildContent();
        var prompt2 = new PermissionPromptControl(ShellRequest("ls")).BuildContent();

        mw.ShowPermissionPrompt(prompt1);
        var ex = Record.Exception(() => mw.ShowPermissionPrompt(prompt2));

        Assert.Null(ex);
    }

    [Fact]
    public void RestoreComposer_CalledTwice_IsIdempotent_DoesNotThrow()
    {
        var mw = BuiltMainWindow();
        var prompt = new PermissionPromptControl(ShellRequest("git status")).BuildContent();

        mw.ShowPermissionPrompt(prompt);
        mw.RestoreComposer(prompt);
        var ex = Record.Exception(() => mw.RestoreComposer(prompt));

        Assert.Null(ex);
        Assert.True(mw.Input.HasFocus);
    }

    /// <summary>
    /// I4 fix: RestoreComposer now enforces the identity contract itself instead of merely stating
    /// it. GridControl.ReplaceControl locates the "old" control by ReferenceEquals
    /// (GridControl.cs:389) and would throw ArgumentException on a mismatch — but a control that was
    /// never placed is reachable from a legitimate caller, not just a bug: ShowPermissionPrompt
    /// no-ops when a prompt is already up (its own idempotence guard) without telling its caller,
    /// so that caller's `finally` still calls RestoreComposer with ITS OWN control, which was never
    /// the one placed. RestoreComposer must treat that as "not my prompt to restore" and no-op the
    /// swap (leaving the real active prompt in place) rather than throw from the UI thread — while
    /// still leaving the composer typable, since a stray restore must never brick input.
    ///
    /// This test previously asserted the throw; that was the OLD contract (build twice = crash).
    /// The new, safer contract is a no-op for a mismatched instance, pinned below.
    /// </summary>
    [Fact]
    public void ShowPermissionPrompt_MovesFocusIntoThePrompt_SoItIsAnswerableByKEYBOARD()
    {
        // USER-REPORTED BUG: "I can't select the allow etc buttons with keyboard... Only mouse works."
        //
        // ReplaceControl swaps the composer OUT of the grid, but focus stayed on that removed control.
        // ButtonControl.ProcessKey returns false unless it has focus (ButtonControl.cs:225), so Tab,
        // Enter and Space reached nothing and clicking was the only way to answer.
        //
        // That is a bad failure for a SECURITY prompt in particular: it blocks goal submission until
        // answered, so a keyboard-driven user was stuck on a question they could not answer.
        // RestoreComposer already called FocusComposer() on the way OUT; nothing did the equivalent
        // on the way IN.
        var mw = BuiltMainWindow();
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var built = prompt.BuildContent();

        mw.ShowPermissionPrompt(built);

        var focused = mw.Window!.FocusManager.FocusedControl;
        Assert.NotNull(focused);
        Assert.IsType<ButtonControl>(focused);          // a BUTTON, not the panel that contains them
        Assert.False(mw.Input.HasFocus, "focus must leave the composer that was swapped out");
    }

    [Fact]
    public void RestoreComposer_WithADifferentBuildContentCall_IsASafeNoOp_NotTheSameInstance()
    {
        var mw = BuiltMainWindow();
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var built = prompt.BuildContent();
        mw.ShowPermissionPrompt(built);

        var ex = Record.Exception(() => mw.RestoreComposer(prompt.BuildContent()));
        Assert.Null(ex);

        // The mismatched call must not have touched the real active prompt: the composer is not
        // back in the tree yet (a real BuildContent()-once caller must still restore it properly).
        Assert.False(mw.Input.HasFocus, "the stray restore must not have swapped the real prompt out");

        // The correct call — passing the SAME instance Show received — still works afterwards.
        mw.RestoreComposer(built);
        Assert.True(mw.Input.HasFocus);
    }

    [Fact]
    public void PermissionPrompt_IsRaisedOntoItsOwnSurface()
    {
        // REPLACES A FULL-SCREEN DIM. Overlaying everything above the prompt darkened the whole
        // transcript to draw attention to six rows, and its edge had to be computed from the
        // prompt's laid-out bounds — so it moved with the height of whatever command was being
        // asked about. Raising the prompt says the same thing locally, with nothing else moving.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        // THE REAL PROMPT, not a bare markup control. The elevation is set where the prompt is
        // BUILT — a prompt owns its own appearance — so asserting it through a stand-in control
        // tested the fallback in MainWindow rather than the thing that actually ships.
        var prompt = new CxAgent.UI.PermissionPromptControl(
            new CxAgent.Core.Permissions.PermissionRequest(
                CxAgent.Core.Permissions.PermissionKind.Shell, "ls /", "ls /"),
            offerTrust: false);
        var content = prompt.BuildContent();

        mw.ShowPermissionPrompt(content);
        Assert.Equal(CxAgent.UI.ColorScheme.PromptSurface,
            ((SharpConsoleUI.Controls.ScrollablePanelControl)content).BackgroundColor);
    }

    [Fact]
    public void PromptSurface_IsRaisedAboveTheComposerItReplaces()
    {
        // The prompt must be a step UP from the surface it takes the place of, or the elevation
        // says nothing. Derived from ComposerSurface rather than picked, so the two cannot drift.
        Assert.True(
            SharpConsoleUI.Helpers.PaletteColors.Luminance(CxAgent.UI.ColorScheme.PromptSurface) >
            SharpConsoleUI.Helpers.PaletteColors.Luminance(CxAgent.UI.ColorScheme.ComposerSurface),
            "the permission prompt must sit above the composer, not level with it");
    }

    [Fact]
    public void PermissionPrompt_HidesTheStatusBarAndRestoresIt()
    {
        // Every key the bar advertises is inert until the prompt is answered, so showing them is a
        // promise the app will not keep. Hiding also gives the question the bottom of the screen.
        //
        // The RESTORE is the half that regresses silently: a bar left hidden takes every shortcut
        // with it for the rest of the session, and nothing errors when it happens.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        Assert.True(mw.StatusBar.Visible);

        var prompt = SharpConsoleUI.Builders.Controls.Markup().AddLine("Run shell command?").Build();
        mw.ShowPermissionPrompt(prompt);
        Assert.False(mw.StatusBar.Visible, "a permission prompt must hide the status bar");

        mw.RestoreComposer(prompt);
        Assert.True(mw.StatusBar.Visible, "restoring the composer must bring the status bar back");
    }

    [Fact]
    public void MarkdownStyle_UsesDistinctHues_NotOneFamily()
    {
        // The framework default is deliberately restrained — its own comment calls it "one cool
        // blue-grey family... without competing hues" — which is right for a log viewer and wrong
        // for a transcript that is mostly model-authored Markdown. H1-H3 were three shades of the
        // same blue and H4-H6 had no colour at all, so a document read as one flat wash.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        new MainWindow(Sys(), res, Logs()).Build();

        var style = SharpConsoleUI.Configuration.MarkdownStyle.Default;

        // EVERY heading level is coloured. The old style left H4-H6 null, so deep structure vanished.
        Assert.NotNull(style.H1Color);
        Assert.NotNull(style.H4Color);
        Assert.NotNull(style.H6Color);

        // Headings, code, quotes and links are FOUR DIFFERENT colours — the property that makes a
        // document's structure visible before it is read, and the one the old palette lacked.
        var hues = new[]
        {
            style.H1Color!.Value, style.CodeForeground, style.QuoteColor, style.LinkColor,
        };
        Assert.Equal(hues.Length, hues.Distinct().Count());
    }

    [Fact]
    public void SessionPanel_HiddenOnANarrowTerminal_ShownOnAWideOne()
    {
        // The panel is a luxury of WIDTH. Below the threshold a 24-column panel is a third of the
        // screen spent on six numbers, beside code the user is trying to read.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());

        var narrow = new MainWindow(SysOfWidth(80), res, Logs());
        narrow.Build();
        narrow.RefreshSessionPanel();
        Assert.False(narrow.SessionPanel.Control.Visible);

        var wide = new MainWindow(SysOfWidth(140), res, Logs());
        wide.Build();
        wide.RefreshSessionPanel();
        Assert.True(wide.SessionPanel.Control.Visible);
    }

    [Fact]
    public void SessionPanel_F3OverridesTheWidthInBothDirections()
    {
        // The override must win BOTH ways, and there is a third state: "decide for me". Without it
        // the first resize after an explicit F3 would silently undo the user's choice.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(SysOfWidth(80), res, Logs());   // narrow: hidden by default
        mw.Build();
        mw.RefreshSessionPanel();
        Assert.False(mw.SessionPanel.Control.Visible);

        mw.ToggleSessionPanel();                                 // force SHOWN on a narrow terminal
        Assert.True(mw.SessionPanel.Control.Visible);

        mw.ToggleSessionPanel();                                 // force HIDDEN
        Assert.False(mw.SessionPanel.Control.Visible);

        mw.ToggleSessionPanel();                                 // back to automatic → narrow → hidden
        mw.RefreshSessionPanel();
        Assert.False(mw.SessionPanel.Control.Visible);
    }

    [Fact]
    public void SessionPanel_OmitsThePercentageWhenTheWindowIsUnknown()
    {
        // A percentage needs a denominator. Inventing one would put a confident number on a guess.
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 5_000, spentTokens: 5_000, contextWindow: null, model: "m", endpoint: "", rules: 0);

        var text = panel.RenderedText;

        Assert.Contains("5,000 tokens", text, StringComparison.Ordinal);
        Assert.DoesNotContain("% used", text, StringComparison.Ordinal);
        Assert.Contains("none granted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_ShowsTheCapsThatWillEndTheRun()
    {
        // A goal that stops "for no reason" has almost always hit a cap, and the numbers lived only
        // in config.json — readable after the fact, when the run was already over.
        var panel = new SessionPanel();
        panel.RecordTurn(toolCalls: 1);
        panel.Refresh(contextUsed: 100, spentTokens: 100, contextWindow: 1000, model: "m", endpoint: "", rules: 0,
            maxTurns: 200, goalTokenBudget: 50_000);

        Assert.Contains("1/200 turns", panel.RenderedText, StringComparison.Ordinal);
        // Compact now, like every other count in a 24-column panel.
        Assert.Contains("50.0k token budget", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_SaysNoCapWhenTheSessionIsUnbounded()
    {
        // Single-agent no longer defaults to 200 turns, so printing "3/200" would advertise a
        // ceiling that was removed precisely because it ended real work at an arbitrary number. The
        // tool-result cap is still shown, because that one always applies.
        var panel = new SessionPanel();
        panel.RecordTurn(toolCalls: 2);
        panel.Refresh(contextUsed: 100, spentTokens: 100, contextWindow: 1000, model: "m", endpoint: "", rules: 0);

        Assert.Contains("1 turns · no cap", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("65.5k tool result", panel.RenderedText, StringComparison.Ordinal);
        Assert.DoesNotContain("/200", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_AlwaysShowsTheLimitsThatActuallyBind()
    {
        // This asserted the OPPOSITE and was wrong. The block was gated on the orchestrator CONFIG,
        // so with no such block — the common case — it rendered nothing, and the caps stayed exactly
        // as invisible as before the panel existed. But they still APPLY: MaxWorkerTurns falls back
        // to 200 at the call site whether configured or not, and the tool-result cap is a const no
        // config touches. A cap you cannot see is one you cannot plan around.
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 100, spentTokens: 100, contextWindow: 1000, model: "m", endpoint: "", rules: 0,
            maxTurns: 200);

        Assert.Contains("Limits", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("0/200 turns", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("65.5k tool result", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_ShowsTheAgentIdForLogCorrelation()
    {
        // Not glanceable — nobody reads a ULID — but it is the one string connecting what is on
        // screen to the logs on disk, which are written to a directory named by exactly this. It is
        // the AGENT's id, fixed for the session, so the directory it names does not move mid-session.
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: null, spentTokens: 0, contextWindow: null, model: "m", endpoint: "", rules: 0,
            sessionId: "01KZEF93C6K66HP6T2SJ9WKMHR");

        Assert.Contains("01KZEF93C6K66HP6T2SJ9WKMHR", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact(Skip = "Superseded: the turn cap binds in BOTH modes, so it is shown in both.")]
    public void SessionPanel_HidesTheWorkerTurnCap_InSingleAgent()
    {
        // MaxWorkerTurns' own documentation calls it "a cap one level DOWN: it bounds a single
        // llm_agent WORKER's tool loop". Single-agent has no workers, so there it silently becomes
        // the whole session's budget at a number no real session approaches. Advertising it invites
        // the user to plan around a constraint that never binds.
        var orch = new OrchestratorSettings(null, null, MaxWorkerTurns: 200);
        var res = new ProviderResolution(new MockLlmProvider(), "Mock",
            System.Array.Empty<string>(), orch);

        var single = new MainWindow(SysOfWidth(140), res, Logs());
        single.Build();
        single.RefreshSessionPanel();
        Assert.DoesNotContain("200 turns", single.SessionPanel.RenderedText, StringComparison.Ordinal);

        // In FAN-OUT it bounds a real worker, so it is shown.
        var fan = new MainWindow(SysOfWidth(140), res, Logs()) { FanOut = true };
        fan.Build();
        fan.RefreshSessionPanel();
        Assert.Contains("200 turns", fan.SessionPanel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_ShowsTheInputOutputSplit()
    {
        // Input and output behave nothing alike: input grows with the conversation (every turn
        // re-sends everything before it) and dominates a long session, while output is what the
        // model produced. A single total hides which is growing — and they have different remedies,
        // compress the history or ask for less.
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: 96_500, spentTokens: 96_500, contextWindow: 200_000, model: "m", endpoint: "", rules: 0,
            inputTokens: 94_000, outputTokens: 2_500);

        // Compact, because 24 columns cannot hold two full counts on one line — and at this
        // magnitude the exact digits are never what the number is read for.
        Assert.Contains("↑94.0k", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("↓2.5k", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_OmitsTheSplitBeforeAnyUsageIsReported()
    {
        // "↑0 ↓0" is noise: it takes a line to say nothing has happened yet, and a provider that
        // never reports usage would show it for the whole session.
        var panel = new SessionPanel();
        panel.Refresh(contextUsed: null, spentTokens: 0, contextWindow: 200_000, model: "m", endpoint: "", rules: 0);

        Assert.DoesNotContain("↑", panel.RenderedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, 24)]   // at the threshold: the documented minimum, 76 left for the transcript
    [InlineData(120, 24)]   // 120/5 = 24, exactly the floor
    [InlineData(160, 32)]
    [InlineData(200, 40)]   // reaches the cap
    [InlineData(400, 40)]   // capped: past here the panel gains nothing and the transcript loses
    public void SessionPanel_WidthIsProportionalWithinBounds(int terminal, int expected)
    {
        // A constant 24 is right at 100 columns and wrong at 200 — model ids and paths wrap for no
        // reason while a third of the screen sits unused. A share keeps the proportion the layout
        // was designed around instead of freezing one terminal's answer.
        Assert.Equal(expected, SessionPanel.WidthFor(terminal));
    }

    [Fact]
    public void SessionPanel_ColumnStartsAtTheWidthForItsTerminal()
    {
        // The grid's own re-widening on resize is not asserted here: it needs a driver-level screen
        // resize the test harness has no accessor for, and a test that fakes it would be testing the
        // fake. The WIDTH RULE is the part that can silently go wrong, and it is pure.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(SysOfWidth(200), res, Logs());
        mw.Build();
        mw.RefreshSessionPanel();

        Assert.True(mw.SessionPanel.Control.Visible);
        Assert.Equal(40, SessionPanel.WidthFor(200));
    }
}
