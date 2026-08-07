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
}

public record ToolCall
{
    public string Name { get; init; } = "";
    public JsonElement Arguments { get; init; }
    public string? Id { get; init; }
}
