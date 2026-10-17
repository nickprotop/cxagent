using CxAgent.Core.Helpers;
using CxAgent.Core.Jobs;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// The Tasks view of the right-hand column: every background shell and sub-agent of the session in front, as a
/// card each — a Running section, then a Finished one (spec decisions 4′, 32, 33).
///
/// <para>CARDS, AS THE CLAUDE APP'S OWN BACKGROUND-TASKS SHEET DRAWS THEM: a kind and a title with one control,
/// then what kind of task it is, how long it has run and how to view it. Two lines, so a column of them reads as a
/// list of tasks rather than a log.</para>
///
/// <para>THE LIBRARY'S CONTROLS, NOT HAND-DRAWN MARKUP: each section is a <see cref="CollapsiblePanel"/> holding a
/// <see cref="ListControl"/>, so collapsing, selection, scrolling, truncation and Enter or a double-click to open
/// are the library's own. What this class adds is the cards' text, which row a click or a key means, and the one
/// key a list has no notion of — x, which does what the card's control does.</para>
///
/// <para>ITS OWN PANEL, NOT A SECTION OF THE INFO PANEL. The info panel is reference — limits, ids, spend — and a
/// section of live tasks at its top pushed all of that down while it ran. F3 and F7 choose which of the two the
/// column shows, and each keeps its full height.</para>
/// </summary>
public sealed class JobsPanel
{
    /// <summary>
    /// One row: a background shell or a sub-agent. Ordered together — running first, newest first — because the
    /// panel answers "what is going on in the background", whichever kind it is.
    /// </summary>
    public abstract record PanelRow
    {
        public abstract bool Running { get; }

        /// <summary>When its work began — newest first within the running and the finished.</summary>
        public abstract DateTimeOffset Began { get; }

        /// <summary>What identifies the row from one refresh to the next, so the selection follows it.</summary>
        public abstract string Key { get; }
    }

    /// <param name="View">The job and how it stands.</param>
    /// <param name="Tail">Its last lines of output, cleaned, oldest first — read by its tab, not drawn on its card.</param>
    /// <param name="Agent">The sub-agent that started it, or null for the session's own agent.</param>
    public sealed record JobRow(JobView View, IReadOnlyList<string> Tail, string? Agent) : PanelRow
    {
        public override bool Running => View.State == BackgroundJobState.Running;
        public override DateTimeOffset Began => View.Job.Started;
        public override string Key => $"job:{View.Job.Pid}";
    }

    /// <param name="View">The agent and how it stands — see <see cref="AgentRows"/>.</param>
    public sealed record AgentRow(AgentRowView View) : PanelRow
    {
        public override bool Running => View.Running;
        public override DateTimeOffset Began => View.Began;
        public override string Key => $"agent:{View.AgentId}";
    }

    /// <summary>
    /// What one refresh shows: the rows, and how many tasks the heading says are running.
    ///
    /// <para>THE COUNT IS GIVEN, NOT TAKEN FROM THE ROWS. The heading must say what the F7 item it was opened from
    /// says, and both read <see cref="MainWindow.RunningTasksOf"/> — running shells and every working agent,
    /// foreground children and the user's sends included (decision 22), which is the number of running rows.</para>
    /// </summary>
    public sealed record JobsShown(IReadOnlyList<PanelRow> Rows, int Running);

    /// <summary>What a key or a click on a card asks for.</summary>
    public enum RowAction
    {
        /// <summary>Enter or a double-click — the card's tab.</summary>
        Open,

        /// <summary><c>x</c> or the card's own control — ⏹ stops it while it runs, ✕ deletes it, everywhere, once it has
        /// ended (decision 13).</summary>
        StopOrForget,
    }

    /// <summary>A card and what was asked of it — for the window, which owns tabs, stops and forgetting.</summary>
    public sealed record RowChoice(PanelRow Row, RowAction Action);

    /// <summary>Running rows first, then the ended — the Finished section; newest first within each.</summary>
    public static IReadOnlyList<PanelRow> Order(IEnumerable<PanelRow> rows) =>
        [.. rows.OrderBy(r => r.Running ? 0 : 1).ThenByDescending(r => r.Began)];

