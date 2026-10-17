using System.Collections.Concurrent;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

// Disambiguate: FwChatId = the framework's ChatMessageId (returned by ChatTranscriptControl.AddMessage),
// which shares the name with P5a's CxAgent.UI.ChatMessageId (our seam id). The _map stores FwChatId
// values keyed by our long id so we can forward Append calls to the right transcript slot.
using FwChatId = SharpConsoleUI.Controls.ChatMessageId;

using CxAgent.Core.Sessions;
// Ours, not SharpConsoleUI's — both exist and this file sees both namespaces.
using ChatMessageId = CxAgent.Core.Sessions.ChatMessageId;

namespace CxAgent.UI;

/// <summary>
/// The real ISessionObserver: marshals every update onto the UI thread via EnqueueOnUIThread and maps the
/// P5a ChatMessageId to the framework's ChatTranscriptControl message ids. AgentHost (which may run
/// on a background thread) calls these; nothing here touches a control off the UI thread.
/// </summary>
public sealed class ChatTranscriptSink : ISessionObserver
{
    private readonly Action<Action, string> _post;
    private readonly ChatTranscriptControl _chat;
    private readonly ConcurrentDictionary<long, FwChatId> _map = new();

    /// <summary>Each step's thinking block, by step id — kept after the step so a second run of
    /// thinking in it rejoins its block.</summary>
    private readonly ConcurrentDictionary<long, Block> _blocks = new();

    /// <summary>Every block in creation order — the last is Ctrl+O's. UI thread only.</summary>
    private readonly List<FwChatId> _allBlocks = [];

    /// <summary>A label given to a step before its answer message existed, for when it does.</summary>
    private readonly ConcurrentDictionary<long, string> _labels = new();

    public ChatTranscriptSink(ConsoleWindowSystem system, ChatTranscriptControl chat)
        : this(chat, (action, name) => system.EnqueueOnUIThread(action, name)) { }

    private ChatTranscriptSink(ChatTranscriptControl chat, Action<Action, string> post)
    {
        _chat = chat;
        _post = post;
    }

    /// <summary>
    /// Runs each write inline. Public with a ForTest suffix because this assembly grants no
    /// InternalsVisibleTo, and a headless test cannot drain the UI queue a real sink writes through.
    /// </summary>
    public static ChatTranscriptSink ForTest(ChatTranscriptControl chat) =>
        new(chat, static (action, _) => action());

    /// <summary>
    /// Adds what the user said, and marks it with a rail down the gutter.
    ///
    /// <para>THE TRANSCRIPT IS MOSTLY NOT THEM. An assistant reply, a dozen tool rows, a worker's
    /// report — finding "where did I ask for that" means re-reading the conversation, because the
    /// only signal is a surface colour that a running turn's own output competes with. The rail is a
    /// different channel: one column in the gutter, scannable without reading anything.</para>
    ///
    /// <para>EXPLICIT, BECAUSE THE DEFAULT IS FOOTER PRESENCE. A message wants a rail when it has
    /// actions or a status row (ChatTranscriptControl's <c>RailOverride ?? HasFooter</c>), which is a
    /// good default and not this one — a user's message has no footer and should still be marked, and
    /// the messages that do have footers are not the user's.</para>
    /// </summary>
    public void UserTurnAdded(ChatMessageId id, string text)
    {
        OnUserTurnAdded?.Invoke();

        _post(() =>
        {
            // THE PLACEHOLDER GOES FIRST. A user turn arriving mid-run is a steer the agent has just
            // taken, and its "queued" block is a stand-in for exactly this message — leaving it up
            // would show the same text twice, the second looking like a duplicate send. Removing
            // before the add means the two never coexist for a frame. Null, and a no-op, for a host
            // that has no such block.
            BeforeUserTurn?.Invoke();

            var added = _chat.AddMessage(ChatRole.User, text);
            _chat.SetMessageRail(added, true);
            _map[id.Value] = added;
        }, "chat.UserTurnAdded");
    }

