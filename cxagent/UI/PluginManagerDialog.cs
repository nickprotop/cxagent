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
    private static MarkupControl? _detail;
    private static MarkupControl? _age;
    private static PromptControl? _filter;
    private static HorizontalGridControl? _buttons;
    private static int _lastRailIndex;

    private static IReadOnlyList<PluginRow> _rows = [];

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

        _detail = new MarkupControl([""]) { Wrap = true };
        grid.Place(_detail, 0, 1);

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
        var searchFolders = PluginDiscovery.SearchFolders(
            _manager!.Config.PluginPaths, projectDirectory, _paths!.ConfigDir);

        _rows = PluginManagerRows.Build(PluginManagerState.Gather(
            _manager.Config.Plugins, _session!.Plugins.LoadedPluginNames, searchFolders,
            _catalog.Plugins, projectDirectory));

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
        // mid-refill.
        if (index < 0)
        {
            ShowDetail(null);
            return;
        }

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

    /// <summary>The right-hand panel: the selected row, said plainly.</summary>
    private static void ShowDetail(PluginRow? row)
    {
        if (_detail is null) return;

        if (row is null)
        {
            _detail.SetContent([""]);
            return;
        }

        var lines = new List<string>
        {
            $"[bold]{MarkupParser.Escape(row.Catalog?.DisplayName ?? row.Name)}[/]",
            "",
            $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(row.State)}[/]",
        };

        if (row.Folder is { } folder)
            lines.Add($"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(folder)}[/]");

        if (row.Detail is { } detail)
        {
            lines.Add("");
            lines.Add(MarkupParser.Escape(detail));
        }

        _detail.SetContent(lines);
    }
}
