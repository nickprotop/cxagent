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
/// The General tab: the app-wide settings that are not a provider, a server or an agent type — what a slow
/// command gets, how far a request may run, when a conversation compacts, and which tools the session's agent
/// is offered.
///
/// <para>ONE PAGE, NO LIST: there is nothing to choose between, so it is the detail pane alone, grouped in the
/// panels the other tabs use.</para>
///
/// <para>READ WHEN A TAB OPENS. Each of these is taken by a session as it starts, so a save reaches the tabs
/// opened after it; the dialog's footer says so while this tab is in front. Changing a running tab's tools would
/// also break its prompt cache, which is the cost a later tab does not pay.</para>
/// </summary>
public sealed class GeneralTab
{
    private readonly ConfigDialogHost _host;
    private readonly Func<Window?> _window;
    private readonly GridControl _root;
    private readonly ScrollablePanelControl _detail;
    private readonly MarkupControl _status;
    private readonly ButtonControl _revert;
    private readonly ButtonControl _save;
    private GeneralDraft? _draft;
    private bool _saving;

    public GeneralTab(ConfigDialogHost host, Func<Window?> window)
    {
        _host = host;
        _window = window;

        _root = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill };
        _root.ColumnDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Auto());
        _detail = new ScrollablePanelControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill, Margin = ListDetail.DetailInset };
        _root.Place(_detail, 0, 0);
        _status = new MarkupControl([""]) { Wrap = true, Margin = ListDetail.DetailInset };
        _root.Place(_status, 1, 0);

        // REVERT AND SAVE BELONG TO THE TAB, SHOWN IN THE DIALOG'S FOOTER while this tab is in front.
        _revert = Controls.Button(" Revert ").OnClick((_, _) => Reload()).Build();
        _save = DialogLook.MainButton(Controls.Button(" Save ").OnClick((_, _) => _ = SaveAsync()).Build(), main: false);

        Reload();
    }

    public IWindowControl Content => _root;

    /// <summary>Revert and Save, for the dialog's footer to host while this tab is in front.</summary>
    public IReadOnlyList<ButtonControl> Actions => [_revert, _save];

    /// <summary>The accent on Save only when there is something to save.</summary>
    public void MarkSave() => DialogLook.MainButton(_save, _draft?.IsDirty == true);

    public bool HasUnsaved => _draft?.IsDirty == true;
    public void SaveFromKey() => _ = SaveAsync();

    /// <summary>The pane rebuilt from config.json as it is now — what Revert and a save both come back to.</summary>
    public void Reload()
    {
        var read = ProvidersFile.Read(_host.Apply.Paths, _host.Apply.Env);
        _detail.ClearContents();
        if (read.Settings is not { } settings)
        {
            _draft = null;
            SetStatus(string.Join("\n", read.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
            MarkSave();
            return;
        }
        _draft = GeneralDraft.From(settings, read.TavilyKeyAsWritten);
        Fill(_draft);
        SetStatus("");
        MarkSave();
    }

    private int PaneWidth() => (_window()?.Width ?? Console.WindowWidth) - 2 - 4 - 1 - 1;

    private void Fill(GeneralDraft draft)
    {
        var muted = ColorScheme.MutedMarkup;
        _detail.AddControl(ListDetail.Header("General", "How every tab works — saved to config.json, read when a tab opens", PaneWidth()).Control);

        // EACH CHOICE WITH WHAT IT COSTS UNDER IT, so the trade is read before it is made.
        MarkupControl Help(string text) => new([$"[{muted}]    {MarkupParser.Escape(text)}[/]"]) { Wrap = true };
        MarkupControl Label(string text) => new([$"[bold]{MarkupParser.Escape(text)}[/]"]);
        MarkupControl Blank() => new([""]);

        var commands = new FieldGrid();
        var detach = new RadioGroup<bool>();
        commands.AddBare(Label("When a command outlasts its timeout"));
        commands.AddBare(new RadioControl<bool>(detach, true, "Hand it to the background"));
        commands.AddBare(Help("The call returns the pid and where its output goes; the agent is told the exit code when it arrives."));
        commands.AddBare(Blank());
        commands.AddBare(new RadioControl<bool>(detach, false, "Kill it at the timeout"));
        commands.AddBare(Help("No command outlives its call — and what it printed before the kill is lost."));
        detach.SelectedValue = draft.DetachOnTimeout;
        detach.SelectionChanged += (_, on) => { draft.DetachOnTimeout = on; MarkSave(); };
        _detail.AddControl(ListDetail.Section("Commands", commands.Control));
        _detail.AddControl(ListDetail.Gap());

        var notes = new FieldGrid();
        var memory = new RadioGroup<bool>();
        notes.AddBare(Label("Notes across sessions"));
        notes.AddBare(new RadioControl<bool>(memory, true, "Keep them"));
        notes.AddBare(Help("The agent writes what it learns about you and the project to cxagent's folder, and reads it in later sessions. /memory lists them."));
        notes.AddBare(Blank());
        notes.AddBare(new RadioControl<bool>(memory, false, "Do not keep any"));
        notes.AddBare(Help("No memory tool and no notes in the prompt. Notes already kept stay on disk."));
        memory.SelectedValue = draft.MemoryEnabled;
        memory.SelectionChanged += (_, on) => { draft.MemoryEnabled = on; MarkSave(); };
        _detail.AddControl(ListDetail.Section("Memory", notes.Control));
        _detail.AddControl(ListDetail.Gap());

        _detail.AddControl(ListDetail.Section("Sandbox", SandboxEditor(draft, Label, Help, Blank)));
        _detail.AddControl(ListDetail.Gap());

        var limits = new FieldGrid();
        var cap = new RadioGroup<TurnCap>();
        limits.AddBare(Label("Turns per request"));
        limits.AddBare(new RadioControl<TurnCap>(cap, TurnCap.Default, "Default"));
        limits.AddBare(Help($"None for you — you are watching and can stop it; {Core.Agents.AgentHost.DefaultTurnCeiling} for sub-agents."));
        limits.AddBare(Blank());
        limits.AddBare(new RadioControl<TurnCap>(cap, TurnCap.None, "No cap"));
        limits.AddBare(Help("Neither you nor a sub-agent is stopped by a count."));
        limits.AddBare(Blank());
        // THE NUMBER ON THE CHOICE'S OWN LINE, so the field reads as part of "Cap at" rather than a setting of
        // its own; it counts only while that choice is picked.
        var capRow = new ToolbarControl { ItemSpacing = 1 };
        capRow.AddItem(new RadioControl<TurnCap>(cap, TurnCap.Number, "Cap at"));
        var capNumber = ListDetail.Input(new PromptControl { Input = draft.CapText, InputWidth = 6 });
        capNumber.InputChanged += (_, t) => { draft.CapText = t; MarkSave(); };
        capRow.AddItem(capNumber);
        capRow.AddItem(new MarkupControl(["turns"]));
        limits.AddBare(capRow);
        limits.AddBare(Help("For you and every sub-agent, unless its type sets its own."));
        cap.SelectedValue = draft.Cap;
        cap.SelectionChanged += (_, chosen) => { draft.Cap = chosen; MarkSave(); };
        limits.AddBare(Blank());
        var compact = ListDetail.Input(new PromptControl { Input = draft.CompactText, InputWidth = 10 });
        compact.InputChanged += (_, t) => { draft.CompactText = t; MarkSave(); };
        limits.Add("Compact above (tokens)", CompactHint(), compact);
        _detail.AddControl(ListDetail.Section("Limits", limits.Control));
        _detail.AddControl(ListDetail.Gap());

        var tools = new FieldGrid();
        tools.AddBare(new MarkupControl([$"[{muted}]What your session's agent may use: a base, and the tools taken from it. Sub-agent types set their own in Agents.[/]"]) { Wrap = true });
        tools.AddBare(ToolsEditor(draft));
        _detail.AddControl(ListDetail.Section("Session tools", tools.Control));
        _detail.AddControl(ListDetail.Gap());

        var search = new FieldGrid();
        search.AddBare(Label("Tavily API key"));
        search.AddBare(TavilyKeyEditor(draft));
        search.AddBare(Help("Empty: searches use DuckDuckGo, which needs no account but may answer with a bot check. "
            + "A free Tavily key (tavily.com, 1,000 searches a month) makes search reliable. {env:NAME} works here."));
        _detail.AddControl(ListDetail.Section("Web search", search.Control));
    }

    /// <summary>
    /// Whether the model's shell commands are contained, and what the user adds to the built-in lists — with this
    /// machine's answer under it, so "on" is never read as "working" on a machine where bubblewrap will not start.
    ///
    /// <para>THE STATUS IS PROBED OFF THE UI THREAD and filled in when it comes: starting bubblewrap takes
    /// milliseconds, and a dialog that waits on a process to paint is the wrong trade even for that.</para>
    /// </summary>
    private IWindowControl SandboxEditor(GeneralDraft draft, Func<string, MarkupControl> label, Func<string, MarkupControl> help,
        Func<MarkupControl> blank)
    {
        var grid = new FieldGrid();
        var status = new MarkupControl([$"[{ColorScheme.MutedMarkup}]Checking this machine…[/]"]) { Wrap = true };
        grid.AddBare(status);
        grid.AddBare(blank());

        var enabled = new RadioGroup<bool>();
        grid.AddBare(label("The model's shell commands"));
        grid.AddBare(new RadioControl<bool>(enabled, true, "Run inside the sandbox"));
        grid.AddBare(help("They write only in the project, temp and package caches; credentials and session sockets are "
            + "out of reach. Where the folder is trusted and the edit mode lets writes be silent, they run without asking."));
        grid.AddBare(blank());
        grid.AddBare(new RadioControl<bool>(enabled, false, "Run as you"));
        grid.AddBare(help("Every command asks, as before, and can touch anything you can."));
        enabled.SelectedValue = draft.SandboxEnabled;
        enabled.SelectionChanged += (_, on) => { draft.SandboxEnabled = on; MarkSave(); };
        grid.AddBare(blank());

        var network = new RadioGroup<bool>();
        grid.AddBare(label("Network, inside the sandbox"));
        grid.AddBare(new RadioControl<bool>(network, true, "On"));
        grid.AddBare(help("Installs and pushes work. A command can still send out what it can read."));
        grid.AddBare(blank());
        grid.AddBare(new RadioControl<bool>(network, false, "Off"));
        grid.AddBare(help("Nothing leaves; installs, restores and pushes need the command to ask to leave the sandbox."));
        network.SelectedValue = draft.SandboxNetwork;
        network.SelectionChanged += (_, on) => { draft.SandboxNetwork = on; MarkSave(); };
        grid.AddBare(blank());

        // ONE ENTRY PER LINE, in the editor the Agents tab uses for a briefing: a comma is a legal character in a path.
        MultilineEditControl List(string text, Action<string> set)
        {
            var editor = new MultilineEditControl(viewportHeight: 3) { HorizontalAlignment = HorizontalAlignment.Stretch };
            editor.SetContent(text);
            editor.ContentChanged += (_, _) => { set(editor.GetContent()); MarkSave(); };
            return editor;
        }
        grid.Add("Also writable", "Folders, one per line, starting with / or ~. Enter to type, Esc when done.",
            List(draft.WritableText, t => draft.WritableText = t));
        grid.Add("Also hidden", "Folders or files seen empty, one per line.", List(draft.HiddenText, t => draft.HiddenText = t));
        grid.Add("Also unset", "Variable names or patterns like MY_*, one per line.", List(draft.HiddenEnvText, t => draft.HiddenEnvText = t));
        grid.Add("Keep anyway", "Variable names a credential pattern would remove, one per line.", List(draft.PassEnvText, t => draft.PassEnvText = t));

        _ = Task.Run(() =>
        {
            var probed = Core.Sandbox.SandboxProbe.Run(Core.Sandbox.SandboxProbeRequest.ThisMachine());
            var line = probed.Kind == Core.Sandbox.SandboxStatusKind.Active
                ? $"[{ColorScheme.AffirmativeMarkup}]✔ bubblewrap works on this machine.[/]"
                : $"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape($"{probed.Reason}. {probed.Fix}".Trim())}[/]";
            _host.System.EnqueueOnUIThread(() => status.SetContent([line]), "generalTab.sandboxStatus");
        });
        return grid.Control;
    }

    /// <summary>
    /// The Tavily key, the way the Providers tab edits a provider's: the stored value is never shown or put
    /// back into a field — only "set" and its last four characters — and a new one is typed into a masked
    /// prompt. A placeholder is shown as written: <c>{env:NAME}</c> names a variable, it is not the secret.
    /// </summary>
    private IWindowControl TavilyKeyEditor(GeneralDraft draft)
    {
        var stack = new FieldGrid();
        var line = new MarkupControl([TavilyLine(draft)]);
        // REDRAWN AFTER A SAVE, which makes the typed key the saved one; left alone, the line went on
        // saying "new key typed — saved with Save" about a key that was already saved.
        _afterSave = () => line.SetContent([TavilyLine(draft)]);
        var entry = ListDetail.Input(new PromptControl { MaskCharacter = '•', Visible = false, HorizontalAlignment = HorizontalAlignment.Stretch, Prompt = "New key: " });
        entry.InputChanged += (_, text) => { draft.TypeTavilyKey(text); line.SetContent([TavilyLine(draft)]); MarkSave(); };

        var set = Controls.Button(" Set… ").OnClick((_, _) => { entry.Visible = true; entry.Input = ""; }).Build();
        var clear = Controls.Button(" Clear ").OnClick((_, _) =>
        {
            draft.ClearTavilyKey();
            entry.Visible = false;
            line.SetContent([TavilyLine(draft)]);
            MarkSave();
        }).Build();

        var row = new ToolbarControl { ItemSpacing = 3, HorizontalAlignment = HorizontalAlignment.Left };
        row.AddItem(line);
        row.AddItem(set);
        row.AddItem(clear);
        stack.AddBare(row);
        stack.AddBare(entry);
        return stack.Control;
    }

    private static string TavilyLine(GeneralDraft draft) => TavilyLine(draft, draft.SavedTavilyKey);

    private static string TavilyLine(GeneralDraft draft, string original) =>
        draft.TavilyKey.Trim() == original.Trim()
            ? (original.TrimStart().StartsWith('{')
                ? MarkupParser.Escape(original)
                : MarkupParser.Escape(SecretField.Describe(original)))
        : draft.TavilyKey.Trim().Length == 0 ? $"[{ColorScheme.CautionMarkup}]will be removed on save — searches use DuckDuckGo[/]"
        : $"[{ColorScheme.CautionMarkup}]new key typed — saved with Save[/]";

    /// <summary>
    /// What an empty threshold means, as a number when it can be one: 80% of the default model's window, or the
    /// built-in fallback when its window is not known.
    /// </summary>
    private string CompactHint()
    {
        var model = _host.Apply.Current.Value.Model;
        return model?.ContextWindow is { } window
            ? $"Empty: 80% of the model's window — {(int)(window * 0.8):N0} for {model.Label}."
            : $"Empty: 80% of the model's window, or {OrchestratorSettings.DefaultCompressThreshold:N0} when the window is not known.";
    }

    /// <summary>A base — inherited or all — and the sixteen tools as boxes: unticked is taken away. The Agents tab's, for the session.</summary>
    private IWindowControl ToolsEditor(GeneralDraft draft)
    {
        var stack = new FieldGrid();
        var baseBox = new DropdownControl("", ["inherited", "all"]) { HorizontalAlignment = HorizontalAlignment.Left };
        baseBox.SelectedIndex = draft.Base == "all" ? 1 : 0;
        baseBox.SelectedValueChanged += (_, v) => { draft.Base = v ?? "inherited"; MarkSave(); };
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
                MarkSave();
            };
            grid.Place(box, i / 3, i % 3);
        }
        stack.AddBare(grid);
        if (draft.Added.Count > 0)
            stack.AddBare(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Also added back: {MarkupParser.Escape(string.Join(", ", draft.Added))}[/]"]));
        return stack.Control;
    }

    private async Task SaveAsync()
    {
        if (_saving || _draft is not { } draft) return;
        if (!draft.IsDirty) { SetStatus($"[{ColorScheme.MutedMarkup}]Nothing to save.[/]"); return; }
        if (draft.Problem is { } problem) { SetStatus($"[{ColorScheme.CautionMarkup}]{MarkupParser.Escape(problem)}[/]"); return; }
        _saving = true;
        try
        {
            var prepared = await Task.Run(() => ConfigApply.Prepare(_host.Apply, draft.Apply));
            _host.System.EnqueueOnUIThread(() =>
            {
                if (!prepared.Write.Saved)
                {
                    SetStatus(string.Join("\n", prepared.Write.Errors.Select(e => $"[{ColorScheme.DangerMarkup}]✖ {MarkupParser.Escape(e)}[/]")));
                    return;
                }
                // CLEAN WITHOUT A REBUILD: rebuilding would replace the field Ctrl+S was pressed in, and a replaced
                // field is left out of the Tab order.
                draft.Saved();
                _afterSave?.Invoke();
                MarkSave();
                // WRITTEN BUT NOT LIVE when the file resolves to no usable provider: nothing is current to open a
                // tab from, so the reason is said rather than "saved".
                if (prepared.Fresh is null)
                {
                    SetStatus(string.Join("\n", prepared.Write.Errors.Select(e => $"[{ColorScheme.CautionMarkup}]Saved, not live: {MarkupParser.Escape(e)}[/]")));
                    return;
                }
                // MADE CURRENT, AND NO RUNNING TAB MOVED: nothing here names an instance or reaches every session,
                // so the change is what the next tab is opened with.
                ConfigApply.Commit(_host.Apply, prepared,
                    new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), false, "general settings saved"),
                    CacheChoice.ApplyNow);
                SetStatus($"[{ColorScheme.AffirmativeMarkup}]✔ Saved.[/] [{ColorScheme.MutedMarkup}]Tabs opened from now on use it.[/]");
            }, "generalTab.Save");
        }
        finally { _saving = false; }
    }

    private void SetStatus(string markup) => _status.SetContent([markup]);

    /// <summary>Redraws what a save changes in place — the Tavily key's line.</summary>
    private Action? _afterSave;
}
