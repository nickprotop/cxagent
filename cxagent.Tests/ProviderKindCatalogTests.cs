using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class ProviderKindCatalogTests
{
    /// <summary>
    /// The loader refuses a keyless config for no kind — it cannot tell an OpenRouter endpoint (needs
    /// a key) from a llama.cpp server on localhost (needs none) from the kind alone, so a missing
    /// apiKey is not a config-time error and the endpoint's own 401 is the honest failure instead.
    /// </summary>
    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai-compatible")]
    [InlineData("ollama")]
    public void LoaderAcceptsAKeylessConfig_ForEveryKind(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-pkc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A config for this kind with NO apiKey. baseUrl is supplied so that the endpoint is
            // otherwise structurally complete.
            File.WriteAllText(Path.Combine(dir, "config.json"), $$"""
            {
              "providers": {
                "p": { "kind": "{{kind}}", "model": "m", "baseUrl": "http://localhost:1234" }
              },
              "defaultProvider": "p"
            }
            """);

            var paths = new CxAgent.Core.Storage.AppPaths(dir);
            var settings = ProviderConfigLoader.LoadAndValidate(paths, new Dictionary<string, string>());
            Assert.Null(settings.Providers["p"].ApiKey);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Presets_CoverEveryKind_AndAddOpenRouter()
    {
        var presets = ProviderKindCatalog.Presets;

        // Every kind stays reachable — a preset list that dropped one would make that driver
        // unconfigurable from the Configuration dialog.
        foreach (var kind in ProviderConfigLoader.KnownKinds)
            Assert.Contains(presets, p => p.Kind == kind);

        var or = presets.Single(p => p.Id == "openrouter");
        Assert.Equal("openai-compatible", or.Kind);          // NOT a new kind
        Assert.Equal("https://openrouter.ai/api/v1", or.BaseUrl);
        Assert.NotNull(or.ExtraHeaders);
        Assert.True(or.ExtraHeaders!.ContainsKey("X-Title"));
    }

    [Fact]
    public void EveryPreset_HasANameAndABlurb() =>
        Assert.All(ProviderKindCatalog.Presets, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(p.Blurb));
        });

    [Fact]
    public void Presets_DoNotIntroduceUnknownKinds()
    {
        Assert.All(ProviderKindCatalog.Presets,
            p => Assert.Contains(p.Kind, ProviderConfigLoader.KnownKinds));
    }
}
