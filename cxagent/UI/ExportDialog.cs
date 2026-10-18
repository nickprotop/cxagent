using CxAgent.Core.Commands;
using CxAgent.Core.Export;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Dialogs;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>What the export dialog opens on.</summary>
/// <param name="SessionLine">Which conversation this is — its title, when, and where — shown at the top.</param>
/// <param name="IsAgent">An agent tab's agent rather than a session: it has no sub-agents to include.</param>
/// <param name="Render">Renders the conversation with the given options. Called off the UI thread.</param>
/// <param name="DefaultPath">
/// Where a file goes unless the user says otherwise — a bare file name, read against <paramref name="BaseDir"/>, so the
/// box shows the part worth reading rather than the start of a long folder path.
/// </param>
/// <param name="BaseDir">The folder a path that is not absolute is read against: the conversation's own.</param>
/// <param name="Choices">The remembered choices to open with.</param>
/// <param name="Prefill">What /export's words asked for; it wins over the remembered choices.</param>
/// <param name="RunningNote">Said under the options when part of the conversation is still being written.</param>
public sealed record ExportDialogRequest(
    string SessionLine,
    bool IsAgent,
    Func<ExportOptions, ExportResult> Render,
    string DefaultPath,
    string BaseDir,
    ExportChoices Choices,
    ExportPrefill Prefill,
    string? RunningNote = null);

/// <summary>What the user chose, with the export it produced.</summary>
/// <param name="Result">The rendered markdown.</param>
/// <param name="Options">The options it was rendered with.</param>
/// <param name="ToClipboard">Copied rather than written.</param>
/// <param name="Path">Where to write it, for a file.</param>
/// <param name="Open">Open the file once written.</param>
public sealed record ExportDecision(ExportResult Result, ExportOptions Options, bool ToClipboard, string? Path, bool Open);

/// <summary>What the dialog runs against.</summary>
/// <param name="System">The window system it opens in.</param>
/// <param name="Deliver">
/// Writes or copies the export; null when done, otherwise why it could not be — the dialog shows that and stays open,
/// so a typo in a path costs a correction rather than the choices.
/// </param>
/// <param name="Post">Runs an action on the UI thread — the window system's queue, or at once in a test.</param>
public sealed record ExportDialogHost(ConsoleWindowSystem System, Func<ExportDecision, string?> Deliver, Action<Action> Post);

/// <summary>
/// "Export session": scope, detail, destination, and the size of what will be written, before it is.
///
/// <para>THE PREVIEW IS RENDERED OFF THE UI THREAD. A full export of a long session is megabytes of tool output, and
/// rendering it on every tick of a checkbox would freeze the window it is drawn in. Each change starts a render after a
/// short pause; only the newest one's answer is shown, and the size line reads "…" until it arrives.</para>
///
/// <para>STATIC, AS <see cref="QuitDialog"/> IS: there is one export question at a time, and Escape closes it through
/// the same chain the other dialogs use.</para>
/// </summary>
public static class ExportDialog
{
    private const int DebounceMs = 150;

    private static State? _state;

    /// <summary>Whether the dialog is up.</summary>
    public static bool IsOpen => _state is not null;

    /// <summary>Closes it with no export. Returns whether it was open.</summary>
    public static bool CloseIfOpen()
    {
        if (_state is not { } state) return false;
        state.Answer.TrySetResult(null);
        state.Window.Close();
        return true;
    }

    // ---- test seams ----------------------------------------------------------------------------------------

    public static string? PresetForTest => _state?.PresetLabel.Text;
    public static string? SizeLineForTest => _state?.SizeLine;
    public static bool ExportEnabledForTest => _state?.ExportButton.IsEnabled ?? false;
    public static string? ErrorForTest => _state?.Error;
    public static bool HasSubAgentBoxForTest => _state?.Boxes.ContainsKey(ExportParts.SubAgents) ?? false;
    public static ExportParts? PartsForTest => _state?.Parts;

    public static void TickForTest(ExportParts part, bool on)
    {
        if (_state?.Boxes.TryGetValue(part, out var box) == true) box.Checked = on;
    }

