using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

/// <summary>
/// The provider HAL. The whole core depends only on this abstraction; concrete
/// vendor drivers (Claude/OpenAI/…) are added in a later plan. cxagent never
/// branches on provider identity above this seam.
/// </summary>
public interface ILlmProvider
{
    string ProviderId { get; }       // e.g. "claude", "openai", "mock"
    string DisplayName { get; }
    string ModelId { get; }
    bool SupportsToolCalling { get; }
    bool SupportsStreaming { get; }

    Task<LlmResponse> ChatAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        CancellationToken ct);

    IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        CancellationToken ct);
}
