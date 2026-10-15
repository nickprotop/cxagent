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
}
