using CxAgent.Core.Commands;
using CxAgent.Core.Models;

namespace CxAgent.Core.Sessions;

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
    /// how it looks. Handing over pre-styled markup instead puts a colour decision inside the turn
    /// loop, and — with one method carrying both styled reasoning and unstyled body text — leaves
    /// the sink unable to tell which of its two inputs is safe to escape. Escape one and not the
    /// other and the unescaped side silently swallows any recognised tag name a model writes.</para>
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
    /// create_plan tool call and no prose — the normal case — never receives any, so without this
    /// call its spinner spins forever, reading as "still working" long after the goal finished.</para>
    /// </summary>
    void AssistantTurnEnded(ChatMessageId id);

    /// <summary>
    /// Replaces a message's HEADER, which is how live state is shown on a turn whose body is still
    /// empty.
    ///
    /// <para>The body cannot carry it: the transcript control clears a message's spinner as soon as
    /// body content arrives, so streaming a reasoning model's thinking into the body would kill the
    /// one indicator that says it is alive. The header is the place ConsoleEx documents for exactly
    /// this ("combined with an inline [spinner] tag the same header carries the running indicator"),
    /// and it is where the job rows already put their status.</para>
    /// </summary>
    void AssistantLabelled(ChatMessageId id, string header);

    /// <summary>
    /// Something the session did, said in words for the user.
    ///
    /// <para>MARKDOWN, and the severity beside it rather than inside it. ONE method, not a pair of
    /// <c>Said</c>/<c>Failed</c>: with tone explicit in the message, a pair says the same thing
    /// twice and a front end implementing both writes the same body with a different colour.</para>
    ///
    /// <para>A plain string converts implicitly and arrives as <see cref="Severity.Info"/>, so an
    /// ordinary line stays an ordinary line at the call site.</para>
    /// </summary>
    /// <param name="message">Markdown, and how loudly to say it.</param>
    void Said(Message message);
}
