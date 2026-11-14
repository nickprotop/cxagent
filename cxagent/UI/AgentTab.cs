using CxAgent.Core.Commands;
using CxAgent.Core.Agents;
using CxAgent.Core.Sessions;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>A prompt taken off a closing tab, with how Escape answers it — for the window to raise elsewhere.</summary>
public sealed record HeldPrompt(IWindowControl Prompt, Action? Deny);

/// <summary>What an agent tab draws with, built by the window.</summary>
/// <param name="Chat">The transcript control — the same one a session tab uses, styled by the window.</param>
/// <param name="Transcript">Writes the child's words into <paramref name="Chat"/>.</param>
/// <param name="Tools">Writes the child's tool rows into <paramref name="Chat"/>.</param>
/// <param name="Calls">The finished calls the job sink draws a round's row from; null draws none.</param>
/// <param name="Clock">Advances live rows and thinking headers — the window's one-second tick.</param>
/// <param name="Post">Runs an action on the UI thread.</param>
public sealed record AgentTabViews(ChatTranscriptControl Chat, ISessionObserver Transcript, IToolObserver Tools,
    Action<ToolCallReport>? Calls, Action Clock, Action<Action> Post);

/// <summary>
/// One kept sub-agent's transcript, live, with a Stop button while it runs.
///
/// <para>A TAB, NOT A PANEL, for a job tab's reason: switching away is not closing it, so a long run can be read and
/// come back to.</para>
///
/// <para>A VIEW OF THE CHILD, NEVER ITS OWNER. Opening it attaches to the child's feed — replayed, then live — and
/// closing it only detaches; the child works on, and its buffers keep recording, either way.</para>
/// </summary>
public sealed class AgentTab : IDisposable
{

    private readonly AgentTabViews _views;
    private readonly IDisposable _attached;
    private readonly MarkupControl _header = Controls.Markup().WithMargin(1, 0, 1, 0).Build();
    /// <summary>
    /// The line under the field, where a session's composer has its mode line: what this agent is, and what Enter does
    /// with what is typed — the session's permission and mode controls do not apply to it.
    /// </summary>
    private readonly MarkupControl _hint = Controls.Markup().WithAlignment(HorizontalAlignment.Stretch)
        .WithBackgroundColor(ColorScheme.ComposerSurface).Build();
    /// <summary>The follow-up field — the session composer's own kind, from the same factory.</summary>
    private readonly PromptControl _input = MainWindow.NewPrompt();

    /// <summary>The tab's input box, for the window to recolour on a theme switch.</summary>
    internal PromptControl Input => _input;
    private readonly ButtonControl _stop;
    private readonly ButtonControl _move;
    private readonly ButtonControl _release;
    private readonly ButtonControl _skipWait;
    private readonly ButtonControl _newTalk;
    private readonly ButtonControl _handOver;

    /// <summary>
    /// <b>Send to session</b> was pressed: the user's last ask and its answer, for the window to put in the session's
    /// composer. An event, because the tab does not know the window that holds the session's tab.
    /// </summary>
    public event Action<Core.Models.PastedText>? HandedOver;
    /// <summary>
    /// The state in capitals at the head of the button row. THE ROW IS NEVER BLANK: Stop and Background are there
    /// only while the agent runs, and without this an idle tab opened on an empty line above a lone rule.
    /// </summary>
    private readonly MarkupControl _state = Controls.Markup().WithMargin(1, 0, 0, 0).Build();
    private string _stateText = "";
    private string _headerText = "";
    private string _hintText = "";
    private AgentRowView? _now;

