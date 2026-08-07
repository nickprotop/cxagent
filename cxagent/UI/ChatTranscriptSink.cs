using System.Collections.Concurrent;
using CxAgent.Core.Models;
using SharpConsoleUI;
using SharpConsoleUI.Controls;

// Disambiguate: FwChatId = the framework's ChatMessageId (returned by ChatTranscriptControl.AddMessage),
// which shares the name with P5a's CxAgent.UI.ChatMessageId (our seam id). The _map stores FwChatId
// values keyed by our long id so we can forward Append calls to the right transcript slot.
using FwChatId = SharpConsoleUI.Controls.ChatMessageId;

namespace CxAgent.UI;

/// <summary>
/// The real IChatSink: marshals every update onto the UI thread via EnqueueOnUIThread and maps the
/// P5a ChatMessageId to the framework's ChatTranscriptControl message ids. GoalRunner (which may run
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

    public void AppendAssistant(ChatMessageId id, string token) =>
        _system.EnqueueOnUIThread(() =>
        {
            if (_map.TryGetValue(id.Value, out var fwId)) _chat.Append(fwId, token);
        });

    /// <summary>
    /// The goal's outcome — expanded on arrival, unlike every other System message.
    ///
    /// <para>System is StartCollapsed by role (MainWindow's style), which is right for the noisy
    /// ones: startup notes and diagnostics stay out of the way behind an "expand…". But the same
    /// default hid the one line the user most needs, so a finished goal looked identical to a
    /// stalled one.</para>
    ///
    /// <para>Collapse state is per-MESSAGE, not just per-role — <c>SetExpanded</c> was added to
    /// SharpConsoleUI for exactly this. The capability already existed internally (every message
    /// owns a CollapsiblePanel with a public IsExpanded); only the accessor was missing.</para>
    /// </summary>
    public void ShowGoalResult(GoalState state, int failedCount) =>
        _system.EnqueueOnUIThread(() =>
        {
            // "JOB(S)" ONLY WHEN THERE ARE JOBS. Single-agent has no dag and no jobs, so a failed
            // run announced itself as "Goal Failed (0 job(s) failed)" — a count of a thing that does
            // not exist there, which reads as a bug in the app rather than a result of the work.
            // The plural is also wrong at 1 either way.
            var detail = failedCount > 0
                ? $" ({failedCount} job{(failedCount == 1 ? "" : "s")} failed)"
                : "";

            var id = _chat.AddMessage(ChatRole.System,
                state == GoalState.Completed ? "[green]▸ Done.[/]"
                : $"[red]▸ {state}{detail}.[/]");
            _chat.SetExpanded(id, true);
        });

    public void ShowError(string message) =>
        _system.EnqueueOnUIThread(() =>
            _chat.AddMessage(ChatRole.System, $"[red]✗ {message}[/]"));

    public void ShowSystemMessage(string message) =>
        _system.EnqueueOnUIThread(() =>
            _chat.AddMessage(ChatRole.System, message));

    // The real affordance lives in the job panel banner (JobPanelControl.SetDraftMode) and the
    // status-bar F9/Esc hint (MainWindow.SetDraftPending) — both driven directly off GoalRunner, not
    // through this sink. This transcript line is a secondary echo so the wait isn't silently
    // invisible in a scrolled-back chat log.
    public void ShowApprovalRequest(string? detail = null) =>
        _system.EnqueueOnUIThread(() =>
            _chat.AddMessage(ChatRole.System,
                "[yellow]▸ " + (detail is null ? "Plan drafted" : detail)
                + " — press F9 to approve, Esc to discard.[/]"));
}
