using CxAgent.Core.Agents;
using CxAgent.Core.Config;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// The Agents tab: the sub-agent types the model can spawn — the built-ins, always there, and the user's
/// own — and where each runs, how long, and with which tools.
///
/// <para>A BUILT-IN'S BRIEFING IS SHOWN, NOT EDITED: it is a contract with the code around it. What an
/// override may set — provider, max turns, tools — is editable, and Reset to shipped removes it.</para>
///
/// <para>SAVED IN PLACE. A type change swaps each session's catalog without a re-wire, and is announced
/// to the model at the front of its next message; no tab's prompt cache is touched, so nothing is asked.</para>
/// </summary>
public sealed class AgentsTab
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

    private ProviderSettings? _settings;
    private AgentDraft? _draft;
    private int _lastIndex = -1;
    private bool _saving;
    private IFocusableControl? _firstField;

    public AgentsTab(ConfigDialogHost host, Func<Window?> window)
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

        // ONLY WHAT DOES NOT ACT ON THE SELECTION. Rename and Remove sit in the detail's header.
        var toolbar = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        toolbar.AddItem(Controls.Button(" + Add type ").OnClick((_, _) => StartNew()).Build());
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
        _save = DialogLook.Main(Controls.Button(" Save ").OnClick((_, _) => _ = SaveAsync()).Build(), main: false);

        Reload(null);
    }

    // THE DETAIL PANE'S WIDTH NOW, so a header whose actions do not fit beside the name wraps them.
    private int PaneWidth() => ListDetail.PaneWidth(_window()?.Width ?? Console.WindowWidth, RailCells);

    public IWindowControl Content => _root;

    /// <summary>Revert and Save, for the dialog's footer to host while this tab is in front.</summary>
    public IReadOnlyList<ButtonControl> Actions => [_revert, _save];

    /// <summary>
    /// The accent on Save only when there is something to save. The dialog calls this after each key:
    /// this tab's editors write straight into the draft, with no change event of their own to hang it on.
    /// </summary>
    public void MarkSave() => DialogLook.Main(_save, _draft?.IsDirty == true);
    public ListControl Rail => _rail;
    public bool HasUnsaved => _draft?.IsDirty == true;
    public string? SelectedName => _draft?.Name;
    public void SaveFromKey() => _ = SaveAsync();

    /// <summary>The rows: every built-in, then the user's own types by name.</summary>
    private IReadOnlyList<(string Name, bool Builtin, bool Overridden, string Line)> Rows()
    {
        var configured = _settings?.AgentTypes ?? new Dictionary<string, AgentTypeConfig>();
        var general = AgentTypeCatalog.DefaultTypeName;

        // THE DEFAULT CHILD FIRST: it is what a spawn that names no type gets — the one used most.
        var rows = new List<(string, bool, bool, string)> { (general, true, configured.ContainsKey(general), "default") };
        rows.AddRange(BuiltinAgentTypes.All
            .Select(t => (t.Name, true, configured.ContainsKey(t.Name), t.Description)));
        rows.AddRange(configured
            .Where(kv => !BuiltinAgentTypes.IsBuiltin(kv.Key) && kv.Key != general)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, false, false, kv.Value.Description ?? "")));
        return rows;
    }

    public void Reload(string? select)
    {
        var read = ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env);
        _settings = read.Settings;
        if (_settings is null)
        {
            _rail.ClearItems();
            _detail.ClearContents();
            _detail.AddControl(new MarkupControl([
                $"[{ColorScheme.CautionMarkup}]config.json cannot be read, so it cannot be edited here.[/]",
                "", .. read.Errors.Select(e => $"[{ColorScheme.CautionMarkup}]· {MarkupParser.Escape(e)}[/]"),
            ]) { Wrap = true });
            _save.IsEnabled = false;
            return;
        }
        _save.IsEnabled = true;
        FillRail(select ?? _draft?.Name);
    }

    private void FillRail(string? select)
    {
        var rows = Rows().ToList();
        if (_draft is { IsNew: true } fresh) rows.Insert(0, (fresh.Name, false, false, "new"));
        _rail.ClearItems();
        foreach (var (name, builtin, overridden, line) in rows)
        {
            var tag = name == AgentTypeCatalog.DefaultTypeName
                ? $"[{ColorScheme.MutedMarkup}]default{(overridden ? " · set" : "")}[/]"
                : builtin
                ? $"[{ColorScheme.MutedMarkup}]built-in{(overridden ? " · overridden" : "")}[/]"
                : line == "new" ? $"[{ColorScheme.CautionMarkup}]new[/]" : $"[{ColorScheme.MutedMarkup}]yours[/]";
            _rail.AddItem(new ListItem($"[bold]{MarkupParser.Escape(name)}[/] {tag}") { Tag = name });
        }
        var index = rows.FindIndex(r => r.Name == select);
        if (index < 0 && rows.Count > 0) index = 0;
        _lastIndex = index;
        if (index >= 0) { _rail.SelectedIndex = index; Show(rows[index].Name); }
        ListDetail.Mark(_rail);
    }

    private void OnRailSelectionChanged(object? sender, int index)
    {
        if (index < 0 || index == _lastIndex) return;
        if (_rail.Items.ElementAtOrDefault(index)?.Tag is not string name) return;
        if (_draft?.IsDirty == true && _draft.Name != name) { _ = AskBeforeLeavingAsync(name); return; }
        _lastIndex = index;
        if (_draft?.IsNew == true) _draft = null;
        Show(name);
    }

    private async Task AskBeforeLeavingAsync(string next)
    {
        var edited = _draft!.Name;
        var answer = await ConfirmDialog.AskAsync(_host.System, $"Unsaved changes to '{edited}'",
            ["Leaving this type drops what you changed and did not save."], ["Keep editing", "Discard them"]);
        if (answer == "Discard them") { _draft = null; FillRail(next); return; }
        FillRail(edited);
    }

    private void Show(string name)
    {
        if (_draft?.Name != name || _draft is null)
        {
            var configured = _settings!.AgentTypes;
            _draft = name == AgentTypeCatalog.DefaultTypeName
                ? AgentDraft.ForGeneral(configured.TryGetValue(name, out var g) ? g : null)
                : BuiltinAgentTypes.Find(name) is { } shipped
                ? AgentDraft.ForBuiltin(shipped, configured.TryGetValue(name, out var o) ? o : null)
                : configured.TryGetValue(name, out var own) ? AgentDraft.ForOwn(name, own) : _draft;
        }
        if (_draft is not null) BuildDetail(_draft);
        MarkSave();
    }

    private void BuildDetail(AgentDraft draft)
    {
        _detail.ClearContents();
        _firstField = null;
        var subtitle = draft.IsGeneral ? "the default" : draft.IsBuiltin ? "ships with cxagent" : "yours";
        // THE ACTIONS ON THIS TYPE, beside its name. A type not yet saved has none: leaving it is Revert.
        _detail.AddControl((draft.IsNew
            ? ListDetail.Header("New agent type", subtitle, PaneWidth())
            : ListDetail.Header(draft.Name, subtitle, PaneWidth(),
                Controls.Button(" Rename ").OnClick((_, _) => ShowRename()).Build(),
                Controls.Button(" Remove / Reset to shipped ").OnClick((_, _) => _ = RemoveOrResetAsync()).Build())).Control);

        var fields = new FieldGrid();
        if (draft.IsGeneral)
        {
            _detail.AddControl(new MarkupControl([
                "What a spawn that names no type gets: a plain child with the session's tools and no briefing of its own.",
                "",
                $"[{ColorScheme.MutedMarkup}]Set where it runs, how long, and with which tools. A briefing here is optional — a standing instruction every such child is given.[/]",
                "",
            ]) { Wrap = true });
            var standing = new MultilineEditControl(viewportHeight: 4) { HorizontalAlignment = HorizontalAlignment.Stretch };
            standing.SetContent(draft.Briefing ?? "");
            standing.ContentChanged += (_, _) => draft.Briefing = standing.GetContent();
            fields.Add("Standing briefing", "Optional. Given to every child spawned without a type. Enter to type, Esc when done.", standing);
        }
        else if (draft.IsBuiltin && draft.Shipped is { } shipped)
        {
            _detail.AddControl(new MarkupControl([
                MarkupParser.Escape(shipped.Description), "",
                $"[{ColorScheme.MutedMarkup}]Its briefing ships with cxagent: the code around this type depends on it. Where it runs, how long, and with which tools are yours to set.[/]",
                "",
            ]) { Wrap = true });
            var briefing = new CollapsiblePanel { Title = "Briefing (read-only)", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
            briefing.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(shipped.Briefing)}[/]"]) { Wrap = true });
            _detail.AddControl(briefing);
            // A BLANK LINE UNDER THE BRIEFING, as between any two fields: the panel below is not part of it.
            _detail.AddControl(new MarkupControl([""]));
        }
        else
        {
            if (draft.IsNew)
            {
                var name = ListDetail.Input(new PromptControl { Input = draft.Name, HorizontalAlignment = HorizontalAlignment.Stretch });
                name.InputChanged += (_, t) => { draft.Name = t.Trim(); };
                fields.Add("Name", "What the model names when it spawns one.", name);
                _firstField = name;
            }
            var description = ListDetail.Input(new PromptControl { Input = draft.Description ?? "", HorizontalAlignment = HorizontalAlignment.Stretch });
            description.InputChanged += (_, t) => draft.Description = t;
            fields.Add("Description", "When to reach for it — what the model reads when choosing.", description);
            var briefing = new MultilineEditControl(viewportHeight: 6) { HorizontalAlignment = HorizontalAlignment.Stretch };
            briefing.SetContent(draft.Briefing ?? "");
            briefing.ContentChanged += (_, _) => draft.Briefing = briefing.GetContent();
            fields.Add("Briefing", "What the agent is told it is, and how to work. Required. Enter to type, Esc when done.", briefing);
        }

        fields.AddBare(ProviderEditor(draft));
        fields.AddBare(new MarkupControl([""]));
        var turns = ListDetail.Input(new PromptControl { Input = draft.MaxTurns?.ToString() ?? "", InputWidth = 8 });
        turns.InputChanged += (_, t) => draft.MaxTurns = int.TryParse(t.Trim(), out var n) && n >= 0 ? n : null;
        fields.Add("Max turns", draft.IsBuiltin && draft.Shipped?.DefaultMaxTurns is { } d
            ? $"Empty is its shipped {d}; 0 is no limit."
            : "Empty inherits the session's limit; 0 is no limit.", turns);
        fields.Add("Tools", "What it may use: a base, and the tools taken from it.", ToolsEditor(draft));
        _detail.AddControl(fields.Control);
        SetStatus(draft.IsNew ? $"[{ColorScheme.MutedMarkup}]Not saved yet — Save adds it to config.json.[/]" : "");
    }

    /// <summary>
    /// "Runs on": the three forms a type's <c>provider</c> takes, as three choices in a panel, with only
    /// the chosen one's detail open — the session's model; a list of providers, the first the default;
    /// or any provider. Rebuilt in place on every change, as the MCP secrets editor is, so the rows
    /// always show the draft. An entry config does not know is shown as such, so a disabled type's bad
    /// entry can be seen and removed.
    ///
    /// <para>THREE CHOICES, NOT A CHECKBOX AND AN IMPLIED EMPTY LIST. An empty list meaning "the session's
    /// model" puts the add-picker's provider right under that sentence, where it reads as the provider the
    /// type runs on; a choice of its own says it outright, and only the chosen option's detail is open.</para>
    /// </summary>
    private IWindowControl ProviderEditor(AgentDraft draft)
    {
        var stack = new FieldGrid();
        var providers = _settings!.Providers;
        var muted = ColorScheme.MutedMarkup;

        // AN ENTRY AS IT WILL RUN, and what is wrong with it: not configured, or pinned to a model the
        // entry no longer runs — the two ways a list disables its type.
        string Label(string written)
        {
            var (name, pinned) = AgentTypeConfig.SplitEntry(written, providers.ContainsKey);
            if (!providers.TryGetValue(name, out var c))
                return $"{MarkupParser.Escape(written)} [{ColorScheme.CautionMarkup}](not configured)[/]";
            if (pinned is not null && !string.Equals(pinned, c.Model, StringComparison.OrdinalIgnoreCase))
                return $"{MarkupParser.Escape(written)} [{ColorScheme.CautionMarkup}](pinned to {MarkupParser.Escape(pinned)}; {MarkupParser.Escape(name)} runs {MarkupParser.Escape(c.Model)})[/]";
            return MarkupParser.Escape($"{name}:{c.Model}");
        }

        // AN INDENTED RULE, under the option it belongs to.
        RuleControl Rule()
        {
            var rule = DialogLook.Rule();
            rule.Margin = new Margin(4, 0, 0, 0);
            return rule;
        }

        // FOCUS FOLLOWS THE EDIT: Rebuild replaces every control, so the one pressed is gone — focus lands
        // on the same button of the row that moved, on + Add, or on the option just chosen.
        void Rebuild(int? focusRow = null, string focusOn = "")
        {
            stack.Clear();
            IWindowControl? focus = null;
            if (draft.Original?.Disabled is { } why)
                stack.AddBare(new MarkupControl([$"[{ColorScheme.CautionMarkup}]Disabled: {MarkupParser.Escape(why)}.[/]", ""]));

            var group = new RadioGroup<RunsOn>();
            RadioControl<RunsOn> Option(RunsOn value, string label, string help)
            {
                var radio = new RadioControl<RunsOn>(group, value, label);
                stack.AddBare(radio);
                stack.AddBare(new MarkupControl([$"[{muted}]    {MarkupParser.Escape(help)}[/]"]) { Wrap = true });
                return radio;
            }

            var session = Option(RunsOn.Session, "The session's model", "Whatever the parent runs on. A spawn cannot choose.");
            // A VALUE THAT IS NOT A NAME OR A LIST has no entries to remove, so it gets a way out.
            if (draft.Mode == RunsOn.Session && draft.Original?.Disabled is not null && draft.Original.Written is null && !draft.ClearBadProvider)
                stack.AddBare(Controls.Button(" Clear the bad value ").OnClick((_, _) => { draft.ClearBadProvider = true; Rebuild(); }).Build());
            stack.AddBare(new MarkupControl([""]));

            var list = Option(RunsOn.List, "These providers", "The first is the default; a spawn may pick another from the list.");
            if (draft.Mode == RunsOn.List)
            {
                // THE LIST BETWEEN TWO RULES, rows or none: an empty list's message stands where the rows
                // would, so the section keeps one shape whether it holds providers or not.
                stack.AddBare(Rule());
                if (draft.ProviderProblem is { } problem)
                    stack.AddBare(new MarkupControl([$"    [{ColorScheme.CautionMarkup}]{MarkupParser.Escape(problem)}[/]"]));
                for (var i = 0; i < draft.Providers.Count; i++)
                {
                    var at = i;
                    // BUTTONS FIRST, IN FIXED SLOTS: every row has ↑ ↓ Remove in the same columns, the ones
                    // that cannot act disabled rather than missing, so the names line up down the list.
                    var row = new ToolbarControl { ItemSpacing = 1, Margin = new Margin(4, 0, 0, 0) };
                    var up = Controls.Button(" ↑ ").OnClick((_, _) => Swap(at, at - 1, "up")).Build();
                    up.IsEnabled = at > 0;
                    var down = Controls.Button(" ↓ ").OnClick((_, _) => Swap(at, at + 1, "down")).Build();
                    down.IsEnabled = at < draft.Providers.Count - 1;
                    var remove = Controls.Button(" Remove ").OnClick((_, _) =>
                    {
                        draft.Providers.RemoveAt(at);
                        Rebuild(Math.Min(at, draft.Providers.Count - 1), "remove");
                    }).Build();
                    row.AddItem(up);
                    row.AddItem(down);
                    row.AddItem(remove);
                    row.AddItem(new MarkupControl([$" [{muted}]{at + 1}[/]  {Label(draft.Providers[at])}"
                        + (at == 0 ? $"  [{muted}]default[/]" : "")]));
                    stack.AddBare(row);
                    if (focusRow == at)
                        focus = focusOn switch { "up" when up.IsEnabled => up, "down" when down.IsEnabled => down, _ => remove };
                }

                var left = providers.Keys.Where(n => !draft.ListsProvider(n)).Order(StringComparer.Ordinal).ToList();
                if (left.Count > 0)
                {
                    // A RULE BETWEEN WHAT IS LISTED AND WHAT CAN BE ADDED, so the picker is not read as a row.
                    stack.AddBare(Rule());
                    var add = new ToolbarControl { ItemSpacing = 1, Margin = new Margin(4, 0, 0, 0) };
                    var pick = new DropdownControl("", left) { HorizontalAlignment = HorizontalAlignment.Left };
                    pick.SelectedIndex = 0;
                    add.AddItem(pick);
                    var addButton = Controls.Button(" + Add ").OnClick((_, _) =>
                    {
                        if (pick.SelectedIndex is var p and >= 0 && p < left.Count) { draft.AddProvider(left[p], providers[left[p]].Model); Rebuild(null, "add"); }
                    }).Build();
                    add.AddItem(addButton);
                    stack.AddBare(add);
                    if (focusOn == "add" || (focusOn == "remove" && focus is null)) focus = addButton;
                }
            }
            stack.AddBare(new MarkupControl([""]));

            var any = Option(RunsOn.Any, "Any provider", "No default — a spawn must name one of every configured provider.");
            if (draft.Mode == RunsOn.Any)
                stack.AddBare(new MarkupControl([$"    [{muted}]{MarkupParser.Escape(string.Join(", ", providers.Keys.Order(StringComparer.Ordinal)))}[/]"]) { Wrap = true });

            group.SelectedValue = draft.Mode;
            group.SelectionChanged += (_, chosen) =>
            {
                if (chosen == draft.Mode) return;
                draft.Choose(chosen);
                Rebuild(null, chosen.ToString());
            };
            focus ??= focusOn switch { nameof(RunsOn.Session) => session, nameof(RunsOn.List) => list, nameof(RunsOn.Any) => any, _ => null };
            if (focus is IFocusableControl target) _window()?.FocusManager.SetFocus(target, FocusReason.Programmatic);
        }

        void Swap(int a, int b, string direction)
        {
            (draft.Providers[a], draft.Providers[b]) = (draft.Providers[b], draft.Providers[a]);
            Rebuild(b, direction);
        }

        Rebuild();
        var panel = new PanelControl
        {
            BorderStyle = BorderStyle.Rounded,
            // THE TITLE IN MUTED TEXT, not the border's colour: drawn in the rule colour it all but
            // vanished into the line it sits on.
            Header = $" [{muted}]Runs on[/] ",
            BorderColor = DialogLook.Palette.Rule,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Padding(1, 0, 1, 0),
        };
        panel.AddControl(stack.Control);
        return panel;
    }

    /// <summary>A base — inherited or all — and the sixteen tools as boxes: unticked is taken away.</summary>
    private IWindowControl ToolsEditor(AgentDraft draft)
    {
        var stack = new FieldGrid();
        var baseBox = new DropdownControl("", ["inherited", "all"]) { HorizontalAlignment = HorizontalAlignment.Left };
        baseBox.SelectedIndex = draft.Base == "all" ? 1 : 0;
        baseBox.SelectedValueChanged += (_, v) => draft.Base = v ?? "inherited";
        stack.AddBare(baseBox);

        var grid = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var c = 0; c < 3; c++) grid.ColumnDefinitions.Add(GridLength.Star(1));
        var names = Tool.KnownNames.Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < names.Count; i++)
        {
            if (i % 3 == 0) grid.RowDefinitions.Add(GridLength.Auto());
            var tool = names[i];
            var box = new CheckboxControl(tool, !draft.Removed.Contains(tool));
            box.CheckedChanged += (_, on) =>
            {
                if (on) draft.Removed.Remove(tool);
                else if (!draft.Removed.Contains(tool)) draft.Removed.Add(tool);
            };
            grid.Place(box, i / 3, i % 3);
        }
        stack.AddBare(grid);
        if (draft.Added.Count > 0)
            stack.AddBare(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Also added back: {MarkupParser.Escape(string.Join(", ", draft.Added))}[/]"]));
        return stack.Control;
    }

    private void SetStatus(string markup) => _status.SetContent([markup]);

    private void Revert()
    {
        if (_draft is { IsNew: true }) { _draft = null; FillRail(null); return; }
        var name = _draft?.Name;
        _draft = null;
        if (name is not null) FillRail(name);
    }

    private void StartNew()
    {
        if (_settings is null) return;
        var taken = _settings.AgentTypes.Keys.Concat(BuiltinAgentTypes.All.Select(t => t.Name)).ToHashSet(StringComparer.Ordinal);
        var name = "custom";
        for (var i = 2; taken.Contains(name); i++) name = $"custom-{i}";
        _draft = AgentDraft.ForNew(name);
        FillRail(name);
        if (_firstField is { } field) _window()?.FocusManager.SetFocus(field, FocusReason.Programmatic);
    }

    private void ShowRename()
    {
        if (_draft is not { IsBuiltin: false, IsNew: false, IsGeneral: false } draft) { SetStatus($"[{ColorScheme.MutedMarkup}]Select a type of your own to rename.[/]"); return; }
        var from = draft.Name;
        _detail.ClearContents();
        var box = ListDetail.Input(new PromptControl { Prompt = "New name: ", Input = from, HorizontalAlignment = HorizontalAlignment.Stretch });
        _detail.AddControl(new MarkupControl([$"[bold]Rename '{MarkupParser.Escape(from)}'[/]", ""]));
        _detail.AddControl(box);
        box.Entered += (_, t) =>
        {
            var to = t.Trim();
            if (to.Length == 0 || to == from) { FillRail(from); return; }
            _draft = null;
            _ = CommitAsync(root => AgentEdits.RenameType(root, from, to), to, $"{from} is now {to}");
        };
        _window()?.FocusManager.SetFocus(box, FocusReason.Programmatic);
    }

    private async Task RemoveOrResetAsync()
    {
        if (_draft is not { IsNew: false } draft) return;
        if (draft.IsBuiltin || draft.IsGeneral)
        {
            if (draft.Original is null) { SetStatus($"[{ColorScheme.MutedMarkup}]'{MarkupParser.Escape(draft.Name)}' is as shipped already.[/]"); return; }
            var name = draft.Name;
            _draft = null;
            await CommitAsync(root => AgentEdits.ResetBuiltin(root, name), name, $"{name} reset to shipped");
            return;
        }
        var answer = await ConfirmDialog.AskAsync(_host.System, $"Remove '{draft.Name}'?", ["The model can no longer spawn it."], ["Keep it", "Remove"]);
        if (answer != "Remove") return;
        var removed = draft.Name;
        _draft = null;
        await CommitAsync(root => AgentEdits.RemoveType(root, removed), null, $"{removed} removed");
    }

    private async Task SaveAsync()
    {
        if (_saving || _draft is not { } draft || _settings is null) return;
        if (!draft.IsDirty) { SetStatus($"[{ColorScheme.MutedMarkup}]Nothing to save.[/]"); return; }
        if (!draft.IsBuiltin && !draft.IsGeneral && string.IsNullOrWhiteSpace(draft.Briefing)) { SetStatus($"[{ColorScheme.CautionMarkup}]A type of your own needs a briefing.[/]"); return; }
        if (draft.ProviderProblem is { } problem) { SetStatus($"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(problem)}[/]"); return; }
        var name = draft.Name;
        var fields = draft.ToFields();
        _saving = true;
        try
        {
            await CommitAsync(draft.IsGeneral ? root => AgentEdits.SetGeneral(root, fields)
                : draft.IsBuiltin ? root => AgentEdits.SetOverride(root, name, fields)
                : draft.IsNew ? root => AgentEdits.AddType(root, name, fields)
                : root => AgentEdits.UpdateType(root, name, fields), name, $"{name} saved");
        }
        finally { _saving = false; }
    }

    /// <summary>Written, then refreshed in every session in place — an agent type touches no prompt cache.</summary>
    private async Task CommitAsync(Action<System.Text.Json.Nodes.JsonObject> edit, string? select, string what)
    {
        var prepared = await Task.Run(() => ConfigApply.Prepare(_host.Apply, edit));
        _host.System.EnqueueOnUIThread(() =>
        {
            if (!prepared.Write.Saved)
            {
                SetStatus(string.Join("\n", prepared.Write.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
                return;
            }
            ConfigApply.Commit(_host.Apply, prepared,
                new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true, what), CacheChoice.ApplyNow);
            _draft = null;
            Reload(select);
            _window()?.FocusManager.SetFocus(_rail, FocusReason.Programmatic);
            SetStatus($"[{ColorScheme.AffirmativeMarkup}]✔ {MarkupParser.Escape(what)}.[/] [{ColorScheme.MutedMarkup}]Running tabs have it now, in place.[/]");
            // A SIDE EFFECT THE USER SHOULD SEE: a type this save disabled, said with the save itself.
            if (prepared.NewlyDisabled.Count > 0)
                SetStatus($"[{ColorScheme.AffirmativeMarkup}]✔ {MarkupParser.Escape(what)}.[/]\n" + string.Join("\n",
                    prepared.NewlyDisabled.Select(w => $"[{ColorScheme.CautionMarkup}]⚠ {MarkupParser.Escape(w)}[/]")));
        }, "agentsTab.Saved");
    }
}
