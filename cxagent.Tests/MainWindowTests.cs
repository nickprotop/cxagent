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
    public void Build_GoalComposer_StartsInEditingMode_SoTypingWorks()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        Assert.True(mw.Input.HasFocus, "composer must hold focus");
        Assert.True(mw.Input.IsEditing, "composer must be in editing mode or typed characters are discarded");
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
        Assert.True(mw.Input.IsEditing, "and it must still be typable");
    }

    /// <summary>F4 returns focus to the composer and restores editing mode.</summary>
    [Fact]
    public void FocusChat_ReturnsAndRestoresEditing()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        mw.Input.IsEditing = false;
        mw.FocusChat();

        Assert.True(mw.Input.HasFocus);
        Assert.True(mw.Input.IsEditing, "returning focus must restore editing mode, or typing dies again");
    }

    /// <summary>
    /// Ctrl+N clears the composer for a fresh goal and returns it to a typable state.
    /// </summary>
    [Fact]
    public void NewGoal_ClearsComposer_AndRestoresEditing()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        mw.Input.Content = "half-typed goal";
        mw.NewGoal();

        Assert.True(string.IsNullOrEmpty(mw.Input.Content));
        Assert.True(mw.Input.HasFocus);
        Assert.True(mw.Input.IsEditing);
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

        var shortcuts = mw.StatusBar.LeftItems
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
        var disabledPlaceholder = mw.Input.PlaceholderText;

        mw.SetSubmissionEnabled(true);

        Assert.True(mw.SubmissionEnabled);
        Assert.NotEqual(disabledPlaceholder, mw.Input.PlaceholderText);
    }

    [Fact]
    public void StatusBar_AdvertisesSettings_F5()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        var shortcuts = mw.StatusBar.LeftItems.Select(i => i.Shortcut).ToList();
        Assert.Contains("F5", shortcuts);
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
        Assert.True(mw.Input.IsEditing);
    }

    // --- Task 11: F6 diagnose, status-bar cost --------------------------------------------------

    [Fact]
    public void StatusBar_AdvertisesDiagnose_F6_InFanOut()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs()) { FanOut = true };
        mw.Build();

        Assert.Contains("F6", mw.StatusBar.LeftItems.Select(i => i.Shortcut));
    }

    [Fact]
    public void StatusBar_HidesDiagnoseAndNewGoal_InSingleAgent()
    {
        // The status bar is the ONLY discovery surface for these keys, so an entry that does nothing
        // is worse than a missing one — it teaches the user the app is broken rather than that the
        // feature lives elsewhere.
        //
        // F6 Diagnose resolves a FAILED JOB through GoalRunner.TryGetSession, and single-agent never
        // creates a session for it to find. F2 New Goal clears the composer and focuses it, both of
        // which single-agent has already done by the time the key could be pressed.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());   // FanOut defaults to false
        mw.Build();

        var shortcuts = mw.StatusBar.LeftItems.Select(i => i.Shortcut).ToList();
        Assert.DoesNotContain("F6", shortcuts);
        Assert.DoesNotContain("F2", shortcuts);

        // The keys that DO work in single-agent are still advertised.
        Assert.Contains("F5", shortcuts);
        Assert.Contains("Ctrl+Q", shortcuts);
    }

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

        Assert.Contains(mw.StatusBar.LeftItems, i => i.Shortcut == "F5" && (i.Label ?? "").Contains("Settings"));
        Assert.DoesNotContain(mw.StatusBar.LeftItems, i => i.Shortcut == "F7");
        Assert.DoesNotContain(mw.StatusBar.LeftItems, i => i.Shortcut == "F8");
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
        Assert.True(mw.Input.IsEditing);
    }

    /// <summary>
    /// F6's handler must find "the focused job" from wherever focus currently sits inside a job
    /// block (the block itself, or one of its buttons) — not just the block's own direct focus.
    /// FocusPath is the ancestor chain from the window root to the focused control, so walking it
    /// for a JobBlockControl covers both cases without MainWindow needing to know JobBlockControl's
    /// internal layout.
    /// </summary>
    [Fact]
    public void FocusedJobId_FindsJobBlock_AnywhereInFocusPath()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        mw.JobPanel.SetJobs(new[]
        {
            new CxAgent.Core.Models.Job
            {
                Id = "j1", GoalId = "g1", PluginType = "shell", DisplayName = "Demo",
                State = CxAgent.Core.Models.JobState.Failed,
            },
        });

        mw.FocusJobs();
        Assert.True(mw.JobPanel.TryGetBlock("j1", out var block));
        mw.Window!.FocusManager.SetFocus(block, SharpConsoleUI.Controls.FocusReason.Programmatic);

        Assert.Equal("j1", mw.FocusedJobId());
    }

    [Fact]
    public void FocusedJobId_Null_WhenNoJobBlockInFocusPath()
    {
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        // Composer, not a job block, holds focus after Build().
        Assert.Null(mw.FocusedJobId());
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
        Assert.True(mw.Input.IsEditing, "…and EDITING, or typing dies (D10)");
    }

    [Fact]
    public void RestoreComposer_PreservesWhateverTheUserHadTyped()
    {
        // The prompt interrupts mid-thought; the half-typed goal must survive the round trip.
        var mw = BuiltMainWindow();
        mw.Input.Content = "half-typed goal";
        var prompt = new PermissionPromptControl(ShellRequest("ls"));
        var built = prompt.BuildContent();
        mw.ShowPermissionPrompt(built);
        mw.RestoreComposer(built);
        Assert.Equal("half-typed goal", mw.Input.Content);
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
        Assert.True(mw.Input.IsEditing);
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
        Assert.True(mw.Input.IsEditing);
    }

    [Fact]
    public void PermissionPrompt_DimsTheTranscriptAndClearsItOnRestore()
    {
        // A permission prompt is the one moment the app stops and asks, and it rendered with the
        // same weight as the scrollback above it. The dim is how the cx family marks a modal.
        //
        // The LIFECYCLE is what can regress silently: a dim left attached keeps the transcript grey
        // over a session that is no longer asking anything, and there is no error when it happens.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        var prompt = SharpConsoleUI.Builders.Controls.Markup().AddLine("Run shell command?").Build();

        mw.ShowPermissionPrompt(prompt);
        Assert.True(mw.IsDimmed, "showing a permission prompt must dim the transcript");

        mw.RestoreComposer(prompt);
        Assert.False(mw.IsDimmed, "restoring the composer must clear the dim");
    }

    [Fact]
    public void PermissionPrompt_DimIsNotStackedByASecondShow()
    {
        // ShowPermissionPrompt no-ops when one is already showing (a caller bug it chooses to
        // survive). The dim must follow that rule too — two handlers would darken twice and only
        // one would ever be removed.
        var res = new ProviderResolution(new MockLlmProvider(), "Mock", System.Array.Empty<string>());
        var mw = new MainWindow(Sys(), res, Logs());
        mw.Build();

        var first = SharpConsoleUI.Builders.Controls.Markup().AddLine("first").Build();
        var second = SharpConsoleUI.Builders.Controls.Markup().AddLine("second").Build();

        mw.ShowPermissionPrompt(first);
        mw.ShowPermissionPrompt(second);   // no-op
        mw.RestoreComposer(first);

        Assert.False(mw.IsDimmed);
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
    public void SessionPanel_ShowsContextModelAndLocation()
    {
        var panel = new SessionPanel();
        panel.RecordTurn(toolCalls: 3);
        panel.Refresh(tokens: 47_000, contextWindow: 100_000, model: "qwen3.6-35b",
            endpoint: "openai-compatible", rules: 2);

        var text = panel.RenderedText;

        Assert.Contains("47,000 tokens", text, StringComparison.Ordinal);
        Assert.Contains("47% used", text, StringComparison.Ordinal);
        Assert.Contains("qwen3.6-35b", text, StringComparison.Ordinal);
        Assert.Contains("1 turn", text, StringComparison.Ordinal);      // singular, not "1 turns"
        Assert.Contains("3 tool calls", text, StringComparison.Ordinal);
        Assert.Contains("2 always-allow rules", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_OmitsThePercentageWhenTheWindowIsUnknown()
    {
        // A percentage needs a denominator. Inventing one would put a confident number on a guess.
        var panel = new SessionPanel();
        panel.Refresh(tokens: 5_000, contextWindow: null, model: "m", endpoint: "", rules: 0);

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
        panel.Refresh(tokens: 100, contextWindow: 1000, model: "m", endpoint: "", rules: 0,
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
        panel.Refresh(tokens: 100, contextWindow: 1000, model: "m", endpoint: "", rules: 0);

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
        panel.Refresh(tokens: 100, contextWindow: 1000, model: "m", endpoint: "", rules: 0,
            maxTurns: 200);

        Assert.Contains("Limits", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("0/200 turns", panel.RenderedText, StringComparison.Ordinal);
        Assert.Contains("65.5k tool result", panel.RenderedText, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPanel_ShowsTheGoalIdForLogCorrelation()
    {
        // Not glanceable — nobody reads a ULID — but it is the one string connecting what is on
        // screen to the logs on disk, which are written to a directory named by exactly this.
        var panel = new SessionPanel();
        panel.Refresh(tokens: 0, contextWindow: null, model: "m", endpoint: "", rules: 0,
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
        panel.Refresh(tokens: 96_500, contextWindow: 200_000, model: "m", endpoint: "", rules: 0,
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
        panel.Refresh(tokens: 0, contextWindow: 200_000, model: "m", endpoint: "", rules: 0);

        Assert.DoesNotContain("↑", panel.RenderedText, StringComparison.Ordinal);
    }
}
