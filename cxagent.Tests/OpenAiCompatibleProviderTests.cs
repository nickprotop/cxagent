using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class OpenAiCompatibleProviderTests : IDisposable
{
    private readonly LoopbackServer _srv = new();
    public void Dispose() => _srv.Dispose();

    private OpenAiCompatibleProvider Make(IReadOnlyDictionary<string, string>? extra = null) =>
        new("openai", "OpenAI gpt-x", "gpt-x", _srv.BaseUrl, "sk-test", extra,
            retryPolicy: RetryPolicy.NoDelay);

    private static List<ChatMessage> Msgs() => new()
    {
        new ChatMessage { Role = "system", Content = "you are a planner" },
        new ChatMessage { Role = "user", Content = "decompose this goal" },
    };

    // ---- prefix cache reporting -----------------------------------------------------------------

    /// <summary>
    /// THE FIELD THAT WAS ON THE WIRE ALL ALONG AND NOWHERE IN THE APP. A tool loop re-sends the
    /// whole conversation every turn, so whether that is cheap or ruinous is decided by the prefix
    /// cache — and nothing here read the number that says which.
    /// </summary>
    [Fact]
    public async Task ChatAsync_ReadsCachedPromptTokens()
    {
        _srv.EnqueueJson(200, """
        {"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1000,"completion_tokens":3,
                  "prompt_tokens_details":{"cached_tokens":950}}}
        """);

        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Equal(1000, r.Usage.InputTokens);
        Assert.Equal(950, r.Usage.CachedInputTokens);
        Assert.True(r.Usage.CacheReported);
    }

    /// <summary>A server that omits the block leaves CacheReported FALSE — which is what stops the
    /// UI claiming a 0% hit rate for an endpoint that simply never said.</summary>
    [Fact]
    public async Task ChatAsync_WithoutCacheDetails_ReportsNothingRatherThanZero()
    {
        _srv.EnqueueJson(200, """
        {"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1000,"completion_tokens":3}}
        """);

        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.False(r.Usage.CacheReported);
        Assert.Equal(0, r.Usage.CachedInputTokens);
    }

    /// <summary>The STREAMING path carries usage on its own final chunk, and had a byte-identical
    /// copy of the parser — which is how the field came to be missing from both.</summary>
    [Fact]
    public async Task ChatStreamAsync_ReadsCachedPromptTokens()
    {
        _srv.EnqueueSse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":800,\"completion_tokens\":4,"
            + "\"prompt_tokens_details\":{\"cached_tokens\":780}}}\n\n",
            "data: [DONE]\n\n");

        LlmUsage? usage = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
            if (chunk.Usage is not null) usage = chunk.Usage;

        Assert.NotNull(usage);
        Assert.Equal(780, usage!.CachedInputTokens);
        Assert.True(usage.CacheReported);
    }

    /// <summary>
    /// CACHE WRITES, WHERE THEY ARE BILLED. OpenRouter reports cache_write_tokens alongside the
    /// reads; a local llama.cpp reports neither, because filling its own RAM is free. Reading hits
    /// without writes understates cost on exactly the providers that charge for warming — OpenAI at
    /// 1.25x normal input, Anthropic up to 2x — and a number that looks trustworthy while being
    /// wrong is worse than no number.
    /// </summary>
    [Fact]
    public async Task ChatAsync_ReadsCacheWriteTokens()
    {
        _srv.EnqueueJson(200, """
        {"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2000,"completion_tokens":3,
                  "prompt_tokens_details":{"cached_tokens":500,"cache_write_tokens":1500}}}
        """);

        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.Equal(500, r.Usage.CachedInputTokens);
        Assert.Equal(1500, r.Usage.CacheWriteTokens);
        Assert.True(r.Usage.CacheReported);
    }

    /// <summary>
    /// A WRITE WITH NO READ STILL COUNTS AS REPORTED. The first turn against a paid provider warms
    /// the cache and hits nothing — that is a real measurement of an expensive turn, not a silent
    /// provider, and treating it as unreported would hide the cost entirely.
    /// </summary>
    [Fact]
    public async Task ChatAsync_AWriteWithoutAHit_IsStillReported()
    {
        _srv.EnqueueJson(200, """
        {"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2000,"completion_tokens":3,
                  "prompt_tokens_details":{"cache_write_tokens":2000}}}
        """);

        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.True(r.Usage.CacheReported);
        Assert.Equal(2000, r.Usage.CacheWriteTokens);
        Assert.Equal(0, r.Usage.CachedInputTokens);
    }

    /// <summary>A local endpoint sends no cache block at all — writes stay zero and nothing claims
    /// a cost that was never incurred.</summary>
    [Fact]
    public async Task ChatAsync_WithNoCacheBlock_ReportsNoWrites()
    {
        _srv.EnqueueJson(200, """
        {"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1000,"completion_tokens":3}}
        """);

        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);

        Assert.False(r.Usage.CacheReported);
        Assert.Equal(0, r.Usage.CacheWriteTokens);
    }

    [Fact]
    public async Task ChatAsync_MapsTextResponse_AndNormalizesStopReason()
    {
        _srv.EnqueueJson(200, """
        {"choices":[{"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":11,"completion_tokens":3}}
        """);
        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);
        Assert.Equal("hello", r.Text);
        Assert.Equal("end_turn", r.StopReason);   // "stop" -> "end_turn"
        Assert.Equal(11, r.Usage.InputTokens);
        Assert.Equal(3, r.Usage.OutputTokens);
    }

    [Fact]
    public async Task ChatAsync_MapsToolCall_AndNormalizesStopReason()
    {
        _srv.EnqueueJson(200, """
        {"choices":[{"message":{"role":"assistant","tool_calls":[
          {"id":"call_1","type":"function","function":{"name":"create_plan","arguments":"{\"summary\":\"x\"}"}}]},
          "finish_reason":"tool_calls"}]}
        """);
        var r = await Make().ChatAsync(Msgs(), null, CancellationToken.None);
        Assert.Equal("tool_use", r.StopReason);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal("create_plan", call.Name);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("x", call.Arguments.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ChatAsync_SendsSystemMessage_BearerKey_AndTools()
    {
        _srv.EnqueueJson(200, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");
        var tools = new List<ToolDefinition> {
            new("create_plan", "make a plan",
                System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement) };
        await Make().ChatAsync(Msgs(), tools, CancellationToken.None);

        Assert.Contains("\"role\":\"system\"", _srv.LastRequestBody);
        Assert.Contains("\"create_plan\"", _srv.LastRequestBody);
        Assert.Equal("Bearer sk-test", _srv.LastRequestHeaders["Authorization"]);
    }

    [Fact]
    public async Task ChatAsync_MergesExtraHeaders()
    {
        _srv.EnqueueJson(200, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");
        var extra = new Dictionary<string, string> { ["HTTP-Referer"] = "https://cxagent.dev", ["X-Title"] = "cxagent" };
        await Make(extra).ChatAsync(Msgs(), null, CancellationToken.None);
        Assert.Equal("https://cxagent.dev", _srv.LastRequestHeaders["HTTP-Referer"]);
        Assert.Equal("cxagent", _srv.LastRequestHeaders["X-Title"]);
    }

    [Fact]
    public async Task ChatAsync_401_ThrowsLlmProviderException()
    {
        _srv.EnqueueJson(401, """{"error":{"message":"bad key"}}""");
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            Make().ChatAsync(Msgs(), null, CancellationToken.None));
        Assert.Equal(401, ex.HttpStatus);
        Assert.Equal("openai", ex.InstanceName);
    }

    [Fact]
    public void Capabilities_AreSet()
    {
        var p = Make();
        Assert.True(p.SupportsToolCalling);
        Assert.True(p.SupportsStreaming);
        Assert.Equal("openai", p.ProviderId);
    }

    /// <summary>
    /// Regression (found by a live drive against a real llama.cpp endpoint): in OpenAI streaming, a
    /// tool call's `arguments` arrives as INCREMENTAL FRAGMENTS spread across many SSE chunks. Only the
    /// FIRST delta carries `function.name` and `id`; every continuation carries just an `arguments`
    /// slice. The real capture that motivated this test had 103 tool-call deltas for one create_plan
    /// call, the first being `{"name":"create_plan","arguments":"{"}` and the rest single tokens like
    /// `"\"jobs\":"`, `"["`, … `"}"`, terminated by an EMPTY delta carrying finish_reason=tool_calls.
    ///
    /// The old parser called JsonDocument.Parse() on each chunk's fragment (always invalid JSON) AND
    /// required `name` to be present (dropping all 102 continuations). The visible symptom was
    /// "Expected depth to be zero at the end of the JSON payload" and NO goal could ever be planned
    /// against a real streaming provider — the whole feature was dead outside --mock, which returns a
    /// single complete response. Fragments must be accumulated and parsed once, at completion.
    /// </summary>
    [Fact]
    public async Task ChatStream_ToolCallArguments_AreAccumulatedAcrossChunks()
    {
        _srv.EnqueueSse(
            // First delta: name + id + the opening brace only.
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"create_plan\",\"arguments\":\"{\"}}]}}]}\n\n",
            // Continuations: arguments fragments only — NO name, NO id.
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"jobs\\\":[\"}}]}}]}\n\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"id\\\":\\\"w\\\",\"}}]}}]}\n\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"type\\\":\\\"file\\\"}\"}}]}}]}\n\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"]}\"}}]}}]}\n\n",
            // Terminator: empty delta + finish_reason.
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n",
            "data: [DONE]\n\n");

        ToolCall? assembled = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
            if (chunk.ToolCallDelta is not null)
                assembled = chunk.ToolCallDelta;   // last one wins: the completed call

        Assert.NotNull(assembled);
        Assert.Equal("create_plan", assembled!.Name);
        Assert.Equal("call_1", assembled.Id);

        // The whole argument payload must be reassembled and parseable — this is what the old code
        // could never produce.
        var jobs = assembled.Arguments.GetProperty("jobs");
        Assert.Equal(1, jobs.GetArrayLength());
        Assert.Equal("w", jobs[0].GetProperty("id").GetString());
        Assert.Equal("file", jobs[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatStream_ConcatenatedDeltas_EqualNonStreamedText()
    {
        // Non-streamed reference:
        _srv.EnqueueJson(200, """{"choices":[{"message":{"content":"Hello world"},"finish_reason":"stop"}]}""");
        var full = await Make().ChatAsync(Msgs(), null, CancellationToken.None);

        // Streamed: same text delivered as SSE deltas.
        _srv.EnqueueSse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: [DONE]\n\n");

        var sb = new System.Text.StringBuilder();
        string? finalStop = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
        {
            if (chunk.TextDelta is not null) sb.Append(chunk.TextDelta);
            if (chunk.IsFinal) finalStop = "end_turn";
        }
        Assert.Equal(full.Text, sb.ToString());
        Assert.Equal("end_turn", finalStop);
    }

    [Fact]
    public async Task ChatStream_EmitsToolCallDelta()
    {
        _srv.EnqueueSse(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"create_plan\",\"arguments\":\"{}\"}}]}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n",
            "data: [DONE]\n\n");
        ToolCall? seen = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
            if (chunk.ToolCallDelta is not null) seen = chunk.ToolCallDelta;
        Assert.NotNull(seen);
        Assert.Equal("create_plan", seen!.Name);
    }

    [Fact]
    public async Task ChatStream_FinalUsageChunk_IsSurfaced()
    {
        // A usage-only chunk has an EMPTY choices array — the existing parser indexes choices[0]
        // unconditionally and would throw on it.
        _srv.EnqueueSse(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\n\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":5}}\n\n",
            "data: [DONE]\n\n");

        LlmUsage? seen = null;
        await foreach (var chunk in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None))
            if (chunk.Usage is not null) seen = chunk.Usage;

        Assert.NotNull(seen);
        Assert.Equal(11, seen!.InputTokens);
        Assert.Equal(5, seen.OutputTokens);
    }

    [Fact]
    public async Task ChatStream_RequestsUsage_ViaStreamOptions()
    {
        _srv.EnqueueSse("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n", "data: [DONE]\n\n");
        await foreach (var _ in Make().ChatStreamAsync(Msgs(), null, CancellationToken.None)) { }

        // Without this opt-in the provider emits no usage at all — verified against a live endpoint.
        Assert.Contains("\"include_usage\":true", _srv.LastRequestBody!.Replace(" ", ""));
    }

    // --- streaming tool-call accumulation ---------------------------------------------------------

    private static JsonElement Delta(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Accumulator_ReassemblesTWOCallsFromOneTurn()
    {
        // THE BUG. The accumulator held ONE name and ONE argument buffer, so a turn carrying two
        // calls appended the second's argument fragments to the first's buffer and emitted a single
        // call. It survived a long time because nothing drove multi-call turns -- a planning turn
        // returns one create_plan, and a worker rarely batches. The single-agent loop batches
        // constantly, and the symptom was twelve consecutive `read_file {}` calls: a name whose
        // arguments had gone into someone else's buffer.
        var acc = new OpenAiWire.StreamingToolCallAccumulator();

        acc.Accept(Delta("""
            {"tool_calls":[{"index":0,"id":"a","function":{"name":"read_file","arguments":"{\"path\":"}}]}
            """), null);
        acc.Accept(Delta("""
            {"tool_calls":[{"index":1,"id":"b","function":{"name":"read_file","arguments":"{\"path\":"}}]}
            """), null);
        acc.Accept(Delta("""
            {"tool_calls":[{"index":0,"function":{"arguments":"\"one.cs\"}"}}]}
            """), null);
        acc.Accept(Delta("""
            {"tool_calls":[{"index":1,"function":{"arguments":"\"two.cs\"}"}}]}
            """), null);

        var calls = acc.Accept(Delta("{}"), "tool_calls");

        Assert.Equal(2, calls.Count);
        Assert.Equal("one.cs", calls[0].Arguments.GetProperty("path").GetString());
        Assert.Equal("two.cs", calls[1].Arguments.GetProperty("path").GetString());
        Assert.Equal("a", calls[0].Id);
        Assert.Equal("b", calls[1].Id);
    }

    [Fact]
    public void Accumulator_StillHandlesASingleCall_WithNoIndexField()
    {
        // A provider that emits one call and omits `index` must keep working: 0 is the right bucket,
        // and something emitting one call cannot be emitting more.
        var acc = new OpenAiWire.StreamingToolCallAccumulator();

        acc.Accept(Delta("""
            {"tool_calls":[{"id":"x","function":{"name":"read_file","arguments":"{\"path\":\"a.cs\"}"}}]}
            """), null);

        var calls = acc.Accept(Delta("{}"), "stop");

        Assert.Single(calls);
        Assert.Equal("a.cs", calls[0].Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void Accumulator_EmitsNothingBeforeTheTurnFinishes()
    {
        // A partial argument buffer never parses; emitting early is how `{}` reaches the model.
        var acc = new OpenAiWire.StreamingToolCallAccumulator();

        var mid = acc.Accept(Delta("""
            {"tool_calls":[{"index":0,"function":{"name":"read_file","arguments":"{\"pa"}}]}
            """), null);

        Assert.Empty(mid);
    }

    [Fact]
    public void Accumulator_ATruncatedStreamYieldsEmptyArgs_NotAThrow()
    {
        // Unbalanced JSON from a length cap or dropped connection must not throw out of an async
        // iterator and kill the goal mid-stream.
        var acc = new OpenAiWire.StreamingToolCallAccumulator();

        acc.Accept(Delta("""
            {"tool_calls":[{"index":0,"function":{"name":"read_file","arguments":"{\"path\":\"unclo"}}]}
            """), null);

        var calls = acc.Accept(Delta("{}"), "length");

        Assert.Single(calls);
        Assert.Equal(JsonValueKind.Object, calls[0].Arguments.ValueKind);
    }
}
