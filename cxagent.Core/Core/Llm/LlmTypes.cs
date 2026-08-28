using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

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

    /// <summary>
    /// Input tokens WRITTEN to the provider's cache on this call.
    ///
    /// <para>A HIT IS NOT ALWAYS FREE, AND A WRITE IS SOMETIMES EXPENSIVE. On a local llama.cpp the
    /// cache costs nothing to fill — it is the server's own RAM. Through OpenRouter it is billed:
    /// OpenAI charges 1.25x normal input to write, Anthropic 1.25x for a 5-minute entry and 2x for a
    /// one-hour one. So a session can show a healthy hit rate while the warming quietly cost more
    /// than the reads saved.</para>
    ///
    /// <para>REPORTING HITS WITHOUT WRITES IS WORSE THAN REPORTING NEITHER, because the number
    /// looks trustworthy: cache write tokens unaccounted, costs understated.</para>
    ///
    /// <para>Zero on a provider that never writes, and on every local endpoint — which is why it is
    /// counted separately rather than folded into <see cref="CachedInputTokens"/>.</para>
    /// </summary>
    public int CacheWriteTokens { get; init; }

    /// <summary>
    /// What the provider says this call cost, in its own currency — or null when it does not say.
    ///
    /// <para>NULL IS NOT ZERO. A local llama.cpp sends no cost field at all, and it IS free, but the
    /// response cannot tell us that: it tells us only that nothing was reported. Rendering "$0.00"
    /// for an unmeasured call is the same class of claim as a 0% cache rate for a provider that
    /// never reported one.</para>
    ///
    /// <para>DECIMAL, NOT DOUBLE. This is money. A session sums thousands of calls at around 1e-06,
    /// and binary floating point accumulates error exactly where these figures live — the fourth
    /// decimal place.</para>
    ///
    /// <para>It arrives UNCONDITIONALLY on OpenRouter, contrary to what its documentation implies:
    /// a request sending no usage block at all still came back with a cost.</para>
    /// </summary>
    public decimal? Cost { get; init; }
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
/// this stream happens to have ended". The stop reason AND the tool-call check are used together
/// rather than either alone, because some providers return 'stop' even when the assistant message
/// contains tool calls. A local llama.cpp or vLLM server is exactly the kind of endpoint that
/// does.</para>
/// </param>
/// <param name="TextDelta">Text produced since the last chunk, or null when this chunk carries none.</param>
/// <param name="ToolCallDelta">A completed tool call, or null. One per chunk — several calls fan out.</param>
/// <param name="IsFinal">Whether this is the last chunk; usage and the stop reason ride it.</param>
/// <param name="Usage">What the call cost, on the final chunk only.</param>
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
