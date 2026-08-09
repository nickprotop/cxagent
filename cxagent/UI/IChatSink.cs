using CxAgent.Core.Models;

namespace CxAgent.UI;

/// <summary>A P5a-owned message id (decouples AgentHost from the framework's ChatTranscript ids).</summary>
public readonly record struct ChatMessageId(long Value);

/// <summary>
/// The UI-update seam AgentHost writes to. A real implementation (ChatTranscriptSink) marshals each
/// call onto the UI thread; tests use a recording fake. AgentHost never touches a control directly.
/// </summary>
public interface IChatSink
{
    ChatMessageId AddUserTurn(string text);
    ChatMessageId BeginAssistantTurn();
    void AppendAssistant(ChatMessageId id, string token);

    /// <summary>
    /// Closes an assistant turn. MUST be called for every <see cref="BeginAssistantTurn"/>, including
    /// turns that produced no text.
    ///
    /// <para>A turn is created with <c>thinking: true</c>, and ChatTranscriptControl only clears that
    /// flag when a message receives BODY CONTENT. A planning turn where the model returns a
    /// create_plan tool call and no prose — the normal case — therefore span its spinner forever,
    /// which read as "still working" long after the goal had finished.</para>
    /// </summary>
    void EndAssistantTurn(ChatMessageId id);

    /// <summary>
    /// Replaces a message's HEADER — used to show live state on a turn whose body is still empty.
    ///
    /// <para>The body cannot carry it: the transcript control clears a message's spinner as soon as
    /// body content arrives, so streaming a reasoning model's thinking into the body would kill the
    /// one indicator that says it is alive. The header is the place ConsoleEx documents for exactly
    /// this ("combined with an inline [spinner] tag the same header carries the running indicator"),
    /// and it is where the job rows already put their status.</para>
    /// </summary>
    void SetAssistantHeader(ChatMessageId id, string header);
    void ShowError(string message);

    /// <summary>
    /// A plain informational line in the transcript — neither an error nor a goal result. Task 4's
    /// permission gate uses this to echo every permission decision (allowed once / always allowing
    /// &lt;rule&gt; / denied / trusted this folder): the transcript is the session's audit trail, and
    /// a decision that leaves no trace is a decision nobody can review.
    /// </summary>
    void ShowSystemMessage(string message);

    /// <summary>
    /// Copilot mode (P9): the plan has been shown (SetJobs already ran) and the goal is now sitting
    /// parked for approval. Tells the UI to surface the approve/discard affordance
    /// (Task 2's F9 binding). One-way — the answer comes back through
    /// AgentHost.ApproveDraft/DiscardDraft, not through this interface.
    ///
    /// <para><paramref name="detail"/> names WHAT is being approved. Null for the initial plan (the
    /// job panel already shows it in full). For P9b's mid-goal gate it lists the jobs the
    /// orchestrator wants to ADD — "3 new jobs" is not reviewable, and the user is being asked to
    /// approve THESE.</para>
    /// </summary>
    void ShowApprovalRequest(string? detail = null);
}
