using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// The MCP servers tab: every configured server with what it is doing now, the selected one's command or
/// URL beside it, and a Save that restarts what changed.
///
/// <para>THE SAME SHELL AS PROVIDERS — rail, hand-placed fields, Save and Revert pinned — so the two tabs
/// read as one dialog.</para>
///
/// <para>A TOOL LIST CHANGE REACHES EVERY TAB AT ONCE. MCP servers are one per process and their tools are
/// shared, so a save that starts, stops or changes a server changes every open tab's tools — and with
/// them each tab's prompt cache. Save says so and offers "Save only", which writes the file and leaves the
/// running servers for <c>/mcp reload</c> or the next start.</para>
/// </summary>
public sealed class McpTab
{
    private const int RailCells = 30;

    private readonly ConfigDialogHost _host;
    private readonly Func<Window?> _window;
    private readonly GridControl _root;
    private readonly ListControl _rail;
    private readonly ScrollablePanelControl _detail;
    private readonly MarkupControl _status;
    private readonly ButtonControl _save;
    private readonly ButtonControl _revert;

    private IReadOnlyDictionary<string, McpServerConfig>? _servers;
    private McpDraft? _draft;
    private bool _saving;
    private IFocusableControl? _firstField;
    private int _lastIndex = -1;

    public McpTab(ConfigDialogHost host, Func<Window?> window)
    {
        _host = host;
        _window = window;

        _root = new GridControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _root.ColumnDefinitions.Add(GridLength.Cells(RailCells, min: 22));
        _root.ColumnDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Cells(1));   // the toolbar
        _root.RowDefinitions.Add(GridLength.Cells(1));   // a rule closing the toolbar off from the list
        _root.RowDefinitions.Add(GridLength.Star(1));    // rail | detail
        _root.RowDefinitions.Add(GridLength.Cells(2));   // status — two lines, so a long one is not cut

        // ONLY WHAT DOES NOT ACT ON THE SELECTION. The per-server actions sit in the detail's header,
        // beside the server they act on.
        var toolbar = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        toolbar.AddItem(Controls.Button(" + Add ").OnClick((_, _) => ShowKinds()).Build());
        _root.Place(toolbar, 0, 0, colSpan: 2);
        _root.Place(DialogLook.Rule(), 1, 0, colSpan: 2);

        _rail = new ListControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _rail.SelectedIndexChanged += OnRailSelectionChanged;
        ListDetail.StyleRail(_rail);
        _root.Place(_rail, 2, 0, rowSpan: 2);
        ListDetail.Divider(_root);

