using System.Runtime.CompilerServices;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

/// <summary>
/// Queue-driven mock provider — the backbone of the dev/test loop. Enqueue the
/// responses a test expects; ChatAsync dequeues them in order.
/// </summary>
public class MockLlmProvider : ILlmProvider
{
    /// <summary>
    /// Everything a clone must SHARE with its original. Both the pending responses and the recorded
    /// messages live here: a test that enqueues on the original and then calls through a WithModel
    /// clone must still dequeue that response AND be able to assert on what the clone was sent.
    /// Per-instance fields would make both silently miss.
    /// </summary>
    private sealed class SharedState
    {
        public readonly Queue<LlmResponse> Responses = new();
        public List<ChatMessage>? LastMessages;
        public List<ToolDefinition>? LastTools;
        public int ChatCallCount;
    }

    private readonly SharedState _state;

    public MockLlmProvider(string model = "mock-model") : this(model, new SharedState()) { }

    private MockLlmProvider(string model, SharedState state)
    {
        ModelId = model;
        _state = state;
    }

    public string ProviderId => "mock";
    public string DisplayName => "Mock Provider";
    public string ModelId { get; }
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    /// <summary>
    /// A clone reporting the new model but SHARING the response queue and recorded messages, so a
    /// response enqueued on the original still satisfies a call made through the clone, and
    /// LastMessages on either instance reflects that call.
    /// </summary>
    public ILlmProvider WithModel(string model) => new MockLlmProvider(model, _state);

    /// <summary>
    /// The messages handed to the most recent ChatAsync/ChatStreamAsync call, for assertions. Shared
    /// with WithModel clones, so it reflects a call made through either.
    /// </summary>
    public List<ChatMessage>? LastMessages => _state.LastMessages;

    /// <summary>The tools handed to the most recent ChatAsync/ChatStreamAsync call, for assertions.</summary>
    public List<ToolDefinition>? LastTools => _state.LastTools;

    /// <summary>How many provider round-trips happened — Task 3 asserts the turn cap with this.</summary>
    public int ChatCallCount => _state.ChatCallCount;

    public void EnqueueResponse(LlmResponse response) => _state.Responses.Enqueue(response);

    public Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, CancellationToken ct)
    {
        _state.LastMessages = messages;
        _state.LastTools = tools;
        _state.ChatCallCount++;
        return Task.FromResult(_state.Responses.Dequeue());
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        _state.LastMessages = messages;
        _state.LastTools = tools;
        _state.ChatCallCount++;
        // Non-streaming mock: yield the whole dequeued response as one final chunk.
        var resp = _state.Responses.Dequeue();
        yield return new LlmStreamChunk(resp.Text, resp.ToolCalls.FirstOrDefault(), IsFinal: true, Usage: resp.Usage);
        await Task.CompletedTask;
    }
}
