using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using Xunit;

namespace CxAgent.Tests;

public class ProviderRegistryTests
{
    private static ProviderSettings Settings(string? def, params (string name, ProviderInstanceConfig cfg)[] provs)
        => new(provs.ToDictionary(p => p.name, p => p.cfg), def,
            Array.Empty<string>(), new Dictionary<string, RoutingTarget>());

    [Fact]
    public void Build_ConstructsEachKind_ByFactory()
    {
        var s = Settings("claude",
            ("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-ant", null, null)),
            ("oai", new ProviderInstanceConfig("openai-compatible", "gpt", "sk", "https://api.openai.com/v1", null)),
            ("local", new ProviderInstanceConfig("ollama", "llama3.3", null, null, null)));
        var reg = ProviderRegistry.Build(s);

        Assert.True(reg.TryGet("claude", out var c));
        Assert.IsType<AnthropicProvider>(c);
        Assert.True(reg.TryGet("oai", out var o));
        Assert.IsType<OpenAiCompatibleProvider>(o);
        Assert.True(reg.TryGet("local", out var l));
        Assert.IsType<OllamaProvider>(l);
    }

    [Fact]
    public void Default_ReturnsConfiguredDefaultInstance()
    {
        var s = Settings("claude",
            ("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-ant", null, null)));
        var reg = ProviderRegistry.Build(s);
        Assert.Equal("claude", reg.Default.ProviderId);
    }

    [Fact]
    public void Default_Throws_WhenDefaultProviderUnset()
    {
        var s = Settings(null,
            ("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-ant", null, null)));
        var reg = ProviderRegistry.Build(s);
        Assert.Throws<InvalidOperationException>(() => _ = reg.Default);
    }

    [Fact]
    public void TryGetDefault_ReturnsFalse_WhenDefaultProviderUnset()
    {
        // The non-throwing counterpart to Default, for callers that must degrade rather than fail.
        var s = Settings(null,
            ("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-ant", null, null)));
        Assert.False(ProviderRegistry.Build(s).TryGetDefault(out _));
    }

    [Fact]
    public void TryGetDefault_ReturnsTheDefault_WhenConfigured()
    {
        var s = Settings("claude",
            ("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-ant", null, null)));
        Assert.True(ProviderRegistry.Build(s).TryGetDefault(out var p));
        Assert.Equal("claude", p.ProviderId);
    }

    [Fact]
    public void TryGet_UnknownInstance_ReturnsFalse()
    {
        var s = Settings("claude",
            ("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-ant", null, null)));
        var reg = ProviderRegistry.Build(s);
        Assert.False(reg.TryGet("nope", out _));
    }

    [Fact]
    public void Build_TwoInstancesOfSameKind_HaveDistinctProviderIds()
    {
        var s = Settings("primary",
            ("primary", new ProviderInstanceConfig("openai-compatible", "gpt", "sk1", "https://a.example/v1", null)),
            ("secondary", new ProviderInstanceConfig("openai-compatible", "gpt", "sk2", "https://b.example/v1", null)));
        var reg = ProviderRegistry.Build(s);

        Assert.True(reg.TryGet("primary", out var p));
        Assert.True(reg.TryGet("secondary", out var q));
        Assert.Equal("primary", p.ProviderId);
        Assert.Equal("secondary", q.ProviderId);
        Assert.NotEqual(p.ProviderId, q.ProviderId);
    }
}