    public static void ChoosePresetForTest(bool full) => (full ? _state?.FullRadio : _state?.ReadableRadio)?.Select();
    public static void ChooseClipboardForTest(bool clipboard) => (clipboard ? _state?.ClipboardRadio : _state?.FileRadio)?.Select();
    public static Task ExportForTest() => _state is { } state ? state.ExportAsync() : Task.CompletedTask;

    public static bool PressForTest(ConsoleKeyInfo key)
    {
        if (_state is not { } state) return false;
        if (key.Key == ConsoleKey.Tab)
        {
            state.Window.FocusManager.MoveFocus(backward: (key.Modifiers & ConsoleModifiers.Shift) != 0);
            return true;
        }
        return (state.Window.FocusManager.FocusedControl as IInteractiveControl)?.ProcessKey(key) ?? false;
    }

    // ---- asking --------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the dialog. Answers the export made — already delivered — or null when it was closed without one. A
    /// second ask while one is up answers null at once.
    /// </summary>
    public static Task<ExportDecision?> AskAsync(ExportDialogHost host, ExportDialogRequest request)
    {
        if (_state is not null) return Task.FromResult<ExportDecision?>(null);
        var state = new State(host, request);
        _state = state;
        state.Show();
        return state.Answer.Task;
    }

    private sealed class State
    {
        private static readonly (ExportParts Part, string Label)[] Options =
        [
            (ExportParts.ToolCalls, "Tool calls, one line each"),
            (ExportParts.Diffs, "Edits as diffs"),
            (ExportParts.Header, "Session header"),
            (ExportParts.ToolResults, "Tool results, whole"),
            (ExportParts.Thinking, "Thinking"),
            (ExportParts.Compaction, "Compaction summaries"),
            (ExportParts.SystemAndInjected, "System prompt & injected"),
            (ExportParts.SubAgents, "Sub-agent conversations"),
        ];

        private readonly ExportDialogHost _host;
        private readonly ExportDialogRequest _request;
        private readonly RadioGroup<ExportScope> _scope = new();
        private readonly RadioGroup<bool> _preset = new();
        private readonly RadioGroup<bool> _destination = new();
        private readonly PromptControl _turns = new() { InputWidth = 3 };
        private readonly PromptControl _path = new();
        private readonly CheckboxControl _open = new("Open when written");
        private readonly MarkupControl _note = new([""]);
        private readonly MarkupControl _error = new([""]) { Visible = false };
        private readonly MarkupControl _size = new([""]);
        private int _version;
        private bool _syncing;
        private ExportResult? _preview;

        public State(ExportDialogHost host, ExportDialogRequest request)
        {
            _host = host;
            _request = request;
            Window = new WindowBuilder(host.System)
                .WithTitle("Export session")
                .WithSize(Math.Min(78, Math.Max(62, Console.WindowWidth - 4)), Math.Min(17, Math.Max(16, Console.WindowHeight - 2)))
                .Centered()
                .AsModal()
                .WithBorderStyle(BorderStyle.Single)
                .Movable(true)
                .Minimizable(false)
                .Build();

            WholeRadio = new RadioControl<ExportScope>(_scope, ExportScope.Whole, "Whole session");
            LastRadio = new RadioControl<ExportScope>(_scope, ExportScope.LastTurn, "Last turn");
            LastNRadio = new RadioControl<ExportScope>(_scope, ExportScope.LastTurns, "Last");
            ReadableRadio = new RadioControl<bool>(_preset, false, "Readable · for a person");
            FullRadio = new RadioControl<bool>(_preset, true, "Full · for an agent");
            FileRadio = new RadioControl<bool>(_destination, false, "File");
            ClipboardRadio = new RadioControl<bool>(_destination, true, "Clipboard");
            PresetLabel = new MarkupControl([""]);
            ExportButton = new ButtonBuilder().WithText(" Export ").WithColorRole(ColorScheme.Accent)
                .OnClick((_, _) => _ = ExportAsync()).Build();

            foreach (var (part, label) in Options)
            {
                if (part == ExportParts.SubAgents && request.IsAgent) continue;
                Boxes[part] = new CheckboxControl(label);
            }
        }