    /// <summary>How many of a job's last lines a refresh reads for it — what its tab opens showing.</summary>
    public const int TailLines = 3;

    /// <summary>
    /// The columns a card's text cannot use: the panel's two margins, the two its scroll panel reserves for a
    /// scrollbar, and the mark with its space — a line sized without them wraps the moment the bar arrives.
    /// </summary>
    private const int Chrome = 6;

    private readonly MarkupControl _heading = Ctl.Markup().WithMargin(1, 1, 1, 0).Build();
    private readonly MarkupControl _empty = Ctl.Markup().WithMargin(1, 1, 1, 0).Build();
    private readonly Section _running;
    private readonly Section _finished;
    private readonly ScrollablePanelControl _host;
    private int _count;
    private int _width = SessionPanel.MaxWidth;

    /// <summary>A card was chosen — by a click, Enter, its ⏹ or ✕, or <c>x</c>.</summary>
    public event Action<RowChoice>? RowChosen;

    public JobsPanel()
    {
        _running = new Section(this, "Running");

        _finished = new Section(this, "Finished");

        _host = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        _host.BackgroundColor = ColorScheme.PanelSurface;
        // EACH SECTION'S KEY HINT SITS UNDER IT, so "x stop" is read beside the running cards it stops and "x delete"
        // beside the finished ones — a single hint at the foot of the column read as being about whatever was last.
        foreach (var control in new IWindowControl[] { _heading, _empty, _running.Panel, _running.Hint, _finished.Panel, _finished.Hint })
            _host.AddControl(control);
    }

    /// <summary>The control the window places in the column.</summary>
    public IWindowControl Control => _host;

    /// <summary>
    /// What takes the keyboard when F7 brings the panel up: the Running list, or Finished's when only it has cards
    /// to show.
    /// </summary>
    public IFocusableControl Rows => Active()?.List ?? _running.List;

    /// <summary>Whether either list has the keyboard — when the window forwards the panel's keys to it.</summary>
    public bool RowsHaveFocus => _running.List.HasFocus || _finished.List.HasFocus;

    /// <summary>
    /// The panel as text: the heading, each section's header and cards, and the key hint — what the controls show,
    /// in order, as markup. For tests, which assert on what a reader would see.
    /// </summary>
    public string RenderedText { get; private set; } = "";

    /// <summary>Re-applies the surface after a theme change — see SessionPanel.ReapplyTheme.</summary>
    public void ReapplyTheme()
    {
        _host.BackgroundColor = ColorScheme.PanelSurface;
        _running.ReapplyTheme();
        _finished.ReapplyTheme();
    }

    /// <summary>
    /// Acts as a click on one of the panel's targets would. PUBLIC so a test can click without a mouse.
    /// </summary>
    /// <param name="url">
    /// <c>job:</c>pid or <c>agent:</c>id to open it; <c>stop:</c>… for a running card's ⏹ and <c>delete:</c>… for
    /// a finished card's ✕ — the one control at a card's right edge, which does what x does.
    /// </param>
    public void Follow(string url)
    {
        var prefix = url.StartsWith("stop:", StringComparison.Ordinal) ? 5
            : url.StartsWith("delete:", StringComparison.Ordinal) ? 7 : 0;
        var stop = prefix > 0;
        var key = url[prefix..];
        var row = _running.Rows.Concat(_finished.Rows).FirstOrDefault(r => r.Key == key);
        if (row is not null) RowChosen?.Invoke(new RowChoice(row, stop ? RowAction.StopOrForget : RowAction.Open));
    }

    /// <summary>
    /// Moves the selection one card up or down in the active list. False when no open section has cards, so the
    /// key is not swallowed.
    /// </summary>
    public bool MoveSelection(bool down)
    {
        if (Active() is not { } section) return false;
        section.Index = Math.Clamp(section.Index + (down ? 1 : -1), 0, section.Rows.Count - 1);
        Redraw();
        return true;
    }

    /// <summary>Opens the selected card — Enter on the rows. False with no card showing.</summary>
    public bool ChooseSelected()
    {
        if (Active()?.Selected is not { } row) return false;
        RowChosen?.Invoke(new RowChoice(row, RowAction.Open));
        return true;
    }

