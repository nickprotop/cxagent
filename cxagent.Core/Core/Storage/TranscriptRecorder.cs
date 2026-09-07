using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;

namespace CxAgent.Core.Storage;

/// <summary>
/// Writes what a session was shown into its <see cref="TranscriptStore"/>.
///
/// <para>AN OBSERVER, NOT A HOOK INSIDE THE TURN LOOP. The events a front end renders are exactly the
/// events worth replaying to another one, so the store subscribes to the same fan-out the TUI does
/// rather than the agent growing a second notion of "what happened". A second subscriber joining is
/// what the fan-out was built for; this is its first user.</para>
///
/// <para><b>COALESCED IN MEMORY, WRITTEN WHEN A MESSAGE ENDS.</b> Assistant text arrives token by
/// token, and the store's <c>Append</c> REPLACES a row's body rather than appending to it — so
/// writing per token would rewrite the whole accumulated message on every one, which is quadratic in
/// the bytes written for a long answer. Buffering the message and writing once at
/// <see cref="AssistantTurnEnded"/> makes the cost proportional to the conversation rather than to
/// the token count.</para>
///
/// <para>THE TRADE IS EXPLICIT: a crash mid-turn loses that turn's assistant text. That matches what
/// the store is for — replaying a conversation to a front end that attached late — and matches the
/// rest of the design, where "durable" means across a restart rather than across a kill. A user
/// message is written immediately, because it is already complete when it arrives and it is the half
/// that cannot be regenerated.</para>
///
/// <para>THE SEQUENCE IS THE SESSION'S OWN. <c>ChatMessageId</c> is minted by
/// <c>Session.NextTurnId</c> through an <c>Interlocked.Increment</c>, so it is monotonic within a
/// session and already means "the order these appeared" — which is exactly what the store's
/// caller-assigned <c>seq</c> is for. Numbering them again here would be a second counter to keep in
/// step with the first.</para>
/// </summary>
public sealed class TranscriptRecorder : ISessionObserver
{
    /// <summary>
    /// The store, opened on the first row rather than at construction.
    /// </summary>
    /// <remarks>
    /// CONSTRUCTING A STORE CREATES ITS DATABASE, and a recorder is wired for every session whether
    /// or not that session ever says anything. Eager, a `--mock` run or a session opened and closed
    /// without a turn would leave a transcript.db behind — and the file appearing in the config
    /// directory changes what is there for anything reading the directory as a whole, which is how a
    /// plugin-load-set hash caught it.
    /// </remarks>
    private readonly Lazy<TranscriptStore> _store;

    private readonly string _sessionId;

    /// <summary>Assistant text accumulating for a message that has not ended yet.</summary>
    /// <remarks>
    /// A DICTIONARY RATHER THAN ONE BUFFER, because reasoning and text arrive under the same id and
    /// nothing guarantees one message is finished before another begins — a sub-agent's output and
    /// the parent's can interleave on the same fan-out.
    /// </remarks>
    private readonly Dictionary<long, System.Text.StringBuilder> _open = [];

    private readonly object _gate = new();

    /// <summary>
    /// Rows that belong to the session but have no message id — a system notice.
    /// </summary>
    /// <remarks>
    /// THEY STILL NEED A PLACE IN THE ORDER. `Said` carries no id, so a notice would otherwise have
    /// no sequence and either overwrite a message or sort arbitrarily on replay. Counting DOWN from
    /// zero keeps them out of the message ids' space entirely, and preserves their order among
    /// themselves — a replay merges the two by timestamp, which is what `at` is for.
    /// </remarks>
    private long _noticeSeq;

    public TranscriptRecorder(Lazy<TranscriptStore> store, string sessionId)
    {
        _store = store;
        _sessionId = sessionId;
    }

    /// <summary>What the user said, written at once: it is complete when it arrives.</summary>
    public void UserTurnAdded(ChatMessageId id, string text) =>
        _store.Value.Append(_sessionId, id.Value, "user", "User", text);

    /// <summary>
    /// A message is opening. Nothing is written yet — the row appears when the message ends.
    /// </summary>
    public void AssistantTurnBegan(ChatMessageId id)
    {
        lock (_gate) _open[id.Value] = new System.Text.StringBuilder();
    }

    public void AssistantTextAppended(ChatMessageId id, string token) => Accumulate(id, token);

    /// <summary>
    /// Reasoning is NOT recorded.
    /// </summary>
    /// <remarks>
    /// IT IS NOT PART OF THE CONVERSATION. A model's thinking is shown live because watching it is
    /// useful, and it is not what the next front end needs to reconstruct what was SAID — the same
    /// division the context itself makes, where reasoning does not survive into the next turn. Storing
    /// it would also multiply the store's size for content nothing reads back.
    /// </remarks>
    public void AssistantReasoningAppended(ChatMessageId id, string text) { }

    /// <summary>The message is complete: write it as one row and forget the buffer.</summary>
    public void AssistantTurnEnded(ChatMessageId id)
    {
        System.Text.StringBuilder? buffer;
        lock (_gate)
        {
            if (!_open.Remove(id.Value, out buffer)) return;
        }

        // AN EMPTY MESSAGE IS NOT A ROW. A turn that produced only tool calls ends with nothing said,
        // and a blank row would replay as an empty assistant message the user never saw.
        var text = buffer.ToString();
        if (text.Length == 0) return;

        _store.Value.Append(_sessionId, id.Value, "assistant", "Assistant", text);
    }

    /// <summary>
    /// A label on a message — which agent produced it.
    /// </summary>
    /// <remarks>
    /// WRITTEN AS ITS OWN ROW rather than folded into the message, because it arrives BEFORE the
    /// message ends and folding it in would mean holding a second field per open buffer for
    /// something a replay can show beside the row it precedes.
    /// </remarks>
    public void AssistantLabelled(ChatMessageId id, string header) =>
        _store.Value.Append(_sessionId, NoticeSeq(), "label", header, null);

    /// <summary>A system notice — a warning, an error, something the session said about itself.</summary>
    public void Said(Message message) =>
        _store.Value.Append(_sessionId, NoticeSeq(), "system", message.Severity.ToString(), message.Text);

    private void Accumulate(ChatMessageId id, string text)
    {
        lock (_gate)
        {
            // A TOKEN FOR A MESSAGE NOBODY OPENED still belongs somewhere. AssistantTurnBegan is
            // raised by the turn loop and this by the stream, and a subscriber attaching between the
            // two would otherwise silently drop the first tokens of the message it joined during.
            if (!_open.TryGetValue(id.Value, out var buffer))
                _open[id.Value] = buffer = new System.Text.StringBuilder();

            buffer.Append(text);
        }
    }

    private long NoticeSeq()
    {
        lock (_gate) return --_noticeSeq;
    }
}
