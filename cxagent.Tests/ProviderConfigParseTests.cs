using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Validating a document before it is written: the same rules startup applies, from text.</summary>
public class ProviderConfigParseTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public void Parse_AValidDocument_ReturnsItsSettings()
    {
        var settings = ProviderConfigLoader.Parse("""
            { "providers": { "local": { "kind": "ollama", "model": "llama3.1" } },
              "defaultProvider": "local" }
            """, NoEnv);

        Assert.Equal("local", settings.DefaultProvider);
        Assert.Equal("llama3.1", settings.Providers["local"].Model);
    }

    [Fact]
    public void Parse_ADanglingDefault_ThrowsTheLoadersOwnError()
    {
        var ex = Assert.Throws<ProviderConfigException>(() => ProviderConfigLoader.Parse("""
            { "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "gone" }
            """, NoEnv));

        Assert.Contains(ex.Errors, e => e.Contains("defaultProvider 'gone'"));
    }

    [Fact]
    public void Parse_NotJson_SaysSo() =>
        Assert.Contains("not valid JSON",
            Assert.Throws<ProviderConfigException>(() => ProviderConfigLoader.Parse("{ nope", NoEnv)).Errors[0]);

    // THE DEFAULT CHILD NEEDS NO BRIEFING: where plain sub-agents run, how long and with what tools is
    // configurable on its own — a briefing would be prefixed to every spawn that names no type.
    [Fact]
    public void General_WithoutABriefing_IsKept()
    {
        var settings = ProviderConfigLoader.Parse("""
            { "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local",
              "agents": { "general": { "provider": "local", "maxTurns": 9 } } }
            """, NoEnv);

        Assert.Equal("local", settings.AgentTypes["general"].Provider);
        Assert.Equal(9, settings.AgentTypes["general"].MaxTurns);
        Assert.DoesNotContain(settings.Warnings, w => w.Contains("general"));
    }

    /// <summary>
    /// PROMPT CACHING DEFAULTS TO WHERE IT PAYS: on for Anthropic and OpenRouter, which cache nothing unless
    /// asked and repay the write from the second request; off everywhere else, where a local server caches
    /// on its own and a plain endpoint ignores the marker or refuses it. An explicit value always wins.
    /// </summary>
    [Theory]
    [InlineData("anthropic", null, null, true)]
    [InlineData("openai-compatible", "https://openrouter.ai/api/v1", null, true)]
    [InlineData("openai-compatible", "http://localhost:8080/v1", null, false)]
    [InlineData("ollama", null, null, false)]
    [InlineData("anthropic", null, "false", false)]
    [InlineData("ollama", null, "true", true)]
    public void PromptCaching_DefaultsToWhereItPays_AndAnExplicitValueWins(string kind, string? baseUrl, string? written, bool expected)
    {
        var url = baseUrl is null ? "" : $", \"baseUrl\": \"{baseUrl}\"";
        var cache = written is null ? "" : $", \"cacheControl\": {written}";
        var settings = ProviderConfigLoader.Parse(
            $$"""{ "providers": { "p": { "kind": "{{kind}}", "model": "m", "apiKey": "k"{{url}}{{cache}} } }, "defaultProvider": "p" }""",
            NoEnv);

        Assert.Equal(expected, settings.Providers["p"].CacheControl);
    }
}