    public AgentTab(Session session, StoredAgent kept, AgentTabViews views)
    {
        Session = session;
        Kept = kept;
        _views = views;

        // OFF THE UI THREAD, as the job tab's Kill: a stop cancels the child's turn, whose callbacks run inline.
        // THROUGH /tasks stop, so the parent's transcript records the stop as the panel's x and the command do.
        _stop = new ButtonBuilder()
            .WithText(" Stop ")
            .WithColorRole(ColorScheme.Destructive)
            .OnClick((object? _, ButtonControl button) =>
            {
                // A FOREGROUND AGENT IS STOPPED ALONE, its call answered and the parent's turn left to go on (Ctrl+B
                // spec decision 11); a background one through /tasks stop. StopAgent decides which, not this tab's
                // view, which can still say foreground for a second after a move.
                _ = Task.Run(() => Session.StopAgent(Kept.Name));

                // THE KEYBOARD TO THE COMPOSER NOW, not when the next tick hides the button: until then a space
                // typed after the press presses Stop again.
                button.GetParentWindow()?.FocusManager.SetFocus(MainControl, FocusReason.Programmatic);
            })
            .Build();
        // BACKGROUND, BESIDE STOP, while the parent waits on this agent (Ctrl+B spec decision 7): the call is answered
        // with a receipt, and the agent goes on in the background.
        _move = new ButtonBuilder()
            .WithText(" Background ")
            .OnClick((object? _, ButtonControl button) =>
            {
                _ = Task.Run(() => Session.MoveToBackground(Kept.Name));
                button.GetParentWindow()?.FocusManager.SetFocus(MainControl, FocusReason.Programmatic);
            })
            .Build();
        // RELEASE, ON A COLLEAGUE AT REST: it leaves the session, through /colleagues release — whose answer is said in
        // the session, and whose let-go closes this tab. On the pool: letting a worker go stops any commands it left.
        _release = new ButtonBuilder()
            .WithText(" Release ")
            .OnClick((object? _, ButtonControl _) =>
            {
                if (Session.SubAgents.BadgeOf(Kept.Name) is { } badge)
                    _ = Task.Run(() => Session.ColleaguesCommand("release " + badge.Name));
            })
            .Build();
        _release.Visible = false;
        // DON'T WAIT, while an ask waits for what the colleague lived to be learned: it starts now, from what it knew.
        _skipWait = new ButtonBuilder()
            .WithText(" Don't wait ")
            .OnClick((object? _, ButtonControl _) => Session.Colleagues?.SkipWait(Kept.Name))
            .Build();
        _skipWait.Visible = false;
        // A NEW CONVERSATION, IN A TALK TAB AT REST: it starts over from what the colleague has learned. Nothing is asked
        // first — every exchange is already distilled. A refusal (it began answering meanwhile) is said in the session.
        _newTalk = new ButtonBuilder()
            .WithText(" New conversation ")
            .OnClick((object? _, ButtonControl _) =>
            {
                if (Session.Colleagues?.NewConversation(Kept.Name) is { } why) Session.NotifyWarning(why);
            })
            .Build();
        _newTalk.Visible = false;
        // SEND TO SESSION, IN AN ASK TAB AT REST after the user's own ask (user, 2026-11-14): the question and its answer
        // go to the session's composer as one block, for the user to say what to do with it — never sent by itself.
        _handOver = new ButtonBuilder()
            .WithText(" Send to session ")
            .OnClick((object? _, ButtonControl _) =>
            {
                if (Session.Colleagues?.HandOver(Kept.Name) is { } block) HandedOver?.Invoke(block);
            })
            .Build();
        _handOver.Visible = false;
        var bar = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        bar.AddItem(_state);
        bar.AddItem(_skipWait);
        bar.AddItem(_newTalk);
        bar.AddItem(_handOver);
        bar.AddItem(_release);
        bar.AddItem(_move);
        bar.AddItem(_stop);

        // THE SESSION'S COMPOSER, BUILT BY THE SAME CODE, with this agent's line in the mode line's place.
        var composer = MainWindow.BuildComposer(_input, _hint);
        Composer = composer.Composer;

        // THE SAME RULE AS A SESSION'S, with the same key on it: F12 here exports this agent alone.
        ComposerRule = composer.Rule;
        MainWindow.TitleComposerRule(ComposerRule, inAside: false);

        // ON ENTER, NOT ON EVERY KEYSTROKE — the question prompt's lesson.
        _input.Entered += (_, _) => _ = SubmitComposer();

        // THE HEADER PINNED ABOVE THE TRANSCRIPT, as the job tab's: following a long run must not carry the state
        // and the clock off the top. Cells for the composer, not Auto — see SessionTab.ComposerRows for the
        // intrinsic-measure trap an Auto row under a scrolling transcript falls into.
        Content = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Cells(1), GridLength.Cells(1), GridLength.Auto(), GridLength.Star(1),
                  GridLength.Cells(MainWindow.ComposerRows))
            .Place(bar, 0, 0)
            .Place(new RuleControl { Color = ColorScheme.MutedRgb }, 1, 0)
            .Place(_header, 2, 0)
            .Place(views.Chat, 3, 0)
            .Place(Composer, 4, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        Tick(DateTimeOffset.UtcNow);

        // ATTACHED LAST, once every control the replay writes into exists.
        _attached = kept.Agent.Feed.Attach(new ChildView(views.Transcript, views.Tools, views.Calls));
    }

