using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What an endpoint says about its models, for the picker: the ids, and what it knows about each —
/// and, unlike the bare id list, WHY it said nothing when it could not be asked.
/// </summary>
public class ModelCatalogDetailTests : IDisposable
{
    private readonly LoopbackServer _srv = new();
    public void Dispose() => _srv.Dispose();

    private OpenAiCompatibleProvider OpenAi() => new(new OpenAiProviderOptions
    {
        ProviderId = "openai", DisplayName = "x", Model = "m", BaseUrl = _srv.BaseUrl,
        ApiKey = "sk-test", Retry = RetryPolicy.NoDelay,
    });

    private AnthropicProvider Anthropic() =>
        new("anthropic", "Claude x", "claude-x", "sk-ant-test", maxTokens: 1024,
            baseUrl: _srv.BaseUrl.TrimEnd('/'), retryPolicy: RetryPolicy.NoDelay);

    [Fact]
    public async Task OpenRouter_GivesEachModelsWindow_AndPricePerMillion()
    {
        _srv.Route("/models", 200, """
            {"data":[{"id":"anthropic/claude-sonnet-5-5","context_length":1000000,
                      "pricing":{"prompt":"0.000003","completion":"0.000015"}},
                     {"id":"plain/model"}]}
            """);

        var result = await OpenAi().ListModelDetailsAsync(CancellationToken.None);

        Assert.Null(result.Error);
        var sonnet = result.Models.Single(m => m.Id == "anthropic/claude-sonnet-5-5");
        Assert.Equal(1_000_000, sonnet.ContextWindow);
        Assert.Equal("$3/$15 per M", sonnet.Price);
        Assert.Null(result.Models.Single(m => m.Id == "plain/model").ContextWindow);
    }

    // THE REASON, NOT AN EMPTY LIST: an endpoint that refuses the key and one that serves nothing must
    // not look alike to the dialog checking whether a save will work.
    [Fact]
    public async Task ARefusal_IsAnErrorWithTheStatus()
    {
        _srv.Route("/models", 401, """{"error":"bad key"}""");
        var result = await OpenAi().ListModelDetailsAsync(CancellationToken.None);
        Assert.Empty(result.Models);
        Assert.Contains("401", result.Error);
    }

    [Fact]
    public async Task Anthropic_GivesEachModelsInputWindow()
    {
        _srv.Route("/models", 200, """
            {"data":[{"id":"claude-opus-5-5","max_input_tokens":1000000},{"id":"claude-haiku-5-5"}]}
            """);

        var result = await Anthropic().ListModelDetailsAsync(CancellationToken.None);

        Assert.Equal(1_000_000, result.Models.Single(m => m.Id == "claude-opus-5-5").ContextWindow);
        Assert.Equal(2, result.Models.Count);
    }

    [Fact]
    public async Task TheBareList_IsTheDetailedListsIds()
    {
        _srv.Route("/models", 200, """{"data":[{"id":"a"},{"id":"b"}]}""");
        Assert.Equal(["a", "b"], await OpenAi().ListModelsAsync(CancellationToken.None));
    }
}
