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
    private readonly ButtonControl _stop;
    private readonly ButtonControl _move;
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
                // spec decision 11); a background one through /tasks stop, as before.
                var foreground = CanMove;
                _ = Task.Run(() =>
                {
                    if (foreground) Session.StopForeground(Kept.Name);
                    else Session.StopTask($"stop {Kept.Name}");
                });

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
        var bar = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        bar.AddItem(_move);
        bar.AddItem(_stop);

        // THE SESSION'S COMPOSER, BUILT BY THE SAME CODE, with this agent's line in the mode line's place.
        Composer = MainWindow.BuildComposer(_input, _hint).Composer;

        // ON ENTER, NOT ON EVERY KEYSTROKE — the question prompt's lesson.
        _input.Entered += (_, text) => _ = Submit(text);

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
    /// Sends what was typed, clears the composer, and shows the tab whatever the send says for itself.
    ///
    /// <para>THE ANSWER IS NOT WRITTEN HERE: the tab is attached to the child, and its answer arrives as it is
    /// written, with the user's own turn above it. A refusal or a delivery note goes through the tab's own transcript
    /// sink, which knows how to reach the UI thread.</para>
    /// </summary>
    public async Task Submit(string text)
    {
        if (!CanType || string.IsNullOrWhiteSpace(text)) return;
        _input.Input = "";
        if (await Session.SendFromAgentTab(Kept.Name, text) is { } line)
            _views.Transcript.Said(new Message(line, Severity.Info));
    }

    /// <summary>Whether Stop is offered: running, in the background, in a session still open.</summary>
    public bool CanStop => _now is { Running: true } && !Session.SubAgents.Ended;

    /// <summary>Whether Background is offered: the parent's turn is waiting on this agent, in a session still open.</summary>
    public bool CanMove => _now is { Running: true, Foreground: true } && !Session.SubAgents.Ended;

    /// <summary>Test seam: the Background button.</summary>
    public ButtonControl MoveForTest => _move;

    /// <summary>The tab title: the state first, as a job tab's.</summary>
    public string Title()
    {
        var glyph = _now?.State switch
        {
            AgentRowState.Queued => "○",
            AgentRowState.Running or AgentRowState.WaitingForPermission => "●",
            AgentRowState.Done => "✓",
            null => "·",
            _ => "✗",
        };
        var name = Kept.Name;
        return $"{glyph} {(name.Length <= 24 ? name : name[..23] + "…")}";
    }

    /// <summary>One clock tick: the state, the header, what the composer row offers, and the live rows' clocks.</summary>
    public void Tick(DateTimeOffset now)
    {
        _now = AgentRows.Describe(Session.SubAgents, Kept, now);
        var stopHadFocus = _stop.HasFocus;
        if (_stop.Visible != CanStop) _stop.Visible = CanStop;
        var moveHadFocus = _move.HasFocus;
        if (_move.Visible != CanMove) _move.Visible = CanMove;
        if (moveHadFocus && !_move.Visible)
            _move.GetParentWindow()?.FocusManager.SetFocus(MainControl, FocusReason.Programmatic);
        if (_input.Visible != CanType) _input.Visible = CanType;

        // THE KEYBOARD LEAVES A STOP THAT HAS GONE. Clicked, Stop keeps the focus; hidden, it kept it still, so what
        // was typed next went nowhere and every space pressed the hidden button again.
        if (stopHadFocus && !_stop.Visible)
            _stop.GetParentWindow()?.FocusManager.SetFocus(MainControl, FocusReason.Programmatic);

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
    public void Recolour() => (_views.Transcript as ChatTranscriptSink)?.Recolour();

    public void Dispose() => _attached.Dispose();

    private List<string> HeaderLines()
    {
        var lines = new List<string> { $"[bold]{MarkupParser.Escape(Kept.Name)}[/]" };
        var facts = Kept.TypeName;
        if (_now is { } row)
        {
            facts += $" · {AgentRows.Word(row.State)} · {JobsPanel.Clock(row.Elapsed)}";
            if (row.Foreground && row.Running) facts += " · the parent is waiting on it";
        }
        lines.Add(Muted(facts));
        if (Kept.Description is { Length: > 0 } description) lines.Add(Muted(description));
        if (Session.SubAgents.Ended) lines.Add(Muted("its session has ended — this agent was stopped with it"));

        // A VIEW, AND IT SAYS SO (decision 29): a tab strip mixes these with session and shell tabs, whose closing
        // ends what is in them; this one's closing ends nothing, and how the agent is stopped is named beside it.
        else if (_now is { Running: true, Foreground: true })
            lines.Add(Muted("Ctrl+B or Background moves it to the background · Stop ends it, and the parent goes on"));
        else if (_now is { Running: true, Foreground: false })
            lines.Add(Muted("keeps running when you close this tab · Stop, or x in Tasks (F7), stops it"));
        else if (_now is { Running: false })
            lines.Add(Muted("closing this tab keeps it · x in Tasks (F7) deletes it"));
        lines.Add("");
        return lines;
    }

    /// <summary>
    /// The line under the field: the agent's type and model, as the session's mode line names its own, then what Enter
    /// does now.
    /// </summary>
    private string Hint() =>
        Muted($" {Kept.Agent.TypeName} · {Kept.Agent.ModelId} · " + (
            _now is { Foreground: true, Running: true } ? "read-only — the parent is waiting on this agent's answer"
            : Session.SubAgents.Ended ? "its session has ended"
            : _now is { Running: true } ? "Enter tells it — it reads this on its next turn"
            : "Enter asks it — its answer appears here"));

    private static string Muted(string text) => $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(text)}[/]";
}
