using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Themes;
using CxAgent.Core.Agents;
using CxAgent.Core.Helpers;

namespace CxAgent.UI;

/// <summary>
/// The app shell: ONE column — ChatTranscript over the multi-line goal input (MultilineEditControl;
/// Enter submits, a trailing backslash continues onto the next line), with a clickable StatusBar. Jobs render INLINE in the
/// transcript via InlineJobSink rather than in a side panel. When no provider resolved, the chat shows an actionable message and
/// submission is disabled (the seam the P5c wizard fills).
/// </summary>
public sealed class MainWindow : IDisposable
{
    private readonly ConsoleWindowSystem _system;
    /// <summary>
    /// The provider this window reports on.
    ///
    /// <para>NOT READONLY, deliberately. A re-wire replaces the session's provider — F5 does it, and
    /// so does <c>/model</c> — and this field has to move with it. Pinned to whatever startup
    /// resolved, the status bar goes on quoting the previous model's context window, so occupancy is
    /// measured against a denominator the agent is not using.</para>
    /// </summary>
    private ResolvedConfig _resolution;

    public ChatTranscriptControl Chat { get; } = new()
    {
        VerticalAlignment = VerticalAlignment.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,

        // A column each side, matching the composer card below it. The message surfaces now run edge
        // to edge, so without this they butt against the pane's borders while the composer sits
        // inset — two different left edges in one column.
        Margin = new Margin(1, 0, 1, 0),
    };
    /// <summary>
    /// The goal composer.
    ///
    /// <para>A PromptControl rather than a MultilineEditControl. The editor is a general text editor
    /// bent into the shape of a prompt; this one IS a prompt, and it arrives with the things a
    /// composer needs — history on ↑/↓, a TabCompleter seam for slash commands, placeholder, max
    /// length — that were all absent before.</para>
    ///
    /// <para>It also avoids a whole class of workaround. MultilineEditControl is MODAL: focused but
    /// not editing it bubbles printable keys instead of inserting them, and it normally leaves that
    /// mode on Enter — which AppBootstrap consumes before the control ever sees it. Every path back
    /// to the composer would therefore have to re-assert IsEditing or typing silently dies.
    /// PromptControl has no such mode; its ProcessKey gates on IsEnabled alone.</para>
    ///
    /// <para>FIXED HEIGHT, deliberately: MinRows == MaxRows. A growing composer would make the
    /// grid's composer row and the grip's height dynamic, and every agent CLI in this class keeps a
    /// fixed prompt. MeasureDOM clamps between the two, so pinning them pins the height.</para>
    /// </summary>
    public PromptControl Input { get; } = new()
    {
        // NO MARGIN. The cell already carries the composer's inset (_composer.Cell(0,0).Padding), and
        // the mode line beside it has none — so a margin here pushed the prompt one column right of
        // the caption under it. Two left edges in a control that reads as one object.
        // STRETCH, so the field claims its whole cell. Left — the default — measures the field from
        // its CONTENT, and a prompt's content is usually short or absent: the composer was allotted
        // ten columns, which showed up as a truncated placeholder, a caret wrapping after ten
        // characters, and clicks past column ten not focusing it at all.
        HorizontalAlignment = HorizontalAlignment.Stretch,

        Multiline = true,
        MinRows = PromptRows,
        MaxRows = PromptRows,

        // Enter SUBMITS. AppBootstrap intercepts it in PreviewKeyPressed before the control sees it,
        // and a trailing backslash is what continues a line — see ComposerContinuation there for why
        // no modifier-based alternative is deliverable on a Unix terminal.
        EnterBehavior = EnterBehavior.Submit,

        // The composer is where the user stays; unfocusing on submit would cost a keystroke to get
        // back for every goal.
        UnfocusOnEnter = false,

        HistoryEnabled = true,

        // BOTH states, so the composer does not change colour when focus enters it. Set only one and
        // unfocused falls through to the app background while focused takes the framework's own grey
        // — two surfaces for one control, and neither matches the mode line below it.
        InputBackgroundColor = ColorScheme.ComposerSurface,
        InputFocusedBackgroundColor = ColorScheme.ComposerSurface,
    };
    public JobPanelControl JobPanel { get; }
    /// <summary>
    /// The bottom line: working directory on the left, context and the two escape keys on the right.
    ///
    /// <para>NOT STICKY. StickyPosition.Bottom pins the control to the WINDOW, so it spanned both
    /// columns and cut across the base of the session panel — the two panes met everywhere except
    /// the last row. As an ordinary control it takes a grid cell under the composer, inside the
    /// chat column, and the panel runs unbroken to the bottom edge.</para>
    /// </summary>
    public StatusBarControl StatusBar { get; } = new(stickyBottom: false)
    {
        // A STEP LIGHTER THAN THE COMPOSER, so the column ascends toward the bottom: chat is the dark
        // field you read against, the composer sits on it, and the bar sits on the composer. It used
        // to take ChatSurface, which put the darkest surface in the app UNDER the lightest
        // interactive one and made the two read as unrelated strips.
        // The chat column's field, so the bar reads as the base of that pane rather than a separate
        // strip laid across the app.
        //
        // TRIED AND REVERTED: a lighter bar (#3a3a3a, then #32) read as a bright stripe competing
        // with the composer, and a darker-than-composer one (#1c) added an edge that bought nothing.
        // Matching the chat is what makes the bottom of the pane continuous.
        BackgroundColor = ColorScheme.ChatSurface,

        // A column each side so the cwd and the shortcut keys are not flush against the pane edges.
        // StatusBarControl insets its CONTENT by Margin but fills the whole bar with its background,
        // so unlike MarkupControl this reads as padding rather than a gap.
        Margin = new Margin(1, 0, 1, 0),
    };
    public bool SubmissionEnabled { get; private set; }
    public Window? Window { get; private set; }

    /// <summary>
    /// Assigned by AppBootstrap to open the settings/first-run wizard flow. A property rather than a
    /// constructor dependency so MainWindow stays independent of SetupWizard; null until wired.
    /// </summary>
    public Func<Task>? ShowSettings { get; set; }


    /// <summary>
    /// This session's id — the directory its logs are written under.
    ///
    /// <para>Set by AppBootstrap, which mints it. Surfaced so a user can correlate what they are
    /// looking at with what is on disk: logs are written to a directory named by the GOAL's id, and
    /// without this the only way to find the right one afterwards is to guess by timestamp. Updated
    /// per goal, because that is the granularity the directories actually use.</para>
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// How the model is named everywhere the UI shows one: <c>instance:model</c>.
    ///
    /// <para>THE INSTANCE ALONE IS AMBIGUOUS and the model alone is incomplete. A `providers` entry
    /// is a name bound to one endpoint and one model, so two instances can serve the SAME model —
    /// `fast` and `careful` against one server — and one instance's model changes the moment config
    /// does. Showing only the model leaves "which of my providers is this?" unanswerable; showing
    /// only the instance leaves "what am I actually talking to?" unanswerable.</para>
    ///
    /// <para>It is also what <c>/model</c> switches BY, so the readout names the thing the user
    /// would type to change it.</para>
    /// </summary>
    private string ModelLabel
    {
        get
        {
            var model = _resolution.Provider?.ModelId;
            if (model is null) return _resolution.DisplayName ?? "no provider";

            return _resolution.InstanceName is { Length: > 0 } instance
                ? $"{instance}:{model}"
                : model;
        }
    }

    /// <summary>
    /// The folder this session works in, for the panel to show.
    ///
    /// <para>SET BY THE COMPOSITION ROOT, which computes it once, rather than read here from the
    /// process. The agent stopped making that ambient read; the panel showing a different answer
    /// than the agent is using would be the same two-sources bug in a place nobody would check.</para>
    /// </summary>
    public string? WorkingDirectory { get; set; }


    // ShowSettings is the SINGLE entry point into the settings dialog. Roles and providers are pages
    // of that one dialog, so separate hooks per page would each open the same window on a different
    // page — that is a parameter, not a seam. The page choice lives inside the dialog, where all
    // four names are visible together.

    private StatusBarItem? _tokenItem;


    /// <summary>
    /// The "type your goal here" hint (D10). Retired the moment the user submits their first goal —
    /// by then they have found the composer, and a permanent hint would just be noise competing with
    /// the token readout for the same corner.
    /// </summary>
    private StatusBarItem? _composerHint;

    /// <summary>The composer's grid, promoted from a Build()-local so Show/RestorePermissionPrompt
    /// can swap the Input cell's content via ReplaceControl (GridControl.cs:381), which preserves
    /// the cell's GridPlacement across the swap.</summary>
    private GridControl _mainGrid = null!;

    /// <summary>
    /// True when the conversation is the tab on screen.
    ///
    /// <para>THE CHAT TAB IS INDEX 0 and is never closable, so the index is a stable test rather than
    /// a lookup by title. Named because two decisions turn on it — whether the status strip hides
    /// behind a prompt, and whether Escape is the chat tab's to spend (see <c>EscapeRouting</c>) —
    /// and a bare <c>ActiveTabIndex == 0</c> in both places is two chances to write <c>!= 0</c>.</para>
    /// </summary>
    public bool ChatTabIsActive => Tabs.ActiveTabIndex == 0;

    /// <summary>Test seam: the panel column's width and the status strip's row height are layout
    /// decisions worth pinning, and both are only observable through the grid. Public rather than
    /// internal because this assembly grants no InternalsVisibleTo; the ForTest suffix follows the
    /// seam convention used elsewhere here.</summary>
    public GridControl MainGridForTest => _mainGrid;

    /// <summary>Rule, prompt and mode line as ONE grid cell — see the comment at its construction.</summary>
    private GridControl _composer = null!;

    /// <summary>The status bar and its rule, in their own main-grid row below the tabs.</summary>
    private GridControl _statusStrip = null!;

    /// <summary>The chat tab's content: the transcript, and the composer under it.</summary>
    private GridControl _chatTab = null!;

    /// <summary>
    /// The row that says an answer is waiting in the chat tab, shown on every other tab.
    ///
    /// <para>THE COMPOSER LIVES IN THE CHAT TAB, and the permission gate and the question tool both
    /// swap themselves INTO it. So a question raised while the user is in a shell is off screen —
    /// and a prompt that blocks the turn while being invisible is the worst of both. This is what
    /// tells them, without dragging them out of what they were doing.</para>
    ///
    /// <para>ONE STRIP, NOT ONE PER TAB. It lives in the main grid beneath the tabs, so a tab added
    /// later gets it for free rather than having to remember to build one.</para>
    /// </summary>
    private GridControl _waitingBar = null!;

    /// <summary>Whether something is waiting for an answer in the chat tab.</summary>
    private bool _waiting;

    /// <summary>
    /// The tabs. The transcript is tab zero and the only one until something opens another.
    ///
    /// <para>NO HEADER AT ONE TAB. <c>ShowTabHeader = false</c> makes <c>TabHeaderHeight</c> report
    /// 0 — not a blank row, no row — so a session that never opens a shell is pixel-identical to one
    /// with no tabs at all. The chrome appears when it is earned.</para>
    ///
    /// <para>TAB ZERO IS NOT CLOSABLE. The conversation is the app; a close button on it would offer
    /// to remove the thing everything else hangs off.</para>
    /// </summary>
    public TabControl Tabs { get; } = new()
    {
        ShowTabHeader = false,

        // NOT A FOCUS STOP. TabControl.CanReceiveFocus is true whenever it holds a tab, and
        // HorizontalGridControl.CollectFocusableContent puts a header container FIRST, ahead of its
        // content — so the strip took the front of the tab order even while ShowTabHeader hid it,
        // and a click that focused the composer lost focus to a header that is not on screen.
        //
        // Re-enabled with the header in AddTab: once the strip is drawn it is a real target, and
        // Left/Right on it change tabs.
        IsEnabled = false,

        HeaderStyle = TabHeaderStyle.Separator,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Fill,
    };

    /// <summary>
    /// Rows the composer occupies: the separator above the card, the prompt's viewport
    /// (<see cref="PromptRows"/>), the mode line, the separator above the status bar, and the status
    /// bar itself. Named so the grid row and the controls inside it cannot drift apart — the outer
    /// grid gives this row a FIXED cell count, so a control added inside without raising this number
    /// is simply not drawn. That is how the status bar vanished when the second separator landed:
    /// four rows of content in three rows of space, and the last one fell off the bottom.
    /// </summary>
    /// <summary>
    /// Adds a tab and reveals the header, which stays hidden while the chat tab is alone.
    ///
    /// <para>ONE PLACE THAT SETS <c>ShowTabHeader</c>, paired with <see cref="CloseTab"/>. A caller
    /// that added a tab without it would get a tab nobody can reach — the strip is the only way to
    /// see that a second one exists.</para>
    /// </summary>
    public void AddTab(string title, IWindowControl content, bool closable = true)
    {
        Tabs.AddTab(title, content, closable);

        // THE HEADER BECOMES REAL TOGETHER: drawn and focusable, or neither. An invisible strip that
        // is still a Tab stop is a place focus can land with nothing to show for it.
        Tabs.ShowTabHeader = Tabs.TabCount > 1;
        Tabs.IsEnabled = Tabs.ShowTabHeader;
        Tabs.ActiveTabIndex = Tabs.TabCount - 1;
    }

