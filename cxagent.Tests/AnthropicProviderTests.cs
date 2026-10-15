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

    private static string Ev(string json) =>
        $"event: x\ndata: {json}\n\n";

    /// <summary>
    /// A STREAMED TOOL CALL CARRIES ITS ARGUMENTS. They arrive as input_json_delta fragments after
    /// the block's start, so a call emitted at the start had <c>{}</c> — every tool failed for
    /// missing arguments in the session that found this.
    /// </summary>
    [Fact]
    public async Task ChatStream_ReassemblesToolInput_FromInputJsonDeltas()
    {
        _srv.EnqueueSse(
            Ev("""{"type":"message_start","message":{"usage":{"input_tokens":120,"output_tokens":1}}}"""),
            Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Looking."}}"""),
            Ev("""{"type":"content_block_stop","index":0}"""),
            Ev("""{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tu_1","name":"grep","input":{}}}"""),
            Ev("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":""}}"""),
            Ev("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"pattern\": \"ar"}}"""),
            Ev("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"gs\"}"}}"""),
            Ev("""{"type":"content_block_stop","index":1}"""),
            Ev("""{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"tu_2","name":"list","input":{}}}"""),
            Ev("""{"type":"content_block_stop","index":2}"""),
            Ev("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}"""),
            Ev("""{"type":"message_stop"}"""));

        var calls = new List<ToolCall>();
        var text = new System.Text.StringBuilder();
        LlmStreamChunk? final = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
        {
            if (chunk.TextDelta is not null) text.Append(chunk.TextDelta);
            if (chunk.ToolCallDelta is not null) calls.Add(chunk.ToolCallDelta);
            if (chunk.IsFinal) final = chunk;
        }

        Assert.Equal("Looking.", text.ToString());
        Assert.Equal(2, calls.Count);
        Assert.Equal("grep", calls[0].Name);
        Assert.Equal("tu_1", calls[0].Id);
        Assert.Equal("args", calls[0].Arguments.GetProperty("pattern").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, calls[1].Arguments.ValueKind);
        Assert.Equal("tool_use", final!.StopReason);
        Assert.Equal(120, final.Usage!.InputTokens);
        Assert.Equal(42, final.Usage.OutputTokens);
    }

    /// <summary>
    /// ONE USER TURN ANSWERS ONE ASSISTANT TURN. All of a message's tool results go in the user turn
    /// right after it, and a user message that follows them is its own turn after that.
    /// </summary>
    [Fact]
    public void BuildRequestBody_GroupsAMessagesToolResults_IntoOneUserTurn()
    {
        var args = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "go" },
            new() { Role = "assistant", Content = "", ToolCalls =
                [new ToolCall { Id = "a", Name = "grep", Arguments = args },
                 new ToolCall { Id = "b", Name = "read_file", Arguments = args }] },
            new() { Role = "tool", ToolCallId = "a", Content = "count: 0" },
            new() { Role = "tool", ToolCallId = "b", Content = "'path' is required." },
            new() { Role = "user", Content = "try something else" },
        };

        var msgs = AnthropicWire.BuildRequestBody("m", 10, messages, null, stream: true)["messages"]!.AsArray();

        Assert.Equal(4, msgs.Count);
        var results = msgs[2]!["content"]!.AsArray();
        Assert.Equal(new[] { "a", "b" }, results.Select(b => b!["tool_use_id"]!.GetValue<string>()));
        Assert.Equal("try something else", msgs[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChatAsync_400_PutsTheVendorsReasonInTheMessage()
    {
        _srv.EnqueueJson(400, """{"type":"error","error":{"type":"invalid_request_error","message":"messages.2: tool_use ids were found without tool_result blocks"}}""");
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            Make().ChatAsync(Msgs(), null, CancellationToken.None));
        Assert.Equal(
            "provider 'anthropic' returned 400 after 1 attempt(s): messages.2: tool_use ids were found without tool_result blocks",
            ex.Message);
    }

    /// <summary>
    /// CACHING IS ASKED FOR WHEN CONFIGURED, and not otherwise. Anthropic caches nothing without a
    /// marker, and this driver sent none — every turn re-processed the whole conversation.
    /// </summary>
    [Fact]
    public void BuildRequestBody_WithCache_MarksSystemPrompt_AndAsksForAutomaticCaching()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = "you are cxagent" },
            new() { Role = "user", Content = "hi" },
        };

        var cached = AnthropicWire.BuildRequestBody("m", 10, messages, null, stream: true, cache: true);
        var system = Assert.Single(cached["system"]!.AsArray())!;
        Assert.Equal("you are cxagent", system["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", system["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", cached["cache_control"]!["type"]!.GetValue<string>());

        var plain = AnthropicWire.BuildRequestBody("m", 10, messages, null, stream: true);
        Assert.Equal("you are cxagent", plain["system"]!.GetValue<string>());
        Assert.Null(plain["cache_control"]);
    }

    /// <summary>
    /// CACHED TOKENS ARE COUNTED. Anthropic's input_tokens is the uncached remainder only; the
    /// neutral figure is the whole prompt, with the reads and writes reported beside it.
    /// </summary>
    [Fact]
    public async Task ChatStream_ReportsCacheReadsAndWrites()
    {
        _srv.EnqueueSse(
            Ev("""{"type":"message_start","message":{"usage":{"input_tokens":50,"cache_read_input_tokens":9000,"cache_creation_input_tokens":400,"output_tokens":1}}}"""),
            Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
            Ev("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}"""),
            Ev("""{"type":"message_stop"}"""));

        LlmUsage? usage = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
            if (chunk.IsFinal) usage = chunk.Usage;

        Assert.Equal(9450, usage!.InputTokens);
        Assert.Equal(9000, usage.CachedInputTokens);
        Assert.Equal(400, usage.CacheWriteTokens);
        Assert.True(usage.CacheReported);
        Assert.Equal(7, usage.OutputTokens);
    }

    [Fact]
    public async Task ChatAsync_WithCacheControl_SendsTheMarkers()
    {
        _srv.EnqueueJson(200, """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"cache_read_input_tokens":100,"output_tokens":1}}""");
        var provider = new AnthropicProvider("anthropic", "Claude x", "claude-x", "sk-ant-test",
            baseUrl: _srv.BaseUrl.TrimEnd('/'), retryPolicy: RetryPolicy.NoDelay, cacheControl: true);

        var r = await provider.ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Contains("\"cache_control\"", _srv.LastRequestBody);
        Assert.Equal(103, r.Usage.InputTokens);
        Assert.Equal(100, r.Usage.CachedInputTokens);
    }

    /// <summary>
    /// THE MARKER SITS BEFORE A TRAILING TASK LIST. The list is removed and re-appended every turn,
    /// so a cache entry ending on it is never read; one ending on the history before it is.
    /// </summary>
    [Fact]
    public void BuildRequestBody_WithCache_MarksTheBlockBeforeATrailingTaskList()
    {
        var args = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = "sys" },
            new() { Role = "user", Content = "go" },
            new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "a", Name = "grep", Arguments = args }] },
            new() { Role = "tool", ToolCallId = "a", Content = "count: 0" },
            new() { Role = "user", Content = "- [ ] step one", IsTaskList = true },
        };

        var msgs = AnthropicWire.BuildRequestBody("m", 10, messages, null, stream: true, cache: true)["messages"]!.AsArray();

        var result = msgs[^2]!["content"]!.AsArray()[^1]!;
        Assert.Equal("tool_result", result["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", result["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("- [ ] step one", msgs[^1]!["content"]!.GetValue<string>());
    }
}
