using System.Collections.Concurrent;
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
    public void UserTurnAdded(ChatMessageId id, string text) =>
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

    /// <summary>Run on the UI thread just before a user turn is written — see
    /// <see cref="UserTurnAdded"/>. The composition root uses it to take down the queued block.</summary>
    public Action? BeforeUserTurn { get; set; }

    public void AssistantTurnBegan(ChatMessageId id) =>
        _system.EnqueueOnUIThread(() =>
            _map[id.Value] = _chat.AddMessage(ChatRole.Assistant, "", thinking: true));

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

    public void Failed(string message) =>
        _system.EnqueueOnUIThread(() =>
            _chat.AddMessage(ChatRole.System, $"[red]✗ {message}[/]"));

    // THE SAME ROW STYLE AS EVERY OTHER SYSTEM LINE, unstyled beyond that: the session sent markup
    // and chose its own emphasis, so wrapping it in a colour here would override a decision already
    // made one layer down.
    public void Said(string message) =>
        _system.EnqueueOnUIThread(() => _chat.AddMessage(ChatRole.System, message));

}