    public Session Session { get; }

    public StoredAgent Kept { get; }

    public string AgentId => Kept.Agent.Agent.Id;

    public GridControl Content { get; }

    /// <summary>The header as last drawn, as markup — for tests, which read what the tab says about itself.</summary>
    public string HeaderText => _headerText;

    /// <summary>The composer row — what a permission prompt takes the place of.</summary>
    internal GridControl Composer { get; }

    /// <summary>The rule above the composer, titled with the export key.</summary>
    public RuleControl ComposerRule { get; }

    public ChatTranscriptControl Chat => _views.Chat;

    /// <summary>Where the keyboard goes when the tab comes to the front.</summary>
    public IFocusableControl MainControl => CanType ? _input : _views.Chat;

    /// <summary>Test seam: the Stop button, whose focus is what a test of its hiding reads.</summary>
    public ButtonControl StopForTest => _stop;

    private IWindowControl? _prompt;

    /// <summary>Whether a permission prompt occupies the composer row.</summary>
    public bool HasPrompt => _prompt is not null;

    /// <summary>How Escape answers the prompt here with "no"; null with none up.</summary>
    public Action? DenyPrompt { get; private set; }

    /// <summary>
    /// Puts a prompt in the composer's place, as a session tab does — the same widget, so it is answered the same
    /// way. The row grows to the question's height while it is up.
    /// </summary>
    public void ShowPrompt(IWindowControl prompt, Action? deny)
    {
        if (_prompt is not null) return;
        Content.ReplaceControl(Composer, prompt);
        Content.RowDefinitions[4] = GridLength.Auto();
        _prompt = prompt;
        DenyPrompt = deny;
    }

    /// <summary>Puts the composer back if this prompt is the one up here; false when it is not.</summary>
    public bool RestorePrompt(IWindowControl prompt)
    {
        if (!ReferenceEquals(_prompt, prompt)) return false;
        Content.ReplaceControl(prompt, Composer);
        Content.RowDefinitions[4] = GridLength.Cells(MainWindow.ComposerRows);
        _prompt = null;
        DenyPrompt = null;
        return true;
    }

    /// <summary>Takes the prompt off the tab, unanswered — the tab is closing and the question still needs one.</summary>
    public HeldPrompt? TakePrompt()
    {
        if (_prompt is not { } prompt) return null;
        var held = new HeldPrompt(prompt, DenyPrompt);
        RestorePrompt(prompt);
        return held;
    }

    /// <summary>Whether the composer takes a follow-up: not while the parent waits on this agent, nor once its session has ended.</summary>
    public bool CanType => _now is not { Foreground: true, Running: true } && !Session.SubAgents.Ended;

