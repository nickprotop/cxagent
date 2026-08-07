using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

public class MockLlmProviderTests
{
    [Fact]
    public async Task ChatAsync_ReturnsEnqueuedResponsesInOrder()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new { summary = "s", jobs = Array.Empty<object>() }));

        var resp = await mock.ChatAsync(new List<ChatMessage>(), null, CancellationToken.None);

        Assert.Equal("tool_use", resp.StopReason);
        Assert.Single(resp.ToolCalls);
        Assert.Equal("create_plan", resp.ToolCalls[0].Name);
    }

    [Fact]
    public void Capabilities_ReportMockDefaults()
    {
        var mock = new MockLlmProvider();
        Assert.Equal("mock", mock.ProviderId);
        Assert.True(mock.SupportsToolCalling);
        Assert.False(mock.SupportsStreaming);
    }

    [Fact]
    public void WithModel_ReportsNewModel_AndDefaultsToMockModel()
    {
        var mock = new MockLlmProvider();
        Assert.Equal("mock-model", mock.ModelId);
        Assert.Equal("other-model", mock.WithModel("other-model").ModelId);
        Assert.Equal("mock-model", mock.ModelId);   // the original is not mutated
    }

    [Fact]
    public async Task WithModel_CloneSharesTheResponseQueue()
    {
        // A response enqueued on the original must satisfy a call made through the clone — otherwise
        // routing a role to a different model silently starves the queue.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new { summary = "s", jobs = Array.Empty<object>() }));

        var clone = mock.WithModel("other-model");
        var resp = await clone.ChatAsync(new List<ChatMessage>(), null, CancellationToken.None);

        Assert.Equal("create_plan", resp.ToolCalls[0].Name);
    }

    [Fact]
    public async Task WithModel_CloneSharesLastMessages()
    {
        // LastMessages is the mock's message-inspection affordance. A call through the clone must be
        // visible on the original, or assertions on a routed call silently see null.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });

        var clone = (MockLlmProvider)mock.WithModel("other-model");
        var sent = new List<ChatMessage> { new() { Role = "user", Content = "hello" } };
        await clone.ChatAsync(sent, null, CancellationToken.None);

        Assert.NotNull(mock.LastMessages);
        Assert.Equal("hello", mock.LastMessages![0].Content);
        Assert.NotNull(clone.LastMessages);
    }
}
