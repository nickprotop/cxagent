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

    /// <summary>
    /// Returns an equivalent provider bound to <paramref name="model"/>, sharing every other setting
    /// (credentials, baseUrl, HttpClient, retry policy). A role's RoutingTarget names an instance AND
    /// a model, but the registry builds each instance with its ONE configured default model — so two
    /// roles bound to different models on the same instance need a per-call override. Implementations
    /// must preserve their concrete type; returning a base-typed clone silently drops overridden
    /// behaviour.
    /// </summary>
    ILlmProvider WithModel(string model);

    Task<LlmResponse> ChatAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        CancellationToken ct);

    IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        CancellationToken ct);
}