    /// <summary>
    /// Sends what the composer holds, clears it, and shows the tab whatever the send says for itself: a pasted
    /// token as its text, and the line as typed — the label in its place — for the tab's row and the parent's
    /// trail, as the session's own composer sends it.
    ///
    /// <para>THE ANSWER IS NOT WRITTEN HERE: the tab is attached to the child, and its answer arrives as it is
    /// written, with the user's own turn above it. A refusal or a delivery note goes through the tab's own transcript
    /// sink, which knows how to reach the UI thread.</para>
    /// </summary>
    public Task SubmitComposer()
    {
        var line = ComposerLine.Of(_input);
        return Send(line.Text, line.Echo, line.Attachments);
    }

    private async Task Send(string text, string? echo, Core.Models.Attachments? attachments)
    {
        if (!CanType || string.IsNullOrWhiteSpace(text)) return;
        _input.Input = "";
        if (await Session.SendFromAgentTab(Kept.Name, text, echo, attachments) is { } line)
            _views.Transcript.Said(new Message(line, Severity.Info));
    }

    /// <summary>Whether this is a colleague's Ask or Talk tab, whose composer runs and offers the colleague's commands.</summary>
    public bool IsColleagueTab => Session.SubAgents.BadgeOf(Kept.Name) is not null;

    /// <summary>Whether Stop is offered: running, in the background, in a session still open.</summary>
    public bool CanStop => _now is { Running: true } && !Session.SubAgents.Ended;

    /// <summary>Whether Background is offered: the parent's turn is waiting on this agent, in a session still open.</summary>
    public bool CanMove => _now is { Running: true, Foreground: true } && !Session.SubAgents.Ended;

    /// <summary>Whether Release is offered: a colleague, at rest through both its doors, in a session still open.</summary>
    public bool CanRelease => Session.SubAgents.BadgeOf(Kept.Name) is not null && _now is not { Running: true }
                              && !AgentRows.OtherDoorBusy(Session.SubAgents, Kept, DateTimeOffset.UtcNow)
                              && !Session.SubAgents.Ended;

    /// <summary>Whether New conversation is offered: a colleague's Talk tab, at rest, in a session still open.</summary>
    public bool CanStartOver => CanRelease && Session.SubAgents.KeepsConversation(Kept.Name);

    /// <summary>Test seam: the Release button.</summary>
    public ButtonControl ReleaseForTest => _release;

    /// <summary>Test seam: the Send to session button.</summary>
    public ButtonControl HandOverForTest => _handOver;

    /// <summary>Test seam: the Background button.</summary>
    public ButtonControl MoveForTest => _move;

    /// <summary>The tab title: the state first, as a job tab's.</summary>
    public string Title() => TitleFor(Kept, _now, Session.SubAgents.BadgeOf(Kept.Name)) + Door(Session.SubAgents, Kept);

    /// <summary>
    /// Which of a colleague's two tabs this is, after its title — " · talk" for the conversation with the user, " · ask"
    /// for its asks — and nothing for any other worker (user, 2026-11-11: separate tabs for Ask and Talk).
    /// </summary>
    public static string Door(SubAgentStore store, StoredAgent kept) =>
        store.BadgeOf(kept.Name) is null ? "" : store.KeepsConversation(kept.Name) ? " · talk" : " · ask";

    /// <summary>
    /// The same door in the state line's capitals — "ASK" or "TALK" — or "" for any other worker. THE LINE ABOVE THE HEADER
    /// IS WHERE THE EYE GOES for how a tab stands, and with two tabs of one colleague side by side, which one this is
    /// belongs beside it (user, 2026-11-11).
    /// </summary>
    public static string DoorWord(SubAgentStore store, StoredAgent kept) => Door(store, kept).TrimStart(' ', '·').ToUpperInvariant();

