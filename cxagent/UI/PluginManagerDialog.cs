using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// The plugin manager: what you have, what needs you, and what else exists.
///
/// <para>DRIVEN MANUALLY, not through <c>Flow</c>. A wizard's chrome is next/back/cancel over a
/// sequence of steps; this is one screen with its own toolbar and per-plugin actions, and the flow
/// machinery would add a frame around it that means nothing here. ServerHub's
/// <c>MarketplaceBrowserDialog</c> is the same shape and the pattern this follows.</para>
///
/// <para>EVERY ACTION RUNS THE COMMAND A USER COULD TYPE, awaited. The registry's own /plugin
/// handler is fire-and-forget (SessionManager.cs:213-217) because a load awaits a permission prompt
/// and the dispatcher is synchronous — so going through it would tell this dialog nothing. Awaiting
/// <c>RunPluginCommand</c> gives an outcome to render, and the command string is parsed by the same
/// parser a typed command uses.</para>
/// </summary>
public static class PluginManagerDialog
{
    /// <summary>The open dialog, or null. Static because the shortcut handlers registered at startup
    /// need to know whether one is up, and there is at most one — a second would clobber the first's
    /// state, since every closure here reads these fields.</summary>
    private static Window? _window;

    // WHERE THE USER LEFT IT, remembered across opens. The dialog is opened repeatedly in one
    // session, and a window that will not stay put is one the user has to move every time.
    private static (int Left, int Top)? _placement;
    private static (int Width, int Height)? _size;

    private static ConsoleWindowSystem? _ws;
    private static SessionManager? _manager;
    private static Session? _session;
    private static AppPaths? _paths;
    private static string? _projectDirectory;

    // FOR REOPENING after a permission prompt (see CloseForPrompt/Reopen) — Show's signature wants
    // the parent even though nothing here reads it, and a reopen must hand back the same one.
    private static Window? _parent;

    // A MESSAGE THAT MUST OUTLIVE REBUILDS, keyed to the plugin it is about. A hash mismatch or a
    // held-file refusal is a standing fact, not a six-second event — a toast dies before the user
    // has compared two hashes. Rendered into the panel by ShowDetail; cleared when the user asks
    // for a re-read (open or F5/Refresh), which is them saying "re-judge everything".
    private static (string Name, IReadOnlyList<string> Lines)? _panelNote;

    // ONE INSTALLER FOR THE DIALOG'S LIFE: it already shares one HttpClient across instances, but
    // holding it here says plainly that installs have no per-call state to reset.
    private static readonly PluginInstaller Installer = new();

    private static ListControl? _rail;
    private static TabControl? _tabs;
    private static MarkupControl? _detailHeader;
    private static MarkupControl? _detailBody;
    /// <summary>
    /// The rail's starting width in cells. Wide enough for the longest plugin name plus its commonest
    /// state on one line ("csharp-lsp-omnisharp" is 20, "disabled" 8) — the long NEEDS ATTENTION
    /// reasons wrap to a second line, which is the right place to spend one. The splitter moves it.
    /// </summary>
    private const int RailCells = 34;

    private static MarkupControl? _age;

    /// <summary>The status bar's left half — what the rail is currently showing.</summary>
    private static MarkupControl? _status;
    private static PromptControl? _filter;
    // A TOOLBAR, NOT A BUTTON GRID. HorizontalGridControl places its columns flush, so the actions
    // ran together as one unbroken bar — "DeactivateDisable auto-loadUninstall" — with nothing to
    // say where one target ends and the next begins. A toolbar spaces its items, which is the
    // whole difference between a row of buttons and a row that can be read.
    private static ToolbarControl? _buttons;
    private static int _lastRailIndex;

    // WHICH PLUGIN THE PANEL LAST SHOWED. The Settings tab is torn down and rebuilt on every
    // selection change, and whether the user's place on it is kept depends on whether the plugin
    // is still the same one — a rebuild for the SAME plugin (a save, an F5) must not throw the
    // user back to Details, while moving to another plugin must.
    private static string? _lastDetailName;

    // THE FIELDS OF THE OPEN SETTINGS FORM, keyed for the save. Rebuilt with the tab; a save reads
    // these rather than walking the control tree, because the tree's shape is a layout decision
    // and the save must not break when the layout changes.
    private static readonly List<(string Key, MultilineEditControl Editor)> _settingsFields = [];
    private static MarkupControl? _settingsStatus;

    // ONE MESSAGE THAT SURVIVES A REBUILD. A successful save re-reads everything, which tears the
    // settings tab down — a status line set before the rebuild would be destroyed with it, so the
    // outcome is parked here and rendered by the next build, once.
    private static string? _settingsNote;

    private static IReadOnlyList<PluginRow> _rows = [];

    // KEPT BESIDE THE ROWS, for the panel. A row carries what the RAIL needs; the panel also needs
    // what only the gather knew — where an unconfigured plugin's files live (for its README) and
    // each installed contract — and re-walking the disk per selection would repeat work Rebuild
    // just did.
    private static PluginManagerInputs? _inputs;
    private static IReadOnlyList<string> _searchFolders = [];

    // KEPT ACROSS OPENS. The catalog changes on the publisher's schedule, not the user's; reopening
    // shows the last read immediately while a fresh fetch runs, instead of an empty AVAILABLE
    // section on every F2.
    private static Catalog _catalog = new([]);
    private static bool _fetching;
    private static DateTimeOffset? _fetchedAt;

