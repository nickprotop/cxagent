using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;

namespace CxAgent.Core.Agents;

/// <summary>
/// Where an aside reports: one block per <c>/btw</c>, never the session's own observer.
///
/// <para>A SEPARATE INTERFACE, NOT ISessionObserver. Everything that observer receives is part of
/// the main conversation (the transcript, the saved session, the turn ids), and an aside's whole
/// promise is that it is not.</para>
/// </summary>
public interface IAsideObserver
{
    void Asked(int aside, string question);
    void Waiting(int aside, bool waiting);
    void AnswerAppended(int aside, string text);
    void LookedUp(int aside, string row);
    void Finished(int aside, AsideEnd end, string? detail);

    /// <summary>The aside's model is thinking: plain text, escaped by the observer. Never part of
    /// the answer.</summary>
    void ThinkingAppended(int aside, string text);

    /// <summary>A run of thinking is over — the answer, a lookup, or the end of the response came
    /// next. Once per run, before the answer text that follows it.</summary>
    void ThinkingEnded(int aside, ThinkingSummary summary);
}

public enum AsideEnd
{
    Answered,

    /// <summary>Hit the tool-round cap and answered from what it had.</summary>
    Capped,

    /// <summary>The user stopped it with Esc.</summary>
    Stopped,

    /// <summary>The provider failed; the detail says how.</summary>
    Failed,
}

/// <summary>One answered question, for the run row <c>/stats</c> lists under <c>aside</c>.</summary>
public sealed record AsideRun(LlmUsage Spent, int Rounds, int ToolCalls, AsideEnd End,
    DateTimeOffset StartedAt, long DurationMs);

/// <summary>Everything one aside needs, named rather than passed as seven arguments.</summary>
public sealed record AsideRuntime
{
    public required ILlmProvider Provider { get; init; }
    public required PublishedContext From { get; init; }
    public required IAsideObserver Observer { get; init; }

    /// <summary>
    /// THE SESSION'S EXECUTORS, already gated by its permission gate and stamped with its policy —
    /// the same registry a sub-agent is handed. An aside is a parallel conversation with fewer tools,
    /// not a different trust level: whatever edit mode, stored rules and folder trust the session has,
    /// the aside's reads meet them, and a read the session would ask about is asked about.
    /// </summary>
    public required JobRegistry Executors { get; init; }

    public string? WorkingDir { get; init; }
    public Action<LlmUsage>? Spent { get; init; }
    public Action<AsideRun>? Ran { get; init; }
}