    /// <summary>Removes a tab and hides the header again when the chat tab is left alone.</summary>
    public void CloseTab(int index)
    {
        Tabs.RemoveTab(index);
        Tabs.ShowTabHeader = Tabs.TabCount > 1;
        Tabs.IsEnabled = Tabs.ShowTabHeader;
    }

    /// <summary>
    /// Shows the status strip unless a prompt is on screen and taking the bottom of the window.
    ///
    /// <para>HIDDEN ONLY WHERE THE PROMPT IS. Every key the strip advertises is inert while a
    /// question is up — which is why it is removed at all — but that is true on the CHAT tab, where
    /// the question is. A prompt raised while the user is in a shell tab is not visible there and
    /// its keys still work, so hiding the strip would remove live information to make room for
    /// something that is not being shown.</para>
    ///
    /// <para>THE ROW GOES WITH IT. A hidden control still holds a fixed row, so the question would
    /// end two rows above the bottom with a blank band under it.</para>
    /// </summary>
    private void RefreshStatusStrip()
    {
        var hide = _activePrompt is not null && ChatTabIsActive;

        StatusBar.Visible = !hide;
        _statusRule.Visible = !hide;
        _mainGrid.RowDefinitions[2] = hide ? GridLength.Cells(0) : GridLength.Cells(StatusRows);
    }

    /// <summary>
    /// Records that something is waiting for an answer, and shows or hides the bar accordingly.
    /// </summary>
    private void SetWaiting(bool waiting)
    {
        _waiting = waiting;
        RefreshWaitingBar();
    }

    /// <summary>
    /// Shows the bar only when a prompt is up AND the user is not looking at it.
    ///
    /// <para>CALLED ON BOTH AXES — when a prompt comes or goes, and when the tab changes — because
    /// either can make it wrong. Switching to Chat while a question waits must clear the bar as
    /// surely as answering it does.</para>
    ///
    /// <para>THE ROW COLLAPSES WITH IT. A hidden control still holds a fixed row, so leaving the row
    /// at its height would cost two blank lines above the status bar for the whole session to
    /// announce something that is almost never true.</para>
    ///
    /// <para>THE CHAT TAB IS MARKED TOO. The bar says an answer is wanted; the marker says WHERE,
    /// and it survives the user scrolling whatever they are reading.</para>
    /// </summary>
    private void RefreshWaitingBar()
    {
        var show = _waiting && Tabs.ActiveTabIndex != 0;

        _waitingBar.Visible = show;
        // TWO CELLS: the rule and the message. The row has to name what the bar draws, or the last
        // of it falls off — the same fixed-cell-count rule the composer and status strip follow.
        _mainGrid.RowDefinitions[1] = show ? GridLength.Cells(2) : GridLength.Cells(0);

        if (Tabs.TabCount > 0)
            Tabs.SetTabTitle(0, _waiting ? "Chat •" : "Chat");
    }

    /// <summary>
    /// Switches to the conversation and puts the caret back in the composer.
    ///
    /// <para>BOTH, NOT JUST THE TAB. The composer belongs to this tab; arriving on it with focus
    /// still in a terminal would leave the user looking at the prompt and typing into the shell.</para>
    /// </summary>
    public void ShowChatTab()
    {
        Tabs.ActiveTabIndex = 0;
        FocusComposer();
    }

    /// <summary>Internal, not private: CommandMenu anchors its portal just above the composer, and
    /// that arithmetic needs the composer's height.</summary>
    ///
    /// <para>PLUS TWO, NOT FOUR: the rule above the prompt, and the prompt itself. The status bar
    /// and its own rule are a separate strip below this one — see <see cref="StatusRows"/> — so a
    /// prompt that swaps into the composer cannot resize them out of existence.</para>
    internal const int ComposerRows = PromptRows + 2;

    /// <summary>
    /// The status strip: its rule, and the bar.
    ///
    /// <para>ITS OWN ROW IN THE MAIN GRID, because it reports on the SESSION rather than on the
    /// composer — the theme, the plugin key, the context percentage — and none of that should
    /// disappear because something happened to the prompt. The same fixed-cell-count rule applies
    /// as above: a control added to that strip without raising this number is simply not drawn.</para>
    /// </summary>
    public const int StatusRows = 2;

    /// <summary>
    /// What the empty composer says.
    ///
    /// <para>"Type a goal… (Enter to run · end a line with \ to continue)" was three instructions in
    /// a hint, and it truncated to "Type a goa" in the space it actually has — a placeholder that
    /// cannot fit is worse than none. A placeholder should name what the box is FOR; the keys are in
    /// Help, and the continuation rule is the kind of thing you learn once.</para>
    /// </summary>
    private const string ComposerPlaceholder = "What should I do?";

    /// <summary>Shown instead when no provider resolved, so the empty box explains itself.</summary>
    private const string NoProviderPlaceholder = "No provider configured — press F5 to set one up";

    /// <summary>
    /// Clears the composer's placeholder for good — called on the first submitted goal.
    ///
    /// <para>The hint answers "what is this box for", which is a question the user has only once.
    /// After a goal has been sent it is a permanent label on an empty prompt, and the composer is
    /// empty most of the time — so the thing meant to orient a newcomer becomes the most repeated
    /// text on screen.</para>
    ///
    /// <para>The placeholder is not gone for good as a mechanism, only this text: it is where a
    /// queued-message hint will go once messages can be queued while a goal runs.</para>
    /// </summary>
    public void RetireComposerPlaceholder()
    {
        if (SubmissionEnabled) Input.Placeholder = string.Empty;
    }

    /// <summary>The prompt's viewport height — must match Input's constructor argument.</summary>
    private const int PromptRows = 3;

    /// <summary>The line between the transcript and the composer — see its placement.</summary>
    private RuleControl _composerRule = null!;
    private RuleControl _statusRule = null!;

    /// <summary>Prompt + mode line, painting the composer surface — see its construction.</summary>
    private GridControl _promptBox = null!;

    /// <summary>The vertical rule marking the prompt as the user's — see its construction.</summary>
    private MarkupControl _promptGrip = null!;

    /// <summary>
    /// The line under the composer: which MODE is running, then the model it runs on.
    ///
    /// <para>THE SLOT EARNS ITS PLACE because the answer changes what the app does. Ours is
    /// single-agent or fan-out — the one piece of state that decides whether a goal becomes a plan
    /// of jobs or one agent with tools, and it has no
    /// other home in the UI. The model beside it needs one too: a startup line scrolls away.</para>
    /// </summary>
    private MarkupControl _modeLine = null!;

    /// <summary>
    /// The mode shown on the row under the composer.
    ///
    /// <para>THE REAL MODE, never a literal. A hardcoded "Single agent" here would claim single mode
    /// while the agent holds a spawn tool. A status line that lies is worse than no status line,
    /// because it is the one thing a user checks INSTEAD of asking.</para>
    /// </summary>
    private WorkingMode _mode = WorkingMode.Default;

    /// <summary>
    /// The mode this session starts in, set BEFORE <see cref="Build"/>.
    ///
    /// <para>An init-only property rather than a call after construction, because the banner is a
    /// CHAT MESSAGE: once <c>Build</c> has written it into the transcript it cannot be revised, and a
    /// later <see cref="SetMode"/> corrects the composer line while leaving the banner claiming
    /// something else. That is precisely what happened — the banner said "single agent" for the life
    /// of a fan-out session, because the word was hardcoded and the correction came too late to
    /// matter anyway. A SECOND AXIS MAKES THAT EASIER TO REINTRODUCE, not harder: there are now two
    /// words that can go stale, so both come from one value set once.</para>
    /// </summary>
    public WorkingMode StartupMode
    {
        init => _mode = value;
    }

    /// <summary>The mode the session is in — both axes.</summary>
    public WorkingMode CurrentMode => _mode;

    /// <summary>What the banner and the composer line are SHOWING. Readable so a test can assert what
    /// was rendered without reaching into the transcript, which exposes ids and not text.</summary>
    public string CurrentModeText => _mode.ToString();

    /// <summary>The right-hand session panel — context, model, session, location, permissions.</summary>
    public SessionPanel SessionPanel { get; } = new();

    /// <summary>
    /// Drives the panel's ELAPSED CLOCK. Everything else in the panel changes on an event — tokens
    /// when a turn ends, rules when one is granted — but elapsed time changes because time passed,
    /// and nothing else was going to say so. Without this the clock froze at whatever the last turn
    /// left it, which is worse than no clock: a stopped one still looks like it is running.
    ///
    /// <para>One second: fast enough that the seconds field is never visibly wrong, slow enough to
    /// be free. Marshalled onto the UI thread like every other mutation (framework Rule 13).</para>
    /// </summary>
    private System.Threading.Timer? _panelClock;

    /// <summary>
    /// User's explicit F3 choice, or null while the panel follows the terminal width.
    ///
    /// <para>Three states, not two: "shown", "hidden", and "decide for me". Without the third, the
    /// first resize after startup would silently override a choice the user had just made.</para>
    /// </summary>
    private bool? _panelOverride;

    /// <summary>The panel column's current width, so a resize only rewrites the definition when the
    /// answer actually changed.</summary>
    private int _panelWidth = UI.SessionPanel.MinWidth;

    /// <summary>Last token total seen, so a panel refresh triggered by a RESIZE still shows the
    /// current number rather than zero.</summary>
    private int _lastTokens;

    /// <summary>The in/out split behind <see cref="_lastTokens"/>, so a refresh driven by the clock
    /// or a resize shows the same numbers as the one driven by a turn.</summary>
    private int _lastInput;
    private int _lastOutput;

    /// <summary>
    /// Spend per model id, for the panel's breakdown. Empty until something is recorded, and it stays
    /// empty for a session that never touches a second model — the panel hides the section then,
    /// because the session total already says everything there is to say.
    /// </summary>
    private IReadOnlyDictionary<string, int> _spendByModel = new Dictionary<string, int>();

    private int _subAgentTokens;
    private IReadOnlyDictionary<string, (int Input, int Output)> _splitByModel
        = new Dictionary<string, (int, int)>();

    /// <summary>
    /// One reading of the ledger: what each instance spent, the ↑/↓ split, the worker share, and how
    /// much of the input the provider served from — or wrote into — its cache.
    ///
    /// <para>ONE VALUE BECAUSE THEY ARE ONE MOMENT. The setter below takes this whole record rather
    /// than a growing list of arguments, and the reason is the same one that put them on a single
    /// setter to begin with: they are views of the same fact, and a panel painting a breakdown from
    /// one moment beside a total from another is wrong in a way nobody would notice.</para>
    ///
    /// <para>It reached five parameters by accretion — the hit rate, then the per-agent split, then
    /// the write count — which is how every long-parameter method in this codebase got long.</para>
    /// </summary>
    public sealed record SpendReading
    {
        public required IReadOnlyDictionary<string, int> ByInstance { get; init; }
        public int SubAgentTokens { get; init; }

        /// <summary>Null LEAVES THE PREVIOUS SPLIT STANDING, unlike the cache fields below: a
        /// provider that reports no breakdown has not contradicted the last one it gave.</summary>
        public IReadOnlyDictionary<string, (int Input, int Output)>? SplitByInstance { get; init; }

        /// <summary>Null MEANS "NOT REPORTED" AND IS KEPT — a provider that stops reporting
        /// mid-session should stop showing a rate, not freeze the last one it happened to send.</summary>
        public double? CacheHitRate { get; init; }
        public (double? Own, double? Workers) CacheByAgent { get; init; }

        /// <summary>Zero where warming is free, which is every local endpoint.</summary>
        public int CacheWrittenTokens { get; init; }

        /// <summary>What each instance has cost; absent for instances that reported nothing.</summary>
        public IReadOnlyDictionary<string, decimal>? CostByInstance { get; init; }

        /// <summary>The session total, or null when nothing reported.</summary>
        public decimal? TotalCost { get; init; }
    }

