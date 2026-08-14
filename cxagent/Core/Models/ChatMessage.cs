using System.Text.Json;

namespace CxAgent.Core.Models;

public record ChatMessage
{
    // "user", "assistant", "system", "tool". NOTE: for a tool RESULT the wire builders ignore this
    // and set the role themselves — OpenAiWire overwrites it to "tool", AnthropicWire emits a "user"
    // turn carrying a tool_result block. ToolCallId below, not this, is what makes it a tool result.
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }  // assistant messages with tool use
    public string? ToolCallId { get; init; }         // tool result messages — the ONLY marker of one

    /// <summary>
    /// True for the ONE regenerated message carrying the model's task list.
    ///
    /// <para>A PROPERTY, NOT A TEXT MATCH. The list is rewritten every turn, so the previous copy has
    /// to be found and replaced; locating it by its rendered prose would delete a user message that
    /// happened to quote the plan back.</para>
    ///
    /// <para>A BOOL RATHER THAN A GENERAL TAG because there is exactly one kind of regenerated
    /// message. Inventing a tagging scheme for a single user is speculative.</para>
    ///
    /// <para>It never reaches a provider: the wire builders construct each message field-by-field
    /// into a fresh JsonObject rather than serialising this record, so a new property cannot change
    /// the bytes that get cached.</para>
    /// </summary>
    public bool IsTaskList { get; init; }
}

public record ToolCall
{
    public string Name { get; init; } = "";
    public JsonElement Arguments { get; init; }
    public string? Id { get; init; }
}