    public static void Show(
        ConsoleWindowSystem ws, Window parent, SessionManager manager, Session session,
        AppPaths paths, string projectDirectory)
    {
        // ONE DIALOG, EVER. F2 toggles rather than stacking, but /plugin browse can be typed while
        // one is already up — and a second Show would clobber the first's state, since every
        // closure here reads the static fields above.
        if (_window is not null)
        {
            ws.SetActiveWindow(_window);
            return;
        }

        _ws = ws;
        _manager = manager;
        _session = session;
        _paths = paths;
        _projectDirectory = projectDirectory;
        _parent = parent;

        // An open is a fresh read of everything — a standing note earned against the last read is
        // re-earned or gone, the same as Refresh.
        _panelNote = null;

        // 90% OF THE TERMINAL, CAPPED. The same sizing MarketplaceBrowserDialog uses (:63-67) —
        // Console.WindowWidth, not a window-system property, because it is what that shipping
        // dialog is proven against.
        var width = Math.Min((int)(Console.WindowWidth * 0.9), 150);
        var height = Math.Min((int)(Console.WindowHeight * 0.9), 40);

        var window = new WindowBuilder(ws)
            .WithTitle("Plugins")
            .WithSize(_size?.Width ?? width, _size?.Height ?? height)
            .Centered()
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(true)
            .Movable(true)
            .Minimizable(false)
            .Build();

        // STICKY FOR WHAT MUST NOT SCROLL, a grid for what must split. StickyPosition
        // (IWindowControl.cs:18-26) pins a control to the visible area while the rest scrolls, which
        // is exactly what a toolbar and a button row are — an Auto grid row reserves space but does
        // not pin. ServerHub does the same with its button grids
        // (MarketplaceBrowserDialog.cs:816, :921).
        //
        //  [ toolbar — filter · refresh · catalog age ]   StickyPosition.Top
        //  [ ───────────────────────────────────────── ]   a rule, so the chrome reads as chrome
        //  [ rail  │ detail                            ]   the grid, split by a draggable splitter
        //  [ ───────────────────────────────────────── ]
        //  [ 4 plugins · 1 loaded    F5 · Esc  [Close] ]   StickyPosition.Bottom
        var toolbar = BuildToolbar();
        toolbar.StickyPosition = StickyPosition.Top;
        window.AddControl(toolbar);

        // A LINE UNDER THE TOOLBAR, so the dialog-wide controls read as a band rather than as the
        // first row of the list. Without it the filter and the rail's first heading sit in one
        // undifferentiated column of text.
        window.AddControl(new RuleControl
        {
            StickyPosition = StickyPosition.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        // TWO COLUMNS, ONE ROW. The rail is a fixed width with a floor so it stays readable when the
        // window is dragged narrow; the panel takes what is left.
        var grid = new GridControl { VerticalAlignment = VerticalAlignment.Fill };
        grid.ColumnDefinitions.Add(GridLength.Cells(RailCells, min: 24));
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        grid.RowDefinitions.Add(GridLength.Star(1));

        // A SECOND ROW FOR THE PANEL'S OWN ACTIONS. They act on the selected plugin, and the panel
        // IS the selected plugin — so the panel's edge draws the boundary of what they affect. Sticky
        // to the window they spanned the rail too, reading as dialog chrome rather than as buttons
        // about one row, which is the same scope-invisibility that put Close among them.
        //
        // A RULE ABOVE THEM, in the same grey the settings hint uses, so the actions read as chrome
        // separated from the panel rather than as the last line of it.
        //
        // ONE CELL EACH, NOT Auto: a control reports no intrinsic height from inside a grid cell, so
        // an Auto track measures it as nothing and the row never appears.
        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.RowDefinitions.Add(GridLength.Cells(1));

        _rail = new ListControl
        {
            VerticalAlignment = VerticalAlignment.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _rail.SelectedIndexChanged += OnRailSelectionChanged;
        // SPANS EVERY ROW, so the rail runs the full height beside the panel, its rule and the
        // action row — a span short of the last row leaves the rail's column ending above the
        // buttons with a gap no border explains.
        grid.Place(_rail, 0, 0, rowSpan: 3);

        // TWO TABS ONLY, Details and Settings — and Settings is added per selection, because for
        // most rows it must not exist at all (see RebuildSettingsTab). A Tools tab was considered
        // and rejected: three tools is a line, not a page, so gating lives in the Details facts.
        _detailHeader = new MarkupControl([""]) { Wrap = true };
        _detailBody = new MarkupControl([""]) { Wrap = true };

        // ONE SCROLL FOR HEADER AND BODY TOGETHER: a README long enough to scroll should carry the
        // identity lines up with it — a pinned header would spend six rows of a 40-row window
        // repeating what the rail already shows beside it.
        var detailScroll = new ScrollablePanelControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
        };
        detailScroll.AddControl(_detailHeader);
        detailScroll.AddControl(_detailBody);

        _tabs = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
        };
        _tabs.AddTab("Details", detailScroll);
        // UNDER THE PANEL, NOT THE WINDOW. These act on the selected plugin, so they sit inside the
        // column that shows it — sticky to the window they spanned the rail as well, reading as
        // chrome about the dialog rather than actions on one row.
        //
        // CLOSE IS NOT AMONG THEM, for the same reason: closing acts on the dialog, and sharing a
        // row gave the two kinds equal weight with no way to tell them apart. It is in the status
        // bar, beside the key that also does it.
        //
        // Rebuilt whenever the selection moves, which is why it is its own control.
        _buttons = new ToolbarControl
        {
            ItemSpacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        grid.Place(_tabs, 0, 1);
        grid.Place(new RuleControl { Color = ColorScheme.MutedRgb }, 1, 1);
        grid.Place(_buttons, 2, 1);

        // A DRAGGABLE DIVIDER, not a drawn one. The splitter IS the line between rail and panel and
        // the handle that moves it, so the two cannot disagree — and it honours the rail column's
        // own minimum (GridSplitterResize.cs:30-44 clamps so neither track crosses its Min), which
        // is why 24 cells is expressed there rather than defended here. Focusable as well as
        // draggable, so a keyboard user can widen the rail for a long plugin name.
        grid.AddColumnSplitterAfter(0);

        // WHERE THE USER LEFT IT. Window.Left/Top are settable (Window.cs:797, :929), so a manager
        // dragged aside or resized reopens where it was rather than jumping back to centre — the
        // dialog is opened repeatedly in one session, and a window that will not stay put is one the
        // user has to move every time.
        if (_placement is { } seen) { window.Left = seen.Left; window.Top = seen.Top; }

        window.AddControl(grid);


        // A LINE ABOVE THE STATUS BAR, closing the frame the toolbar's rule opens.
        window.AddControl(new RuleControl
        {
            StickyPosition = StickyPosition.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        // THE STATUS BAR: what the rail is showing, and how to leave.
        //
        // ESC IS NAMED AND ALSO CLICKABLE. The key is the fast path and the button is the
        // discoverable one — a status line that only said "Esc close" would strand a user reaching
        // for a mouse, and a button alone would never teach the key.
        // THE LABEL TAKES THE ROOM, THE BUTTON TAKES ITS SIZE. A ColumnContainer sizes to its
        // content, so an unwidened label is clipped to whatever it happened to measure — the status
        // text is the variable half and gets the remaining width explicitly.
        _status = new MarkupControl([""]) { Wrap = false };

        var statusBar = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var statusColumn = new ColumnContainer(statusBar) { Width = width - 14 };
        statusColumn.AddContent(_status);
        statusBar.AddColumn(statusColumn);

        var closeColumn = new ColumnContainer(statusBar);
        closeColumn.AddContent(Controls.Button("  Close  ").OnClick((_, _) => CloseIfOpen()).Build());
        statusBar.AddColumn(closeColumn);

        statusBar.StickyPosition = StickyPosition.Bottom;
        window.AddControl(statusBar);

        // F5 STAYS ON THE WINDOW: no global claims it, so this handler is reached directly. That is
        // deliberate rather than lucky — the tab strip is on F6 precisely because F5 means refresh
        // nearly everywhere, and a global claim on it would take the key from this dialog entirely
        // (InputCoordinator.cs:130-134). Escape and F2 ARE claimed globally and are handled there.
        window.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.F5) return;
            Refresh();
            e.Handled = true;
        };

        // THE ROW SHOWS WHAT IS TRUE, whoever made it true. A dialog that only redrew after its own
        // buttons would sit stale while a deferred unwire landed or a typed command changed the same
        // entry behind it — and its whole feedback model is reading state rather than assuming it.
        // Every mutator announces SessionChangeKind.Plugins.
        session.Changed += OnSessionChanged;

        window.OnClosed += (_, _) =>
        {
            // UNSUBSCRIBED WITH THE WINDOW: a handler left attached would be a callback into a
            // closed window's controls the next time anything changes a plugin.
            session.Changed -= OnSessionChanged;

            // REMEMBERED FOR THE NEXT OPEN. Read on close rather than tracked as it moves: the user
            // may drag or resize repeatedly, and only the final resting place matters.
            _placement = (window.Left, window.Top);
            _size = (window.Width, window.Height);
            _window = null;
        };

        _window = window;
        ws.AddWindow(window);
        ws.SetActiveWindow(window);
        window.FocusManager.SetFocus(_rail, FocusReason.Programmatic);

        // LOCAL STATE FIRST, the catalog when it arrives. Installed plugins come from config and
        // disk, so that half of the rail renders before any network is touched — waiting for the
        // fetch would make F2 feel broken on a slow network to serve the half that needs it least.
        Rebuild();
        _ = FetchCatalogAsync();
    }

    /// <summary>Closes the manager if it is open. Returns whether it was.</summary>
    public static bool CloseIfOpen()
    {
        if (_window is null) return false;

        // A SETTINGS FIELD MID-EDIT EATS THE FIRST ESCAPE. The global Escape is consulted before
        // the active window ever sees the key (InputCoordinator.cs:130-134), so the editor's own
        // Escape-exits-editing can never run — without this, Escape while typing a value discards
        // the whole dialog, edits and all. One Escape leaves the field, the next closes. Tab cannot
        // substitute: an editing MultilineEdit consumes Tab as indent, so Escape is the only way
        // back out of a field to the Save button.
        if (_window.FocusManager.FocusedControl is MultilineEditControl { IsEditing: true } editor)
        {
            editor.IsEditing = false;
            return true;
        }

        _window.Close();
        _window = null;
        return true;
    }

    /// <summary>Whether a manager window is up — read by the global Escape handler, which must
    /// decline the key rather than cancelling a turn while a dialog is showing.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>Re-reads everything the rail renders: config and disk now, the catalog when the
    /// fetch lands.</summary>
    private static void Refresh()
    {
        // A refresh is the user asking for a fresh judgement, so a standing panel note is dropped
        // with the stale rows — retrying the action that earned it will re-earn it.
        _panelNote = null;
        Rebuild();
        _ = FetchCatalogAsync();
    }

    /// <summary>
    /// Repaints when any door changes a plugin — /plugin typed at the prompt behind the dialog, a
    /// deferred unwire landing at another session's turn end. Marshalled because the announce runs
    /// on whatever thread the mutator happened to finish on, and Rebuild touches controls the
    /// render loop is painting.
    /// </summary>
    private static void OnSessionChanged(SessionChangeKind kind)
    {
        if (kind is not SessionChangeKind.Plugins) return;

        _ws!.EnqueueOnUIThread(() =>
        {
            if (_window is not null) Rebuild();
        });
    }

    /// <summary>Recomputes the rows from live config, disk, and the last catalog read.</summary>
    private static void Rebuild()
    {
        var projectDirectory = _projectDirectory!;
        _searchFolders = PluginDiscovery.SearchFolders(
            _manager!.Config.PluginPaths, projectDirectory, _paths!.ConfigDir);

        _inputs = PluginManagerState.Gather(
            _manager.Config.Plugins, _session!.Plugins.LoadedPluginNames, _searchFolders,
            _catalog.Plugins, projectDirectory);
        _rows = PluginManagerRows.Build(_inputs);

        RefillRail();
        UpdateAgeLine();
    }

    private static async Task FetchCatalogAsync()
    {
        _fetching = true;
        UpdateAgeLine();

        // BESIDE CONFIG, NOT IN A TEMP DIRECTORY: the cache is what lets the dialog open offline in
        // a later run, so it has to live somewhere that outlives the process and travels with the
        // rest of the app's state.
        // AN OVERRIDE, FOR POINTING AT A CATALOG THAT IS NOT THE PUBLISHED ONE. Testing the install
        // path needs a catalog offering something this machine does not have, and the published one
        // by definition offers what has already shipped. A variable rather than a rebuild, so the
        // path can be exercised against a local file server without a source edit.
        var url = Environment.GetEnvironmentVariable("CXAGENT_PLUGIN_CATALOG") is { Length: > 0 } custom
            ? custom
            : CatalogReader.PublishedUrl;

        var reader = new CatalogReader(null, Path.Combine(_paths!.ConfigDir, "catalog-cache.json"), url);
        var read = await reader.ReadAsync(CancellationToken.None);

        // MARSHALLED, because ReadAsync resumes wherever its ConfigureAwait(false) left it —
        // and everything below mutates controls the render loop is painting.
        _ws!.EnqueueOnUIThread(() =>
        {
            _catalog = read;
            _fetching = false;
            if (read.Error is null) _fetchedAt = DateTimeOffset.UtcNow;
            if (_window is not null) Rebuild();
        });
    }

    private static HorizontalGridControl BuildToolbar()
    {
        var toolbar = new HorizontalGridControl();

        var filterColumn = new ColumnContainer(toolbar) { Width = 32 };
        _filter = new PromptControl { Prompt = "Filter: ", InputWidth = 22 };
        // THE FILTER SEARCHES EVERY SECTION AT ONCE — the argument for one grouped rail rather than
        // tabs. Only the rail refills; the rows themselves are not regathered on a keystroke.
        _filter.InputChanged += (_, _) => RefillRail();
        filterColumn.AddContent(_filter);
        toolbar.AddColumn(filterColumn);

        var refreshColumn = new ColumnContainer(toolbar) { Width = 14 };
        refreshColumn.AddContent(
            Controls.Button(" Refresh ").OnClick((_, _) => Refresh()).Build());
        toolbar.AddColumn(refreshColumn);

        var ageColumn = new ColumnContainer(toolbar);
        _age = new MarkupControl([""]);
        ageColumn.AddContent(_age);
        toolbar.AddColumn(ageColumn);

        return toolbar;
    }

    /// <summary>
    /// The age line is where the catalog read is finally shown: how fresh the AVAILABLE section is,
    /// or why it is stale — which is the honest version of an empty section.
    /// </summary>
    private static void UpdateAgeLine()
    {
        if (_age is null) return;

        var text = _fetching ? "fetching catalog…"
            : _catalog.Error is null
                ? _fetchedAt is { } at ? $"catalog fetched {Age(at)}" : ""
                : _catalog.CachedAt is { } cached
                    ? $"offline — showing a copy from {Age(cached)}"
                    : $"offline — {_catalog.Error}";

        _age.SetContent([$"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(text)}[/]"]);
    }

    private static string Age(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at.ToUniversalTime();
        return span < TimeSpan.FromMinutes(1) ? "just now"
            : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes}m ago"
            : span < TimeSpan.FromDays(1) ? $"{(int)span.TotalHours}h ago"
            : span < TimeSpan.FromDays(2) ? "yesterday"
            : $"{(int)span.TotalDays}d ago";
    }

    /// <summary>Refills the rail through the filter, keeping the selected plugin selected when it
    /// survives the refill — losing the user's place on every keystroke or F5 would make both
    /// features cost more than they give.</summary>
    private static void RefillRail()
    {
        if (_rail is null) return;

        // THE RAIL FIRST, THEN THE LAST PLUGIN SHOWN. On a refresh the rail still holds the
        // selection, so it is read straight off. On an OPEN it is empty — the control is new — and
        // without the fallback the dialog reopens on the first row, which is what makes an action
        // that closes and reopens it (a permission prompt does exactly that) lose the user's place
        // in a list where they had just chosen something.
        //
        // _lastDetailName SURVIVES BECAUSE IT IS STATIC and is cleared only when there is no row to
        // show at all, so it still names whatever was selected when the window went away.
        var keep = _rail.SelectedIndex >= 0 && _rail.SelectedIndex < _rail.Items.Count
            ? (_rail.Items[_rail.SelectedIndex].Tag as PluginRow)?.Name
            : _lastDetailName;

        FillRail(_rail, PluginManagerRows.Filter(_rows, _filter?.Input));

        _lastRailIndex = 0;
        var index = _rail.Items.FindIndex(i => i.Tag is PluginRow row && row.Name == keep);
        if (index < 0) index = _rail.Items.FindIndex(i => i.Tag is not null);

        if (index >= 0) _rail.SelectedIndex = index;
        else ShowDetail(null);

        UpdateStatus();
    }

    /// <summary>
    /// What the rail is showing, and how to leave.
    ///
    /// <para>COUNTED FROM WHAT IS RENDERED, not from the unfiltered set: a user who has typed a
    /// filter is asking about the rows in front of them, and a total that ignored the filter would
    /// answer a question nobody asked. When a filter is hiding rows it says so, because a list that
    /// silently omits things is one a user reads as complete.</para>
    /// </summary>
    private static void UpdateStatus()
    {
        if (_status is null) return;

        var shown = PluginManagerRows.Filter(_rows, _filter?.Input);
        var loaded = shown.Count(r => r.State.StartsWith("loaded", StringComparison.Ordinal));

        var left = shown.Count == 1 ? "1 plugin" : $"{shown.Count} plugins";
        if (loaded > 0) left += $" · {loaded} loaded";
        if (shown.Count != _rows.Count) left += $" · {_rows.Count - shown.Count} hidden by filter";

        _status.SetContent([$"[grey]{left}   ·   F5 refresh · Esc close[/]"]);
    }

    /// <summary>
    /// Fills the rail from the rows, with a disabled item per section header.
    ///
    /// <para>HEADERS ARE UNSELECTABLE ITEMS, not a separate control: a grouped list has to scroll as
    /// one thing, and four lists in a column would each scroll separately and align badly. A section
    /// with nothing left after filtering is not rendered at all — GroupBy only yields groups with
    /// rows, so a heading never stands over nothing.</para>
    /// </summary>
    private static void FillRail(ListControl rail, IReadOnlyList<PluginRow> rows)
    {
        rail.ClearItems();

        foreach (var group in rows.GroupBy(r => r.Section))
        {
            // MUTED, NOT A THEME'S DISABLED STYLE — the list has no per-item disabled styling, so
            // a header left unstyled would read as just another plugin the arrows refuse to act on.
            rail.AddItem(new ListItem($"[{ColorScheme.MutedMarkup} bold]{Heading(group.Key)}[/]")
            {
                IsEnabled = false,
            });

            foreach (var row in group)
                rail.AddItem(new ListItem(RailText(row, RailCells)) { Tag = row });
        }
    }

    /// <summary>
    /// A row's rail text: the name, and the state word under it.
    ///
    /// <para>TWO LINES, NOT TWO COLUMNS. ListItem has no second column, and at the rail's 34 cells
    /// the longest states ("contract 3 · needs a newer cxagent", "loaded · 0.2.1 → 0.3.0") cannot
    /// share a line with any name without truncating the half the user came to read.</para>
    /// </summary>
    /// <summary>
    /// One row: the name, and its state beside it when both fit.
    ///
    /// <para>HEIGHT IS THE SCARCE DIMENSION, so a second line is spent only when the width cannot
    /// take the state — a terminal gives fewer rows than columns, and two lines per plugin halves
    /// how many a user sees at once. Most states are short ("loaded", "disabled"); it is the
    /// NEEDS ATTENTION reasons that are long, and those are the rows worth a second line.</para>
    ///
    /// <para>MEASURED AGAINST THE COLUMN, not a guess: the rail is <see cref="RailCells"/> wide and
    /// the splitter can widen it, so a name that wraps today may fit after a drag.</para>
    /// </summary>
    private static string RailText(PluginRow row, int cells)
    {
        var name = MarkupParser.Escape(row.Name);
        if (row.State.Length == 0) return name;

        var state = MarkupParser.Escape(row.State);

        // Two spaces between, and one cell of margin so the text never touches the splitter.
        return row.Name.Length + 2 + row.State.Length <= cells - 1
            ? $"{name}  [{ColorScheme.MutedMarkup}]{state}[/]"
            : $"{name}\n  [{ColorScheme.MutedMarkup}]{state}[/]";
    }

    private static string Heading(PluginRowSection section) => section switch
    {
        PluginRowSection.Installed => "INSTALLED",
        PluginRowSection.Updates => "UPDATES",
        PluginRowSection.NeedsAttention => "NEEDS ATTENTION",
        _ => "AVAILABLE",
    };

    private static void OnRailSelectionChanged(object? sender, int index)
    {
        if (_rail is null) return;

        // ClearItems raises this with -1 (ListControl.cs:807); indexing with it would throw
        // mid-refill. NOTHING IS TORN DOWN HERE: the refill re-selects immediately after, and a
        // teardown in between forgets which tab the user was on — a save rebuilds the rail, and
        // its user must land back on the Settings tab they pressed Save from. The genuinely empty
        // rail is RefillRail's explicit ShowDetail(null), not this transient.
        if (index < 0) return;

        // A HEADER IS NOT A ROW. IsEnabled would stop Enter activating it but does not stop the
        // arrows landing on it, so the cursor is nudged past in the direction it was going —
        // bouncing it back would trap the user against the top of a section.
        if (_rail.Items[index].Tag is null)
        {
            var forward = index >= _lastRailIndex;
            var next = forward ? index + 1 : index - 1;
            if (next >= 0 && next < _rail.Items.Count)
            {
                _rail.SelectedIndex = next;
                return;
            }

            // At either end, the only non-header neighbour is the other way.
            _rail.SelectedIndex = forward ? index - 1 : index + 1;
            return;
        }

        _lastRailIndex = index;
        ShowDetail(_rail.Items[index].Tag as PluginRow);
    }

    /// <summary>The right-hand panel: the selected row, said plainly — identity, the facts that
    /// differ by state, the body, the settings tab when one is earned, and the action row.</summary>
    private static void ShowDetail(PluginRow? row)
    {
        if (_detailHeader is null || _detailBody is null || _tabs is null) return;

        if (row is null)
        {
            _detailHeader.SetContent([""]);
            _detailBody.SetContent([""]);
            _tabs.RemoveTab("Settings");
            _settingsFields.Clear();
            _settingsStatus = null;
            RebuildButtons(null);
            _lastDetailName = null;
            return;
        }

        var lines = new List<string>
        {
            $"[bold]{MarkupParser.Escape(row.Catalog?.DisplayName ?? row.Name)}[/]",
        };

        if (row.Catalog is { } entry)
        {
            var identity = string.Join(" · ",
                new[] { entry.Name, entry.Version, entry.License, entry.Publisher }
                    .Where(part => part.Length > 0));
            lines.Add($"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(identity)}[/]");
        }

        lines.Add("");
        lines.AddRange(FactLines(row));

        // THE STANDING NOTE, under the facts of the plugin it is about — a hash mismatch or a
        // held-file refusal stays on screen through every rebuild until the user refreshes, where
        // a toast would have died mid-read.
        if (_panelNote is { } note && note.Name == row.Name)
        {
            lines.Add("");
            lines.AddRange(note.Lines.Select(l =>
                l.Length == 0 ? "" : $"[yellow]{MarkupParser.Escape(l)}[/]"));
        }

        if (row.Detail is { } detail)
        {
            lines.Add("");
            lines.Add(MarkupParser.Escape(detail));
        }

        lines.Add("");
        _detailHeader.SetContent(lines);

        ShowBody(row);
        RebuildSettingsTab(row);
        RebuildButtons(row);
        _lastDetailName = row.Name;
    }

    /// <summary>The aligned fact block: STATE, NEEDS, CONTRACT, TOOLS — each only when there is
    /// something true to say, so an undocumented plugin shows a short block rather than empty
    /// labels.</summary>
    private static IEnumerable<string> FactLines(PluginRow row)
    {
        yield return Fact("STATE", StateLine(row));

        if (row.Catalog is { RequiresDescription: { Length: > 0 } needs } entry)
        {
            yield return Fact("NEEDS", needs);
            if (entry.RequiresInstall is { Length: > 0 } install)
                yield return Continuation(install);

            // ONLY WHEN THE DESCRIPTION DOES NOT ALREADY NAME IT — csharp-lsp's own description is
            // "csharp-ls on PATH", and printing "default: csharp-ls" under that is a stutter.
            if (entry.RequiresDefault is { Length: > 0 } fallback
                && !needs.Contains(fallback, StringComparison.Ordinal))
                yield return Continuation($"default: {fallback}");
        }

        if (ContractOf(row) is { } contract)
            yield return Fact("CONTRACT", contract.ToString());

        if (row.Catalog is { } catalogued && ToolsLine(catalogued.Tools) is { } tools)
            yield return Fact("TOOLS", tools);
    }

    private static string Fact(string key, string value) =>
        $"[{ColorScheme.MutedMarkup} bold]{key,-10}[/]{MarkupParser.Escape(value)}";

    /// <summary>A fact's second line, indented under the value column so the key column stays a
    /// column.</summary>
    private static string Continuation(string value) =>
        $"{new string(' ', 10)}[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(value)}[/]";

    /// <summary>State · folder · what config says — the whole placement story in one line, because
    /// each third can surprise independently: a loaded plugin can be disabled in config for the
    /// next start, and a project copy can shadow a global one.</summary>
    private static string StateLine(PluginRow row)
    {
        var parts = new List<string>();
        if (row.State.Length > 0) parts.Add(row.State);
        if (row.Folder is { } folder) parts.Add(folder);
        // WHAT THE ENTRY SAYS, NOT "enabled"/"disabled". `enabled` decides auto-load and nothing
        // else — it no longer forbids a hand load — so calling the plugin "disabled" describes a
        // veto that does not exist.
        //
        // AND SAID FOR AN UNCONFIGURED PLUGIN TOO. Having no entry is a different situation from
        // having one that says no, and the line went silent on exactly the case where "there is no
        // entry" is what the reader needs — leaving two different rows reading identically.
        // Folder is the "it is on disk here" fact, so it is also the test for whether a config
        // line is worth printing: a catalog-only row has nothing to say about config yet.
        if (row.Folder is not null)
            parts.Add(row.Configured is { } config
                ? config.Enabled ? "in config, auto-load" : "in config, no auto-load"
                : "not in config");

        return parts.Count > 0 ? string.Join(" · ", parts) : "not installed";
    }

    /// <summary>The installed sidecar's contract when there is one, else the catalog's claim —
    /// the sidecar wins because it describes the binary actually on disk.</summary>
    private static int? ContractOf(PluginRow row)
    {
        if (_inputs is { } inputs && inputs.InstalledContracts.TryGetValue(row.Name, out var installed))
            return installed;

        return row.Catalog is { PluginContract: > 0 } entry ? entry.PluginContract : null;
    }

    /// <summary>
    /// The tool summary, one line — so gating is visible without opening anything. A user who never
    /// looks further still learns whether these tools ask before running.
    /// </summary>
    private static string? ToolsLine(IReadOnlyList<CatalogTool> tools)
    {
        if (tools.Count == 0) return null;

        var asks = tools.Count(t => string.Equals(t.Gated, "true", StringComparison.OrdinalIgnoreCase));
        var decides = tools.Count(t => string.Equals(t.Gated, "dynamic", StringComparison.OrdinalIgnoreCase));
        var never = tools.Count - asks - decides;

        if (decides == tools.Count)
            return tools.Count == 1 ? "1, decides per call whether to ask"
                                    : $"{tools.Count}, all decide per call whether to ask";
        if (asks == tools.Count)
            return tools.Count == 1 ? "1, asks before running" : $"{tools.Count}, all ask before running";
        if (never == tools.Count)
            return tools.Count == 1 ? "1, never asks" : $"{tools.Count}, none ask before running";

        var parts = new List<string>();
        if (asks > 0) parts.Add($"{asks} ask first");
        if (decides > 0) parts.Add($"{decides} decide per call");
        if (never > 0) parts.Add($"{never} never ask");
        return $"{tools.Count} · {string.Join(" · ", parts)}";
    }

    /// <summary>
    /// The body, and it differs by state because the sources genuinely differ: an installed plugin
    /// has its own README.md on disk beside the binary, an available one has only the catalog's
    /// description — which is a real paragraph, not a stub.
    ///
    /// <para>THE CATALOG'S <c>readme</c> FIELD IS NOT USED. It holds a repo-relative path that
    /// resolves against nothing published, so following it would render a 404 where documentation
    /// should be.</para>
    /// </summary>
    private static void ShowBody(PluginRow row)
    {
        if (HomeOf(row) is { } home)
        {
            var readme = Path.Combine(home, "README.md");
            try
            {
                if (File.Exists(readme))
                {
                    _detailBody!.SetMarkdown(File.ReadAllText(readme));
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable README must not take the panel down — fall through to the
                // description, which is what an install predating shipped READMEs shows anyway.
            }
        }

        _detailBody!.SetMarkdown(row.Catalog?.Description ?? "");
    }

    /// <summary>Where this plugin's files live, or null when it is not on disk. Configured rows
    /// resolve their entry-point file the same way a load would; unconfigured rows were found by
    /// the gather, which already knows the folder it found them in.</summary>
    private static string? HomeOf(PluginRow row) =>
        row.Configured is { } config
            ? PluginDiscovery.FindLoadSetDirectory(config.File, _searchFolders)
            : _inputs?.Unconfigured.FirstOrDefault(u => u.Name == row.Name)?.Folder;

    /// <summary>
    /// Adds or removes the Settings tab for this row.
    ///
    /// <para>ABSENT ENTIRELY — NOT DISABLED — when the plugin is not configured or documents no
    /// settings. A tab that cannot ever do anything costs a row of chrome to say nothing. Configured
    /// is the bar, not merely on-disk: <see cref="SessionManager.SetPluginSettings"/> refuses a name
    /// config does not hold, so a form for an unconfigured plugin would be a form whose Save can
    /// never succeed.</para>
    /// </summary>
    private static void RebuildSettingsTab(PluginRow row)
    {
        // Read before the teardown moves it: whether the user was ON the settings tab, for the
        // same plugin — a save's rebuild must put them back, a selection change must not.
        var keepSettingsOpen = _tabs!.ActiveTabIndex == 1
            && string.Equals(_lastDetailName, row.Name, StringComparison.Ordinal);

        _tabs.RemoveTab("Settings");
        _settingsFields.Clear();
        _settingsStatus = null;

        if (row.Configured is null || row.Catalog is not { } entry || entry.Settings.Count == 0)
            return;

        _tabs.AddTab("Settings", BuildSettingsContent(row, entry));
        if (keepSettingsOpen) _tabs.SwitchToTab("Settings");
    }

    /// <summary>
    /// The settings form: one labelled input per documented key with the catalog's own prose under
    /// it, a Save, and — while the plugin is active — the reason Save will refuse.
    ///
    /// <para>HAND-COMPOSED, NEVER <c>FormControl</c> — a generated form does not give the spacing
    /// and alignment this deserves, so the fields are laid out in a grid deliberately.</para>
    /// </summary>
    private static GridControl BuildSettingsContent(PluginRow row, CatalogEntry entry)
    {
        var fields = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        // ONE COLUMN, READ DOWNWARDS: name, what it is for, then the box you type in. Two equal
        // columns gave the description as much width as the editor and floated it top-right, so a
        // sentence sat level with a label while the field it explained was somewhere to the left —
        // the eye had to cross the dialog to pair them up.
        fields.ColumnDefinitions.Add(GridLength.Star(1, min: 24));

        var r = 0;
        // THE AUTHOR'S ORDER, NOT THE ALPHABET. A manifest lists its settings in the order they
        // matter — csharp-lsp declares `server` then `args`, because you choose a server and then
        // pass arguments TO it — and sorting by name reversed exactly that pair. Nobody reads a
        // settings form alphabetically; they read it as the order the author put things in.
        foreach (var (key, prose) in entry.Settings)
        {
            fields.RowDefinitions.Add(GridLength.Auto());   // name
            fields.RowDefinitions.Add(GridLength.Auto());   // what it is for
            fields.RowDefinitions.Add(GridLength.Auto());   // the field
            fields.RowDefinitions.Add(GridLength.Cells(1)); // a blank line before the next

            fields.Place(new MarkupControl([$"[bold]{MarkupParser.Escape(key)}[/]"]), r, 0);

            // TWO LINES, NOT THREE. These values are a command name or a short JSON array; a
            // taller box reads as a text area waiting for a paragraph and pushes the second field
            // off the screen. It still scrolls when a value is longer.
            var editor = new MultilineEditControl(viewportHeight: 2)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            editor.SetContent(CurrentText(row, key));
            fields.Place(editor, r + 2, 0);
            _settingsFields.Add((key, editor));

            // THE CATALOG'S OWN SENTENCE, under the name it explains. Where a key's type matters
            // the prose already says so — csharp-lsp's args entry reads "OmniSharp needs [\"-lsp\"]"
            // — so the form never has to teach JSON itself.
            fields.Place(
                new MarkupControl([$"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(prose)}[/]"])
                {
                    Wrap = true,
                },
                r + 1, 0);

            r += 4;
        }

        var scroll = new ScrollablePanelControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
        };
        scroll.AddControl(fields);

        // The fields scroll; the status line and the Save row do not — a form long enough to
        // scroll must not hide its own Save at the bottom of the scrollback.
        var wrapper = new GridControl { VerticalAlignment = VerticalAlignment.Fill };
        wrapper.ColumnDefinitions.Add(GridLength.Star(1));
        wrapper.RowDefinitions.Add(GridLength.Star(1));   // the fields, which scroll
        wrapper.RowDefinitions.Add(GridLength.Auto());     // the status line
        wrapper.RowDefinitions.Add(GridLength.Auto());     // Save
        wrapper.RowDefinitions.Add(GridLength.Auto());     // a rule opening the hint
        wrapper.RowDefinitions.Add(GridLength.Auto());     // why Save will refuse, when it will
        wrapper.Place(scroll, 0, 0);

        _settingsStatus = new MarkupControl([TakeSettingsNote()]) { Wrap = true };
        wrapper.Place(_settingsStatus, 1, 0);

        // WARNED ABOUT WHILE LOADED IN ANY SESSION, not just this one. SetPluginSettings checks
        // only its caller, so another session's copy would keep running on settings the file no
        // longer holds and nothing would reconcile the two — a plugin reads its settings once, at
        // Start. Core refuses the same-session case itself; this says so before the press.
        // The refusal replaces Save rather than sitting beside a button that cannot work, and it
        // names where the plugin is loaded so the user knows which session to unwire.
        // THE BUTTON IS ALWAYS THERE, and the hint says when it will refuse. Removing it left a
        // form whose fields invite typing and whose work cannot be committed — the user edits, then
        // looks for the button that was there a moment ago. A control that exists and explains
        // itself beats one that vanishes.
        //
        // SAFE BECAUSE CORE REFUSES ANYWAY: SetPluginSettings checks the calling session's loaded
        // set and returns Refused with its own reason (SessionManager.cs:556), so a press while
        // active reports that rather than writing something the running plugin would never read.
        if (SessionHolding(row.Name) is { } holder)
        {
            var where = ReferenceEquals(holder, _session)
                ? "this session" : $"another session ({holder.WorkingDirectory})";
            // THE BUTTON THAT IS ON SCREEN, not a command. Deactivate is in the action row below
            // this message and does exactly what is being asked for; naming `/plugin unwire`
            // instead sent the user to the composer for something a button beside them does — and
            // named it with the old vocabulary, which no longer appears anywhere in this dialog.
            //
            // ANOTHER SESSION IS THE EXCEPTION: no button here reaches it, so that case still has
            // to name the command and where to run it.
            // NO "BELOW": this hint sits under Save, while Deactivate is in the dialog's own
            // action row at the bottom. A direction that points at the wrong place is worse than
            // none, so the button is named and left to be found.
            var remedy = ReferenceEquals(holder, _session)
                ? "press Deactivate, then save."
                : "deactivate it there first.";

            // FENCED ABOVE AND BELOW, both rules in the hint's own grey — it is an aside about this
            // tab, and without the pair it reads as a caption belonging to whatever it touches.
            wrapper.Place(new RuleControl { Color = ColorScheme.MutedRgb }, 3, 0);

            wrapper.Place(new MarkupControl(
                [$"[{ColorScheme.MutedMarkup}]Active in {MarkupParser.Escape(where)}. A plugin reads "
                 + $"its settings once, when it starts — {remedy}[/]"])
            {
                Wrap = true,
            }, 4, 0);

        }

        // A TOOLBAR HERE TOO, so a second button added later is spaced like the action row rather
        // than flush against Save.
        var saveRow = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        saveRow.AddItem(Controls.Button("  Save  ").OnClick((_, _) => SaveSettings(row, entry)).Build());
        wrapper.Place(saveRow, 2, 0);

        // NO RULE AT THE FOOT. The dialog draws one above its action row, so a second here would
        // put two lines between the same two things.

        return wrapper;
    }

    /// <summary>The one session with this plugin loaded, or null. Any session counts — the refusal
    /// this feeds exists precisely because the mutator's own check stops at the caller.</summary>
    private static Session? SessionHolding(string name) =>
        _manager!.Sessions.FirstOrDefault(
            s => s.Plugins.LoadedPluginNames.Contains(name, StringComparer.Ordinal));

    /// <summary>The saved note, consumed. Rendered by the build that follows a save's rebuild —
    /// setting a control before the rebuild would set one about to be torn down.</summary>
    private static string TakeSettingsNote()
    {
        var note = _settingsNote;
        _settingsNote = null;
        return note is null ? "" : $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(note)}[/]";
    }

