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

}
