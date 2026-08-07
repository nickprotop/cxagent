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

/// <param name="StopReason">
/// The NORMALIZED stop reason, on the final chunk only; null on every other chunk.
///
/// <para>Carried so a caller can tell "the model is done" from "the model emitted tool calls and
/// this stream happens to have ended". Both opencode and crush AND the tool-call check with this
/// value rather than trusting either alone, and opencode's source says why: "Some providers return
/// 'stop' even when the assistant message contains tool calls." A local llama.cpp or vLLM server is
/// exactly the kind of endpoint that does.</para>
/// </param>
public record LlmStreamChunk(string? TextDelta, ToolCall? ToolCallDelta, bool IsFinal,
    LlmUsage? Usage = null, string? StopReason = null);

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