        public Window Window { get; }
        public TaskCompletionSource<ExportDecision?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<ExportParts, CheckboxControl> Boxes { get; } = [];
        public RadioControl<ExportScope> WholeRadio { get; }
        public RadioControl<ExportScope> LastRadio { get; }
        public RadioControl<ExportScope> LastNRadio { get; }
        public RadioControl<bool> ReadableRadio { get; }
        public RadioControl<bool> FullRadio { get; }
        public RadioControl<bool> FileRadio { get; }
        public RadioControl<bool> ClipboardRadio { get; }
        public MarkupControl PresetLabel { get; }
        public ButtonControl ExportButton { get; }
        public string SizeLine { get; private set; } = "…";
        public string? Error { get; private set; }

        // AN AGENT HAS NO SUB-AGENTS, so it has no box for them — and counts the part as included, or its ticks could never
        // match a preset and every agent export would read as Custom.
        public ExportParts Parts =>
            Boxes.Where(b => b.Value.Checked).Aggregate(_request.IsAgent ? ExportParts.SubAgents : ExportParts.None,
                (all, b) => all | b.Key);

        private ExportOptions CurrentOptions => new(
            _scope.SelectedValue,
            int.TryParse(_turns.Input, out var n) && n > 0 ? n : 1,
            Parts);

        public void Show()
        {
            Layout();
            Apply(_request.Choices, _request.Prefill);

            _scope.SelectionChanged += (_, _) => Changed();
            _turns.InputChanged += (_, text) =>
            {
                // DIGITS ONLY: the box holds a count, and anything else would be read as one.
                var digits = new string(text.Where(char.IsDigit).Take(3).ToArray());
                if (digits != text) _turns.Input = digits;
                else { LastNRadio.Select(); Changed(); }
            };
            _preset.SelectionChanged += (_, full) =>
            {
                if (_syncing) return;
                _syncing = true;
                var parts = full ? ExportOptions.Full : ExportOptions.Readable;
                foreach (var (part, box) in Boxes) box.Checked = (parts & part) != 0;
                _syncing = false;
                Changed();
            };
            foreach (var box in Boxes.Values)
                box.CheckedChanged += (_, _) => { if (!_syncing) Changed(); };
            _destination.SelectionChanged += (_, _) => Changed();
            _path.Entered += (_, _) => _ = ExportAsync();

            Window.OnClosed += (_, _) =>
            {
                Answer.TrySetResult(null);
                if (ReferenceEquals(_state, this)) _state = null;
            };

            _host.System.AddWindow(Window);
            _host.System.SetActiveWindow(Window);
            Window.FocusManager.SetFocus(ExportButton, FocusReason.Programmatic);
            Changed();
        }

        // THE SHAPE: a column of quiet labels, the choices beside them, and muted rules between the conversation, the
        // choices and the answer. Advanced opens in place and the window grows to hold it, rather than standing empty
        // until it does.
        private const int LabelWidth = 9;
        private const int AdvancedRows = 4;

        private void Layout()
        {
            Window.AddControl(new MarkupControl([_request.SessionLine]) { Margin = new Margin(1, 0, 1, 0) });
            Window.AddControl(Rule());

            Window.AddControl(Row("Scope", WholeRadio, LastRadio, LastNRadio, _turns, Muted("turns")));
            Window.AddControl(Row("Detail", ReadableRadio, FullRadio));
            Window.AddControl(Row("", PresetLabel));

            var advanced = new CollapsiblePanel
            {
                Title = "Advanced",
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Margin(LabelWidth + 1, 0, 1, 0),
            };
            var boxes = Boxes.Values.ToList();
            var grid = Controls.Grid().Columns(GridLength.Star(1), GridLength.Star(1))
                .Rows([.. Enumerable.Repeat(GridLength.Cells(1), (boxes.Count + 1) / 2)]);
            for (var i = 0; i < boxes.Count; i++) grid = grid.Place(boxes[i], i / 2, i % 2);
            advanced.AddControl(grid.WithAlignment(HorizontalAlignment.Stretch).Build());
            advanced.ExpandedChanged += (_, open) =>
                Window.SetSize(Window.Width, Window.Height + (open ? AdvancedRows : -AdvancedRows));
            Window.AddControl(advanced);
            Window.AddControl(new MarkupControl([""]));

            _path.InputWidth = Math.Max(20, Window.Width - LabelWidth - 18);
            Window.AddControl(Row("Save to", FileRadio, ClipboardRadio));
            Window.AddControl(Row("", _path, new ButtonBuilder().WithText(" Browse… ").OnClick((_, _) => _ = BrowseAsync()).Build()));
            Window.AddControl(Row("", _open));
            Window.AddControl(Indented(_note));
            Window.AddControl(Indented(_error));

            // THE ANSWER AT THE BOTTOM: what will be written, then the buttons at the right edge, where a dialog's
            // buttons are looked for.
            _size.Margin = new Margin(1, 0, 1, 0);
            _size.StickyPosition = StickyPosition.Bottom;
            Window.AddControl(_size);
            var rule = Rule();
            rule.StickyPosition = StickyPosition.Bottom;
            Window.AddControl(rule);
            var buttons = new ToolbarControl
            {
                ItemSpacing = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                StickyPosition = StickyPosition.Bottom,
                Margin = new Margin(1, 0, 1, 0),
            };
            buttons.AddItem(ExportButton);
            buttons.AddItem(new ButtonBuilder().WithText(" Cancel ").OnClick((_, _) => CloseIfOpen()).Build());
            Window.AddControl(buttons);
        }

