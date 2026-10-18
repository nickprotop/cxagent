using System.Text.Json.Serialization;
using CxAgent.Core.Agents;
using CxAgent.Core.Models;

namespace CxAgent.Core.Storage;

/// <summary>One line of an agent's <c>history.jsonl</c>: a message it was given or gave, or a marker.</summary>
/// <param name="Seq">Position in the file, from 1.</param>
/// <param name="At">When it was recorded — the message's own time where it has one.</param>
/// <param name="Kind">One of <see cref="Kinds"/>.</param>
/// <param name="Message">The message, for <see cref="Kinds.Message"/>.</param>
/// <param name="Thinking">
/// The model's reasoning for an assistant message. Kept here because <see cref="ChatMessage.ThinkingBlocks"/>
/// is never saved anywhere else.
/// </param>
/// <param name="From">Who sent a received message, where that is known.</param>
/// <param name="Previous">For <see cref="Kinds.Continues"/>: the agent whose history this one carries on.</param>
public sealed record HistoryLine(
    [property: JsonPropertyName("q")] long Seq,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("m")] ChatMessage? Message = null,
    [property: JsonPropertyName("t")] IReadOnlyList<string>? Thinking = null,
    [property: JsonPropertyName("from")] Incoming? From = null,
    [property: JsonPropertyName("prev")] string? Previous = null)
{
    /// <summary>What a line records.</summary>
    public static class Kinds
    {
        /// <summary>A message that joined the context.</summary>
        public const string Message = "message";

        /// <summary>Compaction replaced earlier context here; the summary is the next line.</summary>
        public const string Compacted = "compacted";

        /// <summary>The conversation was cleared here.</summary>
        public const string Cleared = "cleared";

        /// <summary>
        /// This agent carries on another's conversation — a resume or a model switch mints a new agent, and
        /// the history before it lives in the previous agent's folder.
        /// </summary>
        public const string Continues = "continues";
    }
}