    /// <summary>
    /// A worker tab's title: its state first, then — for a colleague — its glyph and its name, which says more than a
    /// handle slugged from it.
    /// </summary>
    /// <param name="kept">The agent.</param>
    /// <param name="now">How it stands, or null for one never given work.</param>
    /// <param name="badge">What it is — read from the store, not the row: one that joined idle has no row yet.</param>
    public static string TitleFor(StoredAgent kept, AgentRowView? now, WorkerBadge? badge)
    {
        var glyph = now?.State switch
        {
            AgentRowState.Queued or AgentRowState.WaitingToLearn => "○",
            AgentRowState.Running or AgentRowState.WaitingForPermission => "●",
            AgentRowState.Done => "✓",
            null => "·",
            _ => "✗",
        };
        var name = badge?.Name ?? kept.Name;
        var shown = name.Length <= 24 ? name : name[..23] + "…";
        // A COLLEAGUE'S TITLE CARRIES NO ✓ OR ✗: between tasks it is idle, and only working shows beside its glyph.
        if (badge is null) return $"{glyph} {shown}";
        return now is { Running: true } ? $"{glyph} {badge.Glyph} {shown}" : $"{badge.Glyph} {shown}";
    }

    /// <summary>One clock tick: the state, the header, what the composer row offers, and the live rows' clocks.</summary>
    public void Tick(DateTimeOffset now)
    {
        _now = AgentRows.Describe(Session.SubAgents, Kept, now);
        // A TALK WORKER'S WORK IS THE CONVERSATION — the card's For marks it so, and a tab reads one worker alone.
        if (_now is not null && Session.SubAgents.KeepsConversation(Kept.Name)) _now = _now with { Talking = true };
        var stopHadFocus = _stop.HasFocus;
        if (_stop.Visible != CanStop) _stop.Visible = CanStop;
        var moveHadFocus = _move.HasFocus;
        if (_move.Visible != CanMove) _move.Visible = CanMove;
        if (moveHadFocus && !_move.Visible)
            _move.GetParentWindow()?.FocusManager.SetFocus(MainControl, FocusReason.Programmatic);
        if (_input.Visible != CanType) _input.Visible = CanType;
        if (_release.Visible != CanRelease) _release.Visible = CanRelease;
        var waiting = _now is { State: AgentRowState.WaitingToLearn };
        if (_skipWait.Visible != waiting) _skipWait.Visible = waiting;
        if (_newTalk.Visible != CanStartOver) _newTalk.Visible = CanStartOver;
        var handOver = Session.Colleagues?.CanHandOver(Kept.Name) == true;
        var handOverHadFocus = _handOver.HasFocus;
        if (_handOver.Visible != handOver) _handOver.Visible = handOver;
        if (handOverHadFocus && !_handOver.Visible)
            _handOver.GetParentWindow()?.FocusManager.SetFocus(MainControl, FocusReason.Programmatic);

        // THE KEYBOARD LEAVES A STOP THAT HAS GONE. Clicked, Stop keeps the focus; hidden, it kept it still, so what
        // was typed next went nowhere and every space pressed the hidden button again.
        if (stopHadFocus && !_stop.Visible)
            _stop.GetParentWindow()?.FocusManager.SetFocus(MainControl, FocusReason.Programmatic);

        var badge = Session.SubAgents.BadgeOf(Kept.Name);
        var word = StateWord(_now, badge, badge is null ? null : Session.Colleagues?.CardOf(Kept.Name), Session.SubAgents.Ended);
        var door = DoorWord(Session.SubAgents, Kept);
        if (word != _stateText || door != _doorText)
        {
            _stateText = word;
            _doorText = door;
            var stated = $"[bold {StateColour(_now, Session.SubAgents.Ended)}]{MarkupParser.Escape(word)}[/]";
            _state.SetContent([door.Length == 0 ? stated : $"[bold {ColorScheme.AccentMarkup}]{door}[/] {Muted("·")} {stated}"]);
        }

        // ONLY WHEN IT CHANGED: this runs every second for every open tab.
        var header = HeaderLines();
        var text = string.Join("\n", header);
        if (text != _headerText)
        {
            _headerText = text;
            _header.SetContent(header);
        }

        var hint = Hint();
        if (hint != _hintText)
        {
            _hintText = hint;
            _hint.SetContent([hint]);
        }

        _views.Clock();
    }

