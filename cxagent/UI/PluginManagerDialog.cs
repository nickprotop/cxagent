using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
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

    private static ListControl? _rail;
    private static TabControl? _tabs;
    private static MarkupControl? _detailHeader;
    private static MarkupControl? _detailBody;
    private static MarkupControl? _age;
    private static PromptControl? _filter;
    private static HorizontalGridControl? _buttons;
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
        //  [ rail          ][ detail                  ]   the grid: two columns, one row
        //  [ [close]                                  ]   StickyPosition.Bottom
        var toolbar = BuildToolbar();
        toolbar.StickyPosition = StickyPosition.Top;
        window.AddControl(toolbar);

        // TWO COLUMNS, ONE ROW. The rail is a fixed width with a floor so it stays readable when the
        // window is dragged narrow; the panel takes what is left.
        var grid = new GridControl { VerticalAlignment = VerticalAlignment.Fill };
        grid.ColumnDefinitions.Add(GridLength.Cells(34, min: 24));
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        grid.RowDefinitions.Add(GridLength.Star(1));

        _rail = new ListControl
        {
            VerticalAlignment = VerticalAlignment.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _rail.SelectedIndexChanged += OnRailSelectionChanged;
        grid.Place(_rail, 0, 0);

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
        grid.Place(_tabs, 0, 1);

        // WHERE THE USER LEFT IT. Window.Left/Top are settable (Window.cs:797, :929), so a manager
        // dragged aside or resized reopens where it was rather than jumping back to centre — the
        // dialog is opened repeatedly in one session, and a window that will not stay put is one the
        // user has to move every time.
        if (_placement is { } seen) { window.Left = seen.Left; window.Top = seen.Top; }

        window.AddControl(grid);

        // THE BUTTONS ACT ON THE SELECTED PLUGIN, so they are rebuilt whenever the selection moves —
        // which is why they are their own control rather than a grid cell whose contents change.
        _buttons = HorizontalGridControl.ButtonRow(
            Controls.Button("  Close  ").OnClick((_, _) => CloseIfOpen()).Build());
        _buttons.StickyPosition = StickyPosition.Bottom;
        window.AddControl(_buttons);

        // F5 STAYS ON THE WINDOW: nothing global claims it, unlike Escape and F2, which are
        // consulted at the application level before this window ever sees a key
        // (InputCoordinator.cs:130-134) and so are handled there.
        window.KeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.F5) return;
            Refresh();
            e.Handled = true;
        };

        window.OnClosed += (_, _) =>
        {
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
        Rebuild();
        _ = FetchCatalogAsync();
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
        var reader = new CatalogReader(null, Path.Combine(_paths!.ConfigDir, "catalog-cache.json"));
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

        var keep = _rail.SelectedIndex >= 0 && _rail.SelectedIndex < _rail.Items.Count
            ? (_rail.Items[_rail.SelectedIndex].Tag as PluginRow)?.Name
            : null;

        FillRail(_rail, PluginManagerRows.Filter(_rows, _filter?.Input));

        _lastRailIndex = 0;
        var index = _rail.Items.FindIndex(i => i.Tag is PluginRow row && row.Name == keep);
        if (index < 0) index = _rail.Items.FindIndex(i => i.Tag is not null);

        if (index >= 0) _rail.SelectedIndex = index;
        else ShowDetail(null);
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
                rail.AddItem(new ListItem(RailText(row)) { Tag = row });
        }
    }

    /// <summary>
    /// A row's rail text: the name, and the state word under it.
    ///
    /// <para>TWO LINES, NOT TWO COLUMNS. ListItem has no second column, and at the rail's 34 cells
    /// the longest states ("contract 3 · needs a newer cxagent", "loaded · 0.2.1 → 0.3.0") cannot
    /// share a line with any name without truncating the half the user came to read.</para>
    /// </summary>
    private static string RailText(PluginRow row)
    {
        var name = MarkupParser.Escape(row.Name);
        return row.State.Length == 0
            ? name
            : $"{name}\n  [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(row.State)}[/]";
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
        if (row.Configured is { } config)
            parts.Add(config.Enabled ? "enabled in config" : "disabled in config");

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
    /// The settings form: one labelled input per documented key with the catalog's own prose beside
    /// it, and a Save — or, when the plugin is loaded anywhere, the reason there is no Save.
    ///
    /// <para>HAND-COMPOSED, NEVER <c>FormControl</c> — a generated form does not give the spacing
    /// and alignment this deserves, so the fields are laid out in a grid deliberately.</para>
    /// </summary>
    private static GridControl BuildSettingsContent(PluginRow row, CatalogEntry entry)
    {
        var fields = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        fields.ColumnDefinitions.Add(GridLength.Star(1, min: 20));
        fields.ColumnDefinitions.Add(GridLength.Star(1, min: 16));

        var r = 0;
        foreach (var (key, prose) in entry.Settings.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            fields.RowDefinitions.Add(GridLength.Auto());
            fields.RowDefinitions.Add(GridLength.Auto());
            fields.RowDefinitions.Add(GridLength.Cells(1));

            fields.Place(new MarkupControl([$"[bold]{MarkupParser.Escape(key)}[/]"]), r, 0);

            var editor = new MultilineEditControl(viewportHeight: 3)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            editor.SetContent(CurrentText(row, key));
            fields.Place(editor, r + 1, 0);
            _settingsFields.Add((key, editor));

            // THE CATALOG'S OWN SENTENCE, beside the field it explains. Where a key's type matters
            // the prose already says so — csharp-lsp's args entry reads "OmniSharp needs [\"-lsp\"]"
            // — so the form never has to teach JSON itself.
            fields.Place(
                new MarkupControl([$"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(prose)}[/]"])
                {
                    Wrap = true,
                },
                r, 1, rowSpan: 2);

            r += 3;
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
        wrapper.RowDefinitions.Add(GridLength.Star(1));
        wrapper.RowDefinitions.Add(GridLength.Auto());
        wrapper.RowDefinitions.Add(GridLength.Auto());
        wrapper.Place(scroll, 0, 0);

        _settingsStatus = new MarkupControl([TakeSettingsNote()]) { Wrap = true };
        wrapper.Place(_settingsStatus, 1, 0);

        // REFUSED WHILE LOADED IN ANY SESSION, not just this one. SetPluginSettings checks only its
        // caller, so another session's copy would keep running on settings the file no longer holds
        // and nothing would ever reconcile the two — a plugin reads its settings once, at Start.
        // The refusal replaces Save rather than sitting beside a button that cannot work, and it
        // names where the plugin is loaded so the user knows which session to unwire.
        if (SessionHolding(row.Name) is { } holder)
        {
            var where = ReferenceEquals(holder, _session)
                ? "this session" : $"another session ({holder.WorkingDirectory})";
            wrapper.Place(new MarkupControl(
                [$"[{ColorScheme.MutedMarkup}]loaded in {MarkupParser.Escape(where)} — a plugin "
                 + $"reads its settings at start, so `/plugin unwire {MarkupParser.Escape(row.Name)}` "
                 + "there first, then change them.[/]"])
            {
                Wrap = true,
            }, 2, 0);
        }
        else
        {
            wrapper.Place(HorizontalGridControl.ButtonRow(
                Controls.Button("  Save  ").OnClick((_, _) => SaveSettings(row, entry)).Build()), 2, 0);
        }

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

        var failure = PluginConfigPersistence.TrySync(
            Path.Combine(_paths!.ConfigDir, "config.json"), _manager!.Config.Plugins);

        _settingsNote = failure ?? "saved";
        Rebuild();
    }

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

        _buttons.ClearColumns();
        foreach (var button in RowButtons(row)
                     .Append(Controls.Button("  Close  ").OnClick((_, _) => CloseIfOpen()).Build()))
        {
            var column = new ColumnContainer(_buttons);
            column.AddContent(button);
            _buttons.AddColumn(column);
        }
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
                if (row.State.Length == 0)
                    yield return Controls.Button(" Install ").Build();
                break;

            case PluginRowSection.Updates:
                yield return Controls.Button(" Update ").Build();
                break;

            case PluginRowSection.Installed:
                // THE CALLING SESSION'S VIEW decides load/unwire — the same view the rail's state
                // word renders, so the button never contradicts the row it sits under.
                yield return _session!.Plugins.LoadedPluginNames.Contains(row.Name, StringComparer.Ordinal)
                    ? Controls.Button(" Unwire ").Build()
                    : Controls.Button(" Load ").Build();

                if (row.Configured is { } config)
                    yield return config.Enabled
                        ? Controls.Button(" Disable ").Build()
                        : Controls.Button(" Enable ").Build();

                if (row.Folder is not null)
                    yield return Controls.Button(" Uninstall ").Build();
                break;

            case PluginRowSection.NeedsAttention:
                // Uninstall applies to a broken install — files on disk, or a config entry whose
                // file is gone. It does not apply to a contract mismatch, where the files are fine.
                if (!row.State.StartsWith("contract ", StringComparison.Ordinal)
                    && (row.Folder is not null || row.Configured is not null))
                    yield return Controls.Button(" Uninstall ").Build();
                break;
        }
    }
}
