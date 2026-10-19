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
/// card each — a Running section, then a Finished one (spec decisions 4′, 32, 33′).
///
/// <para>CARDS, AS THE CLAUDE APP'S OWN BACKGROUND-TASKS SHEET DRAWS THEM: a kind and a title with one control,
/// then what kind of task it is and how long it has run, then how to view it. Three short lines, so a column of
/// them reads as a list of tasks rather than a log.</para>
///
/// <para>THE LIBRARY'S CONTROLS, NOT HAND-DRAWN MARKUP: each section is a <see cref="CollapsiblePanel"/>, fixed open,
/// holding a <see cref="ListControl"/>, so the heading, selection, scrolling, truncation and Enter or a double-click
/// to open are the library's own. What this class adds is the cards' text, which row a click or a key means, and the one
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

        /// <summary><c>b</c> or ⇥ on a foreground card — moves it to the background (Ctrl+B spec decision 6′).</summary>
        Background,
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
    /// scrollbar, the selection bar with its space, and the mark with its space — a line sized without them wraps the
    /// moment the bar arrives.
    /// </summary>
    private const int Chrome = 8;

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
        // NO HEADING OVER THE RUNNING CARDS: the panel's own title says how many run, and a "Running" under it
        // said the same thing twice. Finished keeps its heading — it is what separates the two.
        _running = new Section(this, title: null);
        _finished = new Section(this, "Finished");

        _host = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        _host.BackgroundColor = ColorScheme.PanelSurface;
        // EACH SECTION'S KEY HINT SITS UNDER IT, so "x stop" is read beside the running cards it stops and "x delete"
        // beside the finished ones — a single hint at the foot of the column reads as being about whatever is last.
        foreach (var control in new IWindowControl[] { _heading, _empty, _running.Panel, _running.Hint, _finished.Panel, _finished.Hint })
            _host.AddControl(control);
    }

    /// <summary>The control the window places in the column.</summary>
    public IWindowControl Control => _host;

    /// <summary>
    /// What takes the keyboard when F7 brings the panel up: the selected card's link, or the column itself with no
    /// cards to show.
    /// </summary>
    public IFocusableControl Rows => SelectedCard()?.Anchor ?? (IFocusableControl)_host;

    /// <summary>Whether a card has the keyboard — when the window forwards the panel's keys to it.</summary>
    public bool RowsHaveFocus => AllCards().Any(c => c.HasFocus);

    /// <summary>
    /// The panel as text: the heading, each section's header and cards, and the key hint — what the controls show,
    /// in order, as markup. For tests, which assert on what a reader would see.
    /// </summary>
    public string RenderedText { get; private set; } = "";

    /// <summary>Re-applies the surfaces after a theme change — see SessionPanel.ReapplyTheme.</summary>
    public void ReapplyTheme()
    {
        _host.BackgroundColor = ColorScheme.PanelSurface;
        foreach (var card in AllCards()) card.Paint();
    }

    /// <summary>Test seam: a card's ⏹ or ✕ button, by its row's key — what a click test presses.</summary>
    public ButtonControl? ControlForTest(string key) => AllCards().FirstOrDefault(c => c.Row.Key == key)?.Button;

    /// <summary>Test seam: a card's own surface, by its row's key.</summary>
    public PanelControl? CardForTest(string key) => AllCards().FirstOrDefault(c => c.Row.Key == key)?.Box;

    /// <summary>Test seam: a card's "View …" link, by its row's key.</summary>
    public MarkupControl? LinkForTest(string key) => AllCards().FirstOrDefault(c => c.Row.Key == key)?.Anchor;

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
        var row = AllRows().FirstOrDefault(r => r.Key == key);
        if (row is not null) RowChosen?.Invoke(new RowChoice(row, stop ? RowAction.StopOrForget : RowAction.Open));
    }

    /// <summary>
    /// Moves the selection one card up or down. False with no card showing, so the key is not swallowed.
    ///
    /// <para>ONE LIST ACROSS BOTH SECTIONS: ↓ on the last running card goes on to the first finished one, and ↑ comes
    /// back — the column reads as one list of tasks.</para>
    /// </summary>
    public bool MoveSelection(bool down)
    {
        var cards = AllCards();
        if (cards.Count == 0) return false;
        var at = SelectedCard() is { } current ? cards.IndexOf(current) : 0;
        Select(cards[Math.Clamp(at + (down ? 1 : -1), 0, cards.Count - 1)], focus: true);
        return true;
    }

    /// <summary>Opens the selected card — Enter on the cards. False with no card showing.</summary>
    public bool ChooseSelected()
    {
        if (SelectedCard()?.Row is not { } row) return false;
        RowChosen?.Invoke(new RowChoice(row, RowAction.Open));
        return true;
    }

    /// <summary>
    /// <c>x</c> on the selected card. False with no card showing, so the key is not swallowed.
    ///
    /// <para>A FOREGROUND AGENT STOPS TOO: stopping it alone answers its call "stopped by the user" and the parent's
    /// turn goes on (Ctrl+B spec decision 11), so x means the same on every running card.</para>
    /// </summary>
    public bool ActOnSelected()
    {
        if (SelectedCard()?.Row is not { } row) return false;
        RowChosen?.Invoke(new RowChoice(row, RowAction.StopOrForget));
        return true;
    }

    /// <summary>
    /// <c>b</c> on the selected card: moves a foreground agent to the background (Ctrl+B spec decision 6′). False on any
    /// other card, so the key is not the panel's to swallow there.
    /// </summary>
    public bool BackgroundSelected()
    {
        if (SelectedCard()?.Row is not AgentRow { View: { Foreground: true, Running: true } } row) return false;
        RowChosen?.Invoke(new RowChoice(row, RowAction.Background));
        return true;
    }

    /// <summary>Redraws with the last rows given — for a change of focus or selection between ticks.</summary>
    public void Redraw() => Refresh(new JobsShown(AllRows(), _count), _width);

    /// <param name="shown">The rows and the running count.</param>
    /// <param name="width">The column's width as laid out — see SessionPanel.SessionPanelState.Width.</param>
    public void Refresh(JobsShown shown, int width)
    {
        _count = shown.Running;
        _width = width;
        var usable = Math.Max(1, width - Chrome);
        var hadFocus = RowsHaveFocus;
        var before = AllRows().Select(r => r.Key).ToList();
        _running.Show([.. shown.Rows.Where(r => r.Running)], usable);
        _finished.Show([.. shown.Rows.Where(r => !r.Running)], usable);

        // THE SELECTION FOLLOWS ITS CARD, across sections too: a card that stops moves from Running to Finished and
        // stays selected, so the x-then-x of decision 13 deletes the card it stopped. A card that is gone hands the
        // selection to the card now where it was.
        var cards = AllCards();
        if (_selected is not null && cards.All(c => c.Row.Key != _selected) && cards.Count > 0)
        {
            var was = Math.Max(0, before.IndexOf(_selected));
            _selected = cards[Math.Min(was, cards.Count - 1)].Row.Key;
        }
        foreach (var card in cards) card.Selected = card.Row.Key == SelectedCard()?.Row.Key;

        // AND THE KEYBOARD WITH IT, when a card had it: the one it was on may have gone, or moved section.
        if (hadFocus && !RowsHaveFocus && SelectedCard() is { } keep) Focus(keep);

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
        var chosen = RowsHaveFocus ? SelectedCard() : null;
        string HintFor(Section section) =>
            chosen is null || !section.Holds(chosen) ? ""
            : Muted(Cut(chosen.Row switch
            {
                AgentRow { View: { Foreground: true, Running: true } } => "x stop · b background",
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

    /// <summary>The selected card's row key; null before anything has been selected.</summary>
    private string? _selected;

    /// <summary>Every card, Running's then Finished's — the order ↑ and ↓ walk.</summary>
    private List<Card> AllCards() => [.. _running.Cards, .. _finished.Cards];

    private List<PanelRow> AllRows() => [.. AllCards().Select(c => c.Row)];

    /// <summary>The selected card, or the first when none has been chosen yet.</summary>
    private Card? SelectedCard()
    {
        var cards = AllCards();
        return cards.FirstOrDefault(c => c.Row.Key == _selected) ?? cards.FirstOrDefault();
    }

    /// <summary>Selects a card — a click, a key, its link taking the keyboard — and redraws the marks and hints.</summary>
    private void Select(Card card, bool focus)
    {
        var changed = _selected != card.Row.Key;
        _selected = card.Row.Key;
        if (focus) Focus(card);
        if (changed || focus) Redraw();
    }

    private static void Focus(Card card) =>
        card.Anchor.GetParentWindow()?.FocusManager.SetFocus(card.Anchor, FocusReason.Programmatic);

    // ONLY WHEN IT CHANGED. The clocks change every second while a task runs, but an idle panel re-set on every tick
    // would invalidate layout for nothing.
    private static void SetIfChanged(MarkupControl control, List<string> lines)
    {
        if (control.Text == string.Join("\n", lines)) return;
        control.SetContent(lines);
    }

    /// <summary>
    /// One section: a heading over its cards.
    ///
    /// <para>NOTHING COLLAPSES (decision 33′). A folded Finished section is one more thing to learn and to wonder
    /// about; the column scrolls, and x deletes what the user is done with.</para>
    /// </summary>
    private sealed class Section
    {
        private readonly JobsPanel _owner;
        private readonly string? _title;

        /// <param name="title">The section's heading; null for none.</param>
        public Section(JobsPanel owner, string? title)
        {
            _owner = owner;
            _title = title;
            var builder = Ctl.CollapsiblePanel(title ?? "").WithMargin(1, 1, 1, 0).NonCollapsible();
            Panel = (title is null ? builder.HideHeader() : builder).Build();
        }

        public CollapsiblePanel Panel { get; }

        /// <summary>This section's key hint, placed under it by the panel.</summary>
        public MarkupControl Hint { get; } = Ctl.Markup().WithMargin(1, 0, 1, 0).Build();

        /// <summary>The cards, in the order shown.</summary>
        public List<Card> Cards { get; } = [];

        public bool Holds(Card card) => Cards.Contains(card);

        public void ShowHint(string hint)
        {
            Hint.Visible = hint.Length > 0;
            if (Hint.Text != hint) Hint.SetContent([hint]);
        }

        /// <summary>
        /// Shows these rows, each as a card.
        ///
        /// <para>A CARD IS KEPT FOR ITS ROW, and updated in place on every tick of a running clock: rebuilt each second,
        /// it would drop the keyboard and the mouse's hover. Only a change in which rows there are, or their order,
        /// re-seats the cards.</para>
        /// </summary>
        public void Show(IReadOnlyList<PanelRow> rows, int usable)
        {
            var byKey = Cards.ToDictionary(c => c.Row.Key);
            var next = rows.Select(r => byKey.TryGetValue(r.Key, out var kept) ? kept : new Card(_owner)).ToList();
            for (var i = 0; i < rows.Count; i++) next[i].Show(rows[i], usable);

            if (!next.SequenceEqual(Cards))
            {
                foreach (var card in Cards) Panel.RemoveControl(card.Box);
                foreach (var card in next) Panel.AddControl(card.Box);
                Cards.Clear();
                Cards.AddRange(next);
            }

            Panel.Visible = rows.Count > 0;
            if (_title is not null && Panel.Title != _title) Panel.Title = _title;
        }

        /// <summary>The section as text — its heading, then its cards.</summary>
        public IEnumerable<string> Text()
        {
            if (Cards.Count == 0) yield break;
            if (_title is not null) yield return _title;
            foreach (var card in Cards) foreach (var line in card.Text) yield return line;
        }
    }

    /// <summary>
    /// One card: a row drawn as real controls — its title with the ⏹ or ✕ button at the right, its facts, and a link
    /// that opens it — on a surface of its own (decision 34).
    ///
    /// <para>REAL CONTROLS, NOT A LIST ITEM'S TEXT: the link and the button take a click and the keyboard as
    /// themselves, where a list item's text could only be hit-tested by column. A click anywhere else on the card
    /// selects it.</para>
    ///
    /// <para>THE KEYBOARD SITS ON THE LINK of the selected card, so Enter opens it and the panel's ↑ ↓ and x act on
    /// the card the bar marks.</para>
    /// </summary>
    private sealed class Card
    {
        private readonly JobsPanel _owner;
        private readonly MarkupControl _title = Ctl.Markup().WithAlignment(HorizontalAlignment.Stretch).Build();
        private readonly MarkupControl _facts = Ctl.Markup().WithAlignment(HorizontalAlignment.Stretch).Build();
        private readonly MarkupControl _runsOn = Ctl.Markup().WithAlignment(HorizontalAlignment.Stretch).Build();
        private readonly GridControl _top;
        private bool _hover;
        private bool _selected;
        private string? _glyph;

        public Card(JobsPanel owner)
        {
            _owner = owner;
            Anchor = Ctl.Markup().WithAlignment(HorizontalAlignment.Stretch).Build();
            Button = new SharpConsoleUI.Builders.ButtonBuilder().WithText("⏹").WithBorder(ButtonBorderStyle.None).Build();
            Move = new SharpConsoleUI.Builders.ButtonBuilder().WithText("⇥").WithBorder(ButtonBorderStyle.None).Build();
            Move.Visible = false;

            _top = Ctl.Grid()
                .Columns(GridLength.Star(1), GridLength.Auto(), GridLength.Auto())
                .Rows(GridLength.Cells(1))
                .Place(_title, 0, 0)
                .Place(Move, 0, 1)
                .Place(Button, 0, 2)
                .WithAlignment(HorizontalAlignment.Stretch)
                .Build();
            _runsOn.Visible = false;
            Box = Ctl.Panel().NoBorder().WithPadding(0).AddControl(_top).AddControl(_facts).AddControl(_runsOn).AddControl(Anchor).Build();
            Box.Margin = new Margin(0, 0, 0, 1);

            // A CLICK ANYWHERE ON THE CARD SELECTS IT; the link opens it, the button does what x does.
            Box.MouseClick += (_, _) => _owner.Select(this, focus: true);
            Box.MouseEnter += (_, _) => { _hover = true; Paint(); };
            Box.MouseLeave += (_, _) => { _hover = false; Paint(); };
            Anchor.LinkClicked += (_, _) => _owner.RowChosen?.Invoke(new RowChoice(Row, RowAction.Open));
            Anchor.GotFocus += (_, _) => _owner.Select(this, focus: false);
            Button.GotFocus += (_, _) => _owner.Select(this, focus: false);
            Button.Click += (_, _) => _owner.RowChosen?.Invoke(new RowChoice(Row, RowAction.StopOrForget));
            Move.GotFocus += (_, _) => _owner.Select(this, focus: false);
            Move.Click += (_, _) => _owner.RowChosen?.Invoke(new RowChoice(Row, RowAction.Background));

            // ON ITS SURFACE FROM THE START, not only once selected or hovered.
            Paint();
        }

        public PanelRow Row { get; private set; } = null!;

        public PanelControl Box { get; }

        /// <summary>The "View …" link — where the keyboard sits while this card is selected.</summary>
        public MarkupControl Anchor { get; }

        /// <summary>⇥ — shown on a foreground agent's card, before ⏹, to move it to the background.</summary>
        public ButtonControl Move { get; }

        /// <summary>⏹ while it runs — a foreground agent's included — or ✕ once it has ended.</summary>
        public ButtonControl Button { get; }

        public bool HasFocus => Anchor.HasFocus || Button.HasFocus || Move.HasFocus;

        /// <summary>The card as text, for the panel's RenderedText.</summary>
        public IReadOnlyList<string> Text { get; private set; } = [];

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Paint();
                Redraw();
            }
        }

        private CardText _shown = null!;

        public void Show(PanelRow row, int usable)
        {
            Row = row;
            var text = CardText.For(row);
            if (_shown == text && _usable == usable) return;
            _shown = text;
            _usable = usable;
            Redraw();
        }

        private int _usable;

        /// <summary>The card's lines, set into its controls and kept as text.</summary>
        private void Redraw()
        {
            if (_shown is null) return;
            var bar = _selected ? $"[{ColorScheme.AccentMarkup}]▐[/] " : "  ";
            // EACH BUTTON TAKES THREE COLUMNS AT THE RIGHT — " ⏹ " — and the bar two at the left.
            var buttons = (_shown.Control is null ? 0 : 1) + (_shown.Movable ? 1 : 0);
            var room = Math.Max(1, _usable - 3 * buttons);
            var title = $"{_shown.Mark} {Value(Cut(_shown.Title, room))}";
            var facts = $"  {Muted(Cut(string.Join(" · ", _shown.Facts), _usable))}";
            var link = $"  [link=open]{Value(_shown.View)} ›[/]";
            SetIfChanged(_title, [bar + title]);
            SetIfChanged(_facts, [bar + facts]);
            // WHERE IT RUNS, ON ITS OWN LINE: the facts line is cut at the panel's width, and a label of
            // thirty-odd characters there would push out how the agent stands and how long it has run.
            var runsOn = _shown.RunsOn is { Length: > 0 } r ? $"  {Muted(Cut(r, _usable))}" : null;
            if (_runsOn.Visible != (runsOn is not null)) _runsOn.Visible = runsOn is not null;
            if (runsOn is not null) SetIfChanged(_runsOn, [bar + runsOn]);
            SetIfChanged(Anchor, [bar + link]);

            if (Move.Visible != _shown.Movable) Move.Visible = _shown.Movable;
            if (_glyph != _shown.Control)
            {
                _glyph = _shown.Control;
                Button.Visible = _glyph is not null;
                if (_glyph is not null) Button.Text = _glyph;
            }

            var controls = _shown.Movable ? $"⇥ {_shown.Control}" : _shown.Control;
            var titleLine = $"{_shown.Mark} {TitleLine(_shown.Title, controls, _usable)}";
            Text = runsOn is null
                ? [titleLine, facts, $"  {Value(_shown.View)} ›"]
                : [titleLine, facts, runsOn, $"  {Value(_shown.View)} ›"];
        }

        /// <summary>The card's surface — hovered, selected or plain — from the theme's current colours.</summary>
        public void Paint()
        {
            var surface = _selected ? ColorScheme.CardSelectedSurface
                : _hover ? ColorScheme.CardHoverSurface
                : ColorScheme.CardSurface;
            Box.BackgroundColor = surface;
            // THE TITLE ROW IS A GRID OF ITS OWN, and a grid paints its own surface unless told the card's — the title
            // line then sat on a different shade from the rest of the card.
            _top.BackgroundColor = surface;
            Button.BackgroundColor = surface;
            Button.ForegroundColor = ColorScheme.MutedRgb;
            Move.BackgroundColor = surface;
            Move.ForegroundColor = ColorScheme.MutedRgb;
        }
    }

    /// <summary>What a card says, independent of how it is drawn — compared to tell whether a tick changed it.</summary>
    /// <param name="Mark">The kind while it runs, ✓ or ✗ once it has ended, as markup.</param>
    /// <param name="Title">The command, or what the agent was asked to do.</param>
    /// <param name="Control">⏹ while it runs, ✕ once it has ended.</param>
    /// <param name="Facts">Its kind, how it stands, how long.</param>
    /// <param name="View">What opening it shows.</param>
    /// <param name="Movable">A foreground agent's card: ⇥ before ⏹, to move it to the background (Ctrl+B spec 6′).</param>
    private sealed record CardText(string Mark, string Title, string? Control, EquatableList Facts, string View,
        bool Movable = false)
    {
        /// <summary>Where an agent runs, `name:model`, shown on a line of its own; null for a shell card.</summary>
        public string? RunsOn { get; init; }

        public static CardText For(PanelRow row) => row switch
        {
            JobRow job => ShellCard(job),
            AgentRow agent => AgentCard(agent),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
    }

    /// <summary>A list compared by its items, so two CardTexts with the same facts are equal.</summary>
    private sealed class EquatableList(IReadOnlyList<string> items) : IEnumerable<string>
    {
        private readonly IReadOnlyList<string> _items = items;
        public override bool Equals(object? obj) => obj is EquatableList o && _items.SequenceEqual(o._items);
        public override int GetHashCode() => _items.Aggregate(0, (h, s) => HashCode.Combine(h, s));
        public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A shell's card: its command, then "Shell · how it ended · how long", then "View output".</summary>
    private static CardText ShellCard(JobRow job)
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

        return new CardText(Mark(running, view.State == BackgroundJobState.Exited && view.ExitCode == 0, "▭"),
            OutputTail.OneLine(view.Job.Command), CardControl(running), new EquatableList(facts),
            "View output");
    }

    /// <summary>
    /// An agent's card: what it was asked to do, then "Agent · how it stands · how long", then "View transcript".
    ///
    /// <para>THE DESCRIPTION IS THE TITLE, as the sheet's: it says what the agent is for, where the handle is a slug
    /// of it. A spawn with none falls back to the handle.</para>
    ///
    /// <para>NO ⏹ ON A FOREGROUND AGENT: the parent's turn is waiting on its answer (decision 9).</para>
    /// </summary>
    private static CardText AgentCard(AgentRow row)
    {
        var view = row.View;
        var title = view.Kept.Description is { Length: > 0 } d ? d : view.Kept.Name;
        var facts = new List<string> { "Agent" };
        if (view.State is not (AgentRowState.Running or AgentRowState.Done)) facts.Add(AgentRows.Word(view.State));
        if (view.Foreground && view.Running) facts.Add("foreground");
        facts.Add(DisplayNumber.Took(view.Elapsed));

        return new CardText(Mark(view.Running, view.State == AgentRowState.Done, "◇"), OutputTail.OneLine(title),
            CardControl(view.Running), new EquatableList(facts), "View transcript", Movable: view is { Running: true, Foreground: true })
        {
            // WHERE IT RUNS, always — a card for a paid child must not read like one for a free one.
            RunsOn = view.Kept.Agent.ModelId is { Length: > 0 } m
                ? CxAgent.Core.Llm.ModelNames.ShortLabel(m) + (view.Kept.Agent.Root is { } folder ? $" in {folder}" : "")
                : null,
        };
    }

    /// <summary>A card's mark, as markup: the kind while it runs, ✓ or ✗ once it has ended (decision 16).</summary>
    private static string Mark(bool running, bool succeeded, string kind) =>
        running ? Muted(kind)
        : succeeded ? $"[{ColorScheme.AffirmativeMarkup}]✓[/]"
        : $"[{ColorScheme.DangerMarkup}]✗[/]";

    /// <summary>
    /// A card's one control — ⏹ to stop it while it runs, ✕ to delete it once it has ended. A foreground agent's
    /// card has ⏹ too, since stopping it alone answers its call and the turn goes on (Ctrl+B spec decision 11).
    ///
    /// <para>ONE PLACE AND ONE KEY FOR BOTH (decision 33′): the control sits at the same edge on every card, and x
    /// does what it does, so there is one thing to learn about a card rather than one per state.</para>
    /// </summary>
    private static string? CardControl(bool running) => running ? "⏹" : "✕";

    /// <summary>The title, and the card's control at the right edge, the title padded so every control lines up.</summary>
    private static string TitleLine(string title, string? control, int usable)
    {
        if (control is null) return Value(Cut(title, usable));
        var room = Math.Max(1, usable - 1 - Columns(control));
        var text = Cut(title, room);
        return $"{Value(text)}{new string(' ', Math.Max(0, room - Columns(text)))} {Muted(control)}";
    }

    /// <summary>
    /// The text cut to fit this many columns, with "…" when it does not.
    ///
    /// <para>IN COLUMNS, NOT CHARACTERS: a CJK character or an emoji takes two, and counting it as one pushed the card's
    /// control out of line with every other card's, or wrapped the title line.</para>
    /// </summary>
    private static string Cut(string text, int width)
    {
        if (Columns(text) <= width) return text;
        var used = 0;
        var kept = new System.Text.StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var w = SharpConsoleUI.Helpers.UnicodeWidth.GetRuneWidth(rune);
            if (used + w > width - 1) break;
            kept.Append(rune.ToString());
            used += w;
        }
        return kept + "…";
    }

    /// <summary>How many terminal columns the text takes.</summary>
    private static int Columns(string text) => SharpConsoleUI.Helpers.UnicodeWidth.GetStringWidth(text);

    private static string Value(string text) => MarkupParser.Escape(text);

    private static string Muted(string text) => $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(text)}[/]";

    /// <summary>m:ss under an hour, h:mm:ss past it — what a stopwatch shows. Shared with the job and agent tabs'
    /// headers, so a header and its tab title cannot disagree about how long a task has run.</summary>
    internal static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