    /// <summary>Re-applies the thinking blocks' colour after a theme change — see MainWindow's theme loop.</summary>
    public void Recolour()
    {
        (_views.Transcript as ChatTranscriptSink)?.Recolour();
        MainWindow.TitleComposerRule(ComposerRule, inAside: false);
    }

    public void Dispose() => _attached.Dispose();

    /// <summary>Test seam: the state word the button row shows.</summary>
    internal string StateText => _stateText;

    // THE DOOR LAST DRAWN: a worker kept as a colleague while its tab is open becomes a Talk worker then, so the line is
    // redrawn when either part changes.
    private string _doorText = "";

    /// <summary>
    /// The button row's state word, in capitals: the same word the header's facts line uses — a colleague's own
    /// (idle between tasks, never done) — so the two never disagree. One never given work is idle.
    /// </summary>
    public static string StateWord(AgentRowView? now, WorkerBadge? badge, CxAgent.Core.Colleagues.ColleagueCard? card, bool ended)
    {
        if (ended) return "SESSION ENDED";
        // A COLLEAGUE'S WORD IS ITS CARD'S — "answering an ask", "talking with you" — so the tab and the card agree.
        var word = badge is not null
            ? card is null ? AgentRows.ColleagueWord(now?.State ?? AgentRowState.Done, learning: false)
              : now is not null ? AgentRows.ColleagueWord(now, card)
              : AgentRows.ColleagueWord(AgentRowState.Done, card)
            : now is null ? "idle" : AgentRows.Word(now.State);
        return word.ToUpperInvariant();
    }

    // WORKING IN THE ACCENT, AN ASK IN CAUTION, AN END BY HOW IT WENT; resting is muted — it is the usual state and
    // should not shout.
    private static string StateColour(AgentRowView? now, bool ended) => ended ? ColorScheme.MutedMarkup : now?.State switch
    {
        AgentRowState.Running or AgentRowState.Queued => ColorScheme.AccentMarkup,
        AgentRowState.WaitingForPermission or AgentRowState.WaitingToLearn => ColorScheme.CautionMarkup,
        AgentRowState.Failed => ColorScheme.DangerMarkup,
        _ => ColorScheme.MutedMarkup,
    };

    /// <summary>Test seam: the header's lines as the tab draws them.</summary>
    internal List<string> HeaderLinesForTest() => HeaderLines();

    private List<string> HeaderLines()
    {
        var badge = Session.SubAgents.BadgeOf(Kept.Name);
        var lines = new List<string>
        {
            badge is null ? $"[bold]{MarkupParser.Escape(Kept.Name)}[/]" : $"[bold]{badge.Glyph} {MarkupParser.Escape(badge.Name)}[/]",
        };
        var card = badge is null ? null : Session.Colleagues?.CardOf(Kept.Name);
        var facts = FactsFor(Kept, _now, badge, card);
        lines.Add(Muted(facts));
        // A COLLEAGUE'S WORKER IS DESCRIBED BY ITS NAME, already the header's first line, and its door by the title.
        if (badge is null && Kept.Description is { Length: > 0 } description) lines.Add(Muted(description));
        if (Session.SubAgents.Ended) lines.Add(Muted("its session has ended — this agent was stopped with it"));

        // A VIEW, AND IT SAYS SO (decision 29): a tab strip mixes these with session and shell tabs, whose closing
        // ends what is in them; this one's closing ends nothing, and how the agent is stopped is named beside it.
        else if (_now is { Running: true, Foreground: true })
            lines.Add(Muted("Ctrl+B or Background moves it to the background · Stop ends it, and the parent goes on"));
        else if (_now is { Running: true, Foreground: false })
            lines.Add(Muted("keeps running when you close this tab · Stop, or x in Tasks (F7), stops it"));
        // A COLLEAGUE BETWEEN TASKS IS NOT A FINISHED TASK: x has nothing to delete on its card.
        else if (badge is not null && _now is not { Running: true })
            lines.Add(Muted(Session.SubAgents.KeepsConversation(Kept.Name)
                ? "closing this tab keeps the conversation · talk with it below, or reopen it from its card in Tasks (F7)"
                : "closing this tab keeps it here · ask it below, or from its card in Tasks (F7)"));
        else if (_now is { Running: false })
            lines.Add(Muted("closing this tab keeps it · x in Tasks (F7) deletes it"));
        lines.Add("");
        return lines;
    }

