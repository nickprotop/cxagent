using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

public record ToolDefinition(string Name, string Description, JsonElement InputSchema);

public record LlmUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    /// <summary>
    /// How much of <see cref="InputTokens"/> the provider served from its prefix cache.
    ///
    /// <para>THE DOMINANT PERFORMANCE FACT OF A TOOL LOOP, and the one nothing here measured. Every
    /// turn re-sends the whole conversation, so a 116-turn session sent 21.3 MB to produce 196 KB of
    /// unique tool output — a 148:1 input-to-output ratio. Whether that is fast or slow is decided
    /// almost entirely by how much of each re-send is cached: measured on a local endpoint, the same
    /// prompt costs 43ms warm against 1,420ms cold.</para>
    ///
    /// <para>ZERO IS AMBIGUOUS and must not be read as "no cache": a provider that does not report
    /// the field looks identical to one that missed entirely. <see cref="CacheReported"/> separates
    /// them, so the UI can stay silent rather than claim a 0% hit rate it cannot know.</para>
    /// </summary>
    public int CachedInputTokens { get; init; }

    /// <summary>Whether the provider reported cache figures at all — see
    /// <see cref="CachedInputTokens"/> for why the distinction matters.</summary>
    public bool CacheReported { get; init; }
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