    /// <summary>
    /// <c>x</c> on the selected card. False with no card showing, so the key is not swallowed.
    ///
    /// <para>NOTHING FOR A FOREGROUND AGENT WHILE IT RUNS: the parent's turn is waiting on its answer, and stopping
    /// it from here would hand that turn a cancellation it never asked for. The key hint says so; the key is still
    /// the panel's, so it does not fall through to anything else.</para>
    /// </summary>
    public bool ActOnSelected()
    {
        if (Active()?.Selected is not { } row) return false;
        if (row is AgentRow { View: { Foreground: true, Running: true } }) return true;
        RowChosen?.Invoke(new RowChoice(row, RowAction.StopOrForget));
        return true;
    }

    /// <summary>Redraws with the last rows given — for a change of focus, selection or section between ticks.</summary>
    public void Redraw() => Refresh(new JobsShown([.. _running.Rows, .. _finished.Rows], _count), _width);

    /// <param name="shown">The rows and the running count.</param>
    /// <param name="width">The column's width as laid out — see SessionPanel.SessionPanelState.Width.</param>
    public void Refresh(JobsShown shown, int width)
    {
        _count = shown.Running;
        _width = width;
        var usable = Math.Max(1, width - Chrome);
        var held = RowsHaveFocus ? Active()?.Selected?.Key : null;
        _running.Show([.. shown.Rows.Where(r => r.Running)], usable);
        _finished.Show([.. shown.Rows.Where(r => !r.Running)], usable);

        // THE KEYBOARD FOLLOWS ITS CARD ACROSS SECTIONS. A card that stopped moves from Running to Finished; left
        // behind, the keyboard sat on an emptied list while x acted on another finished card, unhighlighted — so the
        // x-then-x of decision 13 deleted the wrong agent.
        if (held is not null)
            foreach (var section in new[] { _running, _finished })
                if (!section.List.HasFocus && section.Take(held)) break;

        var heading = $"[bold {ColorScheme.AccentMarkup}]{(_count > 0 ? $"Tasks · {_count} running" : "Tasks")}[/]";
        SetIfChanged(_heading, [heading]);

        var nothing = shown.Rows.Count == 0;
        List<string> empty = nothing
            ? [Muted("Nothing running in the background."), "", Muted("A command or sub-agent started in the background appears here.")]
            : [];
        _empty.Visible = nothing;
        SetIfChanged(_empty, empty);

        // THE KEYS, WHILE THEY WORK, under the section whose card has the keyboard. A hint with nothing selected
        // names keys that would act on nothing. Only the keys a card does not show: each card already says how it
        // is viewed, and the column has room for little else.
        var focused = RowsHaveFocus ? Active() : null;
        string HintFor(Section section) =>
            !ReferenceEquals(section, focused) || section.Selected is not { } chosen ? ""
            : Muted(Cut(chosen switch
            {
                AgentRow { View: { Foreground: true, Running: true } } => "x — the parent waits on it",
                { Running: true } => "x stop",
                _ => "x delete",
            }, width - 4));
        var runningHint = HintFor(_running);
        var finishedHint = HintFor(_finished);
        _running.ShowHint(runningHint);
        _finished.ShowHint(finishedHint);

        var text = new List<string> { heading };
        text.AddRange(empty);
        text.AddRange(_running.Text());
        if (runningHint.Length > 0) text.Add(runningHint);
        text.AddRange(_finished.Text());
        if (finishedHint.Length > 0) text.Add(finishedHint);
        RenderedText = string.Join("\n", text);
    }

    /// <summary>
    /// The section whose list acts for the keys: the one with the keyboard, else the first with cards.
    ///
    /// <para>WHILE THE CARDS HAVE THE KEYBOARD, ONLY THE FOCUSED SECTION: its selection is the one highlighted, and a
    /// key acting on the other section's remembered card would act on one the user cannot see chosen.</para>
    /// </summary>
    private Section? Active() =>
        RowsHaveFocus
            ? new[] { _running, _finished }.FirstOrDefault(s => s.List.HasFocus && s.Rows.Count > 0)
            : new[] { _running, _finished }.FirstOrDefault(s => s.Rows.Count > 0);

