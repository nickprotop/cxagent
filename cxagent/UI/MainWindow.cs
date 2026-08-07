using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>
/// The app shell: ONE column — ChatTranscript over the multi-line goal input (MultilineEditControl;
/// Enter submits, Shift+Enter = newline), with a clickable StatusBar. Jobs render INLINE in the
/// transcript via InlineJobSink rather than in a side panel. When no provider resolved, the chat shows an actionable message and
/// submission is disabled (the seam the P5c wizard fills).
/// </summary>
public sealed class MainWindow
{
    private readonly ConsoleWindowSystem _system;
    private readonly ProviderResolution _resolution;

    public ChatTranscriptControl Chat { get; } = new()
    {
        VerticalAlignment = VerticalAlignment.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    public MultilineEditControl Input { get; } = new(viewportHeight: 3);   // multi-line goal composer
    public JobPanelControl JobPanel { get; }
    public StatusBarControl StatusBar { get; } = new(stickyBottom: true);
    public bool SubmissionEnabled { get; private set; }
    public Window? Window { get; private set; }

    /// <summary>
    /// Assigned by AppBootstrap to open the settings/first-run wizard flow. A property rather than a
    /// constructor dependency so MainWindow stays independent of SetupWizard; null until wired.
    /// </summary>
    public Func<Task>? ShowSettings { get; set; }

    /// <summary>
    /// Assigned by AppBootstrap (F6): diagnoses whichever job currently has focus. Same seam pattern
    /// as ShowSettings — MainWindow doesn't know about JobDiagnoser/RecoveryFlow/DagModifier, it just
    /// exposes where the trigger lands. Null until wired.
    /// </summary>
    public Func<Task>? DiagnoseFocusedJob { get; set; }

    /// <summary>
    /// True in fan-out mode, where a DAG of jobs exists. Single-agent has no dag and no scheduler,
    /// which decides what the status bar may offer: F6 Diagnose resolves a FAILED JOB through
    /// GoalRunner.TryGetSession, and single-agent never creates a session for it to find — the key
    /// was advertised, pressed, and silently did nothing.
    /// </summary>
    public bool FanOut { get; init; }

    // ShowRoles (F7) and ShowProviders (F8) were removed with their keys. They existed to open two
    // SEPARATE editors; once both became pages of the one Settings dialog they opened the same
    // window on a different page, which is a parameter, not a seam. ShowSettings is the single
    // entry point, and the page choice lives inside the dialog where the four names are visible.

    private StatusBarItem? _tokenItem;


    /// <summary>
    /// The "type your goal here" hint (D10). Retired the moment the user submits their first goal —
    /// by then they have found the composer, and a permanent hint would just be noise competing with
    /// the token readout for the same corner.
    /// </summary>
    private StatusBarItem? _composerHint;
    private StatusBarItem? _approveItem;
    private StatusBarItem? _discardItem;

    /// <summary>The composer's grid, promoted from a Build()-local so Show/RestorePermissionPrompt
    /// can swap the Input cell's content via ReplaceControl (GridControl.cs:381), which preserves
    /// the cell's GridPlacement across the swap.</summary>
    private GridControl _mainGrid = null!;
    private RuleControl _composerRule = null!;

    /// <summary>Whichever control currently occupies the composer's grid cell in place of
    /// <see cref="Input"/> — null when the composer itself is there. Tracked so a second
    /// ShowPermissionPrompt (a caller bug in Task 4's serialisation) can no-op instead of crashing
    /// the render loop on GridControl.ReplaceControl's "not currently placed" ArgumentException.</summary>
    private IWindowControl? _activePrompt;

    /// <summary>Whether the transcript is currently dimmed behind a permission prompt. Exposed so
    /// the attach/detach balance can be asserted — a dim left on is invisible in code and obvious
    /// on screen, which is the wrong way round for a defect.</summary>
    public bool IsDimmed => _dimHandler is not null;

    /// <summary>The dim handler while a permission prompt is showing; null otherwise.</summary>
    private SharpConsoleUI.Windows.WindowRenderer.BufferPaintDelegate? _dimHandler;

    public MainWindow(ConsoleWindowSystem system, ProviderResolution resolution, LogFileManager logs)
    {
        _system = system;
        _resolution = resolution;
        JobPanel = new JobPanelControl(system, logs);
    }

    public Window Build()
    {
        SubmissionEnabled = _resolution.HasProvider;

        // Role rendering: ChatRoleStyle.Markdown defaults to TRUE, which routes content through
        // MarkdownToMarkup and ESCAPES literal '[' — so cxagent's own [red]/[cyan] markup renders
        // LITERALLY (e.g. a visible "[red]"). cxagent authors System/User lines using the library's
        // Spectre markup, so those roles must render as MARKUP (Markdown = false). The Assistant role
        // KEEPS markdown ON, because the LLM's chat responses are genuine markdown (headers, bold,
        // lists) and contain no cxagent markup. (Preserve each role's seeded ColorRole/Header/Collapse.)
        Chat.SetRoleStyle(ChatRole.System, new ChatRoleStyle
        {
            Markdown = false,
            ColorRole = ColorRole.Info,
            HeaderStyle = CollapsibleHeaderStyle.Borderless,
            Collapsible = true,
            // EXPANDED by default. These were StartCollapsed, and it cost real comprehension: the
            // chat's own "Type a goal and press Enter" rendered as "▸ System / expand…", and a
            // live-drive agent read exactly that, concluded the app "does not accept typed input",
            // filed a blocking defect and marked four other scenarios NOT RUN. Typing worked fine.
            //
            // System lines are short and few — a goal starting, a warning, a permission denial — and
            // every one of them is something the user is meant to ACT on. Collapsing them hides the
            // message behind a control nobody opens. The bulky output that motivated collapsing is
            // jobs, and those are ChatRole.Tool, which keeps StartCollapsed.
            StartCollapsed = false,
            Header = static (_, author) => author ?? "System",
        });
        Chat.SetRoleStyle(ChatRole.User, new ChatRoleStyle
        {
            Markdown = false,
            ColorRole = ColorRole.Primary,
            HeaderStyle = CollapsibleHeaderStyle.Rounded,
            Header = static (_, author) => author ?? "You",
        });
        // Assistant intentionally left at the default (Markdown = true) — LLM output is markdown.

        // Tool = jobs. Their BODY is model output or command stdout — genuine markdown — so it must
        // render as markdown, exactly like Assistant. They used to post as System, which is
        // Markdown = false because cxagent authors its OWN [red]/[cyan] markup in system lines; that
        // setting is right for system lines and wrong for a worker's prose, which arrived with its
        // headings and lists shown as literal syntax.
        //
        // StartCollapsed: a five-job fan-out each returning paragraphs would push the conversation off
        // screen. The header stays readable, and the detail is one keypress away.
        Chat.SetRoleStyle(ChatRole.Tool, new ChatRoleStyle
        {
            Markdown = true,
            ColorRole = ColorRole.Info,
            HeaderStyle = CollapsibleHeaderStyle.Borderless,
            Collapsible = true,
            StartCollapsed = true,
            Header = static (_, author) => author ?? "Job",
        });

        if (_resolution.HasProvider)
        {
            Chat.AddMessage(ChatRole.System, $"Ready — provider: {_resolution.DisplayName}. Type a goal and press Enter (Shift+Enter for a new line).");
        }
        else
        {
            Chat.AddMessage(ChatRole.System,
                "[red]No LLM provider configured.[/]\n\nEdit config.json and set a provider + defaultProvider "
                + "(the setup wizard arrives in P5c), or run [cyan]cxagent --mock[/] to try the UI.");
            foreach (var err in _resolution.Errors)
                Chat.AddMessage(ChatRole.System, $"[red]• {err}[/]");
            // Input stays constructed but the submission gate ignores Enter when !SubmissionEnabled.
        }
        Input.PlaceholderText = SubmissionEnabled ? "Type a goal… (Enter to run · Shift+Enter for newline)" : "(no provider — submission disabled)";

        // Apply Fill/Stretch so the JobPanelControl fills its Star grid cell (same fix Chat needed).
        JobPanel.VerticalAlignment = VerticalAlignment.Fill;
        JobPanel.HorizontalAlignment = HorizontalAlignment.Stretch;

        // NOTE: do NOT give Input the Fill/Stretch treatment above. Chat and JobPanel both need it
        // because they sit in Star cells; Input sits in the Auto() row, and forcing
        // VerticalAlignment.Fill there makes layout HANG — MainWindowTests goes from 18/18 in 161ms
        // to never completing. Tried while chasing D10; it was not the cause and it is not safe.

        // ONE column: transcript over composer. Jobs render INSIDE the transcript (InlineJobSink),
        // interleaved with the turns that caused them — the Claude Code / opencode shape.
        //
        // This replaced a 50/50 split with a permanent job panel on the left. The panel held the full
        // width of half the screen whether or not a goal was running, and the conversation — the part
        // you actually read — was squeezed into the other half. JobPanelControl is still constructed
        // and still works; it is simply not placed. Nothing in the engine changed: GoalRunner talks to
        // IJobPanel, so swapping which implementation is wired is a UI-only decision.
        // A RULE BETWEEN THE TRANSCRIPT AND THE COMPOSER. Without it the two run together: the
        // conversation ends and the box you type into begins, with nothing saying which is which,
        // and on a full screen of tool output the composer stops being findable at all.
        //
        // Its own grid row rather than a margin on either neighbour — a rule is a control, and
        // giving it Auto height is what keeps it exactly one line regardless of what is above it.
        _composerRule = Controls.RuleBuilder()
            .WithColorRole(ColorScheme.Structure)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        _mainGrid = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Star(1), GridLength.Auto(), GridLength.Auto())
            .Place(Chat, 0, 0)
            .Place(_composerRule, 1, 0)
            .Place(Input, 2, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        // Keep in lockstep with AppBootstrap's RegisterGlobalShortcut calls — the status bar is the
        // only discovery surface for these, so an entry here that isn't bound there is a dead key.
        // F-keys (not Ctrl+letter) because several Ctrl combos are indistinguishable from Enter/
        // Backspace/Tab at the byte level; see the comment on the registrations.
        // F2 ONLY IN FAN-OUT, for the same reason as F6. It clears the composer and focuses it —
        // and in single-agent mode the composer is ALREADY cleared on submit and already holds
        // focus, so the key does nothing observable. In fan-out focus can legitimately be sitting in
        // a job block, and F2 is the way back.
        if (FanOut) StatusBar.AddLeft("F2", "New Goal");
        StatusBar.AddLeft("F4", "Chat");
        StatusBar.AddLeft("F1", "Help");
        // One key for settings, not three. F7 (Roles) and F8 (Providers) were retired when the
        // three dialogs became one: they opened the SAME dialog on a different page, so the bar
        // was advertising three keys for one surface. The pages are named in the dialog's nav.
        StatusBar.AddLeft("F5", "Settings");
        // F6 ONLY IN FAN-OUT. The status bar is the only discovery surface for these keys, so an
        // entry that does nothing is worse than a missing one: it teaches the user the app is
        // broken rather than that the feature is elsewhere.
        if (FanOut) StatusBar.AddLeft("F6", "Diagnose");
        StatusBar.AddLeft("Ctrl+Q", "Quit");

        // D10: the goal composer is INVISIBLE when empty, and nothing on screen says where to type.
        //
        // Not fixable where you would expect. MultilineEditControl.PlaceholderText is set at :110 and
        // IS implemented (MultilineEditControl.Rendering.cs:187), but its condition is `!_isEditing` —
        // and the composer must be in editing mode from startup (FocusComposer, :168) because
        // AppBootstrap's PreviewKeyPressed consumes every Enter, so the control can never flip itself
        // into editing. Editing mode suppresses the placeholder, so the placeholder is structurally
        // unreachable here. SharpConsoleUI is off-limits, so the hint goes somewhere it can be seen.
        //
        // The chat DOES say "Type a goal and press Enter" (:99) — but System messages are
        // StartCollapsed (:85), so it renders as "▸ System / expand…". A live-drive agent read exactly
        // that, concluded the app "does not accept typed input", filed a blocking defect and marked
        // four other scenarios NOT RUN. Typing works fine; nothing tells you so.
        // NOT added here — see ShowComposerHint. Adding a status-bar item during BuildWindow HANGS:
        // StatusBarControl.OnItemChanged calls Invalidate(Relayout), which is a max-join at the
        // render tick, and during construction there is no render tick to join. Measured:
        // MainWindowTests goes from 18/18 in 144ms to hanging indefinitely.

        Window = new WindowBuilder(_system)
            .WithTitle("cxagent")
            .Maximized()
            // FRAMELESS, not Borderless. Borderless is BorderStyle.None, which keeps the one-cell
            // frame RESERVED BUT INVISIBLE — so the app painted inside a blank margin, visible on
            // screen as a gap between the terminal edge and the content on all four sides. Frameless
            // reclaims that space and lets the content fill the window rect, which is what a
            // full-screen TUI wants.
            .Frameless()
            .HideTitle()
            .AddControls(_mainGrid, StatusBar)
            .BuildAndShow();

        // Set initial focus to the goal input so the user can type immediately (the job panel is
        // focusable as a ScrollablePanelControl and would otherwise claim focus first).
        Window.FocusManager.SetFocus(Input, SharpConsoleUI.Controls.FocusReason.Programmatic);

        // ...and put it in EDITING mode (see FocusComposer).
        FocusComposer();

        return Window;
    }

    /// <summary>
    /// Focuses the goal composer AND puts it in editing mode.
    ///
    /// Both halves are load-bearing. MultilineEditControl is modal: while focused but NOT editing it
    /// handles only navigation keys and BUBBLES everything else, so typed characters are silently
    /// discarded. It normally flips to editing on Enter — but AppBootstrap's PreviewKeyPressed
    /// consumes every Enter (Enter = submit) before the control sees it, so that transition can never
    /// happen here. Any path that returns focus to the composer must therefore restore IsEditing too,
    /// or typing dies again. Centralised here so that can't be forgotten at a call site.
    /// </summary>
    public void FocusComposer()
    {
        Window?.FocusManager.SetFocus(Input, SharpConsoleUI.Controls.FocusReason.Programmatic);
        Input.IsEditing = true;
    }

    /// <summary>Ctrl+H — return focus to the chat composer.</summary>
    public void FocusChat() => FocusComposer();

    /// <summary>
    /// Swaps the prompt INTO the composer's grid cell (GridControl.ReplaceControl keeps the
    /// placement — verified, GridControl.cs:381), taking the composer out of the tree entirely so
    /// it cannot hold focus: "you cannot submit until you answer" becomes structural rather than
    /// dependent on focus-traversal checks. UI thread only.
    ///
    /// Takes <see cref="IWindowControl"/>, not <see cref="PermissionPromptControl"/> (Task 2.5):
    /// the trust-on-first-use question rides this same seam, and ReplaceControl's own parameter
    /// type is IWindowControl.
    ///
    /// Idempotence-guarded: a second Show while one is already up is a caller bug in Task 4's
    /// serialisation, but must no-op rather than let ReplaceControl throw ArgumentException (it
    /// throws when the "old" control — Input, already swapped out — is not currently placed).
    ///
    /// CONTRACT: the exact same <paramref name="prompt"/> instance passed here must be the one
    /// passed to <see cref="RestoreComposer"/>. ReplaceControl matches the "old" control by
    /// ReferenceEquals (GridControl.cs:389) — e.g. <c>PermissionPromptControl.BuildContent()</c>
    /// returns a NEW control on every call, so a caller must build once, hold the reference, and
    /// reuse it for the matching Restore. <see cref="RestoreComposer"/> now enforces this itself
    /// (a mismatched instance is a safe no-op rather than a thrown ArgumentException), but callers
    /// should still follow the contract — a mismatch means a caller's OWN prompt was never shown.
    /// </summary>
    public void ShowPermissionPrompt(IWindowControl prompt)
    {
        if (_activePrompt is not null) return;   // already showing one — no-op, not a crash

        _mainGrid.ReplaceControl(Input, prompt);
        _activePrompt = prompt;
        ApplyPromptDim();

        // HIDE THE STATUS BAR OUTRIGHT while a prompt is up. Dimming it was the first attempt and
        // it is the weaker answer: every key it advertises is inert until the prompt is answered,
        // so a dimmed row still shows the user four shortcuts that will not respond. Removing it
        // also gives the question the bottom of the screen to itself.
        StatusBar.Visible = false;

        // MOVE FOCUS INTO THE PROMPT. Without this the buttons were mouse-only: ReplaceControl swaps
        // the composer OUT of the grid but focus stays on that removed control, and ButtonControl's
        // ProcessKey returns false unless it has focus (ButtonControl.cs:225) — so Tab/Enter/Space
        // reached nothing and the only way to answer was to click.
        //
        // That is a bad failure for a SECURITY prompt specifically: a keyboard-driven user faced a
        // question they could not answer, on a modal that blocks goal submission until they do.
        // RestoreComposer already called FocusComposer() on the way OUT; nothing did the equivalent
        // on the way IN.
        //
        // Focus the FIRST focusable descendant rather than the panel: the panel is a container, and
        // focusing it would leave the same dead keyboard. FocusManager.SetFocus with Programmatic is
        // the same call FocusComposer uses.
        if (Window?.FocusManager is { } fm && FirstFocusable(prompt) is { } target)
            fm.SetFocus(target, SharpConsoleUI.Controls.FocusReason.Programmatic);
    }

    /// <summary>
    /// Dims the TRANSCRIPT while a permission prompt is showing, so the question owns the screen.
    ///
    /// <para>A permission prompt is the one moment the app stops and asks, and it was rendering as
    /// just another thing in the column — the same weight as the scrollback above it. Dimming what
    /// the user is not being asked about is how the cx family marks a modal (cxpost dims for every
    /// dialog it opens; cxgpu uses the same 0.45 for its busy overlay), and it is the difference
    /// between a prompt you notice and one you scroll past.</para>
    ///
    /// <para>A REGION, not the window. cxpost dims OTHER WINDOWS because its dialogs are separate
    /// windows; this prompt is an inline swap into the composer's own grid cell, so dimming the
    /// window would dim the prompt too. The region is everything above the prompt — the transcript —
    /// leaving only the prompt at full strength. The status bar needs no dim region of its own —
    /// <see cref="ShowPermissionPrompt"/> hides it entirely for the duration.</para>
    /// </summary>
    private void ApplyPromptDim()
    {
        if (Window is null || _dimHandler is not null) return;

        _dimHandler = (buffer, _, _) =>
        {
            // The prompt's REAL top, from its laid-out bounds. A fixed row reserve was tried first
            // and is visibly wrong: the prompt's height depends on the command it is asking about
            // (one line for `ls ~/source`, many for a long pipeline), so any constant is too big or
            // too small. Measured live at reserve 12 against a ~6-row prompt, it left a bright band
            // of empty space between the dimmed transcript and the question — the one region on
            // screen carrying no information was the brightest thing on it.
            //
            // BaseControl publishes ActualY/ActualHeight, written on the UI thread each layout and
            // read here during paint, so this needs no renderer internals.
            var height = _activePrompt is BaseControl { ActualY: > 0 } bc
                ? bc.ActualY
                : buffer.Height - FallbackReserve;
            if (height <= 0) return;

            SharpConsoleUI.Helpers.ColorBlendHelper.ApplyColorOverlay(
                buffer, Color.Black, DimIntensity, DimForegroundRatio,
                new LayoutRect(0, 0, buffer.Width, height));

        };

        // NO explicit Invalidate. ReplaceControl (the swap that brought us here) already
        // invalidated, and calling it again outside a render tick is a documented HANG in this
        // codebase — Invalidate is a max-join at the tick, and during construction or a test there
        // is no tick to join. Measured elsewhere in this file: 18/18 tests in 144ms became an
        // indefinite hang.
        Window.PostBufferPaint += _dimHandler;
    }

    /// <summary>Removes the dim. Must run on every path out of the prompt, or the transcript stays
    /// dimmed over a session that is no longer asking anything.</summary>
    private void ClearPromptDim()
    {
        if (Window is null || _dimHandler is null) return;

        Window.PostBufferPaint -= _dimHandler;
        _dimHandler = null;
        // Same reasoning as ApplyPromptDim: the caller's ReplaceControl invalidates for us.
    }

    /// <summary>Used only before the prompt has been laid out once (ActualY is 0 until then), so
    /// the first paint dims something rather than nothing.</summary>
    private const int FallbackReserve = 12;

    /// <summary>
    /// Black at 0.65. cxpost's DialogBase and cxgpu's BusyIndicator both use 0.45, and that is the
    /// right weight for a SEPARATE WINDOW floating above the content — the window's own border and
    /// fill already separate it. This prompt is inline, sharing the column with the transcript, so
    /// it has no chrome of its own doing that work and needs the contrast from the dim instead.
    /// </summary>
    private const float DimIntensity = 0.65f;

    /// <summary>Foreground blends less than background, so dimmed text stays readable rather than
    /// dissolving into the fill. cxpost's ratio.</summary>
    private const float DimForegroundRatio = 0.6f;

    /// <summary>
    /// Depth-first search for the first control that can actually take focus. Mirrors the traversal
    /// the prompt's own tests use (a panel of buttons), and returns null rather than throwing for a
    /// prompt with no focusable content — a prompt nobody can answer is a bug, but it must not take
    /// the render loop down with it.
    /// </summary>
    private static IFocusableControl? FirstFocusable(IWindowControl control)
    {
        // CHILDREN FIRST. A container is checked only AFTER its descendants, because
        // ScrollablePanelControl.CanReceiveFocus returns true whenever it merely HAS focusable
        // children (ScrollablePanelControl.Input.cs:348-360) — so a top-down "first focusable wins"
        // search matched the PANEL and stopped, leaving the buttons unfocused. That is the user's
        // report ("when the allow buttons are visible, focus the first one"): the earlier fix moved
        // focus into the prompt, but onto the container rather than onto a button.
        if (control is ScrollablePanelControl panel)
            foreach (var child in panel.Children)
                if (FirstFocusable(child) is { } found)
                    return found;

        if (control is IFocusableControl { CanReceiveFocus: true } focusable) return focusable;

        return null;
    }

    /// <summary>
    /// Puts the composer back and re-enters editing mode via <see cref="FocusComposer"/> —
    /// skipping that recreates D10: focused-but-not-editing silently discards every keystroke.
    /// UI thread only.
    ///
    /// Idempotence-guarded to match <see cref="ShowPermissionPrompt"/>: once the composer is back,
    /// a second Restore just re-asserts focus/editing rather than calling ReplaceControl again
    /// with a prompt control that's no longer placed.
    ///
    /// CONTRACT: <paramref name="prompt"/> must be the SAME instance that was passed to the
    /// matching <see cref="ShowPermissionPrompt"/> call — see that method's doc. A mismatched
    /// instance (e.g. a stale caller whose own Show was a no-op because another prompt was
    /// already up — see <see cref="ShowPermissionPrompt"/>'s idempotence guard) is NOT this
    /// caller's prompt to restore: rather than let ReplaceControl throw ArgumentException on a
    /// control that was never placed, this is a safe no-op for the swap itself — the real
    /// <see cref="_activePrompt"/> is left in place untouched. The composer is still refocused
    /// unconditionally, so a stray restore never leaves the app untypable.
    /// </summary>
    public void RestoreComposer(IWindowControl prompt)
    {
        if (ReferenceEquals(_activePrompt, prompt))
        {
            _mainGrid.ReplaceControl(prompt, Input);
            _activePrompt = null;
            ClearPromptDim();
            StatusBar.Visible = true;
        }

        FocusComposer();
    }

    /// <summary>
    /// Ctrl+J — move focus into the job panel so blocks can be navigated (↑/↓, handled by
    /// ScrollablePanelControl) and collapsed/expanded (Enter, handled by CollapsiblePanel).
    /// Expanding is what starts the live log tail, so without this P5b's headline feature is
    /// unreachable from the keyboard. No-op while the panel is empty (an empty ScrollablePanelControl
    /// reports CanReceiveFocus=false, and SetFocus silently drops non-focusable targets).
    /// </summary>
    public void FocusJobs()
    {
        // No-op since jobs moved INLINE into the transcript: JobPanel is still constructed but is no
        // longer placed in the grid, so focusing it would leave the composer in a non-editing state
        // (see the Input.IsEditing note on FocusComposer) with nothing visible to show for it — the
        // app would silently stop accepting typed input. That is precisely D10's failure mode, and it
        // is not worth reintroducing for a key that now has nothing to focus.
        //
        // The F3 binding and its status-bar entry are removed with it: an advertised key that does
        // nothing is worse than no key, because the status bar is the only discovery surface there is.
    }

    /// <summary>Ctrl+N — clear the composer for a fresh goal and make it typable again.</summary>
    public void NewGoal()
    {
        Input.Content = "";
        FocusComposer();
    }

    /// <summary>
    /// F6's target lookup: the Job.Id of whichever job block currently holds — or contains, via one
    /// of its buttons — keyboard focus. Walks FocusPath (the ancestor chain from the window root to
    /// the focused control) rather than checking FocusedControl alone, so focus landing on the
    /// block's Diagnose/Retry/Skip button (a descendant, not the block itself) still resolves.
    /// Null when nothing job-related has focus (e.g. the composer).
    /// </summary>
    public string? FocusedJobId()
    {
        if (Window is null) return null;
        foreach (var control in Window.FocusManager.FocusPath)
            if (control is JobBlockControl block)
                return block.JobId;
        return null;
    }

    /// <summary>
    /// Copilot mode (P9 Task 2): shows/hides the F9 Approve · Esc Discard footer hint. Wired by
    /// AppBootstrap off GoalRunner.DraftPending, same pattern as SetTokenTotal off TokensUpdated.
    /// Offering the hint only while a draft is actually pending matters as much as offering it at
    /// all — F9/Esc are no-ops (ApproveDraft/DiscardDraft self-guard) when nothing is drafting, and a
    /// hint that's visible but inert would be worse than no hint.
    /// </summary>
    public void SetDraftPending(bool pending)
    {
        if (_approveItem is null)
        {
            _approveItem = StatusBar.AddLeft("F9", "Approve");
            _discardItem = StatusBar.AddLeft("Esc", "Discard");
        }
        _approveItem.IsVisible = pending;
        _discardItem!.IsVisible = pending;
    }

    /// <summary>
    /// Updates the status-bar cost readout with the running orchestrator token total. Renders
    /// nothing for 0 — before any LLM call there is nothing to report, and an unconditional
    /// "0 tokens" is noise on every screen whether or not a goal is running.
    /// </summary>
    /// <summary>
    /// Adds the "type your goal here" hint to the status bar (D10). Call ONCE, from the UI thread,
    /// AFTER the app is running — <c>AppBootstrap</c> does this via <c>EnqueueOnUIThread</c>, the
    /// same way <see cref="SetTokenTotal"/> is driven.
    ///
    /// <para>It cannot go in <c>BuildWindow</c>: <c>StatusBarControl.OnItemChanged</c> calls
    /// <c>Invalidate(Relayout)</c>, which is a max-join at the render tick, and during construction
    /// there is no render tick to join — the call blocks forever. Measured: MainWindowTests went
    /// from 18/18 in 144ms to hanging indefinitely.</para>
    ///
    /// <para>Why the hint exists at all: the composer is INVISIBLE when empty.
    /// <c>MultilineEditControl</c> renders <c>PlaceholderText</c> only when <c>!_isEditing</c>
    /// (MultilineEditControl.Rendering.cs:187), but the composer must be in editing mode from
    /// startup (FocusComposer) because AppBootstrap consumes every Enter — so the placeholder is
    /// structurally unreachable. The chat's "Type a goal and press Enter" is StartCollapsed, showing
    /// only as "▸ System / expand…". A live-drive agent read exactly that, concluded the app "does
    /// not accept typed input", and filed a blocking defect; typing works fine.</para>
    /// </summary>
    public void ShowComposerHint()
    {
        if (_composerHint is not null) return;   // idempotent — a second call must not stack items
        _composerHint = StatusBar.AddRight(string.Empty,
            $"[{ColorScheme.MutedMarkup}]Type your goal below → Enter to run[/]");
    }

    /// <summary>
    /// Retires the composer hint. Called when a goal STARTS, not when tokens first arrive.
    ///
    /// <para>Tying it to the token readout left the hint on screen for the whole of a running goal —
    /// the corner said "Type your goal below" while the agent was several tool calls into one, which
    /// is not merely stale but contradicts what the transcript shows. Usage also lands late (and,
    /// with a provider that reports none, never), so a goal could run to completion under a hint
    /// telling the user to start it.</para>
    /// </summary>
    public void RetireComposerHint()
    {
        if (_composerHint is not null) _composerHint.IsVisible = false;
    }

    public void SetTokenTotal(int total)
    {
        if (total == 0)
        {
            if (_tokenItem is not null) _tokenItem.IsVisible = false;
            return;
        }

        // CONTEXT USED, not a bare token count. "94,102 tokens" is a number without a scale — it
        // says nothing about whether the next turn fits, which is the only question a user actually
        // has. With the window known it reads "ctx 46% · 94,102", and the percentage is coloured by
        // how alarming it is (cxtop's thresholds), so it can be read without being read.
        //
        // Falls back to the plain count when the provider's context window is unknown: inventing a
        // denominator would put a confident percentage on a guess.
        // Read from the resolution rather than a copied field: it is already here, and one source
        // cannot drift from another. Null (window unconfigured) falls back to the plain count — a
        // percentage needs a denominator, and a guessed one is worse than none.
        var label = ContextLabel(total, _resolution.ContextWindow);

        if (_tokenItem is null)
            _tokenItem = StatusBar.AddRight(string.Empty, label);
        else
        {
            _tokenItem.Label = label;
            _tokenItem.IsVisible = true;
        }
    }

    /// <summary>The bottom-right readout: context used, as a percentage when the window is known.</summary>
    private static string ContextLabel(int total, int? window)
    {
        if (window is not > 0) return $"{total:N0} tokens";

        var percent = 100.0 * total / window.Value;
        var colour = ColorScheme.ThresholdMarkup(percent);
        return $"[{ColorScheme.MutedMarkup}]ctx[/] [{colour}]{percent:N0}%[/] "
             + $"[{ColorScheme.MutedMarkup}]· {total:N0}[/]";
    }

    /// <summary>
    /// Flips submission on/off. Not a plain setter: <see cref="Build"/> derives <see cref="Input"/>'s
    /// placeholder text from the same flag at construction time, so anything that changes the flag
    /// later (first-run setup writing a config mid-session) must refresh the placeholder here too, or
    /// the UI keeps reading "(no provider — submission disabled)" while Enter silently works.
    /// </summary>
    public void SetSubmissionEnabled(bool value)
    {
        SubmissionEnabled = value;
        Input.PlaceholderText = SubmissionEnabled
            ? "Type a goal… (Enter to run · Shift+Enter for newline)"
            : "(no provider — submission disabled)";
    }

    /// <summary>
    /// F1 — post the key map into the chat transcript. Deliberately a chat message rather than a
    /// modal: it needs no new window plumbing, is scrollable/re-readable, and can't trap focus.
    /// (A dedicated help window is a P5c concern, alongside the wizard and settings surfaces.)
    /// </summary>
    public void ShowHelp()
    {
        Chat.AddMessage(ChatRole.System,
            "[cyan]Keys[/]\n"
            + "  [cyan]Enter[/]        run the goal in the composer\n"
            + "  [cyan]Shift+Enter[/]  newline in the composer\n"
            + "  [cyan]F2[/]           clear the composer for a new goal\n"
            + "  [cyan]F4[/]           return focus to the composer\n"
            + "  [cyan]F1[/]           this help\n"
            + "  [cyan]F5[/]           settings — providers, roles, orchestrator, permissions\n"
            + "  [cyan]F6[/]           diagnose the focused failed job\n"
            + "  [cyan]F7[/]           settings, roles page\n"
            + "  [cyan]F8[/]           settings, providers page\n"
            + "  [cyan]F9[/]           approve a drafted plan (copilot mode)\n"
            + "  [cyan]Esc[/]          discard a drafted plan (copilot mode)\n"
            + "  [cyan]Ctrl+Q[/]       quit\n"
            + "  [cyan]/clear[/]       wipe the conversation\n"
            + "  [cyan]/compress[/]    drop the oldest turns to free up room");
        FocusComposer();
    }
}
