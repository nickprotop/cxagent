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
}