    /// <summary>Run on the UI thread just before a user turn is written — see
    /// <see cref="UserTurnAdded"/>. The composition root uses it to take down the queued block.</summary>
    public Action? BeforeUserTurn { get; set; }

    /// <summary>
    /// Run when a user turn opens and when a model round ends, so a consumer can scope work to them.
    ///
    /// <para>THE USER'S TURN, NOT THE ASSISTANT'S, for the opening. AssistantTurnBegan/Ended bracket
    /// one MODEL ROUND, and a round returning a tool call closes before the call runs — scoping to
    /// those alone would put every call outside every scope.</para>
    ///
    /// <para>CALLBACKS RATHER THAN A REFERENCE TO THE OTHER SINK: a re-wire (/model, resume) replaces
    /// one without the other knowing, and a field pointing at the current job sink would be a second
    /// thing to keep in step.</para>
    ///
    /// <para>Both are invoked WITHOUT marshalling; the consumer decides what needs the UI thread.
    /// Null, and a no-op, for a host with no job sink at all.</para>
    /// </summary>
    public Action? OnUserTurnAdded { get; set; }

    /// <inheritdoc cref="OnUserTurnAdded"/>
    public Action? OnAssistantRoundEnded { get; set; }

    public void AssistantTurnBegan(ChatMessageId id)
    {
        // THE PREVIOUS ROUND'S ROW SETTLES BEFORE THIS ROUND'S MESSAGE EXISTS. A transcript row is
        // appended at the end, so a row drawn while a round's assistant message is already on screen
        // lands BELOW it — and the prose that round wrote then sits above working that happened
        // after it. Closing at the start of the next round gives working, answer, working, answer.
        OnAssistantRoundEnded?.Invoke();

        _post(() =>
            _map[id.Value] = _chat.AddMessage(ChatRole.Assistant, "", thinking: true),
            "chat.AssistantTurnBegan");
    }

    /// <summary>
    /// The step's header. KEPT as well as set: a step whose spinner gave way to a thinking block has
    /// no answer message yet, and the label belongs on the answer when it arrives.
    /// </summary>
    public void AssistantLabelled(ChatMessageId id, string header) =>
        _post(() =>
        {
            _labels[id.Value] = header;
            if (_map.TryGetValue(id.Value, out var fwId)) _chat.SetHeader(fwId, header);
        }, "chat.AssistantLabelled");

    /// <summary>What a step's block shows, as plain text, for a test.</summary>
    public string? BlockBodyForTest(ChatMessageId id) =>
        _blocks.TryGetValue(id.Value, out var b) ? b.Body.ToString() : null;

    /// <summary>A step's folded header, for a test.</summary>
    public string? BlockHeaderForTest(ChatMessageId id) =>
        _blocks.TryGetValue(id.Value, out var b) ? b.Header : null;

    /// <summary>How many blocks are still receiving thinking, for a test.</summary>
    public int LiveBlocksForTest => _blocks.Values.Count(b => b.Live);

    /// <summary>The label a step's answer carries, for a test.</summary>
    public string? HeaderForTest(ChatMessageId id) => _labels.TryGetValue(id.Value, out var h) ? h : null;

