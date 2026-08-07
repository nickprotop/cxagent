using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class AnthropicProviderTests : IDisposable
{
    private readonly LoopbackServer _srv = new();
    public void Dispose() => _srv.Dispose();

    private AnthropicProvider Make() =>
        new("anthropic", "Claude x", "claude-x", "sk-ant-test", maxTokens: 1024,
            baseUrl: _srv.BaseUrl.TrimEnd('/'), retryPolicy: RetryPolicy.NoDelay);

    private static List<ChatMessage> Msgs() => new()
    {
        new ChatMessage { Role = "system", Content = "you are a planner" },
        new ChatMessage { Role = "user", Content = "decompose this goal" },
    };

    [Fact]
    public async Task ChatAsync_MapsText_HoistsSystem_SetsHeaders_NormalizesStop()
    {
        _srv.EnqueueJson(200, """
        {"content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn",
         "usage":{"input_tokens":7,"output_tokens":2}}
        """);
        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);
        Assert.Equal("hi", r.Text);
        Assert.Equal("end_turn", r.StopReason);
        Assert.Equal(7, r.Usage.InputTokens);
        Assert.Equal(2, r.Usage.OutputTokens);

        // system hoisted to top-level "system", not in messages[]; headers present.
        Assert.Contains("\"system\":", _srv.LastRequestBody);
        Assert.Equal("sk-ant-test", _srv.LastRequestHeaders["x-api-key"]);
        Assert.True(_srv.LastRequestHeaders.ContainsKey("anthropic-version"));
    }

    [Fact]
    public async Task ChatAsync_MapsToolUse_ToNeutralToolCall()
    {
        _srv.EnqueueJson(200, """
        {"content":[{"type":"tool_use","id":"tu_1","name":"create_plan","input":{"summary":"x"}}],
         "stop_reason":"tool_use"}
        """);
        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);
        Assert.Equal("tool_use", r.StopReason);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal("create_plan", call.Name);
        Assert.Equal("tu_1", call.Id);
        Assert.Equal("x", call.Arguments.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ChatAsync_SendsToolsAndInputSchema()
    {
        _srv.EnqueueJson(200, """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""");
        var tools = new List<ToolDefinition> {
            new("create_plan", "make a plan",
                System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement) };
        await Make().ChatAsync(Msgs(), tools, CancellationToken.None);
        Assert.Contains("\"create_plan\"", _srv.LastRequestBody);
        Assert.Contains("\"input_schema\"", _srv.LastRequestBody);
    }

    [Fact]
    public async Task ChatAsync_401_ThrowsLlmProviderException()
    {
        _srv.EnqueueJson(401, """{"error":{"message":"bad key"}}""");
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            Make().ChatAsync(Msgs(), null, CancellationToken.None));
        Assert.Equal(401, ex.HttpStatus);
        Assert.Equal("anthropic", ex.InstanceName);
    }

    [Fact]
    public async Task ChatStream_ConcatenatedDeltas_EqualNonStreamedText()
    {
        _srv.EnqueueJson(200, """{"content":[{"type":"text","text":"Hello world"}],"stop_reason":"end_turn"}""");
        var full = await Make().ChatAsync(Msgs(), null, CancellationToken.None);

        // Anthropic SSE: content_block_delta events carry text_delta; message_delta carries stop_reason.
        _srv.EnqueueSse(
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\" world\"}}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n");

        var sb = new System.Text.StringBuilder();
        bool sawFinal = false;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
        {
            if (chunk.TextDelta is not null) sb.Append(chunk.TextDelta);
            if (chunk.IsFinal) sawFinal = true;
        }
        Assert.Equal(full.Text, sb.ToString());
        Assert.True(sawFinal);
    }

    [Fact]
    public async Task ChatStream_EmitsToolUseName()
    {
        _srv.EnqueueSse(
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\"create_plan\"}}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n\n");
        ToolCall? seen = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
            if (chunk.ToolCallDelta is not null) seen = chunk.ToolCallDelta;
        Assert.NotNull(seen);
        Assert.Equal("create_plan", seen!.Name);
    }
}
