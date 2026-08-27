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
    /// The loader no longer refuses a keyless config for any kind — it cannot tell an OpenRouter
    /// endpoint (needs a key) from a llama.cpp server on localhost (needs none) from the kind alone,
    /// so a missing apiKey is not a config-time error and the endpoint's own 401 is the honest
    /// failure instead. This replaces a prior version of this test that asserted the loader's
    /// rejection agreed with ProviderKindCatalog.RequiresApiKey; that agreement is gone because the
    /// rejection it was checking is gone. RequiresApiKey remains, but only as the wizard's prompt
    /// default — see the comment on ProviderKindCatalog.All.
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