        private static RuleControl Rule() => new() { Color = ColorScheme.MutedRgb, Margin = new Margin(1, 0, 1, 0) };

        private static ToolbarControl Row(string label, params IWindowControl[] items)
        {
            var row = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Margin(1, 0, 1, 0) };
            row.AddItem(new MarkupControl([$"[{ColorScheme.MutedMarkup}]{label.PadRight(LabelWidth - 2)}[/]"]));
            foreach (var item in items) row.AddItem(item);
            return row;
        }

        private static IWindowControl Indented(MarkupControl line)
        {
            line.Margin = new Margin(LabelWidth + 1, 0, 1, 0);
            return line;
        }

        private static MarkupControl Muted(string text) => new([$"[{ColorScheme.MutedMarkup}]{text}[/]"]);

        /// <summary>What the chosen detail includes, in a line, under the choice.</summary>
        private static string Describe(string preset) => preset switch
        {
            "Readable" => "What was said and done — tool calls as lines, edits as diffs.",
            "Full" => "All the model saw — tool output, thinking, the system prompt.",
            _ => "Custom — your own mix, set under Advanced.",
        };

        private void Apply(ExportChoices choices, ExportPrefill prefill)
        {
            _syncing = true;
            var parts = prefill.Full == true ? ExportOptions.Full : choices.Parts;
            foreach (var (part, box) in Boxes) box.Checked = (parts & part) != 0;
            _syncing = false;

            (prefill.Scope switch
            {
                ExportScope.LastTurn => LastRadio,
                ExportScope.LastTurns => LastNRadio,
                _ => WholeRadio,
            }).Select();
            _turns.Input = (prefill.Turns ?? 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            (choices.ToClipboard ? ClipboardRadio : FileRadio).Select();
            _path.Input = _request.DefaultPath;
            _open.Checked = choices.OpenWhenWritten;
        }

        /// <summary>Something changed: the preset's name, what the file options offer, and a fresh preview.</summary>
        private void Changed()
        {
            var options = CurrentOptions;
            _syncing = true;
            if (options.PresetName == "Readable") ReadableRadio.Select();
            else if (options.PresetName == "Full") FullRadio.Select();
            else _preset.Clear();
            _syncing = false;
            PresetLabel.SetContent([$"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(Describe(options.PresetName))}[/]"]);

            var toFile = !_destination.SelectedValue;
            _path.IsEnabled = toFile;
            _open.IsEnabled = toFile;

            var notes = new List<string>();
            if (_request.RunningNote is { } running) notes.Add(running);
            if ((options.Parts & ExportParts.ToolResults) != 0) notes.Add("Full exports include tool output — check before sharing.");
            _note.SetContent([notes.Count == 0 ? "" : $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(string.Join("  ·  ", notes))}[/]"]);
            _note.Visible = notes.Count > 0;

            Preview(options);
        }

        /// <summary>Renders <paramref name="options"/> after a pause, off the UI thread; only the newest answer lands.</summary>
        private void Preview(ExportOptions options)
        {
            var version = ++_version;
            _preview = null;
            ShowSize();
            _ = Task.Run(async () =>
            {
                await Task.Delay(DebounceMs);
                if (version != Volatile.Read(ref _version)) return;
                ExportResult result;
                try { result = _request.Render(options); }
                catch (Exception e)
                {
                    _host.Post(() => { if (version == _version) ShowError($"could not render the export: {e.Message}"); });
                    return;
                }
                _host.Post(() =>
                {
                    if (version != _version) return;
                    _preview = result;
                    ShowError(null);
                    ShowSize();
                });
            });
        }

        private void ShowSize()
        {
            if (_preview is not { Stats: var stats })
            {
                SizeLine = "…";
                ExportButton.IsEnabled = false;
            }
            else
            {
                var line = $"≈ {ExportTarget.Size(stats.Bytes)} · {stats.Messages} message{(stats.Messages == 1 ? "" : "s")}";
                if (!_request.IsAgent) line += $" · {stats.SubAgents} sub-agent{(stats.SubAgents == 1 ? "" : "s")}";
                if (_destination.SelectedValue && stats.Bytes > ExportTarget.ClipboardWarnBytes)
                    line += $" · [{ColorScheme.CautionMarkup}]large for the clipboard[/]";
                SizeLine = line;
                ExportButton.IsEnabled = stats.Messages > 0;
            }
            _size.SetContent([$"[{ColorScheme.MutedMarkup}]{SizeLine}[/]"]);
        }

        private void ShowError(string? error)
        {
            Error = error;
            _error.SetContent([error is null ? "" : $"[{ColorScheme.DangerMarkup}]{MarkupParser.Escape(error)}[/]"]);
            _error.Visible = error is not null;
        }

        public async Task ExportAsync()
        {
            if (!ExportButton.IsEnabled) return;
            var options = CurrentOptions;
            var toClipboard = _destination.SelectedValue;
            var path = toClipboard ? null : Resolve(_path.Input.Trim());
            if (!toClipboard && string.IsNullOrEmpty(path))
            {
                ShowError("Say where the file goes.");
                return;
            }

            // A FILE THAT EXISTS IS REPLACED ONLY WHEN SAID SO: an export of the wrong session over the right one
            // cannot be undone.
            if (path is not null && File.Exists(path)
                && await Config.ConfirmDialog.AskAsync(_host.System, "Replace the file?",
                    [$"{Path.GetFileName(path)} already exists."], ["Replace", "Keep it"]) != "Replace")
                return;

            ExportButton.IsEnabled = false;
            var open = !toClipboard && _open.Checked;
            ExportDecision? decision = null;
            string? error;
            try
            {
                decision = await Task.Run(() => new ExportDecision(_request.Render(options), options, toClipboard, path, open));

                // CLOSED WHILE IT RENDERED, NOTHING IS WRITTEN: Escape is the user changing their mind, and a file
                // appearing after the dialog has gone would be one they said no to.
                if (Answer.Task.IsCompleted) return;
                error = await Task.Run(() => _host.Deliver(decision));
            }
            catch (Exception e)
            {
                // A FAILURE IS SAID, NOT SWALLOWED: left to escape, it disabled Export with no word of why.
                error = e.Message;
            }
            _host.Post(() =>
            {
                if (error is not null)
                {
                    ShowError(error);
                    ExportButton.IsEnabled = true;
                    return;
                }
                Answer.TrySetResult(decision!);
                Window.Close();
            });
        }

        private async Task BrowseAsync()
        {
            var current = Resolve(_path.Input.Trim());
            var folder = Path.GetDirectoryName(current);
            var chosen = await FileDialogs.ShowSaveFileAsync(_host.System,
                string.IsNullOrEmpty(folder) ? null : folder, "*.md", Path.GetFileName(current), Window);
            if (chosen is not null) _host.Post(() => _path.Input = chosen);
        }

        /// <summary>The path as typed, made absolute: <c>~/</c> is home, and anything relative is the conversation's folder.</summary>
        private string Resolve(string path)
        {
            if (path.Length == 0) return path;
            if (path.StartsWith("~/", StringComparison.Ordinal))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_request.BaseDir, path));
        }
    }
}