    /// <summary>Takes one reading of the ledger and repaints. See <see cref="SpendReading"/>.</summary>
    public void SetSpend(SpendReading reading)
    {
        _spendByModel = reading.ByInstance;
        _subAgentTokens = reading.SubAgentTokens;
        if (reading.SplitByInstance is not null) _splitByModel = reading.SplitByInstance;

        _cacheHitRate = reading.CacheHitRate;
        _ownCacheHitRate = reading.CacheByAgent.Own;
        _workerCacheHitRate = reading.CacheByAgent.Workers;
        _cacheWritten = reading.CacheWrittenTokens;
        _costByInstance = reading.CostByInstance;
        _totalCost = reading.TotalCost;
        RefreshSessionPanel();
    }

    /// <summary>Share of input served from the provider's prefix cache; null when unreported.</summary>
    private double? _cacheHitRate;

    /// <summary>Input tokens written into the provider's cache; zero where warming is free.</summary>
    private int _cacheWritten;

    /// <summary>What each instance has cost; absent for instances that reported nothing.</summary>
    private IReadOnlyDictionary<string, decimal>? _costByInstance;

    /// <summary>The session total, or null when nothing reported.</summary>
    private decimal? _totalCost;

    /// <summary>The same, split by who spent it — see TokenLedger.CacheHitRateByAgent.</summary>
    private double? _ownCacheHitRate;
    private double? _workerCacheHitRate;

    /// <summary>Records the input/output split. Separate from SetTokenTotal because the total
    /// arrives through an event that predates the split and is raised from two different paths.</summary>
    public void SetTokenSplit(int input, int output)
    {
        _lastInput = input;
        _lastOutput = output;

        // AND REPAINT, rather than just storing. The status bar shows the split too, and it is the
        // readout that is ALWAYS visible. Storing only and leaving the paint to SetTokenTotal (which
        // the session panel relies on) works solely for as long as the two stay wired to one event.
        RefreshTokenItem();
    }

    /// <summary>Always-allow rules live for this folder; set by AppBootstrap, which owns the store.</summary>
    private int _permissionRuleCount;

    /// <summary>Told by AppBootstrap when a rule is granted, so the count is current without this
    /// class reaching into the permission store itself.</summary>
    public void SetPermissionRuleCount(int count)
    {
        _permissionRuleCount = count;
        RefreshSessionPanel();
    }

    /// <summary>The MCP servers this session configured, for the panel. Set by AppBootstrap, which
    /// owns them — the same pattern as the permission-rule count above.</summary>
    private IReadOnlyList<Core.Mcp.McpServerStatus> _mcpServers = [];

    public void SetMcpServers(IReadOnlyList<Core.Mcp.McpServerStatus> servers)
    {
        _mcpServers = servers;
        RefreshSessionPanel();
    }

    /// <summary>Whichever control currently occupies the composer's grid cell in place of
    /// <see cref="Input"/> — null when the composer itself is there. Tracked so a second
    /// ShowPermissionPrompt (a caller bug in Task 4's serialisation) can no-op instead of crashing
    /// the render loop on GridControl.ReplaceControl's "not currently placed" ArgumentException.</summary>
    private IWindowControl? _activePrompt;

    public MainWindow(ConsoleWindowSystem system, ResolvedConfig resolution, LogFileManager logs)
    {
        _system = system;
        _resolution = resolution;
        JobPanel = new JobPanelControl(system, logs);
    }

