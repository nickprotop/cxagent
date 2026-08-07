using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class ModelCatalogTests : IDisposable
{
    private readonly LoopbackServer _srv = new();
    public void Dispose() => _srv.Dispose();

    [Fact]
    public async Task OpenAiCompatible_ListsModels_FromDataArray()
    {
        _srv.EnqueueJson(200, """{"object":"list","data":[{"id":"gpt-4o-mini"},{"id":"gpt-4o"}]}""");
        var p = new OpenAiCompatibleProvider("p", "d", "gpt-4o-mini",
            baseUrl: _srv.BaseUrl.TrimEnd('/'), apiKey: null, extraHeaders: null,
            retryPolicy: RetryPolicy.NoDelay);

        var models = await ((IModelCatalog)p).ListModelsAsync(default);

        Assert.Equal(new[] { "gpt-4o-mini", "gpt-4o" }, models);
    }

    [Fact]
    public async Task Ollama_ListsModels_FromTagsArray_NotTheOpenAiModelsRoute()
    {
        // OllamaProvider SUBCLASSES OpenAiCompatibleProvider, so this also proves the override
        // took: an inherited /models implementation would not parse this {"models":[{"name"...}]}
        // payload, and the request path must be /api/tags.
        _srv.EnqueueJson(200, """{"models":[{"name":"llama3.1:latest"},{"name":"qwen2.5"}]}""");
        var p = new OllamaProvider("p", "d", "llama3.1",
            baseUrl: _srv.BaseUrl.TrimEnd('/'), retryPolicy: RetryPolicy.NoDelay);

        var models = await ((IModelCatalog)p).ListModelsAsync(default);

        Assert.Equal(new[] { "llama3.1:latest", "qwen2.5" }, models);
    }

    [Fact]
    public async Task Anthropic_ListsModels_FromDataArray()
    {
        _srv.EnqueueJson(200, """{"data":[{"id":"claude-sonnet-4-5"},{"id":"claude-opus-4-1"}]}""");
        var p = new AnthropicProvider("p", "d", "claude-sonnet-4-5", "sk-test",
            baseUrl: _srv.BaseUrl.TrimEnd('/'), retryPolicy: RetryPolicy.NoDelay);

        var models = await ((IModelCatalog)p).ListModelsAsync(default);

        Assert.Contains("claude-sonnet-4-5", models);
    }

    [Fact]
    public async Task ListModels_OnHttpFailure_ReturnsEmpty_NeverThrows()
    {
        _srv.EnqueueJson(500, "nope");
        var p = new OllamaProvider("p", "d", "m",
            baseUrl: _srv.BaseUrl.TrimEnd('/'), retryPolicy: RetryPolicy.NoDelay);

        var models = await ((IModelCatalog)p).ListModelsAsync(default);

        Assert.Empty(models);   // the wizard degrades to free-text entry; it must not crash setup
    }
}
