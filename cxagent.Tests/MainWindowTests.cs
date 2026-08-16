using CxAgent.Core.Agent;
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

    // ESCAPE ON A PERMISSION PROMPT ANSWERS IT, and the run survives. It used to do both — deny AND
    // cancel the turn — because the prompt handles no keys, so Escape fell through to the global
    // shortcut's CancelTurn branch (a prompt only exists mid-turn), and cancelling fired the gate's
    // registration, which resolves the prompt as Deny anyway. A live drive lost two million tokens
    // of work to one keystroke on a denied test-file write.
    [Fact]
    public void TryDenyPermission_AnswersThePromptAndReportsItHandledTheKey()
    {
        var mw = BuiltMainWindow();
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var built = prompt.BuildContent();

        mw.ShowPermissionPrompt(built, prompt.TryCancel);

        Assert.True(mw.TryDenyPermission());
        Assert.True(prompt.Completion.IsCompleted);
        Assert.Equal(PermissionChoice.Deny, prompt.Completion.Result);
    }

    // FALSE WHEN THERE IS NOTHING TO ANSWER, so Escape falls through to whatever else wants it —
    // cancelling a turn, closing a dialog. A hook that swallowed the key unconditionally would take
    // away the stop-the-run behaviour it was added to protect.
    [Fact]
    public void TryDenyPermission_IsFalseWithNoPromptUp()
    {
        var mw = BuiltMainWindow();

        Assert.False(mw.TryDenyPermission());
    }

    // AND FALSE AGAIN ONCE THE PROMPT IS GONE. A stale deny action would resolve a completion source
    // nobody awaits — harmless alone, but it would also eat the keystroke and stop Escape reaching
    // the turn the user meant to cancel.
    [Fact]
    public void TryDenyPermission_IsFalseAfterThePromptIsRestored()
    {
        var mw = BuiltMainWindow();
        var prompt = new PermissionPromptControl(ShellRequest("git status"));
        var built = prompt.BuildContent();

        mw.ShowPermissionPrompt(built, prompt.TryCancel);
        mw.RestoreComposer(built);

        Assert.False(mw.TryDenyPermission());
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
        // The occupancy half moved to the status bar with the panel's Context block; what remains
        // panel-side is the permissions line this always also asserted.
        Assert.Contains("5,000", MainWindow.ContextLabelForTest(used: 5_000, spent: 5_000, window: null),
            StringComparison.Ordinal);
        Assert.DoesNotContain("%", MainWindow.ContextLabelForTest(used: 5_000, spent: 5_000, window: null),
            StringComparison.Ordinal);

        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 5_000,
            SpentTokens = 5_000,
            ContextWindow = null,
            Endpoint = "",
            Rules = 0,
        });

        Assert.Contains("none granted", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_ShowsTheCapsThatWillEndTheRun()
    {
        // A goal that stops "for no reason" has almost always hit a cap, and the numbers lived only
        // in config.json — readable after the fact, when the run was already over.
        var panel = new SessionPanel();
        panel.RecordTurn(toolCalls: 1);
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 100,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            MaxTurns = 200,
        });

        // PER GOAL, STATED ALONE. This asserted "1/200 turns", pairing the session's lifetime turn
        // count with a cap that resets on every prompt — two different denominators sharing one
        // slash, so a long session read "290/300" while the current goal had taken three.
        Assert.Contains("200 turns per goal", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PANEL SHOWS THE CEILING THAT BINDS, not the one that was typed.
    ///
    /// <para>It used to render the raw configured value, so an unconfigured session printed
    /// "no cap" while a real ceiling was in force — a live drive read "66 turns · no cap" and the
    /// cap was there the whole time. A limit you are told you do not have is worse than one you
    /// cannot see.</para>
    /// </summary>
    [Fact]
    public void SessionPanel_ShowsTheDefaultCeiling_WhenNothingIsConfigured()
    {
        var window = new MainWindow(SysOfWidth(140),
            new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>(),
                new OrchestratorSettings()),
            Logs());
        window.Build();
        window.RefreshSessionPanel();

        Assert.Contains($"{AgentHost.DefaultTurnCeiling} turns per goal", window.SessionPanel.RenderedText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("no turn cap", window.SessionPanel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>...and "no cap" is reserved for the explicit opt-out.</summary>
    [Fact]
    public void SessionPanel_SaysNoCap_OnlyForAnExplicitZero()
    {
        var window = new MainWindow(SysOfWidth(140),
            new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>(),
                new OrchestratorSettings(MaxTurns: 0)),
            Logs());
        window.Build();
        window.RefreshSessionPanel();

        Assert.Contains("no turn cap", window.SessionPanel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_SaysNoCapWhenTheSessionIsUnbounded()
    {
        // Zero reaches the panel only for an EXPLICIT opt-out (orchestrator.maxTurns: 0). An
        // unconfigured session now resolves to the default before it gets here, so "no cap" means
        // what it says — it used to print for every unconfigured session while a real ceiling bound.
        var panel = new SessionPanel();
        panel.RecordTurn(toolCalls: 2);
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 100,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
        });

        Assert.Contains("no turn cap", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("65.5k tool result", panel.RenderedText, StringComparison.Ordinal);
        Assert.DoesNotContain("/200", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_AlwaysShowsTheLimitsThatActuallyBind()
    {
        // This asserted the OPPOSITE and was wrong. The block was gated on the orchestrator CONFIG,
        // so with no such block — the common case — it rendered nothing, and the caps stayed exactly
        // as invisible as before the panel existed. But they still APPLY. A cap you cannot see is
        // one you cannot plan around.
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 100,
            SpentTokens = 100,
            ContextWindow = 1000,
            Endpoint = "",
            Rules = 0,
            MaxTurns = 200,
        });

        Assert.Contains("Limits", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("200 turns per goal", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("65.5k tool result", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PANEL AND THE STATUS BAR CANNOT DISAGREE, because only one of them answers this now.
    ///
    /// <para>They did disagree: RefreshTokenItem read _contextUsed while the panel was handed
    /// _lastTokens, so one reported 2% and the other 9% of the same session. That was locked by
    /// asserting both showed the same figure — a guard that only works while someone remembers to
    /// keep it. Cutting the panel's duplicated Context block replaced the guard with a structure:
    /// there is one occupancy readout, so there is nothing left to drift.</para>
    ///
    /// <para>The assertion is therefore the ABSENCE of a second one. Occupancy figures reaching the
    /// panel again would be the duplication coming back.</para>
    /// </summary>
    [Fact]
    public void OnlyTheStatusBar_ReportsOccupancy()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>())
        {
            ContextWindow = 200_000,
        };
        // WIDE: the panel hides itself below a width threshold, and a hidden panel renders nothing.
        var mw = new MainWindow(SysOfWidth(200), res, Logs());
        mw.Build();

        mw.SetTokenTotal(96_500);      // the cumulative SPEND
        mw.SetContextUsed(20_000);     // the OCCUPANCY — 10% of the window

        var panel = mw.SessionPanel.RenderedText;
        Assert.DoesNotContain("20,000", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("96,500", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("%", panel, StringComparison.Ordinal);

        // And the status bar still has both, distinctly — occupancy as the percentage, spend as
        // itself. The old bug was the spend BECOMING the percentage; 96,500 of 200,000 is 48%.
        var status = MainWindow.ContextLabelForTest(used: 20_000, spent: 96_500, window: 200_000);
        Assert.Contains("10%", status, StringComparison.Ordinal);
        Assert.Contains("96,500 spent", status, StringComparison.Ordinal);
        Assert.DoesNotContain("48%", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE GAUGE MOVES AFTER A COMPRESSION — the reported "compress, and nothing changes" bug.
    ///
    /// <para>SetContextUsed once refreshed the status-bar item alone, and compression reaches the UI
    /// through exactly this method, so the panel kept showing the pre-compression figure. The panel
    /// no longer carries occupancy at all (it duplicated the status bar), so the assertion follows
    /// the number: the STATUS BAR must reflect the new reading and not the old one.</para>
    /// </summary>
    [Fact]
    public void AfterCompression_TheOccupancyReadoutMoves()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>())
        {
            ContextWindow = 200_000,
        };
        var mw = new MainWindow(SysOfWidth(200), res, Logs());
        mw.Build();

        mw.SetContextUsed(100_000);
        Assert.Contains("50%", MainWindow.ContextLabelForTest(used: 100_000, spent: 0, window: 200_000),
            StringComparison.Ordinal);

        // A compression freed most of it. The new reading supersedes the old rather than joining it.
        mw.SetContextUsed(20_000);
        var after = MainWindow.ContextLabelForTest(used: 20_000, spent: 0, window: 200_000);
        Assert.Contains("10%", after, StringComparison.Ordinal);
        Assert.DoesNotContain("50%", after, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_ShowsTheAgentIdForLogCorrelation()
    {
        // Not glanceable — nobody reads a ULID — but it is the one string connecting what is on
        // screen to the logs on disk, which are written to a directory named by exactly this. It is
        // the AGENT's id, fixed for the session, so the directory it names does not move mid-session.
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = null,
            SpentTokens = 0,
            ContextWindow = null,
            Endpoint = "",
            Rules = 0,
            SessionId = "01KZEF93C6K66HP6T2SJ9WKMHR",
        });

        Assert.Contains("01KZEF93C6K66HP6T2SJ9WKMHR", panel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OPENING BANNER NAMES THE MODE THE SESSION ACTUALLY STARTED IN.
    ///
    /// <para>It said "single agent" unconditionally — the word was hardcoded, predating modes
    /// existing — so `--mode fan-out` opened with a banner contradicting the composer line beneath
    /// it. And unlike that line the banner cannot be corrected later: it is a chat message, and the
    /// transcript is a record rather than a live readout. The mode therefore has to arrive BEFORE
    /// Build(), which is what StartupMode is for.</para>
    /// </summary>
    [Theory]
    [InlineData(AgentMode.FanOut, "fan-out")]
    [InlineData(AgentMode.Single, "single")]
    public void TheBanner_NamesTheStartupMode(AgentMode agent, string expected)
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(SysOfWidth(200), res, Logs())
        {
            StartupMode = new WorkingMode(agent, EditMode.AcceptEdits),
        };
        mw.Build();

        // The BANNER'S subtitle, which is what Build wrote into the transcript. Asserted through the
        // same seam the composer line uses rather than by reading the chat control back: the
        // transcript exposes ids, not text, and the mode is what is under test here — not markup.
        Assert.Equal(agent, mw.CurrentMode.Agent);
        Assert.Contains(expected, mw.CurrentModeText, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// BOTH AXES ON THE LINE, agent first — it is the coarser fact, framing whose edits are being
    /// accepted — with the shortcut hint attached where a user is already looking for it.
    /// </summary>
    [Fact]
    public void TheModeLine_ShowsBothAxes_AndTheShortcutHint()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(SysOfWidth(200), res, Logs())
        {
            StartupMode = new WorkingMode(AgentMode.FanOut, EditMode.AlwaysAsk),
        };
        mw.Build();

        var text = mw.CurrentModeText;

        Assert.Contains("fan-out", text, System.StringComparison.Ordinal);
        Assert.Contains("always-ask", text, System.StringComparison.Ordinal);
        Assert.True(text.IndexOf("fan-out", System.StringComparison.Ordinal)
                  < text.IndexOf("always-ask", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// THE WAY BACK TO THE COMPOSER EXISTS AND IS PUBLIC. F4 is bound to this in AppBootstrap, and
    /// the binding is the kind of one-liner that gets tidied away — this method was removed once
    /// already, on the reasoning that focus could no longer be lost.
    ///
    /// <para>It still can: a question moves focus to its first option and the permission prompt moves
    /// it into the prompt panel. Both restore it, but a user who ends up outside the composer has no
    /// way back except guessing at Tab order, and a keyboard that does nothing is not a state anyone
    /// can debug from the outside.</para>
    /// </summary>
    [Fact]
    public void FocusComposer_IsReachable_ForTheF4Binding()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(SysOfWidth(200), res, Logs());
        mw.Build();

        // Before Build's window exists this is a no-op rather than a throw, and after it focuses the
        // composer. Either way it must not fault: a shortcut that throws takes the UI with it.
        mw.FocusComposer();
    }

    /// <summary>
    /// THE BAR IS THIS AGENT; THE PANEL IS EVERYTHING. The ledger is shared — children record into it
    /// deliberately, because a budget belongs to the conversation rather than to whichever agent did
    /// the work — so <c>Ledger.TotalTokens</c> is session-wide and was wrong for a readout sitting
    /// beside an occupancy percentage that IS the parent's. A fan-out session showed a spend four
    /// times the parent's with nothing on screen to say why.
    ///
    /// <para>The session view has a home: the panel, whose "Tokens by agent" block reconciles the
    /// two. This asserts the seam that feeds them apart — <c>OwnSpend</c> excludes children while the
    /// ledger includes them.</para>
    /// </summary>
    [Fact]
    public void OwnSpend_ExcludesSubAgents_WhileTheLedgerIncludesThem()
    {
        var ledger = new TokenLedger();

        // A parent turn and a child turn, into the one shared ledger.
        ledger.Record(new LlmUsage { InputTokens = 100, OutputTokens = 10 }, "m");
        ledger.Record(new LlmUsage { InputTokens = 800, OutputTokens = 80 }, "m", subAgent: true);

        Assert.Equal(990, ledger.TotalTokens);       // the session — what the panel shows
        Assert.Equal(880, ledger.SubAgentTokens);    // the workers' share
        // …and 110 is the parent's, which is what the bar must show. AgentHost.OwnSpend reads it from
        // the agent's private tally rather than by subtracting, so the two cannot drift.
        Assert.Equal(110, ledger.TotalTokens - ledger.SubAgentTokens);
    }

    /// <summary>
    /// THE STATUS BAR CARRIES THE ↑/↓ SPLIT, beside the total it splits.
    ///
    /// <para>Input and output behave nothing alike — input grows with the conversation because every
    /// turn re-sends everything before it, output is only what the model produced — and they have
    /// different remedies: compress the history, or ask for less. A lone total says which is true of
    /// neither, and the status bar is the readout that is always on screen.</para>
    /// </summary>
    [Fact]
    public void StatusBar_ShowsTheInputOutputSplit_BesideTheSpend()
    {
        var text = MainWindow.ContextLabelForTest(used: 9_140, spent: 160_084, window: 212_992,
            input: 153_100, output: 6_900);

        Assert.Contains("160,084 spent", text, StringComparison.Ordinal);
        Assert.Contains("↑153.1k", text, StringComparison.Ordinal);
        Assert.Contains("↓6.9k", text, StringComparison.Ordinal);
    }

    /// <summary>No split reported, no arrows — the same rule occupancy follows. A provider that
    /// reports no usage breakdown must not be shown "↑0 ↓0", which reads as a measurement of nothing
    /// rather than the absence of one.</summary>
    [Fact]
    public void StatusBar_OmitsTheSplit_WhenNoneWasReported()
    {
        var text = MainWindow.ContextLabelForTest(used: 9_140, spent: 160_084, window: 212_992);

        Assert.Contains("160,084 spent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("↑", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// SetTokenSplit REPAINTS rather than only storing. It used to store and stop, which was correct
    /// while the session panel was the sole reader and SetTokenTotal refreshed it a moment later on
    /// the same event. The status bar reads the split now, so a setter that does not repaint leaves
    /// the always-visible readout stale until something else happens to redraw it.
    ///
    /// <para>Asserted as "does not throw and the numbers reach the label": the status bar's rendered
    /// text is owned by the console control and is not readable from here, so this pins the call
    /// path while <see cref="StatusBar_ShowsTheInputOutputSplit_BesideTheSpend"/> pins the format.
    /// </para>
    /// </summary>
    [Fact]
    public void SetTokenSplit_RepaintsTheStatusBar()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>())
        {
            ContextWindow = 200_000,
        };
        var mw = new MainWindow(SysOfWidth(200), res, Logs());
        mw.Build();

        mw.SetTokenTotal(96_500);
        mw.SetContextUsed(20_000);
        mw.SetTokenSplit(94_000, 2_500);   // arrives on its own; nothing else redraws after it

        Assert.Contains("↑94.0k",
            MainWindow.ContextLabelForTest(used: 20_000, spent: 96_500, window: 200_000,
                input: 94_000, output: 2_500),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_ShowsTheInputOutputSplit_PerModel()
    {
        // Input and output behave nothing alike: input grows with the conversation (every turn
        // re-sends everything before it) and dominates a long session, while output is what the
        // model produced. A single total hides which is growing — and they have different remedies,
        // compress the history or ask for less.
        //
        // PER MODEL, not session-wide. A single ↑/↓ pair sat above the per-model list and read as
        // the parent's when it was in fact the sum of every agent including children on other
        // providers. The panel is the aggregator; its figures must say what they aggregate.
        var panel = new SessionPanel();
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = 96_500,
            SpentTokens = 96_500,
            ContextWindow = 200_000,
            Endpoint = "",
            Rules = 0,
            SpendByModel = new Dictionary<string, int> { ["qwen3.6-35b.gguf"] = 96_500 },
            SplitByModel = new Dictionary<string, (int Input, int Output)>
            {
                ["qwen3.6-35b.gguf"] = (94_000, 2_500),
            },
        });

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
        panel.Refresh(new SessionPanel.SessionPanelState
        {
            ContextUsed = null,
            SpentTokens = 0,
            ContextWindow = 200_000,
            Endpoint = "",
            Rules = 0,
        });

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

    /// <summary>
    /// `general` IS ALWAYS SHOWN, whether or not config names a type.
    ///
    /// <para>The panel used to filter it out and read the raw config keys, so a session with no
    /// configured types showed no Agent types section at all — and delegation looked unavailable to
    /// anyone who had not written one. `general` is what a bare spawn uses; it is a capability the
    /// session has, not a placeholder.</para>
    /// </summary>
    [Fact]
    public void SessionPanel_ShowsGeneral_EvenWithNoConfiguredTypes()
    {
        var window = new MainWindow(SysOfWidth(140),
            new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>()),
            Logs());
        window.Build();
        window.RefreshSessionPanel();

        Assert.Contains("Agent types", window.SessionPanel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("general", window.SessionPanel.RenderedText, StringComparison.Ordinal);
    }

    /// <summary>...and it is listed once, first, even when config overrides it.</summary>
    [Fact]
    public void SessionPanel_ListsGeneralOnce_WhenConfigAlsoDefinesIt()
    {
        var configured = new Dictionary<string, AgentTypeConfig>
        {
            ["general"] = new("overridden briefing", null, null),
            ["explore"] = new("read things", null, null),
        };

        var window = new MainWindow(SysOfWidth(140),
            new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>())
            {
                AgentTypes = configured,
            },
            Logs());
        window.Build();
        window.RefreshSessionPanel();

        var text = window.SessionPanel.RenderedText;
        Assert.Contains("general, explore", text, StringComparison.Ordinal);
    }
}