    /// <summary>
    /// A field's current text: a stored string shows bare, everything else as JSON — the mirror of
    /// <see cref="ValueOf"/>. Prefilled quotes would teach the user to quote, and a quoted edit
    /// round-trips to a string anyway; the one value this misreads is a stored string that itself
    /// parses as JSON ("true"), the same corner typing one has.
    /// </summary>
    private static string CurrentText(PluginRow row, string key)
    {
        if (row.Configured?.Settings is not { ValueKind: JsonValueKind.Object } settings
            || !settings.TryGetProperty(key, out var value))
            return "";

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }

    /// <summary>
    /// One field's text as the JSON value it means.
    ///
    /// <para>PARSED FIRST, QUOTED AS A FALLBACK. A settings block is handed to the plugin verbatim
    /// and cxagent has no schema for it, so the form cannot know a key's type — but quoting
    /// everything would turn csharp-lsp's `args` array into a string it cannot use, and refusing
    /// anything unparseable would make a bare word an error. Try JSON, fall back to a string.</para>
    /// </summary>
    private static JsonNode? ValueOf(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        try { return JsonNode.Parse(trimmed); }
        catch (JsonException) { return JsonValue.Create(trimmed); }
    }

    /// <summary>
    /// Applies the form through the same mutator every other door uses, then persists — mutate,
    /// then persist, the app's half of the config boundary. Unlike load and unwire, a settings save
    /// always changes a config entry, so syncing here cannot delete a hand-added one.
    /// </summary>
    private static void SaveSettings(PluginRow row, CatalogEntry entry)
    {
        var settings = new JsonObject();

        // KEYS THE CATALOG DOES NOT DOCUMENT SURVIVE. The form edits what it shows; a user who
        // hand-wrote an extra key into config.json must not lose it to a save that never displayed
        // it.
        if (row.Configured?.Settings is { ValueKind: JsonValueKind.Object } existing)
            foreach (var property in existing.EnumerateObject())
                if (!entry.Settings.ContainsKey(property.Name))
                    settings[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        foreach (var (key, editor) in _settingsFields)
            if (ValueOf(editor.GetContent()) is { } value)
                settings[key] = value;

        // An emptied form clears the block rather than writing {} — the mutator's null is "no
        // settings", which is what a user deleting every value means.
        PluginChangeResult result;
        if (settings.Count == 0)
            result = _manager!.SetPluginSettings(_session!, row.Name, null);
        else
        {
            // Disposing the document here is safe: SetPluginSettings clones before storing.
            using var document = JsonDocument.Parse(settings.ToJsonString());
            result = _manager!.SetPluginSettings(_session!, row.Name, document.RootElement);
        }

        if (result is PluginChangeResult.Refused refused)
        {
            // In place, not via the note: a refusal changes no state, so nothing rebuilds the tab
            // and the user's edits stay exactly as typed.
            _settingsStatus?.SetContent(
                [$"[yellow]{MarkupParser.Escape(refused.Reason)}[/]"]);
            return;
        }

        var failure = PluginConfigPersistence.TrySync(ConfigPath(), _manager!.Config.Plugins);

        _settingsNote = failure ?? "saved";
        Rebuild();
    }

    private static string ConfigPath() => Path.Combine(_paths!.ConfigDir, "config.json");

    /// <summary>
    /// Refills the sticky bottom row with the selected row's actions, then Close.
    ///
    /// <para>THE BUTTONS ACT ON THE SELECTED PLUGIN, so the row is rebuilt whenever the selection
    /// moves — which is why it is refilled in place rather than being a grid cell whose control is
    /// swapped: the sticky row is registered with the window once.</para>
    /// </summary>
    private static void RebuildButtons(PluginRow? row)
    {
        if (_buttons is null) return;

        // NO CLOSE APPENDED HERE. These act on the selected plugin; closing acts on the dialog, and
        // sharing a row gave them equal weight with no way to see the difference. Close lives in the
        // status bar, beside the key that also does it.
        _buttons.Clear();
        foreach (var button in RowButtons(row))
            _buttons.AddItem(button);
    }

    /// <summary>
    /// Which actions this row can honestly offer. A button that cannot work is worse than its
    /// absence — an Available row whose state says why it cannot install gets no install button,
    /// and a contract-mismatched row gets no button at all, just its explanation: the remedy is a
    /// newer build or a newer cxagent, and neither is in this dialog.
    /// </summary>
    private static IEnumerable<ButtonControl> RowButtons(PluginRow? row)
    {
        if (row is null) yield break;

        switch (row.Section)
        {
            case PluginRowSection.Available:
                // A STANDING NOTE REPLACES THE BUTTON: after a hash mismatch, neither the catalog
                // nor the release is trustworthy until someone finds out which, and re-offering
                // Install beside the evidence would invite retrying the exact same disagreement.
                if (row.State.Length == 0 && !HasNote(row))
                    yield return Controls.Button(" Install ")
                        .OnClick((_, _) => _ = InstallFlowAsync(row)).Build();
                break;

            // UPDATES IS A FACT ABOUT AN INSTALLED PLUGIN, NOT A KIND OF PLUGIN. A newer version
            // existing changes nothing about what the row can do — it is installed, it may be
            // active or not, configured or not — so this case adds Update and then falls into
            // Installed for the rest. Returning only Update stripped every other action from a
            // perfectly working plugin, which is how an update that could not run left the user
            // with no button that could.
            case PluginRowSection.Updates:
            case PluginRowSection.Installed:
                if (row.Section == PluginRowSection.Updates && !HasNote(row))
                    yield return Controls.Button(" Update ")
                        .OnClick((_, _) => _ = UpdateFlowAsync(row)).Build();

                // THE CALLING SESSION'S VIEW decides which of the two shows — the same view the
                // rail's state word renders, so the button never contradicts the row it sits under.
                //
                // WHETHER THE PLUGIN IS IN EFFECT, which is the fact that actually changes: its
                // tools are offered to the model, or they are not. "Unwire" is this loader's own
                // vocabulary and means nothing to a reader.
                //
                // NOT "UNLOAD", WHICH WOULD BE FALSE. The managed loader uses no
                // AssemblyLoadContext (PluginRegistry.EverLoadedNames), so the assembly stays
                // resident for the life of the process and its file stays held — a button promising
                // to unload is contradicted by the very next thing a user tries, Update, refused
                // because the file is still held. Activate says nothing about residency, so nothing
                // it claims can be falsified by that.
                yield return _session!.Plugins.LoadedPluginNames.Contains(row.Name, StringComparer.Ordinal)
                    ? Controls.Button(" Deactivate ")
                        .OnClick((_, _) => _ = RunAsync($"unwire {row.Name}",
                            $"'{row.Name}' deactivated for this session"))
                        .Build()
                    : Controls.Button(" Activate ")
                        .OnClick((_, _) => _ = LoadAsync(LoadArgument(row),
                            $"'{row.Name}' activated for this session"))
                        .Build();

                // TWO AXES, NAMED APART. Activate/Deactivate is THIS SESSION — is the plugin in
                // effect right now. These two are CONFIG — does it come back next time. Bare
                // "Enable"/"Disable" read as the first axis while controlling the second, so a row
                // showing "loaded" beside "Disable" implied the button would stop it; naming the
                // object — auto-load — puts each verb on its own axis.
                //
                // A VERB, NOT A STATE. "Auto load" describes a condition; a button says what
                // pressing it does, which is what every other label here already did.
                //
                // THE COMMAND VERBS ARE UNCHANGED. /plugin enable is Core's published surface and
                // people have typed it; only these labels move.
                if (row.Configured is { } config)
                    yield return config.Enabled
                        ? Controls.Button(" Disable auto-load ")
                            .OnClick((_, _) => _ = RunAsync($"disable {row.Name}",
                                $"'{row.Name}' will not load at start — config.json updated"))
                            .Build()
                        : Controls.Button(" Enable auto-load ")
                            .OnClick((_, _) => _ = RunAsync($"enable {row.Name}",
                                $"'{row.Name}' will load at start — config.json updated"))
                            .Build();

                // GAP A: A DISCOVERED PLUGIN HAD NO WAY TO BECOME CONFIGURED. Its row said "no auto
                // load" and offered nothing that changed it — the only route was the menu that
                // appears once, right after an install, and never again. AddToConfig already
                // existed; it was simply unreachable from the row it applies to.
                // ITS FILE, NOT ITS CATALOG ENTRY. A discovered plugin may be a local build the
                // catalog has never heard of, and gating this on a catalog entry would leave
                // exactly those rows — the ones most likely to be someone's own work — with no way
                // to be configured. FileOf resolves the entry point for a configured row from
                // config and for a discovered one from the sidecar the gather already read.
                // "ADD TO CONFIG", NOT "ENABLE AUTO-LOAD". The other button flips a flag on an
                // entry that exists; this one CREATES the entry, and the row beside it says "not in
                // config" — so the label answers what the row reports rather than describing a
                // setting there is nothing yet to set. The effect is the same, and auto-load is
                // what an entry means, but a user reading "enable" for a plugin config has never
                // heard of is being told a state changed rather than a line was written.
                if (row.Configured is null && FileOf(row) is { } discoveredFile)
                    yield return Controls.Button(" Add to config ")
                        .OnClick((_, _) => AddToConfig(row.Name, discoveredFile))
                        .Build();

                if (row.Folder is not null)
                    yield return Controls.Button(" Uninstall ")
                        .OnClick((_, _) => _ = UninstallFlowAsync(row)).Build();
                break;

            case PluginRowSection.NeedsAttention:
                // GAP B: A CONTRACT MISMATCH LEFT THE ROW WITH NOTHING AT ALL. Uninstall was
                // withheld because "the files are fine" — true, and beside the point: a plugin this
                // host can never run is one a user may well want gone, and offering no action makes
                // the dialog a place where a problem is displayed and cannot be acted on. The files
                // being intact is a reason not to uninstall BY DEFAULT, not a reason to refuse.
                //
                // It is still last among the buttons, so it is not the first thing a hand lands on.
                if (row.Folder is not null || row.Configured is not null)
                    yield return Controls.Button(" Uninstall ")
                        .OnClick((_, _) => _ = UninstallFlowAsync(row)).Build();
                break;
        }
    }

    private static bool HasNote(PluginRow row) => _panelNote is { } note && note.Name == row.Name;

    /// <summary>
    /// How long a refusal may be before it goes to the panel instead of a toast.
    ///
    /// <para>A TOAST IS ONE LINE AND IT TRUNCATES. Sixty characters is what fits comfortably at the
    /// narrowest terminal this dialog supports; past that the tail is cut, and a refusal's tail is
    /// its reason. The threshold is deliberately conservative — a reason shown in full in the panel
    /// costs a glance, a reason cut in half costs a debugging session.</para>
    /// </summary>
    private const int ToastableReasonLength = 60;

    private static void Toast(string text, NotificationSeverity severity) =>
        _ws!.ToastService.Show(text, severity);

    /// <summary>
    /// Runs one plugin command and shows what it did.
    ///
    /// <para>AWAITED, NOT DISPATCHED. The registry's /plugin handler is fire-and-forget, so a
    /// caller that went through it would learn nothing about the outcome — and this dialog's whole
    /// feedback model is reading what happened rather than assuming it.</para>
    ///
    /// <para>RE-READ, NEVER PREDICTED. The rail is rebuilt from Core's live config and from disk
    /// after every action, so a row that could not change simply does not.</para>
    /// </summary>
    /// <param name="did">The success toast — what the dialog did that the row cannot show, e.g.
    /// that config.json was written. Shown only when the command reports it changed something.</param>
    private static async Task RunAsync(string argument, string? did = null)
    {
        var status = await _session!.RunPluginCommand(argument, CancellationToken.None);

        // ONLY THE VERBS THAT CHANGE AN ENTRY. `Changed` alone is not that test: load and unwire
        // return it too while altering no config, and syncing after them DELETES an entry a user
        // hand-added to the file mid-session — it is in the file, not in the live set, and Sync's
        // diff removes what memory does not have. AppBootstrap.PersistAfterPluginChange draws
        // exactly this line; the same guard belongs here.
        var changesAnEntry = PluginCommand.Parse(argument)
            is PluginRequest.Enable or PluginRequest.Disable;

        var failure = status is CommandStatus.Changed && changesAnEntry
            ? PluginConfigPersistence.TrySync(ConfigPath(), _manager!.Config.Plugins)
            : null;

        // MARSHALLED: RunPluginCommand's continuation resumes wherever its awaits left it, and
        // everything below mutates controls the render loop is painting.
        _ws!.EnqueueOnUIThread(() =>
        {
            if (failure is not null) Toast(failure, NotificationSeverity.Warning);
            else if (status is CommandStatus.Changed && did is not null)
                Toast(did, NotificationSeverity.Success);
            else if (status is CommandStatus.Refused)
                // The one failure whose meaning the status alone carries. Other non-changes said
                // WHY to the transcript, which the user reads after closing; this one would look
                // like a dead button while a turn runs.
                Toast("refused — a turn is running", NotificationSeverity.Warning);

            if (_window is not null) Rebuild();
        });
    }

    /// <summary>
    /// Runs a load with the manager out of the way.
    ///
    /// <para>THE LOAD GATE'S PROMPT RENDERS IN THE MAIN WINDOW'S COMPOSER
    /// (<see cref="WindowPermissionPrompt"/>), and this window is modal: left open, the manager
    /// would sit on top of the approval question its own button raised, with the main window
    /// unactivatable beneath it. Closing first puts the question in front of the user; reopening
    /// after shows the outcome on the rail. Closed even for a re-load a stored rule would wave
    /// through — whether a prompt will appear is the gate's decision, made only after this method
    /// has had to choose.</para>
    /// </summary>
    private static async Task LoadAsync(string argument, string did)
    {
        CloseForPrompt();
        await RunAsync(argument, did);
        _ws!.EnqueueOnUIThread(Reopen);
    }

    /// <summary>What /plugin load takes for this row: a configured NAME resolves through config,
    /// but an unconfigured plugin's name means nothing to the resolver — its bare FILENAME is what
    /// the search folders are scanned for.</summary>
    /// <summary>
    /// What to send to <c>/plugin load</c> for this row.
    ///
    /// <para><c>--once</c> WHEN CONFIG SAYS NO. Core refuses to load a plugin whose entry is
    /// <c>enabled: false</c> unless the flag is given, and that is right for a typed command: the
    /// user should have to say they mean it. Here the button is Activate — a this-session action
    /// sitting beside its own Enable auto-load — so pressing it IS saying they mean it, and
    /// withholding the flag turned a deliberate press into an error that named a flag no button
    /// mentions.</para>
    ///
    /// <para>IT DOES NOT WRITE CONFIG. Activating a disabled plugin leaves it disabled, so it is
    /// gone again next start — which is what "for this session" means and what the other button is
    /// there to change.</para>
    /// </summary>
    private static string LoadArgument(PluginRow row)
    {
        var once = row.Configured is { Enabled: false } ? " --once" : "";

        return row.Configured is not null
            ? $"load {row.Name}{once}"
            : $"load {FileOf(row) ?? row.Name}";
    }

    /// <summary>The entry-point filename this row resolves to — config's word for a configured
    /// row, the sidecar's for one the gather found on disk.</summary>
    private static string? FileOf(PluginRow row) =>
        row.Configured?.File ?? _inputs?.Unconfigured.FirstOrDefault(u => u.Name == row.Name)?.File;

    /// <summary>Closes the manager so the main window is reachable while a permission prompt
    /// stands. On the UI thread only.</summary>
    private static void CloseForPrompt()
    {
        if (_window is null) return;
        _window.Close();
        _window = null;
    }

    /// <summary>Reopens after a prompt round-trip — the user asked the manager for this action, so
    /// it comes back to show the outcome rather than leaving them at the main window.</summary>
    private static void Reopen()
    {
        // Already up: the user pressed F2 themselves while the prompt stood, and Show's own guard
        // makes a second open a focus change.
        if (_window is not null) return;
        Show(_ws!, _parent!, _manager!, _session!, _paths!, _projectDirectory!);
    }

    /// <summary>
    /// The install conversation: where it goes, then the download permission, then the verified
    /// install — and afterwards three separate offers.
    ///
    /// <para>TWO PROMPTS, NOT ONE. A PermissionRequest renders as allow/deny and has no slot for a
    /// folder choice, so the destination is its own question, asked first — a user who picks the
    /// wrong folder should find out before granting a download, not after.</para>
    ///
    /// <para>AFTER IT LANDS, THREE SEPARATE CHOICES — never one button that installs, configures
    /// and loads. Writing config before the user saw the trust prompt would promote "the files
    /// arrived" into "run this at every start" without the approval step between.</para>
    /// </summary>
    private static async Task InstallFlowAsync(PluginRow row)
    {
        if (row.Catalog is not { } entry) return;

        var destinations = InstallDestinations();
        var label = await FlowDialogs.ChooseAsync(_ws!, _window,
            $"Install '{entry.Name}' {entry.Version} where?",
            destinations.Select(d => d.Label).ToList(), CancellationToken.None);
        if (label is null) return;
        var folder = destinations.First(d => d.Label == label).Folder;

        if (!await AskDownloadAsync(entry)) return;

        var result = await Installer.InstallAsync(entry, folder, CancellationToken.None);

        if (result is InstallResult.Installed(var directory, var files))
        {
            _ws!.EnqueueOnUIThread(Rebuild);

            // "done" is this menu's own terminal choice — a synthetic Cancel beside it would offer
            // two names for the same outcome.
            var next = await FlowDialogs.ChooseAsync(_ws, _window,
                $"'{entry.Name}' installed — {files.Count} file(s) in {directory}\n"
              + "Load it now (asks for approval first), add it to config.json so it loads at every "
              + "start, or neither.",
                ["load now", "add to config", "done"], CancellationToken.None, appendCancel: false);

            if (next == "load now")
                await LoadAsync($"load {entry.File}", $"'{entry.Name}' loaded");
            else if (next == "add to config")
                _ws.EnqueueOnUIThread(() => AddToConfig(entry));
            return;
        }

        ShowInstallFailure(entry, result);
    }

    /// <summary>
    /// Updates in place: the download permission, then an install into the folder that already
    /// holds it — the destination is not a question, because it already has an answer.
    ///
    /// <para>THE SAME HELD-FILE REFUSAL AS UNINSTALL, for the same reason: the installer deletes
    /// the old directory before moving the new one in, and a file this process ever loaded is
    /// locked on Windows until it exits.</para>
    /// </summary>
    private static async Task UpdateFlowAsync(PluginRow row)
    {
        if (row.Catalog is not { } entry) return;
        if (RefuseIfHeld(row.Name, "update")) return;
        if (HomeOf(row) is not { } home) return;

        // Nested installs update in place: InstallAsync writes pluginsFolder/name, so the parent
        // of the plugin's own directory is what it wants. A LOOSE copy's home is a search folder
        // itself, and the installer's own shadow check refuses that with the remedy.
        var folder = _searchFolders.Any(f => SamePath(f, home))
            ? home
            : Path.GetDirectoryName(home)!;

        if (!await AskDownloadAsync(entry)) return;

        var result = await Installer.InstallAsync(entry, folder, CancellationToken.None);

        if (result is InstallResult.Installed)
        {
            _ws!.EnqueueOnUIThread(() =>
            {
                Toast($"'{entry.Name}' updated to {entry.Version}", NotificationSeverity.Success);
                Rebuild();
            });
            return;
        }

        ShowInstallFailure(entry, result);
    }

    /// <summary>
    /// Asks for the download, with the manager out of the way while the question stands (see
    /// <see cref="LoadAsync"/> for why a modal manager and a composer-hosted prompt cannot
    /// coexist).
    ///
    /// <para>AlwaysRule: null — NO "ALWAYS" BUTTON. A standing rule for a plugin download has no
    /// honest scope: a host generalises to "any download from github.com", pre-approving every
    /// future plugin, and the full URL is useless because the next version has a different
    /// one.</para>
    /// </summary>
    private static async Task<bool> AskDownloadAsync(CatalogEntry entry)
    {
        if (_session!.Services?.Gate is not { } gate)
        {
            // Reachable only for an embedder that wired no gate; saying so beats hanging on a
            // question nothing can answer.
            _ws!.EnqueueOnUIThread(() =>
                Toast("no permission gate is wired, so nothing can be downloaded",
                    NotificationSeverity.Warning));
            return false;
        }

        _ws!.EnqueueOnUIThread(CloseForPrompt);
        // STAMPED WITH THE SESSION'S POLICY, as the plugin load gate does for the same reason
        // (Session.RequestPluginLoad). The gate holds no session of its own, so a request arriving
        // without a policy has no root to check against and no edit mode to read: the decider
        // refuses it outright rather than judge it by another session's rules. Every path that
        // reaches the gate through the tool executor is stamped there; this one calls the gate
        // directly and so must stamp itself, or the download question is refused before it is ever
        // put to the user.
        var outcome = await gate.RequestAsync(
            new PermissionRequest(PermissionKind.Http,
                $"download '{entry.Name}' {entry.Version} from {entry.DownloadUrl}",
                AlwaysRule: null)
            { Policy = _session.Policy },
            CancellationToken.None);
        _ws.EnqueueOnUIThread(Reopen);

        return outcome.Allowed;
    }

    /// <summary>
    /// A refused or mismatched install, said in the right register. A refusal is a toast — the
    /// situation is over. A HASH MISMATCH IS A STANDING PANEL NOTE: the catalog and the release
    /// disagree, neither is trustworthy until someone finds out which, and six seconds under-sells
    /// that. Nothing was written either way — the installer verifies before touching disk.
    /// </summary>
    private static void ShowInstallFailure(CatalogEntry entry, InstallResult result) =>
        _ws!.EnqueueOnUIThread(() =>
        {
            switch (result)
            {
                // A SHORT REASON TOASTS; A LONG ONE GETS THE PANEL. A toast is one line of fixed
                // width, so a refusal carrying an exception's own words is cut off mid-sentence —
                // and the cut-off half is the half that says why. Losing it turns a diagnosable
                // failure into "something invalid happened", which is unactionable at exactly the
                // moment the user most needs to act.
                case InstallResult.Refused(var reason) when reason.Length <= ToastableReasonLength:
                    Toast(reason, NotificationSeverity.Warning);
                    break;

                case InstallResult.Refused(var reason):
                    _panelNote = (entry.Name, new[]
                    {
                        $"not installed — {entry.Name} could not be installed:",
                        "",
                        reason,
                        "",
                        "Nothing was written.",
                    });
                    break;

                case InstallResult.HashMismatch(var expected, var actual):
                    _panelNote = (entry.Name, new[]
                    {
                        "not installed — the catalog and the release disagree about this file:",
                        $"  catalog sha256:  {expected}",
                        $"  download sha256: {actual}",
                        "",
                        "Nothing was written. Neither is trustworthy until one of them is fixed.",
                    });
                    break;
            }

            Rebuild();
        });

    /// <summary>
    /// Where an install may go: the two built-in folders plus every configured pluginPaths entry.
    ///
    /// <para>READ OFF <see cref="_searchFolders"/> RATHER THAN RECOMPUTED — its last two entries
    /// ARE the built-in project and global folders (PluginDiscovery.SearchFolders appends them in
    /// that order), so this list cannot disagree with where a later load will look.</para>
    /// </summary>
    private static IReadOnlyList<(string Label, string Folder)> InstallDestinations()
    {
        var list = new List<(string, string)>
        {
            ($"global — {_searchFolders[^1]}", _searchFolders[^1]),
            ($"project — {_searchFolders[^2]}", _searchFolders[^2]),
        };

        for (var i = 0; i < _searchFolders.Count - 2; i++)
            list.Add(($"pluginPaths — {_searchFolders[i]}", _searchFolders[i]));

        return list;
    }

    /// <summary>
    /// Uninstalls what can honestly be removed: the held-file refusal first, then the remover's
    /// own plan, shown before anything is deleted — "uninstall csharp-lsp?" gives the user nothing
    /// to check, so the confirmation carries the paths.
    /// </summary>
    private static async Task UninstallFlowAsync(PluginRow row)
    {
        if (RefuseIfHeld(row.Name, "uninstall")) return;

        var home = HomeOf(row);
        var file = FileOf(row);

        // A config entry whose file is gone has nothing on disk to plan over — removing the entry
        // IS the uninstall (NeedsAttention's "file missing" row).
        if (home is null || file is null)
        {
            if (row.Configured is null) return;

            var sure = await FlowDialogs.ChooseAsync(_ws!, _window,
                $"'{row.Name}': no file resolves for it — remove the config entry?",
                ["Remove entry"], CancellationToken.None);
            if (sure is null) return;

            _ws!.EnqueueOnUIThread(() =>
            {
                if (RemoveEntry(row.Name))
                    Toast($"'{row.Name}' removed from config.json", NotificationSeverity.Success);
                Rebuild();
            });
            return;
        }

        var plan = PluginRemover.Plan(row.Name, file, home, _searchFolders, _manager!.Config.Plugins);

        if (plan is RemovalPlan.Blocked(var reason))
        {
            // A standing fact about this row — another entry still names the file, or the path
            // escapes every known folder — so it belongs in the panel, like the hash mismatch.
            _ws!.EnqueueOnUIThread(() =>
            {
                _panelNote = (row.Name, [reason]);
                Rebuild();
            });
            return;
        }

        var removable = (RemovalPlan.Removable)plan;
        var confirmed = await FlowDialogs.ChooseAsync(_ws!, _window,
            ConfirmationText(removable), ["Remove"], CancellationToken.None);
        if (confirmed is null) return;

        var failed = PluginRemover.Remove(removable);

        _ws!.EnqueueOnUIThread(() =>
        {
            // The files went; a config entry naming nothing would come back as NeedsAttention at
            // the next start, which is not what "uninstall" means.
            var entryRemoved = row.Configured is null || RemoveEntry(row.Name);

            if (failed.Count > 0)
                Toast($"'{row.Name}': {failed.Count} file(s) could not be deleted",
                    NotificationSeverity.Warning);
            else if (entryRemoved)
                Toast($"'{row.Name}' uninstalled", NotificationSeverity.Success);

            Rebuild();
        });
    }

    /// <summary>
    /// The held-file refusal, and it spans EVERY session: the assembly loads into the default
    /// context (no AssemblyLoadContext), so whichever session loaded it, this PROCESS holds the
    /// file. <c>EverLoadedNames</c> rather than the loaded list — load → unwire → uninstall would
    /// pass a present-tense check and then half-fail on Windows, where the file stays locked for
    /// the process's life.
    ///
    /// <para>THE SAME REFUSAL ON EVERY PLATFORM, though the delete would succeed on Linux and
    /// macOS. One behaviour is testable; a platform-conditional path means the branch protecting
    /// Windows users is the one CI never exercises.</para>
    /// </summary>
    /// <returns>Whether it refused — and said so in the panel, where the remedy outlives a toast.</returns>
    private static bool RefuseIfHeld(string name, string verb)
    {
        var holder = _manager!.Sessions.FirstOrDefault(
            s => s.Plugins.EverLoadedNames.Contains(name));
        if (holder is null) return false;

        var where = ReferenceEquals(holder, _session)
            ? "this session"
            : $"another session ({holder.WorkingDirectory})";

        // A RESTART ALONE IS NOT THE REMEDY WHEN IT AUTO-LOADS. An enabled plugin is loaded again
        // during startup, so it is held before the dialog can be opened — "restart, then update"
        // sends the user round a loop that cannot terminate. Disabling first is what breaks it, and
        // the note has to say so or the advice is wrong for exactly the plugins most likely to hit
        // it: the ones someone uses enough to have configured.
        var autoLoads = _manager.Config.Plugins.TryGetValue(name, out var cfg) && cfg.Enabled;

        _ws!.EnqueueOnUIThread(() =>
        {
            _panelNote = (name, autoLoads
                ? new[]
                {
                    $"'{name}' was loaded in {where}, so its file is held until cxagent exits.",
                    "",
                    "config.json auto-loads it, so restarting loads it again and holds it again.",
                    $"Disable it here, restart cxagent, {verb}, then enable it.",
                }
                : new[]
                {
                    $"'{name}' was loaded in {where}, so its file is held until cxagent exits.",
                    "",
                    $"Restart cxagent, then {verb}.",
                });
            Rebuild();
        });
        return true;
    }

    /// <summary>The confirmation carries what the plan resolved: each deletion named — a long tail
    /// collapsed to a count, recoverable from the directory the first line already names — and what
    /// stays behind said plainly.</summary>
    private static string ConfirmationText(RemovalPlan.Removable plan)
    {
        var lines = new List<string> { plan.Description };

        lines.AddRange(plan.Files.Take(8).Select(f => $"  {f}"));
        if (plan.Files.Count > 8) lines.Add($"  … and {plan.Files.Count - 8} more");

        if (plan.LeftBehind.Count > 0)
        {
            lines.Add("");
            lines.Add("left behind, because nothing attributes them to this plugin:");
            lines.AddRange(plan.LeftBehind.Take(4).Select(f => $"  {f}"));
            if (plan.LeftBehind.Count > 4) lines.Add($"  … and {plan.LeftBehind.Count - 4} more");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Removes a config entry and persists — mutate then persist, the app's half of the
    /// config boundary. RemovePlugin changes an entry, so syncing here cannot delete a hand-added
    /// one (the guard load and unwire need). Returns whether the entry went.</summary>
    private static bool RemoveEntry(string name)
    {
        if (_manager!.RemovePlugin(_session!, name) is PluginChangeResult.Refused refused)
        {
            Toast(refused.Reason, NotificationSeverity.Warning);
            return false;
        }

        if (PluginConfigPersistence.TrySync(ConfigPath(), _manager.Config.Plugins) is { } failure)
            Toast(failure, NotificationSeverity.Warning);
        return true;
    }

    /// <summary>The "[add to config]" offer after an install: the entry, enabled, with no settings
    /// — the Settings tab exists for those once the row is configured.</summary>
    private static void AddToConfig(CatalogEntry entry) => AddToConfig(entry.Name, entry.File);

    /// <summary>
    /// Writes a config entry so this plugin loads at every start.
    ///
    /// <para>NAME AND FILE, NOT A CATALOG ENTRY, so a plugin found on disk that the catalog never
    /// listed — a local build — can be configured like any other. The install menu still passes an
    /// entry; that overload just unpacks it.</para>
    /// </summary>
    private static void AddToConfig(string name, string file)
    {
        if (_manager!.AddPlugin(_session!, name, new PluginConfig(file))
            is PluginChangeResult.Refused refused)
        {
            Toast(refused.Reason, NotificationSeverity.Warning);
            Rebuild();
            return;
        }

        var failure = PluginConfigPersistence.TrySync(ConfigPath(), _manager.Config.Plugins);
        Toast(failure ?? $"'{name}' added to config.json — it loads at every start",
            failure is null ? NotificationSeverity.Success : NotificationSeverity.Warning);
        Rebuild();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.Ordinal);
}