        _detail = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill, Margin = ListDetail.DetailInset };
        _root.Place(_detail, 2, 1);
        _status = new MarkupControl([""]) { Wrap = true, Margin = ListDetail.DetailInset };
        _root.Place(_status, 3, 1);

        // REVERT AND SAVE BELONG TO THE TAB, SHOWN IN THE DIALOG'S FOOTER while this tab is in front.
        _revert = Controls.Button(" Revert ").OnClick((_, _) => Revert()).Build();
        _save = DialogLook.MainButton(Controls.Button(" Save ").OnClick((_, _) => _ = SaveAsync()).Build(), main: false);

        Reload(select: null);
    }

    // THE DETAIL PANE'S WIDTH NOW, so a header whose actions do not fit beside the name wraps them.
    private int PaneWidth() => ListDetail.PaneWidth(_window()?.Width ?? Console.WindowWidth, RailCells);

    /// <summary>The tab's content.</summary>
    public IWindowControl Content => _root;

    /// <summary>Revert and Save, for the dialog's footer to host while this tab is in front.</summary>
    public IReadOnlyList<ButtonControl> Actions => [_revert, _save];

    /// <summary>The rail, focused when the tab opens.</summary>
    public ListControl Rail => _rail;

    /// <summary>Whether a server has edits not yet saved.</summary>
    public bool HasUnsaved => _draft?.IsDirty == true;

    /// <summary>The selected server's name, or null.</summary>
    public string? SelectedName => _draft?.Name;

    /// <summary>Ctrl+S.</summary>
    public void SaveFromKey() => _ = SaveAsync();

    /// <summary>Re-reads the file and the manager, and rebuilds the rail.</summary>
    public void Reload(string? select)
    {
        var read = ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env);
        if (read.Settings is null)
        {
            _servers = null;
            _draft = null;
            _rail.ClearItems();
            _detail.ClearContents();
            _detail.AddControl(new MarkupControl([
                $"[{ColorScheme.CautionMarkup}]config.json cannot be read, so it cannot be edited here.[/]",
                "",
                .. read.Errors.Select(e => $"[{ColorScheme.CautionMarkup}]· {MarkupParser.Escape(e)}[/]"),
                "",
                $"[{ColorScheme.MutedMarkup}]Fix it in an editor, then reopen this dialog.[/]",
            ]) { Wrap = true });
            _save.IsEnabled = false;
            return;
        }
        _save.IsEnabled = true;
        _servers = read.Settings.McpServers;
        FillRail(select ?? _draft?.Name);
    }

    private void FillRail(string? select)
    {
        var rows = McpRows.For(_servers!, _host.Apply.Mcp.Statuses()).ToList();
        if (_draft is { IsNew: true } fresh)
            rows.Insert(0, new McpRow(fresh.Name, fresh.IsRemote ? fresh.Url ?? "" : fresh.CommandLine, McpState.NotStarted, "new", 0));

        _rail.ClearItems();
        foreach (var row in rows) _rail.AddItem(new ListItem(RowText(row)) { Tag = row.Name });

        var index = rows.FindIndex(r => r.Name == select);
        if (index < 0 && rows.Count > 0) index = 0;
        _lastIndex = index;
        if (index >= 0)
        {
            _rail.SelectedIndex = index;
            ShowServer(rows[index]);
        }
        else
        {
            _draft = null;
            _detail.ClearContents();
            _detail.AddControl(ListDetail.Empty("No MCP servers yet", "+ Add connects one"));
            SetStatus("");
            MarkSave();
        }
        ListDetail.Mark(_rail);
    }

    /// <summary>The name and what it is doing — ● running, ✖ failed, ◌ needs login, ○ off — then the command.</summary>
    private static string RowText(McpRow row)
    {
        var (glyph, colour, word) = row.Reason == "new" ? ("+", ColorScheme.CautionMarkup, "new") : row.State switch
        {
            McpState.Running => ("●", ColorScheme.AffirmativeMarkup, "running"),
            McpState.Failed => ("✖", ColorScheme.DangerMarkup, "failed"),
            McpState.NeedsLogin => ("◌", ColorScheme.CautionMarkup, "login"),
            McpState.Off => ("○", ColorScheme.MutedMarkup, "off"),
            _ => ("○", ColorScheme.MutedMarkup, "not started"),
        };
        return $"[bold]{MarkupParser.Escape(row.Name)}[/] [{colour}]{glyph} {word}[/]\n[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(row.Target)}[/]";
    }

    private void OnRailSelectionChanged(object? sender, int index)
    {
        if (index < 0 || index == _lastIndex) return;
        if (_rail.Items.ElementAtOrDefault(index)?.Tag is not string name) return;
        if (_draft?.IsDirty == true && _draft.Name != name) { _ = AskBeforeLeavingAsync(name); return; }
        _lastIndex = index;
        if (_draft?.IsNew == true) _draft = null;
        FillRail(name);
    }

    private async Task AskBeforeLeavingAsync(string next)
    {
        var edited = _draft!.Name;
        var answer = await ConfirmDialog.AskAsync(_host.System, $"Unsaved changes to '{edited}'",
            ["Leaving this server drops what you changed and did not save."], ["Keep editing", "Discard them"]);
        if (answer == "Discard them") { _draft = null; FillRail(next); return; }
        FillRail(edited);
    }

    private void ShowServer(McpRow row)
    {
        if (_draft is { IsNew: true } fresh && fresh.Name == row.Name) { BuildDetail(fresh, row); return; }
        if (_servers is null || !_servers.TryGetValue(row.Name, out var cfg)) return;
        if (_draft?.Name != row.Name) _draft = new McpDraft(row.Name, cfg);
        _save.Text = " Save ";
        BuildDetail(_draft, row);
        MarkSave();
    }

    private void BuildDetail(McpDraft draft, McpRow row)
    {
        _detail.ClearContents();
        var subtitle = draft.IsRemote ? "remote (HTTP)" : "local command (stdio)";
        // THE ACTIONS ON THIS SERVER, beside its name. A server not yet saved has none: leaving it is Revert.
        _detail.AddControl((draft.IsNew
            ? ListDetail.Header("New MCP server", subtitle, PaneWidth())
            : ListDetail.Header(draft.Name, subtitle, PaneWidth(),
                Controls.Button(" Enable / Disable ").OnClick((_, _) => _ = ToggleAsync()).Build(),
                Controls.Button(" Log in ").OnClick((_, _) => _ = LogInAsync()).Build(),
                Controls.Button(" Rename ").OnClick((_, _) => ShowRename()).Build(),
                Controls.Button(" Remove ").OnClick((_, _) => _ = RemoveAsync()).Build())).Control);

        // WHY IT FAILED, FIRST: that is what the user opened this server to fix, and Save retries it.
        if (row.State == McpState.Failed && row.Reason is { } reason)
            _detail.AddControl(new MarkupControl([$"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(reason)}[/]", ""]) { Wrap = true });
        else if (row.State == McpState.Running)
            _detail.AddControl(new MarkupControl([$"[{ColorScheme.AffirmativeMarkup}]● running · {row.Tools} tool{(row.Tools == 1 ? "" : "s")}[/]", ""]));
        else if (row.State == McpState.NeedsLogin)
            _detail.AddControl(new MarkupControl([$"[{ColorScheme.CautionMarkup}]◌ this server needs you to log in — Log in, at the top[/]", ""]));

        // GROUPED: where it connects, the secrets it is given, and what is set once, if ever, folded.
        var connection = new FieldGrid();
        var secrets = new FieldGrid();
        if (draft.IsNew)
        {
            var nameBox = ListDetail.Input(new PromptControl { Input = draft.Name, HorizontalAlignment = HorizontalAlignment.Stretch });
            nameBox.InputChanged += (_, text) => { draft.Name = text.Trim(); Changed(); };
            connection.Add("Name", "What /mcp and the tool names call it.", nameBox);
            _firstField = nameBox;
        }

        if (draft.IsRemote)
        {
            var url = ListDetail.Input(new PromptControl { Input = draft.Url ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
            url.InputChanged += (_, text) => { draft.Url = text; Changed(); };
            connection.Add("URL", "The server's MCP endpoint.", url);
            secrets.Add("Headers", "Sent with every request — where a token usually goes. Values are never shown again.",
                SecretsEditor(draft.Original.Headers, () => draft.NewHeaders, v => draft.NewHeaders = v));
        }
        else
        {
            var command = ListDetail.Input(new PromptControl { Input = draft.CommandLine, HorizontalAlignment = HorizontalAlignment.Stretch });
            command.InputChanged += (_, text) => { draft.CommandLine = text; Changed(); };
            connection.Add("Command", "One line, as you would type it — quotes group a word with spaces.", command);
            secrets.Add("Environment", "Set for the server's process — where a token usually goes. Values are never shown again.",
                SecretsEditor(draft.Original.Environment, () => draft.NewEnv, v => draft.NewEnv = v));
        }

        var more = new FieldGrid();
        var cwd = ListDetail.Input(new PromptControl { Input = draft.Cwd ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
        cwd.InputChanged += (_, text) => { draft.Cwd = text; Changed(); };
        more.Add("Working directory", "Where the command runs; empty is the session's folder.", cwd);
        var timeout = ListDetail.Input(new PromptControl { Input = draft.TimeoutMs?.ToString() ?? "", InputWidth = 12 });
        timeout.InputChanged += (_, text) => { draft.TimeoutMs = int.TryParse(text.Trim(), out var n) && n > 0 ? n : null; Changed(); };
        more.Add("Timeout", "Milliseconds a call may take; empty is the default.", timeout);

        _detail.AddControl(ListDetail.Section("Connection", connection.Control));
        _detail.AddControl(ListDetail.Gap());
        _detail.AddControl(ListDetail.Section("Secrets", secrets.Control));
        _detail.AddControl(ListDetail.Gap());
        _detail.AddControl(ListDetail.Folded("Advanced", more.Control));
        SetStatus(draft.IsNew ? $"[{ColorScheme.MutedMarkup}]Not saved yet — Save adds it to config.json.[/]" : "");
    }

    /// <summary>
    /// A map of secrets — environment variables, headers: each name with its value masked to a tail, Change
    /// and Remove beside it, and + Variable to add one. A change replaces the whole map on save, built from
    /// the stored values plus the edits, so nothing stored is read back onto the screen.
    /// </summary>
    private IWindowControl SecretsEditor(
        IReadOnlyDictionary<string, string>? stored, Func<IReadOnlyDictionary<string, string>?> edited,
        Action<IReadOnlyDictionary<string, string>> set)
    {
        var stack = new FieldGrid();
        Dictionary<string, string> Current() => new(edited() ?? stored ?? new Dictionary<string, string>());

        void Rebuild()
        {
            stack.Clear();
            foreach (var (key, value) in Current().OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var row = new ToolbarControl { ItemSpacing = 1 };
                row.AddItem(new MarkupControl([$"{MarkupParser.Escape(key)} = [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(SecretField.Describe(value))}[/]"]));
                row.AddItem(Controls.Button(" Change ").OnClick((_, _) => Ask(key)).Build());
                row.AddItem(Controls.Button(" Remove ").OnClick((_, _) =>
                {
                    var map = Current();
                    map.Remove(key);
                    set(map);
                    Changed();
                    Rebuild();
                }).Build());
                stack.AddBare(row);
            }
            stack.AddBare(Controls.Button(" + Variable ").OnClick((_, _) => Ask(null)).Build());
        }

        // NAME, THEN THE VALUE MASKED: the name is not secret, the value is typed once and never shown.
        void Ask(string? key)
        {
            var name = ListDetail.Input(new PromptControl { Prompt = "Name: ", Input = key ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
            var value = ListDetail.Input(new PromptControl { Prompt = "Value: ", MaskCharacter = '•', HorizontalAlignment = HorizontalAlignment.Stretch });
            value.Entered += (_, text) =>
            {
                var n = name.Input.Trim();
                if (n.Length == 0) return;
                var map = Current();
                if (key is not null && key != n) map.Remove(key);
                map[n] = text;
                set(map);
                Changed();
                Rebuild();
            };
            stack.AddBare(name);
            stack.AddBare(value);
            stack.AddBare(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Enter in Value saves the variable to the draft[/]"]));
            _window()?.FocusManager.SetFocus(key is null ? name : value, FocusReason.Programmatic);
        }

        Rebuild();
        return stack.Control;
    }

    private void Changed()
    {
        if (_save.Text != " Save ") _save.Text = " Save ";
        MarkSave();
    }

    // THE ACCENT ONLY WHEN THERE IS SOMETHING TO SAVE.
    public void MarkSave() => DialogLook.MainButton(_save, _draft?.IsDirty == true);

    private void SetStatus(string markup) => _status.SetContent([markup]);

    private void Revert()
    {
        if (_draft is { IsNew: true }) { _draft = null; FillRail(null); FocusRail(); return; }
        if (_draft is null) return;
        var name = _draft.Name;
        _draft = null;
        FillRail(name);
    }

    private void FocusRail() => _window()?.FocusManager.SetFocus(_rail, FocusReason.Programmatic);

    // ---- the toolbar ----------------------------------------------------------------------------------

    private string? Selected()
    {
        if (_draft is null || _draft.IsNew) { SetStatus($"[{ColorScheme.MutedMarkup}]Select a saved server first.[/]"); return null; }
        return _draft.Name;
    }

    /// <summary>Two cards: a command cxagent starts, or a server already running somewhere.</summary>
    private void ShowKinds()
    {
        if (_servers is null) return;
        _detail.ClearContents();
        _detail.AddControl(new MarkupControl(["[bold]Add an MCP server[/]", ""]));
        var grid = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        grid.ColumnDefinitions.Add(GridLength.Cells(2));
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        grid.RowDefinitions.Add(GridLength.Auto());
        var (local, first) = KindCard("Local command", "stdio — cxagent starts it, e.g. npx or uvx", remote: false);
        var (remote, _) = KindCard("Remote", "HTTP — a server already running at a URL", remote: true);
        grid.Place(local, 0, 0);
        grid.Place(remote, 0, 2);
        _detail.AddControl(grid);
        SetStatus("");
        _window()?.FocusManager.SetFocus(first, FocusReason.Programmatic);
    }

    private (IWindowControl, ButtonControl) KindCard(string title, string blurb, bool remote)
    {
        var card = new PanelControl { BorderStyle = BorderStyle.Rounded, HorizontalAlignment = HorizontalAlignment.Stretch };
        var button = Controls.Button($" {title} ").OnClick((_, _) => StartNew(remote)).Build();
        card.AddControl(button);
        card.AddControl(new MarkupControl([$" [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(blurb)}[/]"]));
        return (card, button);
    }

    private void StartNew(bool remote)
    {
        var taken = _servers!.Keys.ToHashSet(StringComparer.Ordinal);
        var name = "server";
        for (var i = 2; taken.Contains(name); i++) name = $"server-{i}";
        _draft = new McpDraft(name, new McpServerConfig([])) { IsNew = true, Url = remote ? "https://" : null };
        FillRail(name);

        // ON THE NAME: the card that had focus is gone with the cards.
        if (_firstField is { } field) _window()?.FocusManager.SetFocus(field, FocusReason.Programmatic);
    }

    /// <summary>"Not now" without retyping a command later — written and applied at once.</summary>
    private async Task ToggleAsync()
    {
        if (Selected() is not { } name || _servers is null) return;
        var enable = !_servers[name].Enabled;
        await CommitAsync(root => McpEdits.SetEnabled(root, name, enable), name, enable ? $"{name} enabled" : $"{name} disabled");
    }

    private void ShowRename()
    {
        if (Selected() is not { } from) return;
        _detail.ClearContents();
        var box = ListDetail.Input(new PromptControl { Prompt = "New name: ", Input = from, HorizontalAlignment = HorizontalAlignment.Stretch });
        _detail.AddControl(new MarkupControl([$"[bold]Rename '{MarkupParser.Escape(from)}'[/]", ""]));
        _detail.AddControl(box);
        _detail.AddControl(new MarkupControl(["", $"[{ColorScheme.MutedMarkup}]Enter renames · Revert cancels[/]"]));
        box.Entered += (_, text) =>
        {
            var to = text.Trim();
            if (to.Length == 0 || to == from) { FillRail(from); return; }
            if (_servers!.ContainsKey(to)) { SetStatus($"[{ColorScheme.CautionMarkup}]'{MarkupParser.Escape(to)}' already exists.[/]"); return; }
            _draft = null;
            _ = CommitAsync(root => McpEdits.Rename(root, from, to), to, $"{from} is now {to}");
        };
        _window()?.FocusManager.SetFocus(box, FocusReason.Programmatic);
    }

    private async Task RemoveAsync()
    {
        if (Selected() is not { } name) return;
        var answer = await ConfirmDialog.AskAsync(_host.System, $"Remove '{name}'?",
            ["Its tools leave every open tab."], ["Keep it", "Remove"]);
        if (answer != "Remove") return;
        _draft = null;
        await CommitAsync(root => McpEdits.Remove(root, name), null, $"{name} removed");
    }

    /// <summary><c>/mcp</c>'s own login, then the rail read again.</summary>
    private async Task LogInAsync()
    {
        if (Selected() is not { } name) return;
        SetStatus($"[{ColorScheme.MutedMarkup}]Logging in to '{MarkupParser.Escape(name)}'…[/]");
        await _host.McpCommand.LoginAsync(name);
        _host.System.EnqueueOnUIThread(() => { FillRail(name); _host.Main.SetMcpServers(_host.Apply.Mcp.Statuses()); }, "mcpTab.LoggedIn");
    }

    // ---- saving ---------------------------------------------------------------------------------------

    private async Task SaveAsync()
    {
        if (_saving || _draft is not { } draft || _servers is null) return;
        if (!draft.IsDirty) { SetStatus($"[{ColorScheme.MutedMarkup}]Nothing to save.[/]"); return; }
        if (draft.IsNew && (draft.Name.Length == 0 || _servers.ContainsKey(draft.Name)))
        { SetStatus($"[{ColorScheme.CautionMarkup}]Choose a name no other server has.[/]"); return; }
        if (!draft.IsRemote && draft.CommandLine.Trim().Length == 0)
        { SetStatus($"[{ColorScheme.CautionMarkup}]A command is required.[/]"); return; }

        var name = draft.Name;
        var fields = draft.ToFields();
        _saving = true;
        try
        {
            await CommitAsync(draft.IsNew
                ? root => McpEdits.Add(root, name, fields)
                : root => McpEdits.Update(root, name, fields), name, $"{name} saved");
        }
        finally { _saving = false; }
    }

    /// <summary>
    /// The write, then the reload — or not, if the user keeps their tabs' caches: when any open tab has
    /// context, a tool list change is asked about first, because it rebuilds every tab's prompt cache.
    /// </summary>
    private async Task CommitAsync(Action<System.Text.Json.Nodes.JsonObject> edit, string? select, string what)
    {
        var tabs = _host.Apply.Manager.Sessions.Where(s => s.ContextUsed is > 0).ToList();
        var apply = true;
        if (tabs.Count > 0)
        {
            var tokens = tabs.Sum(s => s.ContextUsed ?? 0);
            var answer = await ConfirmDialog.AskAsync(_host.System, "Apply the MCP change now?",
                [
                    "MCP tools are shared by every open tab, so changing them now rebuilds each tab's prompt cache:",
                    $"{tabs.Count} tab{(tabs.Count == 1 ? "" : "s")}, about {Math.Max(1, (int)Math.Round(tokens / 1000.0))}K tokens re-sent.",
                    "Save only writes config.json; /mcp reload or the next start applies it.",
                ],
                ["Save only", "Apply now"]);
            if (answer is null) return;
            apply = answer == "Apply now";
        }

        SetStatus($"[{ColorScheme.MutedMarkup}]{(apply ? "Saving and restarting servers…" : "Saving…")}[/]");
        ConfigWrite result = apply
            // BOTH OFF THE UI THREAD: the write is file IO and the reload starts processes and waits on
            // their handshakes, either of which would freeze every window while it ran.
            ? await Task.Run(() => ConfigApply.SaveMcpAsync(_host.Apply, edit))
            : await Task.Run(() => ConfigFile.TryApply(new ConfigTarget(Path.Combine(_host.Apply.Paths.ConfigDir, "config.json"), _host.Apply.Env), edit));

        _host.System.EnqueueOnUIThread(() =>
        {
            if (!result.Saved)
            {
                SetStatus(string.Join("\n", result.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
                return;
            }
            _draft = null;
            Reload(select);
            FocusRail();
            if (apply) _host.Main.SetMcpServers(_host.Apply.Mcp.Statuses());
            SetStatus(apply
                ? $"[{ColorScheme.AffirmativeMarkup}]✔ {MarkupParser.Escape(what)}.[/] [{ColorScheme.MutedMarkup}]Servers restarted; every tab has the new tools.[/]"
                : $"[{ColorScheme.AffirmativeMarkup}]✔ {MarkupParser.Escape(what)}.[/] [{ColorScheme.MutedMarkup}]Saved only — /mcp reload applies it.[/]");
        }, "mcpTab.Saved");
    }
}