    /// <summary>
    /// Closes an assistant turn, and REMOVES it entirely when it produced no text.
    ///
    /// <para>Clearing the body is not enough: a turn that never received content would leave an empty
    /// "Assistant" block in the transcript, and the orchestrator opens one per consult round to show
    /// its spinner — blank headers stacked between real content.</para>
    ///
    /// <para>A turn that DID produce text is left alone; only the empty ones vanish. A step whose
    /// spinner gave way to its thinking block has nothing here to remove.</para>
    /// </summary>
    public void AssistantTurnEnded(ChatMessageId id) =>
        _post(() =>
        {
            // A BLOCK STILL LIVE AT THE TURN'S END was cut off — a stop, a dropped stream — before
            // its run could end. Folded with how long it ran, so it does not read "Thinking" and tick
            // for the life of the window.
            if (_blocks.TryGetValue(id.Value, out var block) && block.Live)
            {
                block.Live = false;
                block.Header = FoldedHeader(
                    new Core.Llm.ThinkingSummary(DateTimeOffset.UtcNow - block.Started, 0, Hidden: true, Redacted: false));
                _chat.SetHeader(block.Id, $"[{ColorScheme.ThinkingMarkup}]{Escape(block.Header)}[/]");
                _chat.SetExpanded(block.Id, false);
            }

            if (!_map.TryGetValue(id.Value, out var fwId)) return;

            // IsThinking is still true exactly when no body content ever arrived — the control clears
            // the flag on first content (see ISessionObserver's note).
            if (_chat.IsThinking(fwId))
            {
                _chat.RemoveMessage(fwId);
                _map.TryRemove(id.Value, out _);
            }
        }, "chat.AssistantTurnEnded");

    /// <summary>
    /// Body text, NOT escaped — the Assistant role renders as MARKDOWN, which escapes for itself.
    ///
    /// <para>FOUND ON A LIVE DRIVE, in the model's own words. It wrote <c>`[abc]`</c> in backticks and
    /// the screen showed <c>[[abc]</c>; it wrote <c>`segments[0].Length`</c> and the screen showed
    /// <c>segments[[0].Length</c>. INSIDE AN INLINE-CODE SPAN the markdown converter treats content
    /// as literal — it neither re-escapes nor unescapes it — so a bracket this sink had already
    /// doubled passed straight through to the screen. Outside code spans the doubling is invisible,
    /// which is exactly why it survived: every existing test here used plain text.</para>
    ///
    /// <para>THE ASYMMETRY WITH <see cref="AssistantThinkingAppended"/> IS CORRECT, and is the thing to
    /// understand before "fixing" it back. Reasoning is wrapped in a colour scope BY THIS SINK, which
    /// makes it markup, and markup must be escaped or a model writing "[red]" opens a style scope.
    /// Body text goes to a role whose <c>Markdown = true</c> (MainWindow.cs:330-335) routes it through
    /// a converter that already handles brackets. Two paths, two rules — escaping both identically is
    /// what produced the doubling, and escaping neither would swallow tags in the reasoning stream.
    /// </para>
    /// </summary>
    public void AssistantTextAppended(ChatMessageId id, string token) =>
        _post(() =>
        {
            if (_map.TryGetValue(id.Value, out var fwId)) { _chat.Append(fwId, token); return; }

            // THE ANSWER, BELOW ITS THINKING. The step's spinner gave way to the thinking block, so
            // the answer message is made now — after the block, which is the order they happened in.
            if (!_blocks.ContainsKey(id.Value)) return;
            var answer = _chat.AddMessage(ChatRole.Assistant, "");
            if (_labels.TryGetValue(id.Value, out var label)) _chat.SetHeader(answer, label);
            _map[id.Value] = answer;
            _chat.Append(answer, token);
        }, "chat.AssistantTextAppended");

    /// <summary>
    /// Thinking text, in the step's own block: escaped, then coloured HERE — the sink owns how a kind
    /// of text looks.
    ///
    /// <para>AMBER, not [dim]. Dim asks the terminal to render the SAME colour more faintly, which
    /// many terminals ignore and none render identically; against a dark background the ones that
    /// honour it produce grey mush. A colour of its own says "this is a different KIND of text"
    /// rather than "this text matters less", which is what thinking is.</para>
    /// </summary>
    public void AssistantThinkingAppended(ChatMessageId id, string text) =>
        _post(() =>
        {
            var block = BlockFor(id);

            // NO BLANK LINES OF ITS OWN at either end. Models open and close their thinking with
            // newlines, and each one renders as an empty row inside the block — on top of the gap
            // every message already has. Whitespace is held back until text follows it, so what
            // ends a run is never written, and what starts a block is skipped.
            var shown = block.Body.Length == 0 ? text.TrimStart() : text;
            var core = shown.TrimEnd();
            if (core.Length == 0)
            {
                if (block.Body.Length > 0) block.Pending += shown;
                return;
            }
            var write = block.Pending + core;
            block.Pending = shown[core.Length..];
            block.Body.Append(write);
            _chat.Append(block.Id, ThinkingMarkup(write));
        }, "chat.AssistantThinkingAppended");

