using CxAgent.Core.Colleagues;
using CxAgent.Core.Config;
using CxAgent.Core.Llm;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>
/// The Colleagues tab: Settings first — the switch and the rules every colleague keeps — then each colleague in the
/// library, with where it runs and where it distils.
///
/// <para>TWO FILES BEHIND ONE TAB. The switch is config.json's <c>colleagues.enabled</c>, written through General's
/// draft so only it changes, and read as a tab opens. A colleague's models are its own record in the library, read at its
/// next bring. The footer and the status line say which a save reached.</para>
///
/// <para>THE BROWSER'S PAGE IS BUILT FROM THE SAME DRAFTS (WebConfigForms.Colleagues), so the two cannot offer
/// different choices or write different things.</para>
/// </summary>
public sealed class ColleaguesTab
{
    private const int RailCells = 30;
    private const string SettingsItem = "";

    private readonly ConfigDialogHost _host;
    private readonly Func<Window?> _window;
    private readonly GridControl _root;
    private readonly ListControl _rail;
    private readonly ScrollablePanelControl _detail;
    private readonly MarkupControl _status;
    private readonly ButtonControl _save;
    private readonly ButtonControl _revert;
    private ColleagueStore Library => new(_host.Apply.Paths.ConfigDir);

    private ProviderSettings? _settings;
    private GeneralDraft? _general;
    private ColleagueDraft? _draft;
    private string _shown = SettingsItem;
    private int _lastIndex = -1;
    private bool _saving;

    public ColleaguesTab(ConfigDialogHost host, Func<Window?> window)
    {
        _host = host;
        _window = window;

        _root = new GridControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _root.ColumnDefinitions.Add(GridLength.Cells(RailCells, min: 22));
        _root.ColumnDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Star(1));    // rail | detail
        _root.RowDefinitions.Add(GridLength.Cells(2));   // status — two lines, so a long one is not cut

        _rail = new ListControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _rail.SelectedIndexChanged += OnRailSelectionChanged;
        ListDetail.StyleRail(_rail);
        _root.Place(_rail, 0, 0, rowSpan: 2);
        ListDetail.Divider(_root);