    // ONLY WHEN IT CHANGED. The clocks change every second while a task runs, but an idle panel re-set on every tick
    // would invalidate layout for nothing.
    private static void SetIfChanged(MarkupControl control, List<string> lines)
    {
        if (control.Text == string.Join("\n", lines)) return;
        control.SetContent(lines);
    }

    /// <summary>
    /// One section: a heading over a list of cards.
    ///
    /// <para>NOTHING COLLAPSES (decision 33′). A folded Finished section was one more thing to learn and to wonder
    /// about; the column scrolls, and x deletes what the user is done with.</para>
    /// </summary>
    private sealed class Section
    {
        private readonly JobsPanel _owner;
        private readonly string _title;

        public Section(JobsPanel owner, string title)
        {
            _owner = owner;
            _title = title;
            Panel = Ctl.CollapsiblePanel(title).WithMargin(1, 1, 1, 0).NonCollapsible().Build();
            List = NewList();
            Panel.AddControl(List);

        }

        public ListControl List { get; private set; }

        /// <summary>This section's key hint, placed under it by the panel.</summary>
        public MarkupControl Hint { get; } = Ctl.Markup().WithMargin(1, 0, 1, 0).Build();

        /// <summary>
        /// Which card the keys act on in this section — remembered while the list does not have the keyboard.
        ///
        /// <para>A LIST SHOWS A SELECTION ONLY WHILE IT HAS THE KEYBOARD. Two lists each drawing their own made two
        /// cards look chosen at once, and "x stop" under a finished card read as stopping it. The position is kept
        /// here and handed back when the list is focused again.</para>
        /// </summary>
        public int Index
        {
            get => List.SelectedIndex >= 0 ? List.SelectedIndex : _remembered;
            set
            {
                _remembered = value;
                if (List.HasFocus) List.SelectedIndex = value;
            }
        }

        private int _remembered;

        /// <summary>Selects the card with this key and takes the keyboard; false when this section does not hold it.</summary>
        public bool Take(string key)
        {
            var at = Rows.ToList().FindIndex(r => r.Key == key);
            if (at < 0 || List.GetParentWindow() is not { } window) return false;
            _remembered = at;
            List.SelectedIndex = at;
            window.FocusManager.SetFocus(List, FocusReason.Programmatic);
            return true;
        }

        public void ShowHint(string hint)
        {
            Hint.Visible = hint.Length > 0;
            if (Hint.Text != hint) Hint.SetContent([hint]);
        }

        /// <summary>A list wired to this section: Enter or a double-click opens a card, a click on its control acts.</summary>
        private ListControl NewList()
        {
            var list = new ListControl { HoverHighlightsItems = true, BackgroundColor = ColorScheme.PanelSurface };

            // THE SELECTION FOLLOWS THE KEYBOARD — see Index.
            list.GotFocus += (_, _) =>
            {
                // ONE TEXT COLOUR FOR AN UNSELECTED CARD, focused list or not: the theme dims a focused list's other
                // items, so the list without the keyboard drew its cards brighter — and read as the one selected.
                list.FocusedForegroundColor = list.ForegroundColor;
                if (list.SelectedIndex < 0 && Rows.Count > 0) list.SelectedIndex = Math.Clamp(_remembered, 0, Rows.Count - 1);
                _owner.Redraw();
            };
            list.LostFocus += (_, _) =>
            {
                if (list.SelectedIndex >= 0) _remembered = list.SelectedIndex;
                list.SelectedIndex = -1;
                _owner.Redraw();
            };

            // ENTER OR A DOUBLE-CLICK OPENS THE CARD — the list's own activation.
            list.ItemActivated += (_, item) =>
            {
                if (item.Tag is PanelRow row) _owner.RowChosen?.Invoke(new RowChoice(row, RowAction.Open));
            };

            // A CARD'S RIGHT EDGE IS ITS ONE CONTROL — ⏹ on a running card, ✕ on a finished one — and a click there
            // does what x does. The click has already selected the card, so the selection says which.
            list.MouseClick += (_, args) =>
            {
                if (Selected is { } row && row is not AgentRow { View: { Foreground: true, Running: true } }
                    && args.Position.X >= list.ActualWidth - list.Margin.Right - 2)
                    _owner.RowChosen?.Invoke(new RowChoice(row, RowAction.StopOrForget));
            };
            return list;
        }