    /// <summary>
    /// The header's facts line. A worker's: its type and full name:model — the tab is where a child is looked at
    /// closely — then how it stands. A colleague's: "colleague · handle · model", NO TYPE (a colleague is what it has
    /// learned, not the worker it was kept from), and its own state words — idle between tasks, never done.
    /// </summary>
    /// <param name="kept">The agent.</param>
    /// <param name="now">How it stands, or null for one never given work.</param>
    /// <param name="badge">What it is, or null for an ordinary worker.</param>
    /// <param name="card">A colleague's card — whether it is learning or frozen — or null for a worker, or one not known.</param>
    public static string FactsFor(StoredAgent kept, AgentRowView? now, WorkerBadge? badge, CxAgent.Core.Colleagues.ColleagueCard? card)
    {
        var model = kept.Agent.ModelId is { Length: > 0 } runsOn ? runsOn : null;
        string facts;
        if (badge is null) facts = model is null ? kept.TypeName : $"{kept.TypeName} · {model}";
        else facts = $"{badge.Kind.ToLowerInvariant()} · {kept.Name}" + (model is null ? "" : $" · {model}");
        if (kept.Agent.Root is { } folder) facts += $" · in {folder}";
        if (badge is not null)
        {
            // THE STATE LINE'S WORD, in lower case — one reading, so the two lines of one tab never disagree.
            facts += $" · {StateWord(now, badge, card, ended: false).ToLowerInvariant()}";
            if (now is { Running: true } running) facts += $" · {JobsPanel.Clock(running.Elapsed)}";
        }
        else if (now is { } row) facts += $" · {AgentRows.Word(row.State)} · {JobsPanel.Clock(row.Elapsed)}";
        if (now is { Foreground: true, Running: true }) facts += " · the parent is waiting on it";
        return facts;
    }

    /// <summary>
    /// The line under the field: the agent's type and model, as the session's mode line names its own, then what Enter
    /// does now.
    /// </summary>
    private string Hint() =>
        // A COLLEAGUE'S NAME IN PLACE OF A TYPE, as its header has it.
        Muted($" {(Session.SubAgents.BadgeOf(Kept.Name)?.Name ?? Kept.Agent.TypeName)} · {Kept.Agent.ModelId} · " + (
            _now is { Foreground: true, Running: true } ? "read-only — the parent is waiting on this agent's answer"
            : Session.SubAgents.Ended ? "its session has ended"
            : _now is { Running: true } ? "Enter tells it — it reads this on its next turn"
            // A COLLEAGUE'S TWO TABS SAY WHAT TYPING DOES: the conversation goes on, or an ask starts from what it knows.
            : Session.SubAgents.BadgeOf(Kept.Name) is null ? "Enter asks it — its answer appears here"
            : Session.SubAgents.KeepsConversation(Kept.Name) ? "Enter talks with it — the conversation goes on"
            : "Enter asks it — fresh from what it has learned"));

    private static string Muted(string text) => $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(text)}[/]";
}