        _detail = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill, Margin = ListDetail.DetailInset };
        _root.Place(_detail, 0, 1);
        _status = new MarkupControl([""]) { Wrap = true, Margin = ListDetail.DetailInset };
        _root.Place(_status, 1, 1);

        // REVERT AND SAVE BELONG TO THE TAB, SHOWN IN THE DIALOG'S FOOTER while this tab is in front.
        _revert = Controls.Button(" Revert ").OnClick((_, _) => Reload(_shown)).Build();
        _save = DialogLook.MainButton(Controls.Button(" Save ").OnClick((_, _) => _ = SaveAsync()).Build(), main: false);

        Reload(SettingsItem);
    }

    public IWindowControl Content => _root;
    public ListControl Rail => _rail;

    /// <summary>Revert and Save, for the dialog's footer to host while this tab is in front.</summary>
    public IReadOnlyList<ButtonControl> Actions => [_revert, _save];

    public bool HasUnsaved => _general?.IsDirty == true || _draft?.IsDirty == true;

    /// <summary>What a close asks about: the colleague being edited, or the settings.</summary>
    public string SelectedName => _draft?.Name ?? "colleague settings";

    public void MarkSave() => DialogLook.MainButton(_save, HasUnsaved);
    public void SaveFromKey() => _ = SaveAsync();

    private int PaneWidth() => ListDetail.PaneWidth(_window()?.Width ?? Console.WindowWidth, RailCells);

    /// <summary>The rail and the pane rebuilt from config.json and the library as they are now.</summary>
    public void Reload(string select)
    {
        var read = ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env);
        _settings = read.Settings;
        _general = null;
        _draft = null;
        _rail.ClearItems();
        if (_settings is null)
        {
            _detail.ClearContents();
            _detail.AddControl(new MarkupControl([
                $"[{ColorScheme.CautionMarkup}]config.json cannot be read, so it cannot be edited here.[/]",
                "", .. read.Errors.Select(e => $"[{ColorScheme.CautionMarkup}]· {MarkupParser.Escape(e)}[/]"),
            ]) { Wrap = true });
            _save.IsEnabled = false;
            return;
        }
        _save.IsEnabled = true;

        var muted = ColorScheme.MutedMarkup;
        _rail.AddItem(new ListItem($"[bold]Settings[/] [{muted}]every colleague[/]") { Tag = SettingsItem });
        foreach (var r in Library.List())
            _rail.AddItem(new ListItem($"[bold]◆ {MarkupParser.Escape(r.Name)}[/] [{muted}]{MarkupParser.Escape(r.Instance ?? "session's")}[/]") { Tag = r.Name });

        var index = _rail.Items.ToList().FindIndex(i => (string?)i.Tag == select);
        if (index < 0) index = 0;
        _lastIndex = index;
        _rail.SelectedIndex = index;
        Show((string)_rail.Items[index].Tag!);
        ListDetail.Mark(_rail);
    }

    private void OnRailSelectionChanged(object? sender, int index)
    {
        if (index < 0 || index == _lastIndex) return;
        if (_rail.Items.ElementAtOrDefault(index)?.Tag is not string name) return;
        if (HasUnsaved) { _ = AskBeforeLeavingAsync(name); return; }
        _lastIndex = index;
        Show(name);
    }

    private async Task AskBeforeLeavingAsync(string next)
    {
        var answer = await ConfirmDialog.AskAsync(_host.System, $"Unsaved changes to {SelectedName}",
            ["Leaving drops what you changed and did not save."], ["Keep editing", "Discard them"]);
        Reload(answer == "Discard them" ? next : _shown);
    }

    private void Show(string item)
    {
        _shown = item;
        _detail.ClearContents();
        _general = null;
        _draft = null;
        SetStatus("");
        if (item == SettingsItem) ShowSettings();
        else if (Library.Find(item) is { } record) ShowColleague(record);
        MarkSave();
    }

    // EACH CHOICE WITH WHAT IT MEANS UNDER IT, as General's tab draws them.
    private static MarkupControl Help(string text) => new([$"[{ColorScheme.MutedMarkup}]    {MarkupParser.Escape(text)}[/]"]) { Wrap = true };
    private static MarkupControl Label(string text) => new([$"[bold]{MarkupParser.Escape(text)}[/]"]);

    private void ShowSettings()
    {
        var draft = _general = GeneralDraft.From(_settings!);
        _detail.AddControl(ListDetail.Header("Colleagues", "for every colleague — saved to config.json, read when a tab opens", PaneWidth()).Control);

        // THE SAME WORDS AS THE BROWSER'S PAGE (WebConfigForms.Colleagues) — both write GeneralDraft.ColleaguesEnabled.
        var kept = new FieldGrid();
        var colleagues = new RadioGroup<bool>();
        kept.AddBare(Label("Workers kept across sessions"));
        kept.AddBare(new RadioControl<bool>(colleagues, true, "Keep colleagues"));
        kept.AddBare(Help(GeneralDraft.ColleaguesOnHelp));
        kept.AddBare(new MarkupControl([""]));
        kept.AddBare(new RadioControl<bool>(colleagues, false, "No colleagues"));
        kept.AddBare(Help(GeneralDraft.ColleaguesOffHelp));
        colleagues.SelectedValue = draft.ColleaguesEnabled;
        colleagues.SelectionChanged += (_, on) => { draft.ColleaguesEnabled = on; MarkSave(); };
        _detail.AddControl(ListDetail.Section("Colleagues", kept.Control));
        _detail.AddControl(ListDetail.Gap());

        // THE SAME WORDS AS THE BROWSER'S PAGE — both write GeneralDraft.ColleagueSuggestions.
        var offers = new FieldGrid();
        var suggest = new RadioGroup<bool>();
        offers.AddBare(Label("Offer to keep a worker"));
        offers.AddBare(new RadioControl<bool>(suggest, true, "Suggest"));
        offers.AddBare(Help(GeneralDraft.SuggestOnHelp));
        offers.AddBare(new MarkupControl([""]));
        offers.AddBare(new RadioControl<bool>(suggest, false, "Don't suggest"));
        offers.AddBare(Help(GeneralDraft.SuggestOffHelp));
        suggest.SelectedValue = draft.ColleagueSuggestions;
        suggest.SelectionChanged += (_, on) => { draft.ColleagueSuggestions = on; MarkSave(); };
        _detail.AddControl(ListDetail.Section("Suggestions", offers.Control));
        _detail.AddControl(ListDetail.Gap());

        // SHOWN, NOT SET: these hold every colleague to what keeps its experience from crowding out its task.
        var rules = new MarkupControl([.. ColleagueDraft.Rules.Select(r => $"· {MarkupParser.Escape(r)}")]) { Wrap = true };
        _detail.AddControl(ListDetail.Section("Rules every colleague keeps", rules));
        _detail.AddControl(ListDetail.Gap());
        _detail.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]The library: {MarkupParser.Escape(Path.Combine(_host.Apply.Paths.ConfigDir, "colleagues"))}[/]"]) { Wrap = true });
    }

    private void ShowColleague(ColleagueRecord record)
    {
        var draft = _draft = ColleagueDraft.For(record);
        _detail.AddControl(ListDetail.Header($"◆ {record.Name}", "saved to its record in the library, used at its next bring", PaneWidth()).Control);

        var line = ColleagueLine.Of(record);
        var about = new List<string>
        {
            MarkupParser.Escape(record.Description.Length > 0 ? record.Description : "Not distilled yet."),
            $"[{ColorScheme.MutedMarkup}]v{record.ExperienceVersion}{(line.Knows.Count > 0 ? " · knows " + MarkupParser.Escape(string.Join(", ", line.Knows)) : "")}[/]",
            $"[{ColorScheme.MutedMarkup}]Its description is rewritten by every distilling, from what it has learned.[/]",
        };
        _detail.AddControl(ListDetail.Section("What it is", new MarkupControl(about) { Wrap = true }));
        _detail.AddControl(ListDetail.Gap());

        // ITS NAME, AS THE BROWSER'S PAGE HAS IT — saved through the same rename as /colleagues rename.
        var naming = new FieldGrid();
        var nameBox = ListDetail.Input(new PromptControl { Input = record.Name, HorizontalAlignment = HorizontalAlignment.Stretch });
        nameBox.InputChanged += (_, text) => { draft.NewName = text; MarkSave(); };
        naming.Add("Name", ColleagueDraft.NameHelp, nameBox);
        _detail.AddControl(ListDetail.Section("Name", naming.Control));
        _detail.AddControl(ListDetail.Gap());

        var options = ColleagueDraft.Options(_settings!, draft);
        var models = new FieldGrid();
        models.Add("Runs on", ColleagueDraft.ModelHelp, Picker(ColleagueDraft.SessionModel, options, draft.Model, v => draft.Model = v));
        models.Add("Distils on", ColleagueDraft.DistillHelp, Picker(ColleagueDraft.OwnModel, options, draft.DistillModel, v => draft.DistillModel = v));
        _detail.AddControl(ListDetail.Section("Models", models.Control));
        _detail.AddControl(ListDetail.Gap());

        // THE SAME WORDS AS THE BROWSER'S PAGE (WebConfigForms.Colleagues) — both set ColleagueDraft.Frozen.
        var learning = new FieldGrid();
        var frozen = new RadioGroup<bool>();
        learning.AddBare(new RadioControl<bool>(frozen, false, "Learns"));
        learning.AddBare(Help(ColleagueDraft.LearnsHelp));
        learning.AddBare(new MarkupControl([""]));
        learning.AddBare(new RadioControl<bool>(frozen, true, "Frozen"));
        learning.AddBare(Help(ColleagueDraft.FrozenHelp));
        frozen.SelectedValue = draft.Frozen;
        frozen.SelectionChanged += (_, on) => { draft.Frozen = on; MarkSave(); };
        _detail.AddControl(ListDetail.Section("Learning", learning.Control));
    }

    /// <summary>A dropdown whose first choice is the inherited one (null) and the rest the entries by name.</summary>
    private DropdownControl Picker(string inherited, IReadOnlyList<ColleagueModelOption> options, string? value, Action<string?> set)
    {
        var labels = options.Select(o => o.Label).Prepend(inherited).ToList();
        var box = new DropdownControl("", labels) { HorizontalAlignment = HorizontalAlignment.Left };
        var at = value is null ? 0 : options.ToList().FindIndex(o => o.Value == value) + 1;
        box.SelectedIndex = Math.Max(0, at);
        box.SelectedIndexChanged += (_, i) => { set(i <= 0 ? null : options[i - 1].Value); MarkSave(); };
        return box;
    }

    private async Task SaveAsync()
    {
        if (_saving || _settings is null) return;
        if (!HasUnsaved) { SetStatus($"[{ColorScheme.MutedMarkup}]Nothing to save.[/]"); return; }
        if (_draft is { } draft)
        {
            var sessions = new Core.Storage.FolderSessionStore(_host.Apply.Paths);
            if ((draft.Problem(_settings) ?? draft.RenameProblem(Library, sessions)) is { } problem)
            {
                SetStatus($"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(problem)}[/]");
                return;
            }
            // THE LIBRARY, NOT config.json: nothing running moves, and its next bring reads it.
            string? refused;
            try { refused = draft.Apply(Library, sessions); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SetStatus($"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e.Message)}[/]");
                return;
            }
            if (refused is not null) { SetStatus($"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(refused)}[/]"); return; }
            var name = draft.SavedName;
            Reload(name);
            SetStatus($"[{ColorScheme.AffirmativeMarkup}]✔ {MarkupParser.Escape(name)} saved.[/] [{ColorScheme.MutedMarkup}]Its next bring uses it; one working now keeps its model.[/]");
            return;
        }
        if (_general is not { } general) return;
        _saving = true;
        try
        {
            var prepared = await Task.Run(() => ConfigApply.Prepare(_host.Apply, general.Apply));
            _host.System.EnqueueOnUIThread(() =>
            {
                if (!prepared.Write.Saved)
                {
                    SetStatus(string.Join("\n", prepared.Write.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
                    return;
                }
                general.Saved();
                MarkSave();
                if (prepared.Fresh is null)
                {
                    SetStatus(string.Join("\n", prepared.Write.Errors.Select(e => $"[{ColorScheme.CautionMarkup}]Saved, not live: {MarkupParser.Escape(e)}[/]")));
                    return;
                }
                ConfigApply.Commit(_host.Apply, prepared,
                    new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), false, "colleagues switched"), CacheChoice.ApplyNow);
                SetStatus($"[{ColorScheme.AffirmativeMarkup}]✔ Saved.[/] [{ColorScheme.MutedMarkup}]Tabs opened from now on use it.[/]");
            }, "colleaguesTab.Save");
        }
        finally { _saving = false; }
    }

    private void SetStatus(string markup) => _status.SetContent([markup]);
}