        /// <summary>
        /// Replaces the list with a fresh one holding these cards — when the number of cards changes.
        ///
        /// <para>A LIST THAT HAS BEEN PAINTED REMEMBERS HOW MANY ITEMS FITTED, and both its scroll and its measured
        /// height read that count before the one this panel sets. A card arriving above the selected one then
        /// scrolled the list by one and measured it from there — one card tall, for good — so the second running
        /// task had no line on screen. A list never painted has no such count.</para>
        ///
        /// <para>THE KEYBOARD GOES WITH IT when the old list had it, or a card arriving would take focus away from
        /// a user mid-way through the list.</para>
        /// </summary>
        private void Replace(List<ListItem> cards, int selected)
        {
            var old = List;
            var hadFocus = old.HasFocus;
            var window = old.GetParentWindow();
            List = NewList();
            List.MaxVisibleItems = Math.Max(1, cards.Count);
            List.Items = cards;
            _remembered = Math.Max(0, selected);
            List.SelectedIndex = hadFocus ? selected : -1;
            Panel.RemoveControl(old);
            Panel.AddControl(List);
            if (hadFocus) window?.FocusManager.SetFocus(List, FocusReason.Programmatic);
        }

        public CollapsiblePanel Panel { get; }

        public IReadOnlyList<PanelRow> Rows { get; private set; } = [];


        public PanelRow? Selected => Index >= 0 && Index < Rows.Count ? Rows[Index] : null;

        public void ReapplyTheme() => List.BackgroundColor = ColorScheme.PanelSurface;

        /// <summary>
        /// Shows these cards, keeping the selection on the row it was on.
        ///
        /// <para>IN PLACE WHEN THE ROWS ARE THE SAME ONES, which every tick of a running clock is: rebuilding the
        /// items each second would reset the list's scroll and flicker its highlight.</para>
        /// </summary>
        public void Show(IReadOnlyList<PanelRow> rows, int usable)
        {
            var keep = Rows.Count > 0 ? Selected?.Key : null;
            var cards = rows.Select(r => Card(r, usable)).ToList();
            var same = List.Items.Count == rows.Count && rows.Select(r => r.Key).SequenceEqual(Rows.Select(r => r.Key));

            // THE NEW ROWS ARE THE SECTION'S BEFORE THE LIST IS REPLACED: handing the keyboard to the new list
            // redraws the panel from Rows, and a redraw that still read the old ones put the old list back.
            Rows = rows;
            if (same)
            {
                for (var i = 0; i < cards.Count; i++)
                {
                    var item = List.Items[i];
                    if (item.Text != cards[i].Text) item.Text = cards[i].Text;
                    item.Tag = rows[i];
                }
            }
            else
            {
                var at = keep is null ? 0 : Math.Max(0, rows.ToList().FindIndex(r => r.Key == keep));
                Replace(cards, rows.Count == 0 ? -1 : Math.Min(at, rows.Count - 1));
            }

            Panel.Visible = rows.Count > 0;
            if (Panel.Title != _title) Panel.Title = _title;
        }

        /// <summary>The section as text — its header as the library draws it, then its cards if it is open.</summary>
        public IEnumerable<string> Text()
        {
            if (Rows.Count == 0) yield break;
            yield return _title;
            foreach (var item in List.Items) foreach (var line in item.Text.Split('\n')) yield return line;
        }
    }

