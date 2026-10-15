using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class ThinkingCapabilitiesTests
{
    private const string Opus55 = """
        {"id":"claude-opus-5-5","max_input_tokens":1000000,"capabilities":{
          "effort":{"supported":true,"low":{"supported":true},"medium":{"supported":true},
                    "high":{"supported":true},"xhigh":{"supported":true},"max":{"supported":true}},
          "thinking":{"supported":true,"types":{"adaptive":{"supported":true},
                    "disabled":{"supported":false},"enabled":{"supported":false}}}}}
        """;

    [Fact]
    public void Anthropic_AdaptiveModel_OffersItsEffortLevels_WithoutOffWhenDisabledIsRefused() =>
        Assert.Equal(["low", "medium", "high", "xhigh", "max"],
            ThinkingCapabilities.ParseAnthropic(Opus55)!.Levels);

    [Fact]
    public void Anthropic_DisabledAccepted_OffersOffFirst() =>
        Assert.Equal("off", ThinkingCapabilities.ParseAnthropic(
            Opus55.Replace("\"disabled\":{\"supported\":false}", "\"disabled\":{\"supported\":true}"))!.Levels[0]);

    [Fact]
    public void Anthropic_NoAdaptive_OffersNothing_AndSaysWhy()
    {
        var o = ThinkingCapabilities.ParseAnthropic(
            Opus55.Replace("\"adaptive\":{\"supported\":true}", "\"adaptive\":{\"supported\":false}"))!;
        Assert.False(o.Any);
        Assert.Contains("adaptive", o.Why);
    }

    [Fact]
    public void Anthropic_NoCapabilitiesObject_IsUnknown_NotEmpty() =>
        Assert.Null(ThinkingCapabilities.ParseAnthropic("""{"id":"x","max_input_tokens":1}"""));

    [Fact]
    public async Task Cache_AsksAgain_WhenTheProbeGaveNoAnswerAtAll()
    {
        var cache = new CapabilityCache();
        var calls = 0;
        Task<ThinkingOptions?> Throws(CancellationToken _) { calls++; throw new HttpRequestException("down"); }
        Assert.False((await cache.GetAsync(Throws, default)).Any);
        Assert.False((await cache.GetAsync(Throws, default)).Any);
        Assert.Equal(2, calls);
        Assert.Null(cache.Known);
    }

    [Fact]
    public async Task Cache_RemembersADefinitiveNone()
    {
        var cache = new CapabilityCache();
        var calls = 0;
        var none = ThinkingOptions.None("no route");
        Task<ThinkingOptions?> Replies(CancellationToken _) { calls++; return Task.FromResult<ThinkingOptions?>(none); }
        await cache.GetAsync(Replies, default);
        await cache.GetAsync(Replies, default);
        Assert.Equal(1, calls);
        Assert.Same(none, cache.Known);
    }

    [Fact]
    public void LlamaProps_TemplateWithEnableThinking_OffersOffAndOn() =>
        Assert.Equal(["off", "on"], ThinkingCapabilities.ParseLlamaProps(
            """{"chat_template":"{% if enable_thinking %}<think>{% endif %}"}""")!.Levels);

    [Fact]
    public void LlamaProps_TemplateWithout_OffersNothing() =>
        Assert.False(ThinkingCapabilities.ParseLlamaProps("""{"chat_template":"plain"}""")!.Any);

    // --- OpenRouter: its model list says, per model, what reasoning it takes ---------------------

    private static string OpenRouterList(string reasoning) =>
        $$$"""{"data":[{"id":"other/model"},{"id":"vendor/m","reasoning":{{{reasoning}}}}]}""";

    [Fact]
    public void OpenRouter_EffortsAndMandatory_OffersTheEffortsOnly() =>
        Assert.Equal(["minimal", "low", "medium", "high"], ThinkingCapabilities.ParseOpenRouterModels(
            OpenRouterList("""{"mandatory":true,"supported_efforts":["high","medium","low","minimal"]}"""), "vendor/m")!.Levels);

    [Fact]
    public void OpenRouter_EffortsNotMandatory_OffersOffFirst() =>
        Assert.Equal(["off", "low", "high"], ThinkingCapabilities.ParseOpenRouterModels(
            OpenRouterList("""{"mandatory":false,"supported_efforts":["high","low"]}"""), "vendor/m")!.Levels);

    [Fact]
    public void OpenRouter_NoEffortsNotMandatory_OffersOffAndOn() =>
        Assert.Equal(["off", "on"], ThinkingCapabilities.ParseOpenRouterModels(
            OpenRouterList("""{"mandatory":false,"default_enabled":false}"""), "vendor/m")!.Levels);

    [Fact]
    public void OpenRouter_MandatoryWithoutEfforts_OffersNothing_AndSaysWhy()
    {
        var o = ThinkingCapabilities.ParseOpenRouterModels(OpenRouterList("""{"mandatory":true}"""), "vendor/m")!;
        Assert.False(o.Any);
        Assert.Contains("always reasons", o.Why);
    }

    [Fact]
    public void OpenRouter_AModelWithoutReasoning_OffersNothing() =>
        Assert.False(ThinkingCapabilities.ParseOpenRouterModels(OpenRouterList("null"), "other/model")!.Any);

    [Fact]
    public void OpenRouter_AModelNotListed_OffersNothing() =>
        Assert.False(ThinkingCapabilities.ParseOpenRouterModels(OpenRouterList("null"), "nobody/x")!.Any);

    // --- which dialect an endpoint speaks, from its host ------------------------------------------

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", ReasoningDialect.OpenRouter)]
    [InlineData("https://api.openai.com/v1", ReasoningDialect.OpenAi)]
    [InlineData("http://localhost:8771/v1", ReasoningDialect.LlamaCpp)]
    [InlineData("https://my-gateway.example/v1", ReasoningDialect.LlamaCpp)]
    public void TheDialect_ComesFromTheHost(string baseUrl, ReasoningDialect dialect) =>
        Assert.Equal(dialect, ReasoningDialects.For(baseUrl));
}
