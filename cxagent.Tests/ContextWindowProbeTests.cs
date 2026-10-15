using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class ContextWindowProbeTests : IDisposable
{
    private readonly LoopbackServer _srv = new() { AnswerProbesWith404 = false };
    public void Dispose() => _srv.Dispose();

    [Theory]
    [InlineData("""{"id":"claude-sonnet-5-5","max_input_tokens":1000000,"max_tokens":128000}""", 1_000_000)]
    [InlineData("""{"id":"claude-sonnet-5-5"}""", null)]
    [InlineData("""{"max_input_tokens":0}""", null)]
    [InlineData("not json", null)]
    public void ParseAnthropicContextWindow_ReadsMaxInputTokens(string json, int? expected) =>
        Assert.Equal(expected, ContextWindowProbe.ParseAnthropicContextWindow(json));

    /// <summary>
    /// AN ANTHROPIC INSTANCE IS ASKED, with its own auth header. It used to be skipped for having no
    /// baseUrl, and a 1M-token model was compacted at the 40,000-token fallback.
    /// </summary>
    [Fact]
    public async Task Anthropic_AsksTheModelsApi_WithItsKey()
    {
        _srv.EnqueueJson(200, """{"id":"claude-sonnet-5-5","max_input_tokens":1000000,"max_tokens":128000}""");

        var window = await ContextWindowProbe.TryGetAsync(
            new ProbeTarget("anthropic", _srv.BaseUrl.TrimEnd('/'), "claude-sonnet-5-5", "sk-ant-test"));

        Assert.Equal(1_000_000, window);
        Assert.Equal("sk-ant-test", _srv.LastRequestHeaders["x-api-key"]);
        Assert.True(_srv.LastRequestHeaders.ContainsKey("anthropic-version"));
    }

    [Fact]
    public async Task Anthropic_RefusedRequest_IsUnknown()
    {
        _srv.EnqueueJson(401, """{"error":{"message":"bad key"}}""");

        Assert.Null(await ContextWindowProbe.TryGetAsync(
            new ProbeTarget("anthropic", _srv.BaseUrl.TrimEnd('/'), "claude-sonnet-5-5", "sk-ant-bad")));
    }

    /// <summary>Every other kind keeps the llama.cpp path: no baseUrl, no request.</summary>
    [Fact]
    public async Task OpenAiCompatible_WithoutBaseUrl_IsUnknown() =>
        Assert.Null(await ContextWindowProbe.TryGetAsync(new ProbeTarget("openai-compatible", null, "m", "k")));

    [Fact]
    public async Task OpenAiCompatible_ReadsLlamaCppNCtx()
    {
        _srv.EnqueueJson(200, """{"data":[{"id":"local","meta":{"n_ctx":212992}}]}""");

        Assert.Equal(212_992, await ContextWindowProbe.TryGetAsync(
            new ProbeTarget("openai-compatible", _srv.BaseUrl.TrimEnd('/'), "local", null)));
    }
}
