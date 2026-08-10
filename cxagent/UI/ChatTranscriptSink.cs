using System.Collections.Concurrent;
using CxAgent.Core.Models;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

// Disambiguate: FwChatId = the framework's ChatMessageId (returned by ChatTranscriptControl.AddMessage),
// which shares the name with P5a's CxAgent.UI.ChatMessageId (our seam id). The _map stores FwChatId
// values keyed by our long id so we can forward Append calls to the right transcript slot.
using FwChatId = SharpConsoleUI.Controls.ChatMessageId;

using CxAgent.Core.Agent;
// Ours, not SharpConsoleUI's — both exist and this file sees both namespaces.
using ChatMessageId = CxAgent.Core.Agent.ChatMessageId;

namespace CxAgent.UI;

/// <summary>
/// The real IChatSink: marshals every update onto the UI thread via EnqueueOnUIThread and maps the
/// P5a ChatMessageId to the framework's ChatTranscriptControl message ids. AgentHost (which may run
/// on a background thread) calls these; nothing here touches a control off the UI thread.
/// </summary>
public sealed class ChatTranscriptSink : IChatSink
{
    private readonly ConsoleWindowSystem _system;
    private readonly ChatTranscriptControl _chat;
    private readonly ConcurrentDictionary<long, FwChatId> _map = new();
    private long _nextId;

    public ChatTranscriptSink(ConsoleWindowSystem system, ChatTranscriptControl chat)
    {
        _system = system;
        _chat = chat;
    }

    public ChatMessageId AddUserTurn(string text)
    {
        var id = new ChatMessageId(Interlocked.Increment(ref _nextId));
        _system.EnqueueOnUIThread(() =>
            _map[id.Value] = _chat.AddMessage(ChatRole.User, text));
        return id;
    }

    public ChatMessageId BeginAssistantTurn()
    {
        var id = new ChatMessageId(Interlocked.Increment(ref _nextId));
        _system.EnqueueOnUIThread(() =>
            _map[id.Value] = _chat.AddMessage(ChatRole.Assistant, "", thinking: true));
        return id;
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
    public void SetAssistantHeader(ChatMessageId id, string header) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (_map.TryGetValue(id.Value, out var fwId)) _chat.SetHeader(fwId, header);
        });

    public void EndAssistantTurn(ChatMessageId id) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (!_map.TryGetValue(id.Value, out var fwId)) return;

            // IsThinking is still true exactly when no body content ever arrived — the control clears
            // the flag on first content (see IChatSink's note).
            if (_chat.IsThinking(fwId))
            {
                _chat.RemoveMessage(fwId);
                _map.TryRemove(id.Value, out _);
            }
        });

    /// <summary>
    /// Body text, ESCAPED. The control parses markup, so an unescaped "[red]" in model output is
    /// swallowed and an unclosed tag recolours everything after it — verified: "we[red]ird" renders
    /// as "weird". This escape used to be missing here and present only on the reasoning path, which
    /// is precisely the asymmetry that made the omission invisible.
    /// </summary>
    public void AppendAssistant(ChatMessageId id, string token) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (_map.TryGetValue(id.Value, out var fwId)) _chat.Append(fwId, Escape(token));
        });

    /// <summary>
    /// Reasoning text: escaped, then coloured HERE — the sink owns how a kind of text looks.
    ///
    /// <para>AMBER, not [dim]. Dim asks the terminal to render the SAME colour more faintly, which
    /// many terminals ignore and none render identically; against a dark background the ones that
    /// honour it produce grey mush. A colour of its own says "this is a different KIND of text"
    /// rather than "this text matters less", which is what reasoning actually is.</para>
    /// </summary>
    public void AppendReasoning(ChatMessageId id, string text) =>
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
    /// <c>SettingsDialog.ProviderRowLabels</c> uses. This codebase has no InternalsVisibleTo grant,
    /// and an escape nobody can test is how the missing one survived.</remarks>
    public static string Escape(string text) => text.Replace("[", "[[");

    public void ShowError(string message) =>
        _system.EnqueueOnUIThread(() =>
            _chat.AddMessage(ChatRole.System, $"[red]✗ {message}[/]"));

    public void ShowSystemMessage(string message) =>
        _system.EnqueueOnUIThread(() =>
            _chat.AddMessage(ChatRole.System, message));

}