    public Window Build()
    {
        SubmissionEnabled = _resolution.HasProvider;

        // MARKDOWN'S PALETTE IS NOT INSTALLED HERE. ColorScheme.DeriveFrom sets
        // MarkdownStyle.Default itself — the same call AppBootstrap already makes at startup and on
        // every ThemeChanged — so Markdown follows a theme switch the same way every other surface
        // in this file does, instead of being fixed once and left behind by a later switch.
        StartPanelClock();

        // EVERY ROLE RENDERS MARKDOWN. Core writes markdown — the same dialect the models write —
        // so the transcript has one dialect instead of two, and a command's `## heading` or pipe
        // table is a heading and a table rather than its own syntax on screen.
        //
        // THE EXCEPTION IS PER MESSAGE, NOT PER ROLE. A severity-coloured system row is wrapped in a
        // SharpConsoleUI colour scope, and MarkdownToMarkup escapes '[' — so that one row asks for
        // markup for itself (see ChatTranscriptSink.Row) while the role stays markdown.
        // (Preserve each role's seeded ColorRole/Header/Collapse.)

        // A RAIL DOWN THE LEFT OF YOUR OWN MESSAGES, and nothing else's.
        //
        // The transcript is mostly not you: an assistant reply, a dozen tool rows, a worker's report.
        // Finding what you actually asked means reading colour, and colour is what a surface already
        // uses. A rail is a different channel — a vertical line in the gutter — so "where did I say
        // that" is answered by scanning one column rather than re-reading the conversation.
        //
        // ON FOR EVERYTHING HERE, then turned OFF per message for every role but User (see
        // ChatTranscriptSink). The control resolves a rail's colour from its message's role, so
        // leaving it on everywhere would give tool rows and system notices their own rails too — and
        // a marker that marks everything marks nothing.
        Chat.MessageRailEnabled = true;

        // HEAVY, NOT LIGHT. The control defaults to '│' (U+2502), which at the rail's dimmed colour
        // is thin enough to read as an artefact of the border rather than a deliberate mark. '┃'
        // (U+2503) is the same shape at the box-drawing family's heavy weight, so it stays a LINE —
        // a margin marker — where a block glyph would read as a highlight over the message.
        Chat.MessageRailGlyph = '┃';

        // AND THE GRIP'S COLOUR, not a dimmed role colour. Left to itself the control blends the
        // message's role colour 50% toward the background, which is right for a rail that marks
        // every role and wrong for one that marks only the user: these two rails are one idea,
        // recorded on ColorScheme.Grip — and a mark that means "yours" should not change shade
        // between the thing you typed and the box you type into.
        Chat.MessageRailColor = ColorScheme.Grip;

        Chat.SetRoleStyle(ChatRole.System, new ChatRoleStyle
        {
            Markdown = true,
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
        // A FLAT BLOCK, NOT A BOX. A surface marks whose turn it is; ours drew a rounded border,
        // which is chrome around the text rather than the text on its own ground — and it competed
        // with the code blocks and tables inside assistant answers, so the loudest
        // frame on screen belonged to the shortest message.
        //
        // NO HEADER EITHER. Stripping the border leaves a bare "You" label captioning a block that
        // already reads as the user's by its colour. The surface says whose turn it is, which is
        // all the label ever said.
        ApplyRoleStyles();

        if (_resolution.HasProvider)
        {
            // NO KEYBINDING HINT HERE: Input.Placeholder below says the same thing, in the control
            // the user is about to type into.
            // THE MODE, NOT THE WORD "single" — a session started with `--mode fan-out` must not
            // open with a banner claiming it is single. Unlike the composer line, a banner cannot be
            // corrected afterwards: it is a chat message, and the transcript is a record rather than
            // a live readout. So the mode has to be right BEFORE Build() runs, which is why it is a
            // property set at construction.
            // MARKUP, NOT MARKDOWN, for this row alone. The wordmark is ASCII art coloured per
            // character, so a markdown renderer would both reflow the art and show its colour tags.
            ChatTranscriptSink.Post(Chat, new ChatTranscriptSink.SystemRow(
                Banner.Render(_system.DesktopDimensions.Width, $"{_mode} · {ModelLabel}"), false));
        }
        else
        {
            Chat.AddMessage(ChatRole.System,
                // POINTS AT THE WIZARD, not at hand-editing config.json: the wizard is on screen
                // offering to write that file, so any other instruction here contradicts it.
                "**No LLM provider configured.**\n\nUse the setup wizard (it opens on this "
                + "screen, or press `F5` later), or run `/model` to pick a configured "
                + "instance. `cxagent --mock` tries the UI without a provider.");
            // THROUGH THE SAME SEAM AS EVERY OTHER ERROR ROW, so a resolution failure is coloured
            // and marked exactly like one Core reports, and renders as markup for the same reason.
            foreach (var err in _resolution.Errors)
                ChatTranscriptSink.Post(Chat, ChatTranscriptSink.Row(new Message(err, Severity.Error)));
            // Input stays constructed but the submission gate ignores Enter when !SubmissionEnabled.
        }
        Input.Placeholder = SubmissionEnabled ? ComposerPlaceholder : NoProviderPlaceholder;

        // Apply Fill/Stretch so the JobPanelControl fills its Star grid cell (same fix Chat needed).
        JobPanel.VerticalAlignment = VerticalAlignment.Fill;
        JobPanel.HorizontalAlignment = HorizontalAlignment.Stretch;

        // NOTE: do NOT give Input the Fill/Stretch treatment above. Chat and JobPanel both need it
        // because they sit in Star cells; Input sits in the Auto() row, and forcing
        // VerticalAlignment.Fill there makes layout HANG — MainWindowTests goes from 18/18 in 161ms
        // to never completing. Tried while chasing D10; it was not the cause and it is not safe.

        // ONE column: transcript over composer. Jobs render INSIDE the transcript (InlineJobSink),
        // interleaved with the turns that caused them.
        //
        // This replaced a 50/50 split with a permanent job panel on the left. The panel held the full
        // width of half the screen whether or not a goal was running, and the conversation — the part
        // you actually read — was squeezed into the other half. JobPanelControl is still constructed
        // and still works; it is simply not placed. Nothing in the engine changed: AgentHost talks to
        // IToolObserver, so swapping which implementation is wired is a UI-only decision.
        // A RULE BETWEEN THE TRANSCRIPT AND THE COMPOSER. Without it the two run together: the
        // conversation ends and the box you type into begins, with nothing saying which is which,
        // and on a full screen of tool output the composer stops being findable at all.
        //
        // Its own grid row rather than a margin on either neighbour — a rule is a control, and
        // giving it Auto height is what keeps it exactly one line regardless of what is above it.
        // Mode first and accented, model after and muted: the mode is a property of the SESSION and
        // the model is a detail of it, and reading them the other way round invites a user to think
        // the model is what they are choosing.
        var model = ModelLabel;
        _modeLine = Controls.Markup()
            // THE BACKGROUND IS IN THE MARKUP, not on the control. MarkupControl.PaintDOM fills from
            // Container?.BackgroundColor and never consults its own, so the builder's
            // WithBackgroundColor set a property nothing reads. An `on <colour>` tag plus [fillwidth]
            // paints the row itself — and fillwidth is the same tag that carries a code block's
            // background to the end of a wrapped line, so it is already load-bearing here.
            // `[fg on bg]` is ONE tag in this parser — a bare `[on #…]` has no foreground and is not
            // the background form, so it painted nothing. Each run carries its own background, and
            // [fillwidth] carries the last one to the end of the row.
            .AddLine(ModeLineText(_mode, model))
            // STRETCH, so [fillwidth] has a full-width rect to fill INTO. The painter extends the
            // flagged cell's background to `bounds.Right`, and without stretch those bounds ended at
            // the last character — the fill was working, it simply had nothing to cross.
            .WithAlignment(HorizontalAlignment.Stretch)
            // THE CONTROL CARRIES THE SURFACE TOO, not only the markup runs.
            //
            // MarkupControl reads its own BackgroundColor for the right-hand fill (PaintDOM's
            // rightFillBg) but takes the main fill from Container?.BackgroundColor — so the colour
            // has to be set in BOTH places for every paint path to agree. With only the markup runs
            // carrying it, any cell the runs did not cover fell back to whatever was behind.
            .WithBackgroundColor(ColorScheme.ComposerSurface)
            // NO CONTROL MARGIN. MarkupControl paints its margins with a HARDCODED Transparent
            // (PaintDOM's marginBg), so a margin here is a hole showing whatever sits behind the
            // control. Giving it a container whose background is the composer surface makes the
            // hole land on the right colour — but only where that container's background actually
            // resolves, and it demonstrably does not everywhere: the margins rendered as dark
            // columns either side of the band while the same build measured as continuous here.
            //
            // The inset is INSIDE THE PAINTED RUN instead: a leading space in the first styled run
            // and [fillwidth] carrying the last one past the trailing edge. There is no margin to
            // show through, so the row cannot break regardless of what is behind it.
            .Build();

        // EXPERIMENT: PROMPT AND MODE LINE IN ONE GRID, and the GRID carries the surface.
        //
        // MarkupControl resolves its main fill from `Container?.BackgroundColor`, so a container
        // whose background IS the composer surface should let the mode line drop every workaround it
        // accumulated — the per-run `on <colour>` tags, the [fillwidth] marker, and the leading
        // space standing in for a margin. If the grid paints the row, the markup only has to colour
        // TEXT, which is what markup is for.
        // ONE GRID, with CELL PADDING carrying the inset.
        //
        // GridControl fills its ENTIRE rect with its background, margin included — PaintDOM's
        // per-line FillRect runs from bounds.X for the full bounds.Width with nothing subtracted —
        // so a MARGIN here would be inset for layout but painted the composer surface anyway, and
        // the gap would read as padding. Cell padding is the opposite and the one that is wanted:
        // GridLayout subtracts it when measuring the child (GridLayout.cs:427), so the content is
        // inset while the cell's own rect, and therefore the surface, is not.
        //
        // The prompt box paints the surface across its rect; the composer cell holding it is padded
        // by one column, so the chat field shows through at the edges and the composer reads as a
        // card inset from the pane.
        // THE GRIP: a one-column rule down the prompt's left edge, marking where the user types.
        //
        // Its own column rather than a markup prefix on each line, because a prefix fights everything
        // the edit control does — wrapping, scrolling, selection — and would have to be re-derived on
        // every keystroke. A column is laid out once and the control beside it is untouched.
        //
        // THE WHOLE COMPOSER, mode line included. The grip marks one object — the thing at the
        // bottom that belongs to the user — and stopping it at the prompt's last row split that
        // object in two, leaving the caption looking like something separate that had drifted
        // underneath. PromptRows + 1 is the prompt's viewport plus the mode line's single row.
        _promptGrip = Controls.Markup()
            .AddLine(string.Join('\n',
                // ▌ (U+258C), A HALF BLOCK — the heaviest of these that still reads as a rule. It
                // was ▏ (U+258F), a one-eighth sliver thin enough to disappear against the surface
                // it was marking.
                //
                // HEAVIER THAN THE MESSAGE RAIL'S ┃, AND THAT IS THE POINT rather than an
                // inconsistency. The two marks share a colour and a meaning — Grip's own comment
                // records it: the user owns two surfaces, but not a job. A message rail is
                // transient: it scrolls past, in a column shared with tool rows and
                // worker reports, so it stays a drawn line. The grip is chrome that never leaves the
                // screen and has nothing to compete with, so it can afford to be solid.
                Enumerable.Repeat($"[#{ColorScheme.Grip.R:x2}{ColorScheme.Grip.G:x2}{ColorScheme.Grip.B:x2}]▌[/]",
                    PromptRows + 1)))
            .WithAlignment(HorizontalAlignment.Left)
            .Build();

        _promptBox = Controls.Grid()
            .Columns(GridLength.Cells(1), GridLength.Star(1))
            .Rows(GridLength.Cells(PromptRows), GridLength.Auto())
            .Place(_promptGrip, 0, 0, rowSpan: 2)
            .Place(Input, 0, 1)
            .Place(_modeLine, 1, 1)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        _promptBox.BackgroundColor = ColorScheme.ComposerSurface;

        _composerRule = Controls.RuleBuilder()
            .WithColor(ColorScheme.Separator)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        // THE SAME LINE UNDER THE COMPOSER. The status bar is a third kind of surface again — not
        // transcript, not input — and it sat directly against the composer with nothing marking the
        // change. One rule above it and one above the composer make the three read as three bands
        // rather than as a prompt with a caption stuck to it.
        //
        // UNPADDED, unlike the composer's rule. That one is inset a column so it stops at the card's
        // edges; the status bar fills the pane edge to edge, so a rule that stopped short of the
        // frame would leave a notch at each end.
        _statusRule = Controls.RuleBuilder()
            .WithColor(ColorScheme.Separator)
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithMargin(1,0,1,0)
            .Build();

        _composer = Controls.Grid()
            .Columns(GridLength.Star(1))
            // CELLS, not Auto, for the prompt's row. MultilineEditControl grows to fill its bounds
            // when its VerticalAlignment is Fill (GetEffectiveViewportHeight), and Fill is what it
            // inherits here — so an Auto row handed it the composer's whole share and it reported
            // back that it wanted all of it. The result was three usable lines of prompt sitting on
            // seven rows of its own background, with the mode line pushed to the bottom of the gap.
            // Naming the row's height makes the control's own viewportHeight the thing that decides.
            // A SEPARATOR ABOVE THE CARD. The transcript and the composer are two different kinds
            // of surface, and without a line between them a long answer runs straight into the
            // prompt with nothing marking where output ends and input begins. Structure-coloured
            // and one cell tall: enough to read as a boundary, not enough to read as chrome.
            // AUTO for the prompt box's row, not a fixed height: a permission prompt takes its
            // place there and is as tall as its question. The prompt box itself pins its own two
            // rows, so Auto still resolves to exactly PromptRows + 1 in the ordinary case.
            // THE STATUS BAR IS NOT IN HERE. It reports on the SESSION — the theme, the plugin
            // key, the context percentage — none of which belong to the composer, and all of which
            // must stay on screen when the composer does not. Keeping them in one grid meant a
            // composer that moves takes the shortcut hints and the token readout with it.
            .Rows(GridLength.Cells(1), GridLength.Auto())
            .Place(_composerRule, 0, 0)
            .Place(_promptBox, 1, 0)
            .WithAlignment(HorizontalAlignment.Stretch)
            // NOT Bottom. Bottom-aligning the composer inside its row only makes sense if the row is
            // taller than the composer — and when it is, that is the defect: a row measuring six rows
            // larger than its contents sinks the composer to the bottom, and the unused top becomes a
            // band of dead space between the end of the transcript and the prompt. The chat's Star(1)
            // row ends where this row begins, so those rows are unreachable by either pane. Sizing to
            // content leaves nothing to sink through.
            .WithVerticalAlignment(VerticalAlignment.Top)
            .Build();

        // The one-column inset, on the CELL rather than the control — see the prompt box's note.
        // The separator's row is padded to match, so the rule stops where the card's edges are
        // rather than running the full width of the pane.
        _composer.Cell(0, 0).Padding = new Padding(1, 0, 1, 0);
        _composer.Cell(1, 0).Padding = new Padding(1, 0, 1, 0);


        // TWO COLUMNS: the transcript, and the session panel beside it. The panel's column is Auto,
        // so hiding the control collapses the column to nothing rather than leaving a gap — which is
        // what makes the responsive behaviour a visibility flip rather than a rebuild.
        // THE PANEL SPANS THE FULL HEIGHT, beside everything rather than above the composer. It is
        // a standing readout of where you are, not a note attached to the transcript — and running
        // it to the bottom edge makes the two columns read as two panes instead of one pane with
        // something stacked on it.
        //
        // CELLS, not Auto: Auto sizes a column to its content's INTRINSIC width, and a
        // ScrollablePanel has none — it fills whatever it is given. The measure never resolved and
        // the window painted its background with NO TEXT AT ALL. Measured on one build: 0 lines
        // with Auto, 22 with a fixed width.
        // THE WAITING BAR, between the tabs and the status strip. Hidden until a prompt is up AND
        // the user is somewhere other than the chat tab — on the chat tab the prompt is on screen and
        // a bar saying so would be noise.
        //
        // IT DOES NOT SAY WHAT THE QUESTION IS. A permission request runs several lines, and a
        // security question truncated into one row is how people approve what they did not read.
        var go = new ButtonBuilder()
            .WithText(" Go ")
            .WithColorRole(ColorScheme.Accent)
            // SWITCHES AND FOCUSES IN ONE PRESS. Landing on the tab but not the prompt would need a
            // second key to answer a question that is already blocking the turn.
            .OnClick((_, _) => ShowChatTab())
            .Build();

        // LEFT, LIKE THE STATUS BAR UNDER IT. The two are neighbouring rows of chrome at the foot
        // of the window, and a centred row above a left-aligned one reads as a different kind of
        // thing that happens to be floating there. Aligned, the bar is plainly a temporary line
        // above the permanent one.
        var waitingRow = new ToolbarControl
        {
            ItemSpacing = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        waitingRow.AddItem(new MarkupControl(
            [$"[{ColorScheme.CautionMarkup}]cxagent is waiting for an answer in Chat[/]"]));
        waitingRow.AddItem(go);

        // A RULE ABOVE IT, closing the bar off from the tab content. Without one the message runs
        // straight into whatever the tab was showing — on a shell, into the last line of output,
        // where it reads as something the command printed.
        //
        // ITS OWN RULE, EVEN THOUGH THE STATUS STRIP DRAWS ONE. The strip's rule separates the strip
        // from what is above it, which is this bar; this one separates the bar from the tab. Two
        // boundaries because there are two things to bound, and they only look adjacent because the
        // bar between them is a single row.
        _waitingBar = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Cells(1), GridLength.Cells(1))
            .Place(new RuleControl { Color = ColorScheme.MutedRgb }, 0, 0)
            .Place(waitingRow, 1, 0)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        _waitingBar.Visible = false;

        // THE STATUS BAR'S OWN STRIP, below everything. Its rule travels with it: the line marks
        // where the session's readout begins, so a rule left behind in the composer would draw a
        // boundary above the wrong thing.
        _statusStrip = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Cells(1), GridLength.Auto())
            .Place(_statusRule, 0, 0)
            .Place(StatusBar, 1, 0)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        // TAB ZERO, BEFORE THE GRID IS BUILT. The control has to have its content when it is
        // measured, or the first layout runs against an empty container and the transcript is drawn
        // into nothing.
        //
        // NOT CLOSABLE — isClosable defaults false, and it is left that way deliberately: the
        // conversation is the app, and a close button on it offers to remove what everything else
        // hangs off.
        // THE CHAT TAB IS THE TRANSCRIPT AND THE COMPOSER. They are one surface — a conversation
        // you read and the place you answer it — so a shell tab gets the full height rather than a
        // prompt hanging under it, where the next keystroke belongs to neither clearly.
        //
        // STAR THEN CELLS, the same split the main grid used while these were siblings: the
        // transcript takes what is left, the composer is exactly what it draws. An Auto row here
        // would hand the transcript's ScrollablePanel an intrinsic measure, which is the shape that
        // once painted 0 lines of text.
        _chatTab = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Star(1), GridLength.Cells(ComposerRows))
            .Place(Chat, 0, 0)
            .Place(_composer, 1, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        Tabs.AddTab("Chat", _chatTab);

        // THE BAR FOLLOWS THE TAB, not only the prompt: arriving at Chat while a question waits must
        // clear it, and leaving Chat while one waits must raise it.
        Tabs.TabChanged += (_, _) =>
        {
            RefreshWaitingBar();

            // AND THE STRIP: whether a prompt is hiding it depends on which tab is showing, so
            // switching tabs changes the answer as surely as raising or answering one does.
            RefreshStatusStrip();
        };

        _mainGrid = Controls.Grid()
            .Columns(GridLength.Star(1), GridLength.Cells(SessionPanel.WidthFor(_system.DesktopDimensions.Width)))
            // THE COMPOSER'S ROW IS NAMED, not Auto. Auto measured it at ~11 rows for 5 rows of
            // content, and the surplus showed as dead space between the transcript and the prompt —
            // the chat's Star(1) row ends where this row begins, so those rows belonged to neither
            // pane and simply went blank. Cells(ComposerRows) makes the row exactly what the
            // composer draws: the prompt's viewport, the mode line, the status bar.
            // TWO ROWS: the tabs, and the status strip under them. The composer is INSIDE the chat
            // tab now, so it is not a sibling here — only the strip stays global, because it reports
            // on the session rather than the conversation and must survive switching away from it.
            // THREE ROWS: the tabs, the waiting bar, the status strip. The bar's row is Cells(0)
            // until something waits — a hidden control still holds a fixed row, and two blank lines
            // above the status bar would be a permanent cost for a rare event.
            .Rows(GridLength.Star(1), GridLength.Cells(0), GridLength.Cells(StatusRows))
            // THE TABS TAKE THE TRANSCRIPT'S PLACE, and the transcript becomes their first tab. The
            // row is unchanged — Star(1), a definite share — because that is what the transcript
            // needed and the reason is recorded above: an Auto track measured a ScrollablePanel at
            // nothing and painted 0 lines of text. A container that sizes its content inherits that
            // hazard, so it gets the same definite row rather than being measured intrinsically.
            .Place(Tabs, 0, 0)
            .Place(_waitingBar, 1, 0)
            .Place(_statusStrip, 2, 0)
            .Place(SessionPanel.Control, 0, 1, rowSpan: 3)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        // Keep in lockstep with AppBootstrap's RegisterGlobalShortcut calls — the status bar is the
        // only discovery surface for these, so an entry here that isn't bound there is a dead key.
        // F-keys (not Ctrl+letter) because several Ctrl combos are indistinguishable from Enter/
        // Backspace/Tab at the byte level; see the comment on the registrations.
        // THE STATUS BAR IS TWO THINGS: where you are, and how to leave. Six shortcuts along here
        // make a menu rather than a status bar, and every one of them is a key the user either knows
        // or will find in Help. All the keys WORK regardless of whether they are advertised here;
        // the bar's job is to stay quiet enough that the two things it does say get read.
        //
        // The working directory takes the left, because "which checkout am I editing" is the
        // question a status bar should answer and the one whose wrong answer costs most.
        // NO WORKING DIRECTORY HERE. The session panel carries it under "Location", and two readouts
        // of one unchanging value is how they drift — the same rule that moved the model line out of
        // the panel and the context block out after it. The path is also the least volatile thing on
        // screen: it is fixed for the life of the process, so it earns a place you look up rather
        // than a permanent slot beside numbers that change every turn.

        // ONE KEY HINT. Quit went first — Ctrl+Q is the binding nobody needs told, and /exit says it
        // in the command list. F1 follows it: help is discoverable from `/help`, which is where
        // someone lost already looks, and the slot is better spent on the token split beside the
        // context readout — a number that changes, rather than a key that never does.
        //
        // F3 stays because it is the only key that changes what is ON SCREEN; everything else opens
        // something and comes back.
        StatusBar.AddRight("F3", "Panel");

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
            // THE LEFT COLUMN'S FIELD. The transcript control paints per-message panels, not a
            // surface of its own, so what shows between and around them is the WINDOW background —
            // which makes this the only place to set the chat column's colour. The session panel
            // overrides it with PanelSurface, so darkening here darkens the chat side alone.
            .WithBackgroundColor(ColorScheme.ChatSurface)
            .HideTitle()
            .AddControls(_mainGrid)
            .BuildAndShow();

        // Set initial focus to the goal input so the user can type immediately (the job panel is
        // focusable as a ScrollablePanelControl and would otherwise claim focus first).
        Window.FocusManager.SetFocus(Input, SharpConsoleUI.Controls.FocusReason.Programmatic);

        // ...and put it in EDITING mode (see FocusComposer).
        FocusComposer();

        return Window;
    }

    /// <summary>
    /// Focuses the goal composer.
    ///
    /// <para>FOCUS IS THE WHOLE JOB, because PromptControl has no editing mode — ProcessKey gates on
    /// IsEnabled alone. A MODAL composer would need more: MultilineEditControl, focused but not
    /// editing, bubbles printable keys instead of inserting them, and the Enter that would normally
    /// leave that mode is consumed by AppBootstrap before the control sees it, so every path back to
    /// the composer would have to re-assert IsEditing or typing silently dies.</para>
    ///
    /// <para>Kept as a named method because callers say what they mean.</para>
    /// </summary>
    public void FocusComposer()
        => Window?.FocusManager.SetFocus(Input, SharpConsoleUI.Controls.FocusReason.Programmatic);


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
    /// <summary>
    /// Asks the model's question in the composer and waits for the answer.
    ///
    /// <para>THE SAME SWAP THE PERMISSION GATE USES, deliberately: one place the session asks for
    /// input, so a user who has approved a shell command already knows where to look.</para>
    ///
    /// <para>Several questions are STEPPED THROUGH, one on screen at a time, with the answers
    /// returned together — the composer is a few rows tall, and stacking three described option
    /// lists into it would clip the last of them.</para>
    ///
    /// <para>A skipped question returns "" and the tool reads that as "decide it yourself".
    /// Cancellation resolves the CONTROL, not just this await: a prompt left holding a live
    /// TaskCompletionSource keeps the composer swapped out, and the user would be looking at a
    /// question nobody is waiting on.</para>
    /// </summary>
    public async Task<QuestionAnswers> AskQuestionAsync(
        IReadOnlyList<UserQuestion> questions, CancellationToken ct)
    {
        var prompt = new QuestionPromptControl(questions);
        var content = prompt.BuildContent();

        // THE CURRENT STEP, tracked so the right control is torn down at the end. Each step builds a
        // fresh panel, so restoring the one built here would leave the last step's on screen.
        var shown = content;

        prompt.StepChanged += next =>
        {
            RestoreComposer(shown);
            shown = next;
            ShowPermissionPrompt(next);
            FocusQuestion(prompt);
        };

        _activeQuestion = prompt;
        ShowPermissionPrompt(content);
        FocusQuestion(prompt);

        using var _ = ct.Register(() => prompt.Resolve(QuestionAnswers.Cancel));
        try
        {
            return await prompt.Completion;
        }
        finally
        {
            _activeQuestion = null;
            RestoreComposer(shown);
            FocusComposer();
        }
    }

    /// <summary>
    /// The question currently on screen, or null. Escape reads this — skipping a question must not
    /// have to kill the turn to get out of a dialog.
    /// </summary>
    private QuestionPromptControl? _activeQuestion;

    /// <summary>
    /// Puts focus where the answer is given — the option list when there is one, else the field.
    ///
    /// <para>THE DRIVE FOUND THIS. Focus landed on the panel, so the first Enter on a question with
    /// options did NOTHING: the user had to press Down before the list would respond. A question
    /// whose most obvious keystroke has no effect reads as a hung app.</para>
    /// </summary>
    private void FocusQuestion(QuestionPromptControl prompt)
    {
        if (prompt.FocusTarget is { } target)
            Window?.FocusManager.SetFocus(target, SharpConsoleUI.Controls.FocusReason.Programmatic);
    }

    /// <summary>Escape while a question is up: skip it, and let the run continue.</summary>
    public bool TrySkipQuestion()
    {
        if (_activeQuestion is null) return false;
        _activeQuestion.Skip();
        return true;
    }

    /// <summary>
    /// Escape while a permission prompt is up: answer "no", and let the run continue.
    ///
    /// <para>DENY ONLY — this must not also kill the turn. Without a handler here Escape reaches no
    /// handler on the prompt (it is buttons only), falls through to the global shortcut, and takes
    /// the CancelTurn branch because a prompt only appears mid-turn; cancelling then fires the gate's
    /// registration, which resolves the prompt as Deny. The conventional "get me out of this" key
    /// then destroys the whole run, shows only "Stopped.", and the model never sees the refusal it
    /// could have adapted to. Observed in a live drive: a denied test-file write ending a drive that
    /// had cost two million tokens, with the frozen token counter reading as a hang.</para>
    ///
    /// <para>Deny is a real answer, not an escape hatch — the same reasoning as
    /// <see cref="TrySkipQuestion"/>, whose comment says a user's reluctance to answer must not cost
    /// them their work. Escape means "no" wherever something is being asked, and cancels the turn
    /// only when nothing is.</para>
    /// </summary>
    public bool TryDenyPermission()
    {
        if (_denyActivePrompt is not { } deny) return false;
        deny();
        return true;
    }

    /// <summary>How to answer the prompt currently on screen with "no". See
    /// <see cref="TryDenyPermission"/>.</summary>
    private Action? _denyActivePrompt;

    /// <summary>The content <see cref="_denyActivePrompt"/> belongs to — NOT always
    /// <see cref="_activePrompt"/>, which is why it is tracked separately. See RestoreComposer.</summary>
    private IWindowControl? _denyOwner;

    /// <summary>
    /// Alt+← while a multi-question run is up: back to the previous one.
    ///
    /// <para>False when there is no question, or it is the first — so the shortcut falls through to
    /// whatever else wants it rather than silently eating the key.</para>
    /// </summary>
    public bool TryQuestionBack() => _activeQuestion?.Back() ?? false;

    public void ShowPermissionPrompt(IWindowControl prompt) => ShowPermissionPrompt(prompt, null);

    /// <summary>
    /// Shows a permission prompt, and records how to answer "no" to it from a keystroke.
    ///
    /// <para><paramref name="deny"/> is what Escape resolves the prompt with. It is a callback rather
    /// than the control itself because this window is handed the BUILT content, not the
    /// PermissionPromptControl behind it — the caller keeps that, so the caller supplies the one
    /// operation a shortcut needs. Null for a prompt with no keyboard answer, which is why the plain
    /// overload above still exists.</para>
    /// </summary>
    public void ShowPermissionPrompt(IWindowControl prompt, Action? deny)
    {
        // THE DENY ACTION IS TAKEN EVEN WHEN THE SWAP IS SKIPPED, and that ordering is the whole
        // point. Restore is ENQUEUED onto the UI thread rather than run inline, so a denied prompt
        // whose turn immediately asks again can have its replacement shown BEFORE the outgoing one
        // is restored — the guard below then fires, and assigning after it would leave a visible
        // prompt that Escape cannot answer. Observed live: deny once, and the second prompt was
        // dead to the keyboard.
        //
        // Answering the newest prompt is right in either order: the callback belongs to whichever
        // control the user is looking at, and a stale one is cleared by the restore that follows.
        if (deny is not null)
        {
            _denyActivePrompt = deny;
            _denyOwner = prompt;
        }

        if (_activePrompt is not null) return;   // already showing one — no-op, not a crash

        // SWAP THE WHOLE PROMPT BOX, not the Input inside it.
        //
        // Replacing Input put the permission prompt in a row sized Cells(PromptRows) — a fixed three
        // — so the question was clipped to nothing and only its buttons survived, with the mode line
        // still sitting underneath the thing asking to be answered. A permission prompt is however
        // tall its question needs, and it cannot be asked to fit the composer's dimensions.
        //
        // Taking the prompt box's place puts it in the composer's own row, which sizes to content
        // (see the row definitions), and removes the mode line with it — the composer is not usable
        // while a prompt is up, so showing its furniture is noise around the only live control.
        _composer.ReplaceControl(_promptBox, prompt);
        _activePrompt = prompt;

        // RAISED HERE, CLEARED IN RestoreComposer. Every prompt — a permission request, a question
        // tool call, each step of a multi-step question — passes through this pair, so a caller
        // added later gets the bar without knowing it exists. Wiring it per-caller would make that
        // future one silently invisible, which is the failure the bar is being built to prevent.
        SetWaiting(true);

        // AND LET THE ROW GROW. The composer's row in the main grid is a fixed Cells(ComposerRows) —
        // that is what closed the dead band between the transcript and the prompt — but a permission
        // prompt is taller than the composer, and a fixed row would clip the question just as the
        // fixed prompt row did. Auto for as long as the prompt is up; restored on the way out, so
        // the band cannot come back.
        // THE CHAT TAB'S OWN ROW, not the main grid's. The composer lives inside the tab now, so
        // the row that has to grow for a taller-than-usual prompt is the one holding it there —
        // resizing the main grid's row 1 would stretch the STATUS STRIP instead, and row 2 no longer
        // exists at all.
        _chatTab.RowDefinitions[1] = GridLength.Auto();

        ElevatePrompt(prompt);

        // HIDE THE STATUS BAR OUTRIGHT while a prompt is up. Dimming it is the weaker answer: every
        // key it advertises is inert until the prompt is answered, so a dimmed row still shows the
        // user four shortcuts that will not respond. Removing it also gives the question the bottom
        // of the screen to itself.
        RefreshStatusStrip();

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
    /// Starts the panel's one-second tick.
    ///
    /// <para>ELAPSED TIME IS THE ONE VALUE NOTHING ELSE ANNOUNCES. Tokens arrive with a turn, rules
    /// with a grant — but time passes on its own, and without a clock the field froze at whatever
    /// the last event left it. A stopped clock is worse than none: it still looks like it is
    /// running, so it lies rather than abstains.</para>
    ///
    /// <para>One second is fast enough that the seconds field is never visibly wrong and slow enough
    /// to cost nothing. Marshalled onto the UI thread like every other mutation, and skipped
    /// entirely while the panel is hidden — a timer repainting an invisible control is pure waste.</para>
    /// </summary>
    /// <summary>Stops the panel clock. The window outlives no goal, so this runs once at shutdown —
    /// but a Timer holds a callback rooting this instance, and leaving it running keeps the whole
    /// UI graph alive after the app has stopped drawing it.</summary>
    public void Dispose()
    {
        _panelClock?.Dispose();
        _panelClock = null;
    }

    /// <summary>
    /// The job sink whose running rows this window's clock ticks, set by AppBootstrap once it exists.
    ///
    /// <para>A PROPERTY RATHER THAN A CONSTRUCTOR ARGUMENT because of build order: the window is
    /// constructed first and the sink is handed the window's own Chat control, so there is no moment
    /// at which both could be passed to the other. Null until then, and the tick simply does nothing
    /// — the window runs for a while before any session is wired, and there are no rows to tick.</para>
    /// </summary>
    public InlineJobSink? JobSink { get; set; }

    private void StartPanelClock()
    {
        _panelClock = new System.Threading.Timer(
            _ => _system.EnqueueOnUIThread(() =>
            {
                if (SessionPanel.Control.Visible) RefreshSessionPanel();

                // OUTSIDE THE VISIBILITY GUARD ABOVE, and that is the point of putting it here rather
                // than folding it into RefreshSessionPanel. The panel is a luxury of width and hides
                // itself on a narrow terminal; the running rows are in the TRANSCRIPT and are always
                // on screen, so a clock that stopped when the side panel did would freeze for exactly
                // the users with the least room to spare.
                //
                // This is the only thing that advances a running row's elapsed time — see
                // InlineJobSink.RefreshRunningHeaders for why the header cannot tick itself.
                JobSink?.RefreshRunningHeaders();
            }),
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Applies the panel's visibility from the terminal width and any F3 override, and refreshes it.
    ///
    /// <para>RESPONSIVE BY DEFAULT: the panel is a luxury of width — taken when there is room, and
    /// yielded when the transcript needs every column. Below the threshold a 24-column panel would
    /// be a third of the screen spent on six numbers, next to code the user is trying to read.</para>
    ///
    /// <para>The override wins in BOTH directions, because "decide for me" is a state the user can
    /// leave but the app must not re-enter on their behalf — a resize silently undoing an explicit
    /// F3 is the kind of thing that reads as a bug.</para>
    /// </summary>
    public void RefreshSessionPanel()
    {
        var terminalWidth = _system.DesktopDimensions.Width;
        var wide = terminalWidth >= UI.SessionPanel.ResponsiveThreshold;
        SessionPanel.Control.Visible = _panelOverride ?? wide;

        // RE-WIDEN ON RESIZE. The column was fixed at construction, so a terminal that grew from 100
        // to 200 columns kept a 24-wide panel wrapping model ids and paths while a third of the new
        // space sat unused. Assigned only on CHANGE — ColumnDefinitions is a live list and writing
        // it invalidates layout, so an unconditional write would relayout on every refresh, which
        // now includes the one-second clock tick.
        if (_mainGrid.ColumnDefinitions.Count > 1)
        {
            // ZERO WHEN HIDDEN. Visible=false stops the panel PAINTING; it does nothing to the column
            // reserving its width, so hiding it left a 24-to-40 column strip of empty background and
            // the transcript still wrapping as though the panel were there. The width is the column's
            // to give back, not the control's.
            var want = SessionPanel.Control.Visible ? UI.SessionPanel.WidthFor(terminalWidth) : 0;
            if (_panelWidth != want)
            {
                _panelWidth = want;
                _mainGrid.ColumnDefinitions[1] = GridLength.Cells(want);
            }
        }

        if (!SessionPanel.Control.Visible) return;

        // DisplayName is the instance label ("openai-compatible qwen3.6-…"); ModelId is what the
        // provider will actually send. Both are shown because they differ often enough that seeing
        // only one leaves the question open.
        SessionPanel.Refresh(new SessionPanel.SessionPanelState
        {
            // OCCUPANCY, and _lastTokens is NOT it. _lastTokens is Ledger.TotalTokens — a cumulative
            // sum that only ever grows — and passing it as the context size is what put "9%" on a
            // context measured at 2%, and what stopped the gauge falling after a compression. It is
            // the SPEND, which is what it has always been.
            ContextUsed = _contextUsed,
            SpentTokens = _lastTokens,

            // MEASURED, NOT DERIVED — see SessionPanelState.OwnTokens. _lastTokens IS this agent's
            // own spend (SetTokenTotal is fed OwnSpend), so the panel can use it directly rather
            // than subtracting a session-wide worker total from it.
            OwnTokens = _lastTokens,
            ContextWindow = _resolution.ContextWindow,

            WorkingDirectory = WorkingDirectory,
            SessionId = SessionId,

            Rules = _permissionRuleCount,

            // THE CEILING THAT ACTUALLY BINDS, not the configured value: passing the raw setting
            // makes an unconfigured session print "no cap" while a real ceiling is in force.
            // int.MaxValue is the genuine no-cap case (an explicit 0), rendered as "no cap".
            MaxTurns = AgentHost.CeilingFor(_resolution.Orchestrator?.MaxTurns) is var ceiling
                && ceiling == int.MaxValue ? 0 : ceiling,

            InputTokens = _lastInput,
            OutputTokens = _lastOutput,
            SubAgentTokens = _subAgentTokens,
            SpendByModel = _spendByModel,
            SplitByModel = _splitByModel,
            CacheHitRate = _cacheHitRate,
            CacheWrittenTokens = _cacheWritten,
            CostByInstance = _costByInstance,
            TotalCost = _totalCost,
            OwnCacheHitRate = _ownCacheHitRate,
            WorkerCacheHitRate = _workerCacheHitRate,

            McpServers = _mcpServers,

            // THE NAMES THE CATALOG WILL RESOLVE, not the raw config keys — the shipped types live
            // in code, so `general` exists whether or not config mentions it, and so do the five
            // built-ins. A panel built from config alone reports three types on a session that has
            // six when the user's config holds only the two entries that still say anything (a
            // maxTurns each), making delegation look narrower than it is — the same failure the
            // `general` note below describes, one source further along.
            //
            // Union, deduplicated: built-ins, then whatever config adds on top.
            AgentTypes =
            [
                AgentTypeCatalog.DefaultTypeName,
                .. BuiltinAgentTypes.All.Select(t => t.Name),
                .. _resolution.AgentTypes.Keys
                    .Where(n => n != AgentTypeCatalog.DefaultTypeName && !BuiltinAgentTypes.IsBuiltin(n)),
            ],

            // WHAT EXISTS, AND WHAT IS IN FORCE. The count comes from discovery; the loaded names
            // are derived from this agent's window, so the line vanishes by itself when compaction
            // takes a body — the silent change the panel exists to make visible. THE PARENT'S, not a
            // child's: a child reports its skills on its own row and is gone by the next turn.
            SkillCount = SkillCount,
            LoadedSkills = LoadedSkills,
        });
    }

    /// <summary>How many skills discovery found. Set by the composition root, which owns the read.</summary>
    public int SkillCount { get; set; }

    /// <summary>
    /// The skills whose bodies are still in the PARENT agent's window. A function of the conversation
    /// rather than a remembered list, so it stops reporting one the moment compaction removes it.
    /// </summary>
    public IReadOnlyList<string> LoadedSkills { get; set; } = [];

    /// <summary>F3 — show the panel, hide it, or hand it back to the terminal width.</summary>
    public void ToggleSessionPanel()
    {
        // Cycles through the THREE states rather than flipping two, so a user can get back to
        // responsive without restarting: shown -> hidden -> automatic.
        _panelOverride = _panelOverride switch
        {
            null => !SessionPanel.Control.Visible,
            true => false,
            false => null,
        };
        RefreshSessionPanel();
    }

    /// <summary>
    /// Raises the permission prompt onto its own surface.
    ///
    /// <para>ELEVATION, NOT A FULL-SCREEN DIM. Overlaying everything above the prompt with black at
    /// 0.45 is cxpost's convention for modal dialogs and it does not fit an inline swap: it darkens
    /// the entire transcript to draw attention to six rows, which is a lot of screen changing state
    /// to say one thing, and the edge between dimmed and undimmed has to be computed from the
    /// prompt's laid-out bounds, so it moves with the height of whatever command is being asked
    /// about and leaves a bright band above the question whenever the estimate runs long.</para>
    ///
    /// <para>Elevation says the same thing locally: the prompt sits a step above the composer it
    /// replaced, nothing else on screen moves, and there is no boundary to compute. The colour is
    /// derived from the composer surface, so the two cannot drift apart.</para>
    /// </summary>
    private static void ElevatePrompt(IWindowControl prompt)
    {
        // IWindowControl carries no background, and enumerating concrete control types here was
        // both fragile and wrong: it silently missed ScrollablePanelControl — what the permission
        // prompt actually is — so the elevation applied to nothing. A prompt owns its own
        // appearance (PermissionPromptControl.BuildContent sets it), and this remains only for a
        // caller that passes a bare control.
        if (prompt is ScrollablePanelControl p) p.BackgroundColor = ColorScheme.PromptSurface;
    }




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
            SetWaiting(false);
            _composer.ReplaceControl(prompt, _promptBox);
            _chatTab.RowDefinitions[1] = GridLength.Cells(ComposerRows);

            // CLEARED BEFORE THE REFRESH, because RefreshStatusStrip decides from _activePrompt.
            // Refreshing first recomputes hide=true from the prompt now being torn down, collapsing
            // the strip's row to zero cells with nothing left to expand it again.
            _activePrompt = null;
            RefreshStatusStrip();

            // CLEARED WITH THE PROMPT IT BELONGS TO. A stale deny action would let a later Escape
            // resolve a TaskCompletionSource nobody is waiting on — harmless in itself, but it would
            // also swallow the keystroke and stop Escape reaching the turn it was meant for.
            //
            // ONLY WHEN THE ACTION STILL BELONGS TO THE PROMPT BEING RESTORED. Matching on
            // _activePrompt is not enough: a replacement shown before this restore ran hits the
            // idempotence guard above, so _activePrompt is STILL the outgoing content while the deny
            // action is already the new prompt's — and clearing on that match would take Escape away
            // from the prompt on screen. A test pins the _denyOwner check.
            if (ReferenceEquals(_denyOwner, prompt))
            {
                _denyActivePrompt = null;
                _denyOwner = null;
            }
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
    /// Applies every chat role's style — colours included.
    ///
    /// <para>EXTRACTED SO A THEME CHANGE CAN RE-RUN IT. Each style captures its Background by VALUE
    /// when SetRoleStyle is called, so re-deriving the palette without re-running this leaves every
    /// message in the transcript painted in the outgoing theme's surfaces while the chrome around
    /// them moves. Reported live: the user and assistant replies keeping their dark grounds under a
    /// light theme.</para>
    ///
    /// <para>Messages ALREADY on screen do re-paint from these styles, unlike their inline markup —
    /// the surface is a property of the role, not text baked into the transcript.</para>
    /// </summary>
    private void ApplyRoleStyles()
    {
        Chat.SetRoleStyle(ChatRole.User, new ChatRoleStyle
        {
            Markdown = true,
            ColorRole = ColorRole.Primary,
            HeaderStyle = CollapsibleHeaderStyle.Borderless,
            ShowHeader = false,
            Background = ColorScheme.UserSurface,
            Header = static (_, author) => author ?? "You",
        });

        // The assistant gets ground of its own too, one step quieter than the user's — see
        // ColorScheme.AssistantSurface for why the longer voice is the darker one. Markdown stays ON
        // (the default): LLM output is genuine markdown.
        //
        // The "Assistant" header STAYS. Unlike "You", it is not redundant: an answer can be many
        // screens long and its start is worth marking, and the reasoning stream that now precedes it
        // in the body would otherwise run straight into the prose with nothing dividing them.
        // BUILT FROM THE SEEDED STYLE, not from scratch. SetRoleStyle REPLACES the entry outright
        // (ChatTranscriptControl: `_roleStyles[role] = style`), so a fresh ChatRoleStyle carrying only
        // a Background would silently drop the seeded Header and ColorRole — the "Assistant" label
        // would vanish, which is the opposite of what is wanted here.
        var assistant = Chat.GetRoleStyle(ChatRole.Assistant);
        Chat.SetRoleStyle(ChatRole.Assistant, new ChatRoleStyle
        {
            Markdown = assistant.Markdown,
            ColorRole = assistant.ColorRole,
            HeaderStyle = assistant.HeaderStyle,
            ShowHeader = assistant.ShowHeader,
            Collapsible = assistant.Collapsible,
            StartCollapsed = assistant.StartCollapsed,
            Margin = assistant.Margin,
            Header = assistant.Header,
            Background = ColorScheme.AssistantSurface,
        });

        // Tool = jobs. Their BODY is model output or command stdout — genuine markdown — so it
        // renders as markdown, exactly like Assistant. A job posted as a system notice rather than a
        // role of its own would inherit that role's collapse and header, and a worker's report is
        // neither short nor a notice.
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
    }

    /// <summary>
    /// Re-applies every surface this window captured at construction, after a theme change.
    ///
    /// <para>THE FIELD INITIALISERS ARE THE PROBLEM THIS SOLVES. Controls here take their colours
    /// with <c>BackgroundColor = ColorScheme.Something</c> in an object initialiser, which copies the
    /// VALUE once and never looks again. Re-deriving ColorScheme moves the constants and leaves every
    /// control holding what it was handed at construction, so a theme switch changed the status-bar
    /// label and nothing else — the app stayed dark under a light theme. Seen live.</para>
    ///
    /// <para>A REPAINT IS NOT ENOUGH EITHER: ForceFullRedraw draws the values the controls hold, and
    /// those are the stale ones. The colours have to be pushed back in.</para>
    /// </summary>
    public void ReapplyTheme()
    {
        Input.InputBackgroundColor = ColorScheme.ComposerSurface;
        Input.InputFocusedBackgroundColor = ColorScheme.ComposerSurface;
        StatusBar.BackgroundColor = ColorScheme.ChatSurface;
        // NULL UNTIL BuildWindow RUNS, and a config-named theme switches before that.
        if (Window is not null) Window.BackgroundColor = ColorScheme.ChatSurface;
        // NULL BEFORE THE COMPOSER IS BUILT — a theme switch during startup is possible (config names
        // one) and must not fault on a control that does not exist yet.
        if (_promptBox is not null) _promptBox.BackgroundColor = ColorScheme.ComposerSurface;

        // THE MODE LINE CARRIES ITS BACKGROUND IN ITS MARKUP, not on the control — see where it is
        // built. So it cannot be re-coloured by setting a property; the text has to be regenerated.
        // BOTH PLACES, as the comment where this control is built explains: MarkupControl takes its
        // main fill from Container?.BackgroundColor and its right-hand fill from its OWN, so setting
        // only the container leaves the strip past the text in the old colour. Reported live — the
        // composer's mode line kept the dark surface under a light theme.
        if (_modeLine is not null)
        {
            _modeLine.BackgroundColor = ColorScheme.ComposerSurface;
            _modeLine.SetContent([ModeLineText(_mode, ModelLabel)]);
        }

        // THE TWO GRIPS, which are the accent rather than a surface. Both captured ColorScheme.Grip
        // by value when they were built, so a theme switch left the user's marks in the OUTGOING
        // theme's accent — the one piece of chrome whose whole job is to track the active colour,
        // showing the colour that is no longer active.
        //
        // THE RAIL IS A PROPERTY, THE COMPOSER'S IS MARKUP. The rail takes a Color and re-colours by
        // assignment; the grip's colour is baked into the text of each row, so like the mode line
        // above it the content has to be regenerated rather than re-coloured.
        Chat.MessageRailColor = ColorScheme.Grip;
        if (_promptGrip is not null)
            _promptGrip.SetContent([.. Enumerable.Repeat(
                $"[#{ColorScheme.Grip.R:x2}{ColorScheme.Grip.G:x2}{ColorScheme.Grip.B:x2}]▌[/]",
                PromptRows + 1)]);

        // The session panel captured its own surfaces the same way this window did.
        ApplyRoleStyles();   // each style captured its Background by value
        SessionPanel.ReapplyTheme();
    }

    /// <summary>
    /// Takes the caret out of the composer while an overlay owns the screen, and puts it back after.
    ///
    /// <para>THE CURSOR IS THE TELL. The composer is in editing mode from startup — it has to be,
    /// because AppBootstrap consumes every Enter and the control can never flip itself in — so its
    /// caret blinks whenever it is focused. An overlay that draws without changing that leaves the
    /// cursor sitting in the input behind it, which reads as "the keyboard is still down there", and
    /// on a portal that DOES take keys it is simply a lie.</para>
    /// </summary>
    /// <param name="editing">False while an overlay is open, true to return the caret.</param>
    public void SetComposerEditing(bool editing)
    {
        if (editing) { FocusComposer(); return; }

        // FOCUS RELEASED, NOT MOVED. PromptControl has no editing flag of its own — its caret follows
        // FOCUS — so the only way to stop the cursor blinking behind an overlay is to take focus off
        // it, and the status bar (the natural place to park it) is not focusable. Null clears it.
        Window?.FocusManager.SetFocus(null, SharpConsoleUI.Controls.FocusReason.Programmatic);
    }

    /// <summary>The theme item at the far LEFT of the status bar, or null before it is shown.</summary>
    private StatusBarItem? _themeItem;

    /// <summary>
    /// Puts the theme name at the left end of the status bar and returns the item, so the caller can
    /// keep its label current as the theme changes.
    ///
    /// <para>AFTER CONSTRUCTION, NEVER DURING IT. Adding a status-bar item inside BuildWindow HANGS:
    /// StatusBarControl.OnItemChanged calls Invalidate(Relayout), a max-join at the render tick, and
    /// during construction there is no tick to join. Measured on this codebase — MainWindowTests goes
    /// from passing in milliseconds to hanging indefinitely. ShowComposerHint exists for the same
    /// reason and this follows it.</para>
    ///
    /// <para>THE LEFT SIDE WAS EMPTY. Every other item — the panel key, the composer hint, the token
    /// readout — is AddRight, so the theme sits alone at the far left with nothing to crowd.</para>
    /// </summary>
    /// <param name="themeName">The active theme's name, shown beside the glyph.</param>
    /// <param name="onClick">Invoked when the item is clicked.</param>
    public void ShowThemeItem(string themeName, Action onClick)
    {
        if (_themeItem is not null) return;   // idempotent, like ShowComposerHint
        // THE KEY, THEN WHAT IT DOES — "F9:Theme" — which is the shape F2 and F3 already use. The
        // value moves into the label beside it, so the bar reads as one column of keys rather than
        // one item that names its key first and another that buries it after a word.
        _themeItem = StatusBar.AddLeft("F9", $"Theme {themeName}", onClick);
    }

    /// <summary>
    /// Puts the plugin manager beside the theme item, at the left of the status bar.
    ///
    /// <para>AFTER CONSTRUCTION, for the reason <see cref="ShowThemeItem"/> gives: adding a
    /// status-bar item during BuildWindow hangs on a relayout that has no render tick to join.</para>
    ///
    /// <para>THE SAME SHAPE AS THE THEME ITEM — the key in the shortcut slot so the separator lands
    /// after it, and a click that runs what the key runs. Two ways to reach one thing, never two
    /// implementations of it: a click that opened the dialog by a different path is one that can
    /// drift from what F2 does.</para>
    /// </summary>
    /// <param name="onClick">Invoked when the item is clicked — the same toggle F2 uses.</param>
    public void ShowPluginItem(Action onClick)
    {
        if (_pluginItem is not null) return;   // idempotent, like the theme item

        // THE KEY FIRST, unlike the theme item beside it, and the difference is the value. "Theme
        // F9: cxagent" names a setting and then reports it, so the key belongs with the word it
        // qualifies. This item has nothing to report — it is a way in, not a readout — so it reads
        // as the key and what the key does.
        _pluginItem = StatusBar.AddLeft("F2", "Plugins", onClick);
    }

    private StatusBarItem? _pluginItem;

    /// <summary>Re-labels the theme item after a switch. No-op before it is shown.</summary>
    /// <param name="themeName">The newly active theme's name.</param>
    public void SetThemeLabel(string themeName)
    {
        // THE WHOLE LABEL, since it carries the word as well as the value — see ShowThemeItem.
        if (_themeItem is not null) _themeItem.Label = $"Theme {themeName}";
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

    /// <summary>
    /// What the LAST turn actually sent — the context readout's real numerator.
    ///
    /// <para>SEPARATE FROM THE CUMULATIVE TOTAL, because the two answer different questions and
    /// conflating them was a live bug. <see cref="SetTokenTotal"/> carries
    /// <see cref="TokenLedger.TotalTokens"/>, a running SUM of every turn's input and output; since
    /// each turn re-sends the whole conversation, it grows quadratically and passes 100% of any
    /// window while the context itself may be half empty. It also cannot go DOWN, so compressing —
    /// the one operation whose entire purpose is to free context — left the readout unchanged and the
    /// user with no evidence it had worked.</para>
    ///
    /// <para>Occupancy is one turn's <c>Usage.InputTokens</c>: exactly what the compression trigger
    /// measures (Agent), so the gauge and the trigger now agree. Null until a turn reports
    /// usage, which is why the percentage only appears once there is something real to divide.</para>
    /// </summary>
    /// <param name="estimated">
    /// True when the figure is arithmetic rather than a measurement — compaction scaling its last
    /// reading by the size it just changed. Keeps the "~" and the delta on screen; a real measurement
    /// clears both.
    /// </param>
    /// <summary>
    /// Updates the mode shown under the composer. Called at startup and again on every accepted
    /// <c>/mode</c> — the row is the only place the mode is visible, so it has to move when the mode
    /// does or the command has no observable effect.
    /// </summary>
    /// <summary>
    /// Point the window at a different provider — after a <c>/model</c> switch or an F5 re-wire.
    ///
    /// <para>The window, the model name and the agent types all come off the resolution, so this is
    /// the one assignment that keeps the panel describing the session that is actually running.</para>
    /// </summary>
    public void SetResolution(ResolvedConfig resolution)
    {
        _resolution = resolution;

        // THE COMPOSER LINE TOO. It carries the model name, so a switch that refreshed only the
        // panel left "local:…" under the prompt while the session was talking to `small` — the same
        // stale-readout bug the panel's own window had, one line lower.
        _modeLine?.SetContent([ModeLineText(_mode, ModelLabel)]);

        RefreshSessionPanel();
        RefreshTokenItem();
    }

    public void SetMode(WorkingMode mode)
    {
        _mode = mode;
        var model = ModelLabel;
        // SetContent rather than a rebuild: the control is already placed in the grid, and replacing
        // it would detach the instance the layout holds.
        _modeLine?.SetContent([ModeLineText(_mode, model)]);
    }

    /// <summary>
    /// The row's text: agent mode accented, edit mode and model muted, shortcut hinted.
    ///
    /// <para>Mode first because it is a property of the SESSION and the model is a detail of it —
    /// reading them the other way round invites a user to think the model is what they chose. AGENT
    /// BEFORE EDITS for the same reason one step down: whether there is one agent or several frames
    /// everything else, including whose edits are being accepted.</para>
    ///
    /// <para>THE HINT RIDES THE LINE because a shortcut nobody is told about is a shortcut nobody
    /// uses, and this row is where a user already looks when wondering what mode they are in.</para>
    ///
    /// <para>The model name is escaped (it comes from config); the mode words are ours.</para>
    /// </summary>
    private static string ModeLineText(WorkingMode mode, string model) =>
        $"[{ColorScheme.AccentMarkup}]{AgentModes.Name(mode.Agent)}[/]"
      + $"[{ColorScheme.MutedMarkup}] · [/]"
      + $"[{EditModeMarkup(mode.Edits)}]{EditModes.Name(mode.Edits)}[/]"
      + $"[{ColorScheme.MutedMarkup}] (shift+tab to change · F4 focus)[/]"
      + $"[{ColorScheme.MutedMarkup}] · {SharpConsoleUI.Parsing.MarkupParser.Escape(model)}[/]";

    /// <summary>
    /// AUTO IS THE ONE WORTH NOTICING, so it alone is coloured. In the other two modes a human
    /// approves every write that is not already covered by trust or a stored rule; in auto a MODEL
    /// does, and a user glancing at this line should be able to tell those apart without reading.
    ///
    /// <para>CAUTION RATHER THAN DESTRUCTIVE: nothing is wrong, and a red mode line would read as an
    /// error for a state the user deliberately chose. Through the role rather than a literal colour,
    /// because the permission gate says "you should know this" with the same role — and a hardcoded
    /// yellow is the one that goes illegible on a light ground.</para>
    /// </summary>
    private static string EditModeMarkup(EditMode edits) =>
        edits == EditMode.Auto ? ColorScheme.CautionMarkup : ColorScheme.MutedMarkup;

    public void SetContextUsed(int inputTokens, bool estimated = false)
    {
        _contextUsed = inputTokens > 0 ? inputTokens : null;
        if (!estimated)
        {
            _contextStale = false;
            _contextDelta = null;   // a real measurement supersedes what the compression had to say
        }
        RefreshTokenItem();

        // AND THE PANEL, which shows this same number larger and more prominently. It refreshed only
        // from SetTokenTotal, so occupancy reached the status bar and not the panel — and compression
        // arrives through THIS method, not that one. The result was the reported bug surviving in the
        // surface that matters most: compress, and the panel's gauge does not move.
        RefreshSessionPanel();
    }

    /// <summary>
    /// Marks the context reading as no longer describing the conversation — called when compression
    /// has just rewritten it.
    ///
    /// <para>WHY NOT SIMPLY RECOMPUTE. Occupancy is only ever known from what a provider reports it
    /// RECEIVED, and after a compression no call has been made yet — so the true new figure does not
    /// exist until the next turn. The alternatives were both worse than admitting that: keep showing
    /// the pre-compression number (the reported bug — the user compresses, the gauge does not move,
    /// and there is no way to tell whether anything happened), or estimate one locally, which puts a
    /// guess where every other figure in this readout is a measurement.</para>
    ///
    /// <para>So the number is shown struck through with a <c>~</c> until the next turn replaces it
    /// with a real reading. That is honest about the uncertainty AND visibly acknowledges the
    /// compression, which is the feedback that was missing.</para>
    /// </summary>
    public void MarkContextStale(int charsBefore, int charsAfter)
    {
        if (_contextUsed is null) return;
        _contextStale = true;

        // The SCALED occupancy arrives separately, via SetContextUsed, because AgentContext owns both
        // the reading and the size it was taken at — the only place the ratio can be computed
        // correctly. This method says only what the compaction DID.

        // SAY WHICH WAY IT WENT. Compression can make a SHORT conversation bigger: the older half is
        // replaced by a summary, and summarising one already-short message produces more text than it
        // replaced (measured: 536→667 chars on a two-message session). Printing that as "compressed
        // 536→667" claims a win that did not happen, so the verb follows the arithmetic.
        var freed = PercentFreed(charsBefore, charsAfter);
        _contextDelta = charsAfter < charsBefore
            ? freed >= 1
                ? $"compressed −{freed}%"
                : $"compressed {Compact(charsBefore)}→{Compact(charsAfter)} chars"
            : "summarised, nothing to free";

        RefreshTokenItem();
    }

    /// <summary>
    /// A character count at a glance: <c>128k</c> rather than <c>128,412</c>.
    ///
    /// <para>The status bar is read sideways, not studied, and this figure only has to convey an
    /// ORDER of magnitude — that a compression took a lot out. Full precision spends a dozen columns
    /// on digits nobody compares.</para>
    /// </summary>
    private static string Compact(int n) => n switch
    {
        >= 1_000_000 => $"{DisplayNumber.Trimmed(n / 1_000_000.0)}M",
        >= 1_000 => $"{DisplayNumber.Trimmed(n / 1_000.0)}k",
        _ => n.ToString(),
    };

    /// <summary>
    /// How much a compression took out, as a percentage — the one figure that stays legible whatever
    /// the scale.
    ///
    /// <para>Rounded absolute counts collide: a real 5,540→5,490 renders as "5.5k→5.5k", which reads
    /// as nothing having happened. Adding decimal places would fix that pair and lose to the next
    /// one, and precision is the wrong answer to a question about magnitude anyway.</para>
    /// </summary>
    private static int PercentFreed(int before, int after) =>
        before <= 0 ? 0 : (int)Math.Round(100.0 * (before - after) / before);

    private int? _contextUsed;
    private bool _contextStale;

    /// <summary>
    /// What the last compression did, in messages — the one CONCRETE thing that can be said after
    /// compressing.
    ///
    /// <para>The percentage cannot be restated: occupancy is only ever known from what a provider
    /// reports it received, and no call has been made yet, so the figure beside it is a stale upper
    /// bound wearing a "~". A bare tilde says "this number is now wrong" without saying anything
    /// about what happened — which is barely better than the original bug, where the readout did not
    /// move at all. The message counts are already known here for free, so they are what gets shown
    /// until a real measurement replaces the whole thing.</para>
    /// </summary>
    private string? _contextDelta;

    public void SetTokenTotal(int total)
    {
        // THE PANEL IS UPDATED FIRST, and unconditionally — before the zero check below. Returning
        // early at zero hides the status-bar item, which is right, but it would take the panel
        // refresh with it: a provider that reports no usage (a local llama.cpp build often does not)
        // then leaves the whole panel frozen at its startup values — 0 tokens, 0 turns, 0m 0s,
        // forever. The one number that is missing must not hide four that are not.
        _lastTokens = total;
        RefreshSessionPanel();

        if (total == 0)
        {
            if (_tokenItem is not null) _tokenItem.IsVisible = false;
            return;
        }

        RefreshTokenItem();
    }

    /// <summary>
    /// Redraws the bottom-right readout from whatever is currently known. Called by both
    /// <see cref="SetTokenTotal"/> and <see cref="SetContextUsed"/>, since either can change it.
    /// </summary>
    private void RefreshTokenItem()
    {
        // CONTEXT USED, not a bare token count. "94,102 tokens" is a number without a scale — it
        // says nothing about whether the next turn fits, which is the only question a user actually
        // has. With the window known it reads "ctx 46% · 94,102 · 1.2M spent", and the percentage is
        // coloured by how alarming it is (cxtop's thresholds), so it can be read without being read.
        //
        // Falls back to the plain count when the provider's context window is unknown: inventing a
        // denominator would put a confident percentage on a guess.
        // Read from the resolution rather than a copied field: it is already here, and one source
        // cannot drift from another. Null (window unconfigured) falls back to the plain count — a
        // percentage needs a denominator, and a guessed one is worse than none.
        var label = ContextLabel(_contextUsed, _lastTokens, _resolution.ContextWindow, _contextStale,
            _contextDelta, _lastInput, _lastOutput);
        if (label.Length == 0)
        {
            if (_tokenItem is not null) _tokenItem.IsVisible = false;
            return;
        }

        if (_tokenItem is null)
            _tokenItem = StatusBar.AddRight(string.Empty, label);
        else
        {
            _tokenItem.Label = label;
            _tokenItem.IsVisible = true;
        }
    }


    /// <summary>
    /// The bottom-right readout: how full the context is, and what the session has spent.
    ///
    /// <para>THE PERCENTAGE IS OCCUPANCY, NOT SPEND. Dividing the cumulative ledger total by the
    /// window would be wrong twice over: that total sums input AND output across every turn, and
    /// every turn re-sends the whole conversation, so it climbs quadratically and sails past 100%
    /// while the context may be nowhere near full. Worse, a cumulative counter never decreases — so
    /// compressing, whose entire purpose is to free context, would move the number not at all, and a
    /// user watching "107%" after a successful compression would have no way to tell it worked.</para>
    ///
    /// <para>The numerator is <paramref name="used"/> — the last turn's input tokens, the same
    /// measurement the compression trigger acts on, so what the user sees and what the app decides
    /// cannot disagree. Cumulative spend is shown too, as its own clearly-labelled figure, because
    /// "what has this session cost" is a real question; it just is not this percentage.</para>
    /// </summary>
    /// <param name="used">Last turn's input tokens; null before any turn has reported usage.</param>
    /// <param name="spent">Cumulative tokens across the session.</param>
    /// <param name="window">The provider's context window, when known.</param>
    /// <param name="stale">
    /// True once compression has rewritten the conversation, until the next turn measures it again.
    /// Shown as a <c>~</c> prefix and muted throughout: the figure is an upper bound, not a
    /// reading, and dressing it as a reading would be exactly the lie this readout must not tell.
    /// </param>
    /// <summary>
    /// Test seam for <see cref="ContextLabel"/>. Public because this codebase has no
    /// InternalsVisibleTo grant; the ForTest suffix follows the convention used elsewhere here.
    /// </summary>
    public static string ContextLabelForTest(int? used, int spent, int? window, bool stale = false,
        string? delta = null, int input = 0, int output = 0)
        => ContextLabel(used, spent, window, stale, delta, input, output);

    /// <summary>Status-bar magnitudes: two counts and a label must fit beside the context figures,
    /// so thousands collapse. Still its own name rather than calling the shared helper at each site
    /// — "compact tokens" is what the bar means, and the wrapper is where that intent lives — but
    /// the formatting itself is shared, because a local <c>:0.0</c> renders "153,1k" under a
    /// comma-decimal culture.
    /// </summary>
    private static string CompactTokens(int n) =>
        DisplayNumber.Compact(n);

    private static string ContextLabel(int? used, int spent, int? window, bool stale = false,
        string? delta = null, int input = 0, int output = 0)
    {
        var parts = new List<string>(2);

        if (used is { } u && u > 0)
        {
            // A percentage needs a denominator, and a guessed one is worse than none — so without a
            // known window this degrades to the raw occupancy figure rather than inventing a scale.
            if (window is > 0)
            {
                var percent = 100.0 * u / window.Value;
                // A stale figure never gets the alarm colours: it is the number BEFORE the
                // compression that was just performed, so colouring it red would raise an alarm about
                // pressure the app has already relieved.
                var colour = stale ? ColorScheme.MutedMarkup : ColorScheme.ThresholdMarkup(percent);
                var tilde = stale ? "~" : "";

                // BOTH, WHILE STALE. The fraction can be shown here because the reading is scaled
                // by the character ratio compaction just measured — it estimates the new occupancy
                // rather than reporting the pre-compression figure, which beside a "~" would give a
                // wrong number the look of precision. The delta says what happened, the fraction
                // says where that leaves us, and the "~" says the second is arithmetic rather than
                // a measurement.
                var detail = stale && delta is { Length: > 0 }
                    ? $"{delta} · ~{DisplayNumber.Grouped(u)}/{DisplayNumber.Grouped(window.Value)}"
                    : $"{DisplayNumber.Grouped(u)}/{DisplayNumber.Grouped(window.Value)}";

                parts.Add($"[{ColorScheme.MutedMarkup}]ctx[/] [{colour}]{tilde}{DisplayNumber.Fixed(percent, 0)}%[/] "
                        + $"[{ColorScheme.MutedMarkup}]· {detail}[/]");
            }
            else
            {
                parts.Add($"[{ColorScheme.MutedMarkup}]ctx {(stale ? "~" : "")}{DisplayNumber.Grouped(u)}[/]");
            }
        }

        if (spent > 0)
        {
            // THE SPLIT RIDES WITH THE TOTAL, in the one readout that is always on screen. Input and
            // output behave nothing alike — input grows with the conversation because every turn
            // re-sends everything before it, while output is only what the model produced — and they
            // have different remedies: compress the history, or ask for less. A lone total says
            // which is true of neither.
            //
            // Compact, because this is a status bar: "↑153.1k ↓6.9k" is four columns of information
            // where the exact digits were never what the number is read for.
            var split = input > 0 || output > 0
                ? $" [{ColorScheme.MutedMarkup}]↑{CompactTokens(input)} ↓{CompactTokens(output)}[/]"
                : "";

            // THIS AGENT'S SPEND, NOT THE SESSION'S. The ledger is shared — children record into it
            // deliberately, since a budget belongs to the conversation — so `Ledger.TotalTokens` is
            // everything, sub-agents included. Shown here it sat beside `ctx 17%`, which IS this
            // agent's, and read as one figure about one agent: a fan-out session showed a number four
            // times the parent's with nothing to say why.
            //
            // THE BAR IS THIS AGENT, THE PANEL IS EVERYTHING. That division already holds for
            // occupancy, and the panel now carries "Tokens by agent" — workers against this agent —
            // so the breakdown has a home and the bar does not need to hedge.
            parts.Add($"[{ColorScheme.MutedMarkup}]{DisplayNumber.Grouped(spent)} spent[/]{split}");
        }

        return string.Join($"[{ColorScheme.MutedMarkup}] · [/]", parts);
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
        Input.Placeholder = SubmissionEnabled ? ComposerPlaceholder : NoProviderPlaceholder;
    }

    /// <summary>
    /// F1 — post the key map into the chat transcript. Deliberately a chat message rather than a
    /// modal: it needs no new window plumbing, is scrollable/re-readable, and can't trap focus.
    /// (A dedicated help window is a P5c concern, alongside the wizard and settings surfaces.)
    /// </summary>
    /// <summary>
    /// The registry this window's help should describe — the same lookup dispatch uses, so F1 and
    /// <c>/help</c> cannot list different commands. Null falls back to the table's own arguments.
    /// </summary>
    public CxAgent.Core.Commands.CommandRegistry? Registry { get; set; }

    public void ShowHelp()
    {
        // MARKDOWN, LIKE EVERY OTHER SYSTEM ROW. A colour tag here would reach the screen as its
        // own five characters, and hand-counted padding is only a layout at one terminal width. A
        // table says "these are columns" and lets the renderer measure them.
        Chat.AddMessage(ChatRole.System,
            "## Keys\n"
            + "\n"
            + "| key | what it does |\n"
            + "|-----|--------------|\n"
            + "| `Enter` | run the goal in the composer |\n"
            + "| `\\` + `Enter` | continue on a new line (Shift+Enter is not deliverable on a Unix terminal) |\n"
            + "| `↑` / `↓` | recall an earlier goal |\n"
            + "| `F1` | this help |\n"
            + "| `F3` | show or hide the session panel |\n"
            + "| `F4` | put the cursor back in the composer |\n"
            + "| `F5` | settings — providers, roles, orchestrator, permissions |\n"
            + "| `Ctrl+Q` | quit |\n"
            + "\n## Commands\n"
            + "\n"
            // FROM THE TABLE, not a second copy. Every list of commands that is maintained by hand
            // drifts from the dispatcher the first time one is added.
            + SessionCommands.HelpLines(Registry));
        FocusComposer();
    }
}
