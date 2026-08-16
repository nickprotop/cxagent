using CxAgent.Core.Models;

namespace CxAgent.Core.Agent;

/// <summary>
/// What the session REPORTS as a conversation runs — text, reasoning, turn boundaries, failures.
///
/// <para>REPORTS, DOES NOT INSTRUCT. Every member names something that happened, never something to
/// display. The session does not know a screen exists; a server, a log file and a test recorder are
/// all equally valid implementations, and the names should read naturally for each. The verbs used to
/// be Show/Set/Append — instructions to a display — which is how a UI concern comes to live in a
/// session's vocabulary.</para>
///
/// <para>Plain text, never markup. The implementation escapes what it renders: a model writing
/// "[red]" as ordinary prose must not open a style scope, and a model discussing THIS codebase does
/// exactly that.</para>
/// </summary>
public interface ISessionObserver
{
    /// <summary>The user said something, under the id the SESSION assigned it.</summary>
    void UserTurnAdded(ChatMessageId id, string text);

    /// <summary>A turn began. Every later report about it carries this same id.</summary>
    void AssistantTurnBegan(ChatMessageId id);
    /// <summary>
    /// Body text from the model — what it is SAYING.
    ///
    /// <para>Plain text, never markup. The sink escapes it before rendering: a model writing "[red]"
    /// or "[dim]" as ordinary prose must not open a style scope, and a model discussing THIS codebase
    /// does exactly that. The agent does not know markup exists.</para>
    /// </summary>
    void AssistantTextAppended(ChatMessageId id, string token);

    /// <summary>
    /// Reasoning text from the model — what it is THINKING.
    ///
    /// <para>A SEPARATE METHOD RATHER THAN A FLAG, so the agent states a KIND and the sink chooses
    /// how it looks. It used to hand over pre-styled markup, which put a colour decision inside the
    /// turn loop and — because the same method also carried unstyled body text — left the sink unable
    /// to tell which of its two inputs was safe to escape. Only one of them was escaped; the other
    /// silently swallowed any recognised tag name a model happened to write.</para>
    ///
    /// <para>Same contract as <see cref="AssistantTextAppended"/>: plain text, escaped by the sink.</para>
    /// </summary>
    void AssistantReasoningAppended(ChatMessageId id, string text);

    /// <summary>
    /// Closes an assistant turn. MUST be called for every <see cref="AssistantTurnBegan"/>, including
    /// turns that produced no text.
    ///
    /// <para>A turn is created with <c>thinking: true</c>, and ChatTranscriptControl only clears that
    /// flag when a message receives BODY CONTENT. A planning turn where the model returns a
    /// create_plan tool call and no prose — the normal case — therefore span its spinner forever,
    /// which read as "still working" long after the goal had finished.</para>
    /// </summary>
    void AssistantTurnEnded(ChatMessageId id);

    /// <summary>
    /// Replaces a message's HEADER — used to show live state on a turn whose body is still empty.
    ///
    /// <para>The body cannot carry it: the transcript control clears a message's spinner as soon as
    /// body content arrives, so streaming a reasoning model's thinking into the body would kill the
    /// one indicator that says it is alive. The header is the place ConsoleEx documents for exactly
    /// this ("combined with an inline [spinner] tag the same header carries the running indicator"),
    /// and it is where the job rows already put their status.</para>
    /// </summary>
    void AssistantLabelled(ChatMessageId id, string header);
    void Failed(string message);

    /// <summary>
    /// Something the session did, said in words for the user.
    ///
    /// <para>THE NON-FAILURE SIBLING OF <see cref="Failed"/>, and it exists because that was the only
    /// way for a session to reach the transcript — so a session that switched model or changed mode
    /// had no way to say so, and the composition root composed the sentence instead by reaching into
    /// session state it should not have needed to read. Every front end would have had to reimplement
    /// that, and the first one to miss a line has a session whose state changed silently.</para>
    ///
    /// <para>MARKUP, like the rest of this contract's text. The session is choosing EMPHASIS, which
    /// is part of a sentence; a front end that does not render markup strips it, exactly as it would
    /// for a model's own output.</para>
    /// </summary>
    void Said(string message);
}
