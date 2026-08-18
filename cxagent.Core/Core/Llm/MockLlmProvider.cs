using System.Runtime.CompilerServices;
using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

/// <summary>
/// Queue-driven mock provider — the backbone of the dev/test loop. Enqueue the
/// responses a test expects; ChatAsync dequeues them in order.
/// </summary>
public class MockLlmProvider : ILlmProvider
{
    private readonly Queue<LlmResponse> _responses = new();

    public MockLlmProvider(string model = "mock-model") => ModelId = model;

    public string ProviderId => "mock";
    public string DisplayName => "Mock Provider";
    public string ModelId { get; }
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    /// <summary>
    /// The messages handed to the most recent ChatAsync/ChatStreamAsync call, for assertions.
    /// </summary>
    public List<ChatMessage>? LastMessages { get; private set; }

    /// <summary>The tools handed to the most recent ChatAsync/ChatStreamAsync call, for assertions.</summary>
    public List<ToolDefinition>? LastTools { get; private set; }

    /// <summary>How many provider round-trips happened — Task 3 asserts the turn cap with this.</summary>
    public int ChatCallCount { get; private set; }

    public void EnqueueResponse(LlmResponse response) => _responses.Enqueue(response);

    public Task<LlmResponse> ChatAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, CancellationToken ct)
    {
        LastMessages = messages;
        LastTools = tools;
        ChatCallCount++;
        return Task.FromResult(_responses.Dequeue());
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        LastMessages = messages;
        LastTools = tools;
        ChatCallCount++;
        var resp = _responses.Dequeue();

        // ONE CHUNK PER TOOL CALL, which is how the real providers stream them — a chunk carries at
        // most one ToolCallDelta (LlmTypes.cs:38). This used to yield `ToolCalls.FirstOrDefault()`
        // in a single chunk, so every call after the first was SILENTLY DROPPED and no test in the
        // suite could exercise a multi-call turn at all. Found while testing cancellation backfill:
        // a test enqueued three calls, the agent saw one, and the assertion failed against correct
        // code.
        //
        // The non-final chunks carry no StopReason and no Usage; both ride the last one, as the real
        // providers emit them — without that a test cannot exercise the "server said tool_use but no
        // call was parsed" path.
        for (var i = 0; i < resp.ToolCalls.Count - 1; i++)
            yield return new LlmStreamChunk(null, resp.ToolCalls[i], IsFinal: false);

        yield return new LlmStreamChunk(resp.Text, resp.ToolCalls.LastOrDefault(), IsFinal: true,
            Usage: resp.Usage, StopReason: resp.StopReason);
        await Task.CompletedTask;
    }
}