    /// <summary>
    /// Thinking text as markup, coloured LINE BY LINE: the transcript does not carry a colour scope
    /// across a line break, so one scope around "a\nb" leaves "b" in the default colour.
    /// </summary>
    public static string ThinkingMarkup(string text) =>
        string.Join("\n", text.Split('\n').Select(line =>
            line.Length == 0 ? "" : $"[{ColorScheme.ThinkingMarkup}]{Escape(line)}[/]"));

    /// <summary>
    /// The run is over: the block's header says for how long and how much, and the block folds so
    /// the answer below it is what the eye lands on.
    /// </summary>
    public void AssistantThinkingEnded(ChatMessageId id, Core.Llm.ThinkingSummary summary) =>
        _post(() =>
        {
            var block = BlockFor(id);
            block.Live = false;

            // THE BLOCK'S TOTAL, not this run's: a step whose thinking resumed after its text holds
            // both runs in one body, and a header counting only the last would under-report it.
            block.Total = block.Total is { } before
                ? new Core.Llm.ThinkingSummary(before.Duration + summary.Duration, before.Words + summary.Words,
                    Hidden: before.Words + summary.Words == 0, before.Redacted || summary.Redacted)
                : summary;
            block.Header = FoldedHeader(block.Total);
            _chat.SetHeader(block.Id, $"[{ColorScheme.ThinkingMarkup}]{Escape(block.Header)}[/]");
            _chat.SetExpanded(block.Id, false);
        }, "chat.AssistantThinkingEnded");

    /// <summary>
    /// The step's block, made on its first thinking. UI thread only.
    ///
    /// <para>IN THE SPINNER'S PLACE. The control only appends, and the step's message was made empty
    /// before the request so the wait showed a spinner. Removing it and adding the block puts the
    /// thinking first; the answer is made below it when text arrives. A run that resumes after the
    /// step's text rejoins this same block — one block per step, as the step happened.</para>
    /// </summary>
    private Block BlockFor(ChatMessageId id)
    {
        if (_blocks.TryGetValue(id.Value, out var known))
        {
            if (!known.Live)
            {
                known.Live = true;
                known.Started = DateTimeOffset.UtcNow;
                // A SECOND RUN STARTS ON ITS OWN PARAGRAPH, not run on from the first's last word.
                if (known.Body.Length > 0) known.Pending = "\n\n";
                // LEFT AS THE USER HAS IT. Thinking resuming is not a request to read it, and a block that
                // sprang open again mid-answer would push the answer down under text nobody asked for.
            }
            return known;
        }

        if (_map.TryGetValue(id.Value, out var spinner) && _chat.IsThinking(spinner))
        {
            _chat.RemoveMessage(spinner);
            _map.TryRemove(id.Value, out _);
        }

        // THE THINKING ROLE, whose style — rail, ground, plain markup body — is set once in MainWindow.
        var added = _chat.AddMessage(ChatRole.Thinking, "");
        _chat.SetHeader(added, $"[{ColorScheme.ThinkingMarkup}]{LiveHeader(TimeSpan.Zero)}[/]");
        // FOLDED FROM THE START. The ticking header already says thinking is under way; the text is for
        // whoever wants it — Ctrl+O, a click, or Tab and Enter — not a stream every reader scrolls past.
        _chat.SetExpanded(added, false);
        _allBlocks.Add(added);
        var block = new Block(added) { Started = DateTimeOffset.UtcNow };
        _blocks[id.Value] = block;
        return block;
    }

