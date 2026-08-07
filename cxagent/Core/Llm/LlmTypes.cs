using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

public record ToolDefinition(string Name, string Description, JsonElement InputSchema);

public record LlmUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

public record LlmResponse
{
    public string? Text { get; init; }
    public List<ToolCall> ToolCalls { get; init; } = new();
    public string StopReason { get; init; } = "";   // "end_turn" | "tool_use" | "refusal" | ...
    public LlmUsage Usage { get; init; } = new();

    public static LlmResponse WithToolCall(string name, object args) =>
        new()
        {
            StopReason = "tool_use",
            ToolCalls = { new ToolCall { Name = name, Arguments = JsonSerializer.SerializeToElement(args) } }
        };
}

public record LlmStreamChunk(string? TextDelta, ToolCall? ToolCallDelta, bool IsFinal, LlmUsage? Usage = null);

/// <summary>A terminal LLM provider failure (auth, bad request, model-not-found, or retries exhausted).</summary>
public sealed class LlmProviderException : Exception
{
    public string InstanceName { get; }
    public int? HttpStatus { get; }
    public string? VendorBody { get; }

    public LlmProviderException(string instanceName, int? httpStatus, string? vendorBody, string message)
        : base(message)
    {
        InstanceName = instanceName;
        HttpStatus = httpStatus;
        VendorBody = vendorBody;
    }
}
