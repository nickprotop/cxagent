using CxAgent.Core.Hooks;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// The Hooks tab: every hook this folder has — the user's, the repository's shared file and its local one —
/// in one list, each with whether it fires here and why not.
///
/// <para>THE SAME SHELL AS MCP SERVERS — rail, hand-placed fields, Save and Revert in the footer — so the
/// tabs read as one dialog.</para>
///
/// <para>WHERE A HOOK IS SAVED IS A FIELD, NOT A TAB. The user's hooks and the repository's are the same
/// kind of thing that differ only in which file holds them; moving one between files is changing that
/// field and saving.</para>
///
/// <para>SAVING IS THE REVIEW. A hooks file changed outside a running session is not applied until the
/// user looks; a save here is that look, and makes what was saved the hooks the session runs.</para>
/// </summary>
public sealed class HooksTab
{
    private const int RailCells = 32;

    private readonly ConfigDialogHost _host;
    private readonly Func<Window?> _window;
    private readonly GridControl _root;
    private readonly ListControl _rail;
    private readonly ScrollablePanelControl _detail;
    private readonly MarkupControl _status;
    private readonly MarkupControl _trust;
    private readonly ButtonControl _save;
    private readonly ButtonControl _revert;

    private HooksDraft? _draft;
    private HookEntry? _selected;
    private bool _saving;
    private int _lastIndex = -1;
    private IFocusableControl? _firstField;
    private readonly DropdownControl _defaultModel;
    private bool _fillingDefault;

    public HooksTab(ConfigDialogHost host, Func<Window?> window)
    {
        _host = host;
        _window = window;

        _root = new GridControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _root.ColumnDefinitions.Add(GridLength.Cells(RailCells, min: 24));
        _root.ColumnDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Cells(1));   // the toolbar
        _root.RowDefinitions.Add(GridLength.Cells(1));   // a rule closing the toolbar off from the list
        _root.RowDefinitions.Add(GridLength.Star(1));    // rail | detail
        _root.RowDefinitions.Add(GridLength.Cells(2));   // the folder's trust | status

        var toolbar = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        toolbar.AddItem(Controls.Button(" + Add hook ").OnClick((_, _) => AddHook()).Build());
        // THE MODEL PROMPT AND AGENT HOOKS ASK WHEN THEY NAME NONE. Here, beside Add, because it is a setting of
        // the whole list rather than of one hook; "none" means such a hook must name its own.
        toolbar.AddItem(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Default model:[/]"]));
        _defaultModel = new DropdownControl("", ["none"]) { HorizontalAlignment = HorizontalAlignment.Left };
        _defaultModel.SelectedValueChanged += (_, v) =>
        {
            if (_draft is null || _fillingDefault) return;
            _draft.DefaultModel = v is null or "none" ? null : v;
            MarkSave();
        };
        toolbar.AddItem(_defaultModel);
        _root.Place(toolbar, 0, 0, colSpan: 2);
        _root.Place(DialogLook.Rule(), 1, 0, colSpan: 2);

        _rail = new ListControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _rail.SelectedIndexChanged += OnRailSelectionChanged;
        ListDetail.StyleRail(_rail);
        _root.Place(_rail, 2, 0);
        ListDetail.Divider(_root);

        _detail = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill, Margin = ListDetail.DetailInset };
        _root.Place(_detail, 2, 1);

        // THE FOLDER'S TRUST UNDER THE RAIL, because it decides whether half the rail fires at all.
        _trust = new MarkupControl([""]) { Wrap = true };
        _root.Place(_trust, 3, 0);
        _status = new MarkupControl([""]) { Wrap = true, Margin = ListDetail.DetailInset };
        _root.Place(_status, 3, 1);

        _revert = Controls.Button(" Revert ").OnClick((_, _) => Reload()).Build();
        _save = DialogLook.MainButton(Controls.Button(" Save ").OnClick((_, _) => _ = SaveAsync()).Build(), main: false);

