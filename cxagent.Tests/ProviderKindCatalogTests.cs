using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class ProviderKindCatalogTests
{
    [Fact]
    public void All_CoversExactly_TheLoadersKnownKinds()
    {
        var catalogKinds = ProviderKindCatalog.All.Select(k => k.Kind).OrderBy(x => x);
        var loaderKinds = ProviderConfigLoader.KnownKinds.OrderBy(x => x);
        Assert.Equal(loaderKinds, catalogKinds);
    }

    [Fact]
    public void Ollama_IsKeyless_ButNeedsBaseUrl()
    {
        var o = ProviderKindCatalog.All.Single(k => k.Kind == "ollama");
        Assert.False(o.RequiresApiKey);
        Assert.True(o.RequiresBaseUrl);
    }

    [Fact]
    public void Anthropic_NeedsKey_ButNoBaseUrl()
    {
        var a = ProviderKindCatalog.All.Single(k => k.Kind == "anthropic");
        Assert.True(a.RequiresApiKey);
        Assert.False(a.RequiresBaseUrl);
    }

    /// <summary>
    /// The catalog's RequiresApiKey must agree with ProviderConfigLoader's validation, which requires
    /// an apiKey for every KnownKind except those in its private KeylessKinds set ({"ollama"}).
    /// A catalog that says "no key needed" for a kind the loader demands one from makes the wizard
    /// skip the prompt and then write a config that fails to load — setup that produces a broken
    /// install. Asserted by round-tripping a key-less config through the real loader.
    /// </summary>
    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai-compatible")]
    [InlineData("ollama")]
    public void RequiresApiKey_AgreesWithLoaderValidation(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-pkc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A config for this kind with NO apiKey. baseUrl is supplied so that only the
            // key rule can fail the load.
            File.WriteAllText(Path.Combine(dir, "config.json"), $$"""
            {
              "providers": {
                "p": { "kind": "{{kind}}", "model": "m", "baseUrl": "http://localhost:1234" }
              },
              "defaultProvider": "p"
            }
            """);

            var paths = new CxAgent.Core.Storage.AppPaths(dir);
            bool loaderRejectedIt;
            try
            {
                ProviderConfigLoader.LoadAndValidate(paths, new Dictionary<string, string>());
                loaderRejectedIt = false;
            }
            catch (ProviderConfigException)
            {
                loaderRejectedIt = true;
            }

            var catalogSaysKeyNeeded = ProviderKindCatalog.For(kind).RequiresApiKey;
            Assert.Equal(loaderRejectedIt, catalogSaysKeyNeeded);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EveryKind_HasDisplayNameAndModelHint()
    {
        Assert.All(ProviderKindCatalog.All, k =>
        {
            Assert.False(string.IsNullOrWhiteSpace(k.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(k.ModelHint));
        });
    }

    [Fact]
    public void Presets_CoverEveryKind_AndAddOpenRouter()
    {
        var presets = ProviderKindCatalog.Presets;

        // Every kind stays reachable — a preset list that dropped one would make that driver
        // unconfigurable from the wizard.
        foreach (var kind in ProviderConfigLoader.KnownKinds)
            Assert.Contains(presets, p => p.Kind == kind);

        var or = presets.Single(p => p.Id == "openrouter");
        Assert.Equal("openai-compatible", or.Kind);          // NOT a new kind
        Assert.Equal("https://openrouter.ai/api/v1", or.BaseUrl);
        Assert.NotNull(or.ExtraHeaders);
        Assert.True(or.ExtraHeaders!.ContainsKey("X-Title"));
    }

    [Fact]
    public void Presets_DoNotIntroduceUnknownKinds()
    {
        Assert.All(ProviderKindCatalog.Presets,
            p => Assert.Contains(p.Kind, ProviderConfigLoader.KnownKinds));
    }
}