    /// <summary>A step's thinking block, and whether thinking is arriving in it now.</summary>
    private sealed class Block(FwChatId id)
    {
        public FwChatId Id { get; } = id;
        public DateTimeOffset Started { get; set; }
        public bool Live { get; set; } = true;

        /// <summary>Whitespace held back until more text follows it — see AssistantThinkingAppended.</summary>
        public string Pending { get; set; } = "";

        /// <summary>What the block shows, as plain text — the control cannot read a body back.</summary>
        public System.Text.StringBuilder Body { get; } = new();

        /// <summary>Every finished run in this block, added together.</summary>
        public Core.Llm.ThinkingSummary? Total { get; set; }

        /// <summary>The folded header as last written, for a test — the control cannot read it back.</summary>
        public string? Header { get; set; }
    }

    /// <summary>The header while thinking arrives.</summary>
    public static string LiveHeader(TimeSpan elapsed) => $"◆ Thinking · {(int)elapsed.TotalSeconds}s";

    /// <summary>
    /// The header once the run is over. Never "0s": a run that finished inside a second still
    /// happened. No word count when the provider hid the text — zero would read as "said nothing".
    /// </summary>
    public static string FoldedHeader(Core.Llm.ThinkingSummary s) =>
        $"◆ Thought for {Math.Max(1, (int)Math.Round(s.Duration.TotalSeconds))}s"
        + (s.Words > 0 ? $" · {s.Words} words" : "")
        + (s.Redacted ? " (redacted)" : "");

    /// <summary>
    /// Ctrl+O: opens or folds the most recent thinking block — the one just folded, which is the one
    /// a reader wants back. Any other block opens with a click, or Tab to it and Enter.
    /// </summary>
    public void ToggleLatestThinking() => _post(() =>
    {
        if (_allBlocks.Count == 0) return;
        var latest = _allBlocks[^1];
        _chat.SetExpanded(latest, !_chat.IsExpanded(latest));
    }, "chat.ToggleLatestThinking");

    /// <summary>
    /// Advances each live block's elapsed time. Driven by the window's one-second tick, because a
    /// header cannot tick itself.
    /// </summary>
    public void RefreshThinkingHeaders() => _post(() =>
    {
        foreach (var b in _blocks.Values.Where(b => b.Live))
            _chat.SetHeader(b.Id, $"[{ColorScheme.ThinkingMarkup}]{LiveHeader(DateTimeOffset.UtcNow - b.Started)}[/]");
    }, "chat.RefreshThinkingHeaders");

    /// <summary>
    /// Rewrites every block's text and header in the current theme's colour. Each was written as
    /// markup carrying the colour of its day, which a role restyle cannot reach.
    /// </summary>
    /// <param name="observed">Told each block's new body and header — for a test.</param>
    public void Recolour(Action<string, string>? observed = null) => _post(() =>
    {
        foreach (var block in _blocks.Values)
        {
            var body = ThinkingMarkup(block.Body.ToString());
            var header = block.Live
                ? $"[{ColorScheme.ThinkingMarkup}]{LiveHeader(DateTimeOffset.UtcNow - block.Started)}[/]"
                : $"[{ColorScheme.ThinkingMarkup}]{Escape(block.Header ?? "")}[/]";
            _chat.UpdateMessage(block.Id, body);
            _chat.SetHeader(block.Id, header);
            observed?.Invoke(body, header);
        }
    }, "chat.Recolour");

    /// <summary>The transcript was cleared: its blocks are gone, so stop pointing at them.</summary>
    public void Forget() => _post(() =>
    {
        _blocks.Clear();
        _allBlocks.Clear();
        _labels.Clear();
    }, "chat.Forget");