    /// <summary>A card: the list item for one row, its icon the kind while it runs and ✓ or ✗ once it has ended.</summary>
    private static ListItem Card(PanelRow row, int usable) => row switch
    {
        JobRow job => ShellCard(job, usable),
        AgentRow agent => AgentCard(agent, usable),
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    /// <summary>A shell's card: its command, then "Shell · how it ended · how long", then "View output".</summary>
    private static ListItem ShellCard(JobRow job, int usable)
    {
        var view = job.View;
        var running = view.State == BackgroundJobState.Running;
        // THE TIME BEFORE THE AGENT'S NAME: a name is long and is what the column cuts, and how long a command has
        // run is what a glance at its card is for.
        var facts = new List<string> { "Shell" };
        if (view.State == BackgroundJobState.Killed) facts.Add("killed");
        else if (view.State == BackgroundJobState.Exited) facts.Add($"exit {view.ExitCode}");
        facts.Add(DisplayNumber.Took(view.Elapsed));
        if (job.Agent is { } agent) facts.Add(agent);

        var mark = Mark(running, view.State == BackgroundJobState.Exited && view.ExitCode == 0, "▭");
        return new ListItem($"{mark} {TitleLine(OutputTail.OneLine(view.Job.Command), CardControl(running, foreground: false), usable)}\n"
                            + DetailLines(facts, "View output", usable))
        { Tag = job };
    }

    /// <summary>
    /// An agent's card: what it was asked to do, then "Agent · how it stands · how long", then "View transcript".
    ///
    /// <para>THE DESCRIPTION IS THE TITLE, as the sheet's: it says what the agent is for, where the handle is a slug
    /// of it. A spawn with none falls back to the handle.</para>
    ///
    /// <para>NO ⏹ ON A FOREGROUND AGENT: the parent's turn is waiting on its answer (decision 9).</para>
    /// </summary>
    private static ListItem AgentCard(AgentRow row, int usable)
    {
        var view = row.View;
        var title = view.Kept.Description is { Length: > 0 } d ? d : view.Kept.Name;
        var facts = new List<string> { "Agent" };
        if (view.State is not (AgentRowState.Running or AgentRowState.Done)) facts.Add(AgentRows.Word(view.State));
        if (view.Foreground && view.Running) facts.Add("foreground");
        facts.Add(DisplayNumber.Took(view.Elapsed));

        var mark = Mark(view.Running, view.State == AgentRowState.Done, "◇");
        return new ListItem($"{mark} {TitleLine(OutputTail.OneLine(title), CardControl(view.Running, view.Foreground), usable)}\n"
                            + DetailLines(facts, "View transcript", usable))
        { Tag = row };
    }

    /// <summary>A card's mark, as markup: the kind while it runs, ✓ or ✗ once it has ended (decision 16).</summary>
    private static string Mark(bool running, bool succeeded, string kind) =>
        running ? Muted(kind)
        : succeeded ? $"[{ColorScheme.AffirmativeMarkup}]✓[/]"
        : $"[{ColorScheme.DangerMarkup}]✗[/]";

    /// <summary>
    /// A card's one control — ⏹ to stop it while it runs, ✕ to delete it once it has ended — or null for a foreground
    /// agent, which the parent's turn is waiting on (decision 9).
    ///
    /// <para>ONE PLACE AND ONE KEY FOR BOTH (decision 33′): the control sits at the same edge on every card, and x
    /// does what it does, so there is one thing to learn about a card rather than one per state.</para>
    /// </summary>
    private static string? CardControl(bool running, bool foreground) =>
        running ? (foreground ? null : "⏹") : "✕";

    /// <summary>The title, and the card's control at the right edge, the title padded so every control lines up.</summary>
    private static string TitleLine(string title, string? control, int usable)
    {
        if (control is null) return Value(Cut(title, usable));
        var room = Math.Max(1, usable - 2);
        var text = Cut(title, room);
        return $"{Value(text)}{new string(' ', room - text.Length)} {Muted(control)}";
    }

    /// <summary>
    /// A card's last two lines, under the title: its facts, then how to view it — each on its own line, as the sheet
    /// draws them, because the column is at most 34 characters of text and "Agent · waiting for permission ·
    /// 1m 14s" alone fills it.
    /// </summary>
    private static string DetailLines(IReadOnlyList<string> facts, string view, int usable) =>
        $"  {Muted(Cut(string.Join(" · ", facts), usable))}\n  {Value(view)}";

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";

    private static string Value(string text) => MarkupParser.Escape(text);

    private static string Muted(string text) => $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(text)}[/]";

    /// <summary>m:ss under an hour, h:mm:ss past it — what a stopwatch shows. Shared with the job and agent tabs'
    /// headers, so a header and its tab title cannot disagree about how long a task has run.</summary>
    internal static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
