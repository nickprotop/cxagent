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
    private readonly ConsoleWindowSystem _system;
    private readonly ChatTranscriptControl _chat;
    private readonly ConcurrentDictionary<long, FwChatId> _map = new();

    public ChatTranscriptSink(ConsoleWindowSystem system, ChatTranscriptControl chat)
    {
        _system = system;
        _chat = chat;
    }

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
        // OUTSIDE THE ENQUEUE, AND THAT IS THE POINT. Tool calls are recorded off the UI thread, so
        // a scope opened inside this hop opens after the turn's first call has already been filed.
        OnUserTurnAdded?.Invoke();

        _system.EnqueueOnUIThread(() =>
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
        });
    }

    /// <summary>Run on the UI thread just before a user turn is written — see
    /// <see cref="UserTurnAdded"/>. The composition root uses it to take down the queued block.</summary>
    public Action? BeforeUserTurn { get; set; }

    /// <summary>
    /// Run when the user says something, before their message is written — so a consumer can scope
    /// work to the turn it starts.
    ///
    /// <para>THE USER'S TURN, NOT THE ASSISTANT'S. AssistantTurnBegan/Ended bracket one MODEL ROUND,
    /// and a round that returns a tool call closes before the call runs — the interface says as much
    /// where it explains a planning turn that returns create_plan and no prose. Scoping to those
    /// would put every tool call outside every scope. A user message is the boundary a reader
    /// perceives, and the rounds nest inside it.</para>
    ///
    /// <para>NOT MARSHALLED, unlike the transcript work below: tool calls are recorded off the UI
    /// thread, so a scope opened inside that hop would open after the first call was already filed.
    /// This callback touches no control.</para>
    ///
    /// <para>Null, and a no-op, for a host with no job sink at all.</para>
    /// </summary>
    public Action? OnUserTurnAdded { get; set; }

    /// <summary>
    /// Run when one model round ends — the point where the working done for that round stops and
    /// whatever the model said about it is on screen.
    ///
    /// <para>ROUNDS, NOT THE WHOLE TURN. A turn is often several: work, say something, work again.
    /// One row per round puts each batch of calls beside the answer it produced, where a single row
    /// for the turn would collect calls made before and after a paragraph the reader has already
    /// passed.</para>
    /// </summary>
    public Action? OnAssistantRoundEnded { get; set; }

    public void AssistantTurnBegan(ChatMessageId id)
    {
        // THE PREVIOUS ROUND'S TOOL ROW SETTLES HERE, BEFORE THIS ROUND'S MESSAGE EXISTS. A
        // transcript row is appended at the end, so a row drawn while a round's assistant message is
        // already on screen lands BELOW it — and the prose that round wrote then sits above working
        // that happened after it. Closing at the start of the next round means each row is complete
        // before anything is added beneath it, and the reader gets working, answer, working, answer.
        //
        // NOT MARSHALLED, unlike the transcript work below: tool calls are recorded off the UI
        // thread, and a boundary that landed a hop later would let the next round's first call join
        // the row that just closed.
        OnAssistantRoundEnded?.Invoke();

        _system.EnqueueOnUIThread(() =>
            _map[id.Value] = _chat.AddMessage(ChatRole.Assistant, "", thinking: true));
    }

    /// <summary>
    /// Stops the turn's spinner. Writing an EMPTY body is what clears Thinking (the control clears it
    /// on body creation, ChatTranscriptControl.cs:579), so a silent turn collapses to nothing visible
    /// rather than animating forever.
    /// </summary>
    /// <summary>
    /// Closes an assistant turn, and REMOVES it entirely when it produced no text.
    ///
    /// <para>Clearing the body was not enough: a turn that never received content left an empty
    /// "Assistant" block in the transcript, and the orchestrator opens one per consult round to show
    /// its thinking spinner. A live screenshot showed four blank Assistant headers stacked between
    /// real content — the spinner did its job while running and then left litter behind.</para>
    ///
    /// <para>A turn that DID produce text is left alone; only the empty ones vanish.</para>
    /// </summary>
    public void AssistantLabelled(ChatMessageId id, string header) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (_map.TryGetValue(id.Value, out var fwId)) _chat.SetHeader(fwId, header);
        });

    public void AssistantTurnEnded(ChatMessageId id) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (!_map.TryGetValue(id.Value, out var fwId)) return;

            // IsThinking is still true exactly when no body content ever arrived — the control clears
            // the flag on first content (see ISessionObserver's note).
            if (_chat.IsThinking(fwId))
            {
                _chat.RemoveMessage(fwId);
                _map.TryRemove(id.Value, out _);
            }
        });

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
    /// <para>THE ASYMMETRY WITH <see cref="AssistantReasoningAppended"/> IS CORRECT, and is the thing to
    /// understand before "fixing" it back. Reasoning is wrapped in a colour scope BY THIS SINK, which
    /// makes it markup, and markup must be escaped or a model writing "[red]" opens a style scope.
    /// Body text goes to a role whose <c>Markdown = true</c> (MainWindow.cs:330-335) routes it through
    /// a converter that already handles brackets. Two paths, two rules — escaping both identically is
    /// what produced the doubling, and escaping neither would swallow tags in the reasoning stream.
    /// </para>
    /// </summary>
    public void AssistantTextAppended(ChatMessageId id, string token) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (_map.TryGetValue(id.Value, out var fwId)) _chat.Append(fwId, token);
        });

    /// <summary>
    /// Reasoning text: escaped, then coloured HERE — the sink owns how a kind of text looks.
    ///
    /// <para>AMBER, not [dim]. Dim asks the terminal to render the SAME colour more faintly, which
    /// many terminals ignore and none render identically; against a dark background the ones that
    /// honour it produce grey mush. A colour of its own says "this is a different KIND of text"
    /// rather than "this text matters less", which is what reasoning actually is.</para>
    /// </summary>
    public void AssistantReasoningAppended(ChatMessageId id, string text) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (_map.TryGetValue(id.Value, out var fwId))
                _chat.Append(fwId, $"[{ColorScheme.ThinkingMarkup}]{Escape(text)}[/]");
        });

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
        _system.EnqueueOnUIThread(() => Post(_chat, Row(message)));
}