        Reload();
    }

    private int PaneWidth() => ListDetail.PaneWidth(_window()?.Width ?? Console.WindowWidth, RailCells);

    public IWindowControl Content => _root;

    /// <summary>The footer's buttons while this tab is in front.</summary>
    public IReadOnlyList<ButtonControl> Actions => [_revert, _save];

    public ListControl Rail => _rail;
    public bool HasUnsaved => _draft?.IsDirty == true;
    public string? SelectedName => _selected?.ToDefinition().DisplayName;
    public void SaveFromKey() => _ = SaveAsync();
    public void MarkSave() => DialogLook.MainButton(_save, _draft?.IsDirty == true);

    private Core.Sessions.Session Session => _host.ActiveSession();
    private bool Trusted => Session.Policy?.FolderTrusted == true;

    /// <summary>Re-reads the three files and rebuilds the rail.</summary>
    public void Reload()
    {
        _draft = HooksFile.Load(_host.Apply.Paths, _host.Apply.Env, Session.WorkingDirectory);
        FillDefaultModel();
        _selected = _draft.Entries.FirstOrDefault();
        FillRail();
        SayTrust();
    }

    private void FillDefaultModel()
    {
        var names = new List<string> { "none" };
        names.AddRange(ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env).Settings?.Providers.Keys.Order() ?? Enumerable.Empty<string>());
        if (_draft?.DefaultModel is { } d && !names.Contains(d)) names.Add(d);
        _fillingDefault = true;
        _defaultModel.ClearItems();
        foreach (var n in names) _defaultModel.AddItem(n);
        _defaultModel.SelectedIndex = Math.Max(0, names.IndexOf(_draft?.DefaultModel ?? "none"));
        _fillingDefault = false;
    }

    private void SayTrust()
    {
        var muted = ColorScheme.MutedMarkup;
        _trust.SetContent([_draft?.RepoRoot is null
            ? $"[{muted}]No repository here: system hooks only.[/]"
            : Trusted
                ? $"[{ColorScheme.AffirmativeMarkup}]Folder trusted[/] [{muted}]· project hooks run[/]"
                : $"[{ColorScheme.CautionMarkup}]Folder not trusted[/] [{muted}]· project hooks do not run; /trust[/]"]);
    }

    private void FillRail()
    {
        _rail.ClearItems();
        if (_draft is null) return;
        var rows = Ordered();
        foreach (var e in rows) _rail.AddItem(new ListItem(RowText(e)) { Tag = e });

        var index = _selected is null ? -1 : rows.IndexOf(_selected);
        if (index < 0 && rows.Count > 0) { index = 0; _selected = rows[0]; }
        _lastIndex = index;
        if (index >= 0)
        {
            _rail.SelectedIndex = index;
            BuildDetail(_selected!);
        }
        else
        {
            _detail.ClearContents();
            _detail.AddControl(ListDetail.Empty("No hooks yet", "+ Add hook runs a command on an event"));
            SetStatus("");
        }
        ListDetail.Mark(_rail);
        MarkSave();
    }

    /// <summary>In the contract's event order, then by file: the rail reads as "what happens when".</summary>
    private List<HookEntry> Ordered() =>
        [.. _draft!.Entries.OrderBy(e => EventIndex(e.Event)).ThenBy(e => e.Source)];

    private static int EventIndex(string ev)
    {
        var i = HookEvents.All.ToList().IndexOf(ev);
        return i < 0 ? int.MaxValue : i;
    }

    private string RowText(HookEntry e)
    {
        var def = e.ToDefinition();
        var state = HookList.State(def, Trusted);
        var (glyph, colour) = state switch
        {
            "active" => ("●", ColorScheme.AffirmativeMarkup),
            "not supported here" => ("◌", ColorScheme.MutedMarkup),
            _ => ("○", ColorScheme.CautionMarkup),
        };
        var name = e.Name is { Length: > 0 } n ? n : FirstWords(e.Command ?? e.Type);
        // NOT WHAT THIS TAB RUNS: a hook that differs from the session's — edited here and not saved, or changed
        // on disk and not reviewed — is marked, so a change is seen before it is applied.
        var running = Session.Hooks?.Snapshot.Hooks.Select(HookList.Key).ToHashSet();
        var pending = running is not null && !running.Contains(HookList.Key(def))
            ? $" [{ColorScheme.CautionMarkup}]◆ not running yet[/]" : "";
        return $"[bold]{MarkupParser.Escape(name)}[/] [{colour}]{glyph}[/]{pending}\n"
               + $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(e.Event)}{(e.Matcher is { Length: > 0 } m ? " · " + MarkupParser.Escape(m) : "")} · {HookList.SourceTag(e.Source)}[/]";
    }

    private static string FirstWords(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2));

    private void OnRailSelectionChanged(object? sender, int index)
    {
        if (index < 0 || index == _lastIndex) return;
        if (_rail.Items.ElementAtOrDefault(index)?.Tag is not HookEntry entry) return;
        _lastIndex = index;
        _selected = entry;
        BuildDetail(entry);
    }

    private void BuildDetail(HookEntry e)
    {
        _detail.ClearContents();
        var def = e.ToDefinition();
        _detail.AddControl(ListDetail.Header(def.DisplayName, $"{e.Type} hook · {HooksFile.Label(e.Source)}", PaneWidth(),
            Controls.Button(" Duplicate ").OnClick((_, _) => Duplicate(e)).Build(),
            Controls.Button(" Remove ").OnClick((_, _) => Remove(e)).Build()).Control);

        var state = HookList.State(def, Trusted);
        var colour = state == "active" ? ColorScheme.AffirmativeMarkup
            : state == "not supported here" ? ColorScheme.MutedMarkup : ColorScheme.CautionMarkup;
        _detail.AddControl(new MarkupControl([$"[{colour}]{MarkupParser.Escape(state)}[/]", ""]) { Wrap = true });

        var when = new FieldGrid();
        var events = HookEvents.All.ToList();
        var eventBox = new DropdownControl("", events) { HorizontalAlignment = HorizontalAlignment.Left };
        eventBox.SelectedIndex = Math.Max(0, events.IndexOf(e.Event));
        eventBox.SelectedValueChanged += (_, v) => { if (v is not null) { e.Event = v; Changed(); } };
        when.Add("Event", HookEvents.Fires(e.Event)
            ? "When it runs."
            : "When it runs. This event does not fire in cxagent yet; the hook is kept for when it does.", eventBox);

        var matcher = ListDetail.Input(new PromptControl { Input = e.Matcher ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
        matcher.InputChanged += (_, text) => { e.Matcher = text; Changed(); };
        _firstField = matcher;
        when.Add("Matcher", "Which tools, as a pattern: run_shell, write_file|replace_in_file. Empty is every tool.", matcher);

        var what = new FieldGrid();
        if (e.Editable)
        {
            var types = new List<string> { "command", "prompt", "agent", "http", "mcp_tool" };
            var typeBox = new DropdownControl("", types) { HorizontalAlignment = HorizontalAlignment.Left };
            typeBox.SelectedIndex = Math.Max(0, types.IndexOf(e.Type));
            typeBox.SelectedValueChanged += (_, v) =>
            {
                if (v is null || v == e.Type) return;
                e.Type = v;
                Changed();
                // THE FIELDS FOLLOW THE TYPE, so the pane is rebuilt for the one chosen.
                _host.System.EnqueueOnUIThread(() => BuildDetail(e), "hooksTab.Type");
            };
            what.Add("Type", e.Type switch
            {
                "prompt" => "Asks a model a yes-or-no question about the event.",
                "agent" => "A read-only agent looks at the project, then answers yes or no.",
                "http" => "Posts the event's JSON to a URL and reads the answer.",
                "mcp_tool" => "Calls a tool on one of your MCP servers.",
                _ => "Runs a command with the event's JSON on stdin.",
            }, typeBox);

            switch (e.Type)
            {
                case "command":
                {
                    var command = ListDetail.Input(new PromptControl { Input = e.Command ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
                    command.InputChanged += (_, text) => { e.Command = text; Changed(); };
                    what.Add("Command", "Run in a shell with the event's JSON on stdin. Exit 2 blocks, with stderr as the reason.", command);
                    break;
                }
                case "prompt" or "agent":
                {
                    var prompt = ListDetail.Input(new PromptControl { Input = e.Prompt ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
                    prompt.InputChanged += (_, text) => { e.Prompt = text; Changed(); };
                    what.Add("Prompt", "The question. $ARGUMENTS stands for the event's JSON; without it, the JSON is added after.", prompt);
                    what.Add("Model", "Which of your providers answers. Default uses the default model set above.", ModelPicker(e));
                    if (e.Type == "prompt")
                    {
                        var carry = new CheckboxControl("Carry on when it says no", e.ContinueOnBlock);
                        carry.CheckedChanged += (_, on) => { e.ContinueOnBlock = on; Changed(); };
                        what.Add("On a no", "Off: a no on a tool call or prompt ends the turn. On: the reason goes back to the model and it carries on.", carry);
                    }
                    break;
                }
                case "http":
                {
                    var url = ListDetail.Input(new PromptControl { Input = e.Url ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
                    url.InputChanged += (_, text) => { e.Url = text; Changed(); };
                    what.Add("URL", "Receives a POST with the event's JSON. Headers are edited in the file.", url);
                    break;
                }
                case "mcp_tool":
                {
                    what.Add("Server", "One of your MCP servers. A repository's hook cannot bring its own.", ServerPicker(e));
                    var tool = ListDetail.Input(new PromptControl { Input = e.Tool ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
                    tool.InputChanged += (_, text) => { e.Tool = text; Changed(); };
                    what.Add("Tool", "The tool to call. Its input is edited in the file; ${tool_input.path} reads from the event.", tool);
                    break;
                }
            }

            var name = ListDetail.Input(new PromptControl { Input = e.Name ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
            name.InputChanged += (_, text) => { e.Name = text; Changed(); };
            what.Add("Name", "What messages and this list call it. Optional.", name);

            var block = new CheckboxControl("Block when it fails", e.OnFailureBlock);
            block.CheckedChanged += (_, on) => { e.OnFailureBlock = on; Changed(); };
            what.Add("On failure", HookEvents.CanBlock(e.Event)
                ? "A hook that crashes, times out or cannot reach its model or server lets the action through, unless this is on."
                : "This event cannot block, so a failure is only reported.", block);
        }
        else
        {
            what.AddBare(new MarkupControl([$"[{ColorScheme.MutedMarkup}]A {MarkupParser.Escape(e.Type)} hook is not a type cxagent knows. It is kept exactly as written, and its event, matcher and file can still be changed here.[/]"]) { Wrap = true });
        }

        var where = new FieldGrid();
        var sources = HooksDraft.Editable.Where(_draft!.CanSaveTo).ToList();
        var labels = sources.Select(SourceLabel).ToList();
        var sourceBox = new DropdownControl("", labels) { HorizontalAlignment = HorizontalAlignment.Left };
        sourceBox.SelectedIndex = Math.Max(0, sources.IndexOf(e.Source));
        sourceBox.SelectedValueChanged += (_, v) =>
        {
            var i = labels.IndexOf(v ?? "");
            if (i < 0 || sources[i] == e.Source) return;
            e.Source = sources[i];
            Changed();
            if (e.Source == HookSource.Project)
                SetStatus($"[{ColorScheme.CautionMarkup}]Saved in .cxagent/hooks.json, it is committed with the repository and runs for everyone who trusts it.[/]");
        };
        where.Add("Saved in", _draft.RepoRoot is null
            ? "No repository here, so only the system config."
            : "The system config runs everywhere; the project's shared file travels with the repository; the local one stays on this machine.",
            sourceBox);

        var more = new FieldGrid();
        if (e.Editable)
        {
            var timeout = ListDetail.Input(new PromptControl { Input = e.TimeoutSeconds?.ToString() ?? "", InputWidth = 8 });
            timeout.InputChanged += (_, text) => { e.TimeoutSeconds = int.TryParse(text.Trim(), out var n) && n > 0 ? n : null; Changed(); };
            more.Add("Timeout", $"Seconds it may run; empty is {HookDispatch.TimeoutSeconds(def with { TimeoutSeconds = null })} for this type and event.", timeout);

            if (e.Type == "command")
            {
            var shells = new List<string> { "default", "bash", "powershell" };
            var shellBox = new DropdownControl("", shells) { HorizontalAlignment = HorizontalAlignment.Left };
            shellBox.SelectedIndex = Math.Max(0, shells.IndexOf(e.Shell ?? "default"));
            shellBox.SelectedValueChanged += (_, v) => { e.Shell = v is null or "default" ? null : v; Changed(); };
            more.Add("Shell", "bash everywhere; on Windows without Git Bash, PowerShell.", shellBox);
            }
        }

        _detail.AddControl(ListDetail.Section("When", when.Control));
        _detail.AddControl(ListDetail.Gap());
        _detail.AddControl(ListDetail.Section("What", what.Control));
        _detail.AddControl(ListDetail.Gap());
        _detail.AddControl(ListDetail.Section("Where", where.Control));
        if (e.Editable)
        {
            _detail.AddControl(ListDetail.Gap());
            _detail.AddControl(ListDetail.Folded("Advanced", more.Control));
        }
    }

    private static string SourceLabel(HookSource s) => s switch
    {
        HookSource.User => "System, all projects (config.json)",
        HookSource.Project => "This project, shared (.cxagent/hooks.json)",
        _ => "This project, only me (.cxagent/hooks.local.json)",
    };

    /// <summary>A pick of your configured providers by name, with "default" for hooks.defaultModel.</summary>
    private IWindowControl ModelPicker(HookEntry e)
    {
        var names = new List<string> { "default" };
        names.AddRange(ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env).Settings?.Providers.Keys.Order() ?? Enumerable.Empty<string>());
        if (e.Model is { } m && !names.Contains(m)) names.Add(m);
        var box = new DropdownControl("", names) { HorizontalAlignment = HorizontalAlignment.Left };
        box.SelectedIndex = Math.Max(0, names.IndexOf(e.Model ?? "default"));
        box.SelectedValueChanged += (_, v) => { e.Model = v is null or "default" ? null : v; Changed(); };
        return box;
    }

    /// <summary>A pick of your configured MCP servers by name.</summary>
    private IWindowControl ServerPicker(HookEntry e)
    {
        var names = (ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env).Settings?.McpServers.Keys.Order() ?? Enumerable.Empty<string>()).ToList();
        if (e.Server is { } sv && !names.Contains(sv)) names.Add(sv);
        if (names.Count == 0) return new MarkupControl([$"[{ColorScheme.CautionMarkup}]No MCP servers are configured. Add one on the MCP servers tab.[/]"]) { Wrap = true };
        var box = new DropdownControl("", names) { HorizontalAlignment = HorizontalAlignment.Left };
        box.SelectedIndex = Math.Max(0, names.IndexOf(e.Server ?? ""));
        if (e.Server is null) e.Server = names[0];
        box.SelectedValueChanged += (_, v) => { e.Server = v; Changed(); };
        return box;
    }

    private void AddHook()
    {
        if (_draft is null) return;
        _selected = _draft.Add(HookSource.User);
        FillRail();
        // TYPING STARTS IN THE NEW HOOK, not on the button that made it.
        if (_firstField is not null) _window()?.FocusManager.SetFocus(_firstField, FocusReason.Programmatic);
        SetStatus($"[{ColorScheme.MutedMarkup}]Not saved yet. Set its command, then Save.[/]");
    }

    private void Duplicate(HookEntry e)
    {
        if (_draft is null) return;
        var copy = new HookEntry
        {
            Source = e.Source, Event = e.Event, Matcher = e.Matcher, Type = e.Type, Command = e.Command, Shell = e.Shell,
            TimeoutSeconds = e.TimeoutSeconds, OnFailureBlock = e.OnFailureBlock,
            Name = e.Name is { Length: > 0 } n ? n + " copy" : null, Raw = e.Raw?.DeepClone().AsObject(),
        };
        _draft.Entries.Add(copy);
        _selected = copy;
        FillRail();
    }

    private void Remove(HookEntry e)
    {
        _draft?.Remove(e);
        _selected = null;
        FillRail();
        SetStatus($"[{ColorScheme.MutedMarkup}]Removed from the draft. Save to write it.[/]");
    }

    private void Changed()
    {
        // THE RAIL'S ROW FOLLOWS THE FIELDS, so a name or event typed here shows where the hook now sits.
        if (_rail.Items.ElementAtOrDefault(_lastIndex) is { Tag: HookEntry e } item) item.Text = RowText(e);
        MarkSave();
    }

    private void SetStatus(string markup) => _status.SetContent([markup]);

    /// <summary>What a hook of its type cannot be saved without, or null.</summary>
    private static string? MissingField(HookEntry e) => !e.Editable ? null : e.Type switch
    {
        "command" when string.IsNullOrWhiteSpace(e.Command) => "A command hook needs a command.",
        "prompt" or "agent" when string.IsNullOrWhiteSpace(e.Prompt) => $"A {e.Type} hook needs a prompt.",
        "http" when string.IsNullOrWhiteSpace(e.Url) => "An http hook needs a URL.",
        "mcp_tool" when string.IsNullOrWhiteSpace(e.Server) || string.IsNullOrWhiteSpace(e.Tool) => "An mcp_tool hook needs a server and a tool.",
        _ => null,
    };

    private async Task SaveAsync()
    {
        if (_saving || _draft is not { } draft) return;
        if (!draft.IsDirty) { SetStatus($"[{ColorScheme.MutedMarkup}]Nothing to save.[/]"); return; }
        if (draft.Entries.Select(e => (Entry: e, Missing: MissingField(e))).FirstOrDefault(x => x.Missing is not null) is { Entry: { } empty } bad)
        {
            _selected = empty;
            FillRail();
            SetStatus($"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(bad.Missing!)}[/]");
            return;
        }

        _saving = true;
        try
        {
            var written = draft.Changed.Select(HooksFile.Label).ToList();
            var errors = await Task.Run(() => HooksFile.Save(draft, _host.Apply.Paths, _host.Apply.Env));
            _host.System.EnqueueOnUIThread(() =>
            {
                if (errors.Count > 0)
                {
                    SetStatus(string.Join("\n", errors.Select(m => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(m)}[/]")));
                    return;
                }
                // THE SAVE IS THE REVIEW — of what this dialog showed, and nothing more. The tab whose repository
                // it showed runs the files as saved, unless one changed on disk while the dialog was open; every
                // other tab takes the system hooks only, its own repository's files still waiting for their
                // own review.
                var user = ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env).Settings?.Hooks ?? HooksSettings.None;
                var shown = Session;
                var safe = HooksFile.SafeToApply(draft)
                           && HookSources.RepoRoot(shown.WorkingDirectory) == draft.RepoRoot;
                foreach (var s in _host.Apply.Manager.Sessions)
                {
                    if (ReferenceEquals(s, shown) && safe) s.ReviewHooks(user);
                    else s.ReviewSystemHooks(user);
                }
                var keep = _selected;
                Reload();
                if (keep is not null)
                    _selected = _draft!.Entries.FirstOrDefault(x => x.Event == keep.Event && x.Command == keep.Command && x.Source == keep.Source) ?? _selected;
                FillRail();
                SetStatus(safe
                    ? $"[{ColorScheme.AffirmativeMarkup}]✔ Saved {MarkupParser.Escape(string.Join(", ", written))}.[/] [{ColorScheme.MutedMarkup}]This tab runs these hooks now.[/]"
                    : $"[{ColorScheme.AffirmativeMarkup}]✔ Saved {MarkupParser.Escape(string.Join(", ", written))}.[/] [{ColorScheme.CautionMarkup}]A project hooks file changed on disk while this was open, so only system hooks were applied. /hooks shows the change; /hooks review applies it.[/]");
            }, "hooksTab.Saved");
        }
        finally { _saving = false; }
    }
}