    /// <summary>
    /// Makes model text safe to hand a markup parser.
    ///
    /// <para>Doubling '[' is the parser's own escape. It lives in the SINK because markup is the
    /// sink's domain — the agent emits semantics and has no reason to know a tag syntax exists.</para>
    /// </summary>
    /// <remarks>Public so it is testable without a live ConsoleWindowSystem — the same seam
    /// <c>SettingsDialog.ProviderRowLabels</c> uses. The grant in AssemblyWiring covers Core's own
    /// assemble members rather than the UI,
    /// and an escape nobody can test is how the missing one survived.</remarks>
    public static string Escape(string text) => text.Replace("[", "[[");

    /// <summary>
    /// One transcript row: what to write, and which dialect it is written in.
    /// </summary>
    /// <param name="Text">The body, already wrapped in a colour scope when the severity warrants one.</param>
    /// <param name="Markdown">
    /// The per-message rendering override — <c>false</c> for markup, <c>null</c> to inherit the role.
    /// </param>
    public readonly record struct SystemRow(string Text, bool? Markdown);

    /// <summary>
    /// Chooses a row's text and its rendering dialect from the message's severity.
    ///
    /// <para>THE FRONT END COLOURS BY MEANING, which it could not do while the colour was baked into
    /// the text: Core chose the same red under every theme. These resolve through the theme.</para>
    ///
    /// <para>AND THE COLOUR IS WHY THE DIALECT IS PER MESSAGE. A colour scope is SharpConsoleUI
    /// markup, and the System role renders markdown — handing a "[red]" to the markdown converter
    /// puts those five characters on screen. An Info row carries Core's text untouched, so it defers
    /// to the role and its tables and headings render; the two coloured branches say "markup" for
    /// themselves alone. Neither has to be given up for the other.</para>
    /// </summary>
    /// <param name="message">What Core said, and how loudly.</param>
    /// <returns>The row to add.</returns>
    /// <remarks>Public and pure so the choice is testable without a live ConsoleWindowSystem — the
    /// same seam <see cref="Escape"/> uses.</remarks>
    public static SystemRow Row(Message message) => message.Severity switch
    {
        Severity.Error => new($"[{ColorScheme.DangerMarkup}]✗ {message.Text}[/]", false),
        Severity.Warning => new($"[{ColorScheme.CautionMarkup}]{message.Text}[/]", false),
        _ => new(message.Text, null),
    };

    /// <summary>
    /// The stand-in for text typed into a running turn: a dim label, then the text as typed.
    /// MARKUP, said here, for the reason <see cref="Row"/> gives — the System role renders markdown,
    /// which put the label's own "[dim]" on screen.
    /// </summary>
    public static SystemRow QueuedRow(string body) => new($"[dim]queued[/] {Escape(body)}", false);

    /// <summary>The stand-in for a command waiting for the running turn to end. Markup, as above.</summary>
    public static SystemRow HeldRow(string line) =>
        new($"[dim]held[/] {Escape(line)} [dim]— runs when this turn ends[/]", false);

    /// <summary>
    /// Posts a system row in the dialect it asks for.
    /// </summary>
    /// <remarks>
    /// THE OVERLOAD THAT TAKES A MODE TAKES FIVE ARGUMENTS, three of which are only ever null here.
    /// Spelling that out at each call site puts the one argument that MATTERS last, behind a row of
    /// nulls that all look alike — and a transposed null is a silent behaviour change rather than a
    /// build error. One place says it; everywhere else names the row.
    /// </remarks>
    /// <param name="chat">The transcript to add to.</param>
    /// <param name="row">The text and its rendering mode.</param>
    /// <returns>The id of the added message.</returns>
    public static FwChatId Post(ChatTranscriptControl chat, SystemRow row) =>
        chat.AddMessage(ChatRole.System, row.Text, null, null, null, row.Markdown);

    public void Said(Message message) =>
        _post(() => Post(_chat, Row(message)), "chat.Said");
}
