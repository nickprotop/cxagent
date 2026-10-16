using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// The Providers tab: every instance on a rail, the selected one's fields beside it, and a Save that
/// makes the change live.
///
/// <para>THE FILE, NOT THE RUNNING CONFIG, is what the tab shows and edits. The running config may be a
/// <c>--model</c> override, or a moment behind a save; the file is what a save changes, so it is read
/// afresh every time the tab is built.</para>
///
/// <para>HAND-COMPOSED FIELDS, the plugin settings tab's layout: a bold label, one muted line saying what
/// the field is for, then the editor — one column read downward, so a label and the field it names are
/// never across the dialog from each other.</para>
/// </summary>
public sealed class ProvidersTab
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
    private bool _wiredFirst;

    /// <summary>The field a new provider's editor starts on.</summary>
    private IFocusableControl? _firstField;
    private InstanceDraft? _draft;
    private bool _saveAnyway;
    private bool _saving;
    private int _lastIndex = -1;

    public ProvidersTab(ConfigDialogHost host, Func<Window?> window)
    {
        _host = host;
        _window = window;

        _root = new GridControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _root.ColumnDefinitions.Add(GridLength.Cells(RailCells, min: 22));
        _root.ColumnDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Cells(1));   // the toolbar
        _root.RowDefinitions.Add(GridLength.Star(1));    // rail | detail
        _root.RowDefinitions.Add(GridLength.Cells(1));   // a rule over the pinned actions
        _root.RowDefinitions.Add(GridLength.Cells(2));   // status — two lines, so a long one is not cut
        _root.RowDefinitions.Add(GridLength.Cells(1));   // Revert · Save

        Toolbar = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        Toolbar.AddItem(Controls.Button(" + Add ").OnClick((_, _) => ShowGallery()).Build());
        Toolbar.AddItem(Controls.Button(" ★ Default ").OnClick((_, _) => _ = MakeDefaultAsync()).Build());
        Toolbar.AddItem(Controls.Button(" ◆ Reviewer ").OnClick((_, _) => _ = ToggleReviewerAsync()).Build());
        Toolbar.AddItem(Controls.Button(" Rename ").OnClick((_, _) => ShowRename()).Build());
        Toolbar.AddItem(Controls.Button(" Remove ").OnClick((_, _) => _ = RemoveAsync()).Build());
        _root.Place(Toolbar, 0, 0, colSpan: 2);

        _rail = new ListControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _rail.SelectedIndexChanged += OnRailSelectionChanged;
        _root.Place(_rail, 1, 0, rowSpan: 4);
        _root.AddColumnSplitterAfter(0);

        _detail = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill };
        _root.Place(_detail, 1, 1);

        // PINNED UNDER THE SCROLL: a long Advanced section must never push Save out of sight.
        _root.Place(new RuleControl { Color = ColorScheme.MutedRgb }, 2, 1);
        _status = new MarkupControl([""]) { Wrap = true };
        _root.Place(_status, 3, 1);

        _revert = Controls.Button(" Revert ").OnClick((_, _) => Revert()).Build();
        _save = Controls.Button("  Save  ").OnClick((_, _) => _ = SaveAsync()).Build();
        var actions = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        actions.AddItem(_revert);
        actions.AddItem(_save);
        _root.Place(actions, 4, 1);

        Reload(select: null);
    }

    /// <summary>The tab's content.</summary>
    public IWindowControl Content => _root;

    /// <summary>The actions on the selected instance; the dialog's other parts add to it.</summary>
    public ToolbarControl Toolbar { get; }

    /// <summary>Whether an instance has edits not yet saved — asked before the dialog closes.</summary>
    public bool HasUnsaved => _draft?.IsDirty == true;

    /// <summary>The rail — focused when the tab opens, so the arrows move between instances at once.</summary>
    public ListControl Rail => _rail;

    /// <summary>Ctrl+S: the same Save the button runs.</summary>
    public void SaveFromKey() => _ = SaveAsync();

    /// <summary>The selected instance's name, or null.</summary>
    public string? SelectedName => _draft?.Name;

    /// <summary>The file as last read, or null when it cannot be read.</summary>
    public ProviderSettings? Settings => _settings;

    /// <summary>Re-reads the file and rebuilds the rail, selecting <paramref name="select"/> when it is there.</summary>
    public void Reload(string? select)
    {
        var read = ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env);
        _settings = read.Settings;
        if (_settings is null)
        {
            // NOTHING IS EDITABLE OVER A FILE THE LOADER REFUSES: a save would have to start from it, and
            // starting fresh would delete what the user has. The reasons, and the way out, instead.
            _settings = null;
            _draft = null;
            _rail.ClearItems();
            ShowLines([
                $"[{ColorScheme.CautionMarkup}]config.json cannot be read, so it cannot be edited here.[/]",
                "",
                .. read.Errors.Select(e => $"[{ColorScheme.CautionMarkup}]· {MarkupParser.Escape(e)}[/]"),
                "",
                $"[{ColorScheme.MutedMarkup}]Fix it in an editor, then reopen this dialog.[/]",
            ]);
            SetStatus("");
            _save.IsEnabled = false;
            _revert.IsEnabled = false;
            return;
        }

        _save.IsEnabled = true;
        _revert.IsEnabled = true;
        FillRail(select ?? _draft?.Name);
    }

    private IReadOnlyDictionary<string, int> TabsByInstance() =>
        _host.Apply.Manager.Sessions
            .Where(s => s.InstanceName is not null)
            .GroupBy(s => s.InstanceName!)
            .ToDictionary(g => g.Key, g => g.Count());

    private void FillRail(string? select)
    {
        var rows = ProviderRows.For(_settings!, TabsByInstance(), _draft?.IsDirty == true ? _draft.Name : null).ToList();

        // A NEW INSTANCE IS ON THE RAIL BEFORE IT IS IN THE FILE, marked new — the user sees where it will
        // be, and selecting another row asks about it like any unsaved edit.
        if (_draft is { IsNew: true } fresh)
            rows.Insert(0, new ProviderRow(fresh.Name, fresh.Model, "new", false, false, 0, Unsaved: true));
        _rail.ClearItems();
        foreach (var row in rows)
            _rail.AddItem(new ListItem(row.Preset == "new" ? NewRowText(row) : RowText(row)) { Tag = row.Name });

        var index = rows.FindIndex(r => r.Name == select);
        if (index < 0 && rows.Count > 0) index = 0;
        _lastIndex = index;
        if (index >= 0)
        {
            _rail.SelectedIndex = index;
            ShowInstance(rows[index].Name);
        }
        else
        {
            _draft = null;
            ShowLines([$"[{ColorScheme.MutedMarkup}]No providers yet. + Add picks where your model runs.[/]"]);
        }
    }

    /// <summary>
    /// Two lines: the name and its markers, then the model. ★ default, ◆ reviews auto mode, ● unsaved,
    /// and the open tabs on it — the facts that decide what a save or a removal will touch.
    /// </summary>
    private static string RowText(ProviderRow row)
    {
        var marks = string.Concat(
            row.Unsaved ? $" [{ColorScheme.CautionMarkup}]●[/]" : "",
            row.IsDefault ? " ★" : "",
            row.IsReviewer ? " ◆" : "",
            row.Tabs > 0 ? $" [{ColorScheme.MutedMarkup}]· {row.Tabs} tab{(row.Tabs == 1 ? "" : "s")}[/]" : "");
        return $"[bold]{MarkupParser.Escape(row.Name)}[/]{marks}\n[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(row.Model)}[/]";
    }

    private static string NewRowText(ProviderRow row) =>
        $"[bold]{MarkupParser.Escape(row.Name)}[/] [{ColorScheme.CautionMarkup}]new[/]\n[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(row.Model.Length > 0 ? row.Model : "no model yet")}[/]";

    private void OnRailSelectionChanged(object? sender, int index)
    {
        if (index < 0 || index == _lastIndex) return;
        if (_rail.Items.ElementAtOrDefault(index)?.Tag is not string name) return;
        if (_draft?.IsDirty == true && _draft.Name != name) { _ = AskBeforeLeavingAsync(name); return; }
        _lastIndex = index;
        ShowInstance(name);
    }

    /// <summary>
    /// Unsaved edits are asked about, never dropped by moving the selection. "Keep editing" puts the
    /// selection back on the edited row.
    /// </summary>
    private async Task AskBeforeLeavingAsync(string next)
    {
        var edited = _draft!.Name;
        var answer = await ConfirmDialog.AskAsync(_host.System, $"Unsaved changes to '{edited}'",
            ["Leaving this provider drops what you changed and did not save."], ["Keep editing", "Discard them"]);
        if (answer == "Discard them")
        {
            _draft = null;
            FillRail(next);
            return;
        }
        FillRail(edited);
    }

    // ---- the detail pane ------------------------------------------------------------------------

    private void ShowLines(IEnumerable<string> lines)
    {
        _detail.ClearContents();
        _detail.AddControl(new MarkupControl([.. lines]) { Wrap = true });
    }

    private void ShowInstance(string name)
    {
        if (_draft is { IsNew: true } fresh && fresh.Name == name)
        {
            BuildDetail(fresh);
            SetStatus($"[{ColorScheme.MutedMarkup}]Not saved yet — Save adds it to config.json.[/]");
            return;
        }
        if (_settings is null || !_settings.Providers.TryGetValue(name, out var cfg)) return;
        if (_draft?.Name != name) _draft = new InstanceDraft(name, cfg);
        _saveAnyway = false;
        _save.Text = "  Save  ";
        BuildDetail(_draft);
        SetStatus(TabsLine(name));
    }

    private string TabsLine(string name)
    {
        var tabs = TabsByInstance().TryGetValue(name, out var n) ? n : 0;
        return tabs == 0 ? "" : $"[{ColorScheme.MutedMarkup}]{(tabs == 1 ? "1 open tab" : $"{tabs} open tabs")} on '{MarkupParser.Escape(name)}' {(tabs == 1 ? "moves" : "move")} to a saved change when idle.[/]";
    }

    private void BuildDetail(InstanceDraft draft)
    {
        _detail.ClearContents();
        var preset = draft.IsNew ? _addingFrom?.DisplayName ?? draft.Original.Kind : ProviderRows.PresetLabel(draft.Original);
        _detail.AddControl(new MarkupControl([
            $"[bold]{MarkupParser.Escape(draft.IsNew ? "New provider" : draft.Name)}[/]  [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(preset == draft.Original.Kind ? preset : $"{preset} · {draft.Original.Kind}")}[/]",
            "",
        ]));

        var fields = new FieldGrid();

        // THE NAME IS SET ONCE, HERE. After this it is renamed with Rename, which moves every reference.
        if (draft.IsNew)
        {
            var nameBox = new PromptControl { Input = draft.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
            _firstField = nameBox;
            nameBox.InputChanged += (_, text) => { draft.Name = text.Trim(); Changed(); };
            fields.Add("Name", "What /model, the rail and agent types call it. Letters, digits, - and _.", nameBox);
        }

        // WHAT THIS PRESET IS TREATED AS, said where it matters: OpenAI cannot be asked whether a model
        // reasons, and llama.cpp's thinking switch is read from the server's chat template.
        if (_addingFrom?.Id == "openai" || (!draft.IsNew && draft.BaseUrl?.Contains("api.openai.com") == true))
            fields.Add("Reasoning", "OpenAI cannot say which models reason — tick this for one that does (the o-series, GPT-5).",
                Check("this model reasons", draft.Reasoning, v => draft.Reasoning = v));
        if (_addingFrom?.Id == "llamacpp")
            _detail.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Thinking on/off is read from the server's chat template once it answers.[/]", ""]) { Wrap = true });

        var url = new PromptControl { Input = draft.BaseUrl ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
        url.InputChanged += (_, text) => { draft.BaseUrl = text; Changed(); };
        fields.Add("Endpoint", draft.Original.Kind == "anthropic"
            ? "Anthropic's own unless set — a gateway in front of it goes here."
            : "The server's base URL, usually ending in /v1.", url);

        fields.Add("Model", "The model id this instance sends requests to.", ModelEditor(draft));
        fields.Add("API key", "Stored in config.json, readable only by you. Never shown again once saved.", KeyEditor(draft));
        fields.Add("Thinking", "The level new sessions start at; /think and F10 change it per session.", ThinkingEditor(draft));

        // FOLDED: these are set once, if ever, and an open section of them would push the fields people
        // actually change below the fold.
        var advanced = new CollapsiblePanel { Title = "Advanced", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        var more = new FieldGrid();
        more.Add("Context window", "Tokens; empty asks the endpoint on first use.",
            NumberEditor(draft.ContextWindow, v => draft.ContextWindow = v));
        more.Add("Max concurrent agents", "Sub-agents this instance runs at once when it is the default; empty for no limit.",
            NumberEditor(draft.MaxConcurrentAgents, v => draft.MaxConcurrentAgents = v));
        more.Add("Prompt caching", "Mark the prompt for caching — Anthropic, and OpenRouter in front of it.",
            Check("cache the prompt", draft.CacheControl, v => draft.CacheControl = v));
        more.Add("Reasoning", "OpenAI cannot say which models reason; tick this for one that does.",
            Check("this model reasons", draft.Reasoning, v => draft.Reasoning = v));
        more.Add("Thinking display", "Anthropic: summarized shows the thinking, omitted hides its text.", DisplayEditor(draft));
        more.Add("Extra headers", "One per line, Name: value — for gateways that route or bill by header. Enter to type, Esc when done.", HeadersEditor(draft));
        advanced.AddControl(more.Control);

        _detail.AddControl(fields.Control);
        _detail.AddControl(advanced);
    }

    /// <summary>
    /// The model in use, and Change… — which opens the picker over the dialog. The picker lists what
    /// the endpoint on screen serves, so a just-edited endpoint or key is what is asked.
    /// </summary>
    private IWindowControl ModelEditor(InstanceDraft draft)
    {
        var stack = new FieldGrid();
        var line = new MarkupControl([MarkupParser.Escape(draft.Model)]);
        var change = Controls.Button(" Change… ").OnClick((_, _) => _ = PickModelAsync()).Build();
        stack.AddBare(line);
        stack.AddBare(change);
        return stack.Control;

        async Task PickModelAsync()
        {
            var picked = await ModelPickerOverlay.PickAsync(_host.System, draft.Name, draft.ToConfig(), draft.Model);
            if (picked is null) return;
            _host.System.EnqueueOnUIThread(() =>
            {
                draft.Model = picked;
                line.SetContent([MarkupParser.Escape(picked)]);
                Changed();
            }, "providersTab.Picked");
        }
    }

    private IWindowControl KeyEditor(InstanceDraft draft)
    {
        var stack = new FieldGrid();
        var line = new MarkupControl([SecretLine(draft)]);
        var entry = new PromptControl { MaskCharacter = '•', Visible = false, HorizontalAlignment = HorizontalAlignment.Stretch, Prompt = "New key: " };
        entry.InputChanged += (_, text) => { draft.NewApiKey = text; draft.ClearApiKey = false; line.SetContent([SecretLine(draft)]); Changed(); };

        var set = Controls.Button(" Set… ").OnClick((_, _) => { entry.Visible = true; entry.Input = ""; }).Build();
        var clear = Controls.Button(" Clear ").OnClick((_, _) =>
        {
            draft.ClearApiKey = true;
            draft.NewApiKey = null;
            entry.Visible = false;
            line.SetContent([SecretLine(draft)]);
            Changed();
        }).Build();
        var buttons = new ToolbarControl { ItemSpacing = 1 };
        buttons.AddItem(set);
        buttons.AddItem(clear);

        stack.AddBare(line);
        stack.AddBare(buttons);
        stack.AddBare(entry);
        return stack.Control;
    }

    private static string SecretLine(InstanceDraft draft) =>
        draft.ClearApiKey ? $"[{ColorScheme.CautionMarkup}]will be removed on save[/]"
        : !string.IsNullOrEmpty(draft.NewApiKey) ? $"[{ColorScheme.CautionMarkup}]new key typed — saved with Save[/]"
        : MarkupParser.Escape(SecretField.Describe(draft.Original.ApiKey));

    private IWindowControl ThinkingEditor(InstanceDraft draft)
    {
        var dropdown = new DropdownControl("") { HorizontalAlignment = HorizontalAlignment.Left };
        var note = new MarkupControl([$"[{ColorScheme.MutedMarkup}]asking the model which levels it offers…[/]"]);
        var stack = new FieldGrid();
        stack.AddBare(dropdown);
        stack.AddBare(note);

        // THE LEVELS COME FROM THE MODEL, asked off the UI thread through the registry's provider —
        // the same probe /think and F10 read, so the dialog never offers a level a request would refuse.
        _ = Task.Run(async () =>
        {
            if (_host.Apply.Current.Value.Providers?.TryGet(draft.Name, out var provider) == true
                && provider is IThinkingProvider thinker)
                return await thinker.GetThinkingOptionsAsync();
            return ThinkingOptions.None("this provider has no thinking levels");
        }).ContinueWith(t => _host.System.EnqueueOnUIThread(() =>
        {
            var options = t.IsCompletedSuccessfully ? t.Result : ThinkingOptions.None("the model did not say");
            if (!options.Any)
            {
                note.SetContent([$"[{ColorScheme.MutedMarkup}]no levels: {MarkupParser.Escape(options.Why ?? "")}[/]"]);
                dropdown.Visible = false;
                return;
            }
            dropdown.ClearItems();
            dropdown.AddItem(ThinkingLevels.Default);
            foreach (var level in options.Levels) dropdown.AddItem(level);
            dropdown.SelectedIndex = draft.Thinking is { } now ? Math.Max(0, options.Levels.ToList().IndexOf(now) + 1) : 0;
            dropdown.SelectedValueChanged += (_, value) =>
            {
                draft.Thinking = value is null or ThinkingLevels.Default ? null : value;
                Changed();
            };
            note.SetContent([$"[{ColorScheme.MutedMarkup}]default is the model's own; nothing is sent[/]"]);
        }, "providersTab.Levels"), TaskScheduler.Default);

        return stack.Control;
    }

    private IWindowControl DisplayEditor(InstanceDraft draft)
    {
        var dropdown = new DropdownControl("", ["summarized", "omitted"]) { HorizontalAlignment = HorizontalAlignment.Left };
        dropdown.SelectedIndex = draft.ThinkingDisplay == ThinkingDisplay.Omitted ? 1 : 0;
        dropdown.SelectedValueChanged += (_, value) =>
        {
            draft.ThinkingDisplay = value == "omitted" ? ThinkingDisplay.Omitted : ThinkingDisplay.Summarized;
            Changed();
        };
        return dropdown;
    }

    private IWindowControl NumberEditor(int? value, Action<int?> set)
    {
        var prompt = new PromptControl { Input = value?.ToString() ?? "", InputWidth = 12 };
        prompt.InputChanged += (_, text) =>
        {
            set(int.TryParse(text.Trim(), out var n) && n > 0 ? n : null);
            Changed();
        };
        return prompt;
    }

    private IWindowControl Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckboxControl(label, value);
        box.CheckedChanged += (_, on) => { set(on); Changed(); };
        return box;
    }

    private IWindowControl HeadersEditor(InstanceDraft draft)
    {
        var editor = new MultilineEditControl(viewportHeight: 3) { HorizontalAlignment = HorizontalAlignment.Stretch };
        editor.SetContent(string.Join("\n", (draft.ExtraHeaders ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}: {kv.Value}")));
        editor.ContentChanged += (_, _) =>
        {
            var headers = new Dictionary<string, string>();
            foreach (var line in editor.GetContent().Split('\n'))
                if (line.IndexOf(':') is var at and > 0)
                    headers[line[..at].Trim()] = line[(at + 1)..].Trim();
            draft.ExtraHeaders = headers;
            Changed();
        };
        return editor;
    }

    /// <summary>A field changed: the rail's dot, and a "Save anyway" that no longer applies.</summary>
    private void Changed()
    {
        if (_saveAnyway) { _saveAnyway = false; _save.Text = "  Save  "; }
        if (_settings is not null && _draft is not null)
        {
            var rows = ProviderRows.For(_settings, TabsByInstance(), _draft.IsDirty ? _draft.Name : null);
            var i = rows.ToList().FindIndex(r => r.Name == _draft.Name);
            if (i >= 0 && _rail.Items.ElementAtOrDefault(i) is { } item) item.Text = RowText(rows[i]);
        }
    }

    private void SetStatus(string markup) => _status.SetContent([markup]);

    private void Revert()
    {
        if (_draft is { IsNew: true }) { CancelNew(); return; }
        if (_draft is null || _settings is null) return;
        var name = _draft.Name;
        _draft = null;
        ShowInstance(name);
        Changed();
    }

    // ---- the toolbar's actions on the selected instance -----------------------------------------------

    /// <summary>The saved instance the toolbar acts on, or null — with the reason said — when there is none.</summary>
    private string? Selected()
    {
        if (_draft is null || _draft.IsNew || _settings is null)
        {
            SetStatus($"[{ColorScheme.MutedMarkup}]Select a saved provider first.[/]");
            return null;
        }
        return _draft.Name;
    }

    /// <summary>
    /// Every session refreshes what the default decides — the sub-agent limit — in place; nobody moves,
    /// because a running tab is on its own instance, not on "the default".
    /// </summary>
    private async Task MakeDefaultAsync()
    {
        if (Selected() is not { } name) return;
        if (_settings!.DefaultProvider == name) { SetStatus($"[{ColorScheme.MutedMarkup}]'{MarkupParser.Escape(name)}' is already the default.[/]"); return; }
        await CommitAsync(root => ProviderEdits.SetDefault(root, name),
            new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true, $"{name} is now the default"), name);
    }

    /// <summary>
    /// On the reviewer it clears (auto mode is then not offered); on any other instance it makes that one
    /// the reviewer. Set on each session's policy in place — the reviewer's requests are its own, so no
    /// tab's cache is touched.
    /// </summary>
    private async Task ToggleReviewerAsync()
    {
        if (Selected() is not { } name) return;
        var clearing = _settings!.Classifier == name;
        await CommitAsync(root => ProviderEdits.SetReviewer(root, clearing ? null : name),
            new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true,
                clearing ? "auto-mode reviewer cleared" : $"{name} now reviews auto mode"), name);
    }

    /// <summary>The new name, asked in the pane; Enter renames, a clash is said beside it.</summary>
    private void ShowRename()
    {
        if (Selected() is not { } from) return;
        _detail.ClearContents();
        var box = new PromptControl { Prompt = "New name: ", Input = from, HorizontalAlignment = HorizontalAlignment.Stretch };
        _detail.AddControl(new MarkupControl([
            $"[bold]Rename '{MarkupParser.Escape(from)}'[/]",
            $"[{ColorScheme.MutedMarkup}]Every reference moves with it — the default, the reviewer, agent types — and tabs on it follow the new name.[/]",
            "",
        ]) { Wrap = true });
        _detail.AddControl(box);
        _detail.AddControl(new MarkupControl(["", $"[{ColorScheme.MutedMarkup}]Enter renames · Revert cancels[/]"]));
        box.Entered += (_, text) => _ = RenameAsync(from, text.Trim());
        _window()?.FocusManager.SetFocus(box, FocusReason.Programmatic);
    }

    private async Task RenameAsync(string from, string to)
    {
        if (to.Length == 0 || to == from) { ShowInstance(from); return; }
        if (_settings!.Providers.ContainsKey(to)) { SetStatus($"[{ColorScheme.CautionMarkup}]'{MarkupParser.Escape(to)}' already exists.[/]"); return; }

        // EVERYONE when a session-wide reference names it: the reviewer and agent types are rebuilt per
        // session, and the default's limit sizes every sub-agent semaphore. Tabs on it move in place —
        // the same model under a new name sends the same request.
        var wide = _settings.Classifier == from || _settings.DefaultProvider == from
            || _settings.AgentTypes.Values.Any(t => t.Provider == from);
        _draft = null;
        await CommitAsync(root => ProviderEdits.Rename(root, from, to),
            new ConfigChange(new HashSet<string> { from }, new Dictionary<string, string> { [from] = to }, wide, $"{from} is now {to}"), to);
    }

    private async Task RemoveAsync()
    {
        if (Selected() is not { } name) return;
        var tabs = _host.Apply.Manager.Sessions
            .Where(s => s.InstanceName == name)
            .Select(s => Path.GetFileName(s.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar)))
            .ToList();
        var plan = ProviderRemovalPlan.For(_settings!, name, tabs);
        if (plan.Refused)
        {
            SetStatus($"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(plan.Lines[0])}[/]");
            return;
        }

        var answer = await ConfirmDialog.AskAsync(_host.System, $"Remove '{name}'?", plan.Lines, ["Keep it", "Remove"]);
        if (answer != "Remove") return;
        _draft = null;
        await CommitAsync(root => ProviderEdits.Remove(root, name),
            new ConfigChange(new HashSet<string> { name }, new Dictionary<string, string>(), true, $"{name} was removed"), "");
    }

    // ---- adding: the gallery ----------------------------------------------------------------------

    private ProviderKindCatalog.ProviderPreset? _addingFrom;

    /// <summary>
    /// The preset cards, in the detail pane. A card is a bordered panel with a button — Tab reaches it,
    /// Enter or a click picks it — and one muted line saying what it is.
    /// </summary>
    public void ShowGallery(string? lead = null)
    {
        if (_settings is null) return;
        _detail.ClearContents();
        _detail.AddControl(new MarkupControl([
            "[bold]Add a provider[/]",
            .. lead is null ? Array.Empty<string>() : [$"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(lead)}[/]"],
            "",
        ]) { Wrap = true });

        var grid = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        grid.ColumnDefinitions.Add(GridLength.Cells(2));
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        var presets = ProviderKindCatalog.Presets;
        ButtonControl? first = null;
        for (var i = 0; i < presets.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(GridLength.Auto());
            var (card, button) = Card(presets[i]);
            first ??= button;
            grid.Place(card, i / 2, i % 2 == 0 ? 0 : 2);
        }
        _detail.AddControl(grid);
        _detail.AddControl(new MarkupControl(["", $"[{ColorScheme.MutedMarkup}]Tab moves between cards · Enter picks one[/]"]));
        SetStatus("");

        // ON THE FIRST CARD: the gallery is a choice to make, so the keys start where the choice is.
        if (first is not null) _window()?.FocusManager.SetFocus(first, FocusReason.Programmatic);
    }

    private (IWindowControl Card, ButtonControl Button) Card(ProviderKindCatalog.ProviderPreset preset)
    {
        var card = new PanelControl { BorderStyle = BorderStyle.Rounded, HorizontalAlignment = HorizontalAlignment.Stretch };
        var button = Controls.Button($" {preset.DisplayName} ").OnClick((_, _) => StartNew(preset)).Build();
        card.AddControl(button);
        card.AddControl(new MarkupControl([$" [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(preset.Blurb)}[/]"]));
        return (card, button);
    }

    private void StartNew(ProviderKindCatalog.ProviderPreset preset)
    {
        _addingFrom = preset;
        _draft = PresetGallery.DraftFor(preset, PresetGallery.SuggestName(_settings!.Providers.Keys, preset));
        FillRail(_draft.Name);

        // ON THE NAME: the card that had focus is gone with the gallery, and keys sent to it go nowhere.
        if (_firstField is { } field) _window()?.FocusManager.SetFocus(field, FocusReason.Programmatic);
    }

    private void CancelNew()
    {
        _draft = null;
        _addingFrom = null;
        FillRail(null);
        FocusRail();
    }

    /// <summary>
    /// FOCUS BACK ON THE RAIL after the pane is rebuilt: the control that had focus — a name box, a
    /// button — is gone with the old pane, and keys sent to a control no longer on screen go nowhere,
    /// Escape included.
    /// </summary>
    private void FocusRail() => _window()?.FocusManager.SetFocus(_rail, FocusReason.Programmatic);

    // ---- saving -------------------------------------------------------------------------------

    /// <summary>
    /// Save: a reachability check, then the write, then the change made live.
    ///
    /// <para>THE CHECK LISTS MODELS, IT DOES NOT CHAT. Listing proves the endpoint answers and accepts
    /// the key, costs no tokens, and is what the model field needs anyway. A failure is not a refusal:
    /// a server that is down now may be up later, so the reason is shown and Save becomes
    /// <em>Save anyway</em>.</para>
    /// </summary>
    private async Task SaveAsync()
    {
        if (_saving || _draft is not { } draft || _settings is not { } settings) return;
        if (!draft.IsDirty && !draft.IsNew) { SetStatus($"[{ColorScheme.MutedMarkup}]Nothing to save.[/]"); return; }
        if (draft.IsNew && (string.IsNullOrWhiteSpace(draft.Name) || settings.Providers.ContainsKey(draft.Name)))
        { SetStatus($"[{ColorScheme.CautionMarkup}]Choose a name no other provider has.[/]"); return; }
        if (string.IsNullOrWhiteSpace(draft.Model)) { SetStatus($"[{ColorScheme.CautionMarkup}]A model is required.[/]"); return; }

        _saving = true;
        try
        {
            if (!_saveAnyway)
            {
                SetStatus($"[{ColorScheme.MutedMarkup}]Checking the endpoint…[/]");
                var cfg = draft.ToConfig();
                using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                ModelCatalogResult listing;
                try { listing = await Task.Run(() => ModelCatalogQuery.ListAsync(draft.Name, cfg, bounded.Token)); }
                catch (OperationCanceledException) { listing = new ModelCatalogResult([], "the endpoint did not answer within 6 seconds"); }
                if (listing.Error is { } why)
                {
                    _saveAnyway = true;
                    _save.Text = " Save anyway ";
                    SetStatus($"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(why)}[/] [{ColorScheme.MutedMarkup}]— Save anyway writes it regardless.[/]");
                    return;
                }
            }

            SetStatus($"[{ColorScheme.MutedMarkup}]Saving…[/]");
            var fields = draft.ToFields();
            if (draft.IsNew)
            {
                // NOBODY IS ON A NEW INSTANCE, so no tab moves — unless it is the first one, which the edit
                // makes the default, and the default's limit sizes every session's sub-agent semaphore.
                var added = new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(),
                    settings.DefaultProvider is null, $"{draft.Name} added");
                var name = draft.Name;
                await CommitAsync(root => ProviderEdits.Add(root, name, fields), added, name);
                _addingFrom = null;
                return;
            }
            var change = draft.ChangeFor(settings);
            await CommitAsync(root => ProviderEdits.Update(root, draft.Name, fields), change, draft.Name);
        }
        finally { _saving = false; }
    }

    /// <summary>
    /// Writes the edit off the UI thread, asks how to treat the tabs whose cache it would break, then makes
    /// it live — the one path every save on this tab takes.
    /// </summary>
    internal async Task CommitAsync(Action<System.Text.Json.Nodes.JsonObject> edit, ConfigChange change, string select)
    {
        var prepared = await Task.Run(() => ConfigApply.Prepare(_host.Apply, edit));
        var done = new TaskCompletionSource();
        _host.System.EnqueueOnUIThread(async () =>
        {
            try
            {
                var choice = CacheChoice.ApplyNow;
                if (prepared.Fresh is { } fresh && _host.Apply.Manager.BreaksCache(fresh, change) is { Count: > 0 } breaks)
                    choice = await ApplyToTabsDialog.AskAsync(_host.System, change.Note + ".", breaks);
                ConfigApply.Commit(_host.Apply, prepared, change, choice);

                // THE FIRST PROVIDER, ON FIRST RUN: the startup session has no instance for a rebind to
                // find, so it is wired onto the new config here, once.
                if (prepared.Fresh is { } wired && _host.WireFirst is { } wireFirst && !_wiredFirst)
                {
                    _wiredFirst = true;
                    wireFirst(wired);
                }
                Saved(prepared.Write, select, change);
            }
            finally { done.TrySetResult(); }
        }, "providersTab.Commit");
        await done.Task;
    }

    /// <summary>After a write: the errors beside the field, or the rail rebuilt from the new file.</summary>
    internal void Saved(ConfigWrite result, string select, ConfigChange change)
    {
        if (!result.Saved)
        {
            SetStatus(string.Join("\n", result.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
            return;
        }

        _draft = null;
        Reload(select);
        FocusRail();
        // WHAT HAPPENED TO THE OPEN TABS, in the terms that cost something: a change no model sees is
        // applied in place, and only one that breaks a tab's cache is a move.
        var moved = _host.Apply.Manager.Sessions.Count(s => s.InstanceName is { } n && change.ModelChanged.Contains(n));
        var tabs = moved == 0 ? "Applied to open tabs in place — no prompt cache is touched."
            : $"{moved} tab{(moved == 1 ? "" : "s")} on it move{(moved == 1 ? "s" : "")} as you chose; the prompt cache rebuilds.";
        SetStatus(result.Errors.Count > 0
            ? $"[{ColorScheme.CautionMarkup}]Saved, but not live: {MarkupParser.Escape(string.Join("; ", result.Errors))}[/]"
            : $"[{ColorScheme.AffirmativeMarkup}]✔ Saved.[/] [{ColorScheme.MutedMarkup}]{tabs}[/]");
    }
}

/// <summary>
/// One column of fields, read downward: a bold label, a muted line saying what it is for, the editor,
/// then a blank line. Hand-placed rather than generated, because a generated form cannot give the
/// spacing this needs.
/// </summary>
internal sealed class FieldGrid
{
    private int _row;

    public GridControl Control { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    public FieldGrid() => Control.ColumnDefinitions.Add(GridLength.Star(1, min: 24));

    public void Add(string label, string what, IWindowControl editor)
    {
        AddBare(new MarkupControl([$"[bold]{MarkupParser.Escape(label)}[/]"]));
        AddBare(new MarkupControl([$"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(what)}[/]"]) { Wrap = true });
        AddBare(editor);
        Control.RowDefinitions.Add(GridLength.Cells(1));
        _row++;
    }

    /// <summary>Empties the grid, for a list that is rebuilt in place.</summary>
    public void Clear()
    {
        foreach (var child in Control.Children.ToList()) Control.RemoveControl(child);
        Control.RowDefinitions.Clear();
        _row = 0;
    }

    public void AddBare(IWindowControl control)
    {
        Control.RowDefinitions.Add(GridLength.Auto());
        Control.Place(control, _row++, 0);
    }
}
