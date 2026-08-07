using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class SetupWizardTests
{
    [Fact]
    public void BuildSettings_ProducesLoadableSettings_ForKeyedProvider()
    {
        var state = new WizardState
        {
            InstanceName = "claude", Kind = "anthropic",
            ApiKey = "sk-abc", BaseUrl = null, Model = "claude-sonnet-4-5",
        };

        var s = SetupWizard.BuildSettings(state);

        Assert.Equal("claude", s.DefaultProvider);
        var cfg = s.Providers["claude"];
        Assert.Equal("anthropic", cfg.Kind);
        Assert.Equal("claude-sonnet-4-5", cfg.Model);
        Assert.Equal("sk-abc", cfg.ApiKey);
        Assert.Null(cfg.BaseUrl);
    }

    [Fact]
    public void BuildSettings_KeylessProvider_HasNullApiKey_AndKeepsBaseUrl()
    {
        var state = new WizardState
        {
            InstanceName = "local", Kind = "ollama",
            ApiKey = null, BaseUrl = "http://localhost:11434", Model = "llama3.1",
        };

        var cfg = SetupWizard.BuildSettings(state).Providers["local"];

        Assert.Null(cfg.ApiKey);
        Assert.Equal("http://localhost:11434", cfg.BaseUrl);
    }

    [Fact]
    public void BuildSettings_Output_IsAcceptedByProviderRegistry()
    {
        // The wizard's whole job is producing settings the rest of the app can consume.
        var state = new WizardState
        {
            InstanceName = "local", Kind = "ollama",
            BaseUrl = "http://localhost:11434", Model = "llama3.1",
        };

        var registry = ProviderRegistry.Build(SetupWizard.BuildSettings(state));

        Assert.Contains("local", registry.InstanceNames);
        Assert.NotNull(registry.Default);
    }

    [Fact]
    public void BuildSettings_WithExisting_AppendsRatherThanReplaces()
    {
        var existing = ProviderCatalogEditor.AddOrReplace(
            new ProviderSettings(new Dictionary<string, ProviderInstanceConfig>(), null,
                Array.Empty<string>(), new Dictionary<string, RoutingTarget>()),
            "local", new ProviderInstanceConfig("ollama", "llama3.1", null, "http://localhost:11434", null), true);

        var state = new WizardState
        {
            InstanceName = "openrouter-main", Kind = "openai-compatible",
            ApiKey = "sk-or", BaseUrl = "https://openrouter.ai/api/v1", Model = "anthropic/claude-sonnet-4-5",
        };

        var result = SetupWizard.BuildSettings(state, existing);
        Assert.Equal(2, result.Providers.Count);
        Assert.True(result.Providers.ContainsKey("local"));
        Assert.True(result.Providers.ContainsKey("openrouter-main"));
    }

    [Fact]
    public void BuildSettings_WithExisting_DoesNotStealTheDefault()
    {
        // F5 adding a second provider must leave the user's chosen default alone; silently repointing
        // it would change which provider every unbound role runs on.
        var existing = new ProviderSettings(
            new Dictionary<string, ProviderInstanceConfig>
            {
                ["local"] = new("ollama", "llama3.1", null, "http://localhost:11434", null),
            },
            "local", Array.Empty<string>(), new Dictionary<string, RoutingTarget>());

        var state = new WizardState
        {
            InstanceName = "or", Kind = "openai-compatible",
            ApiKey = "k", BaseUrl = "https://openrouter.ai/api/v1", Model = "a/b",
        };

        Assert.Equal("local", SetupWizard.BuildSettings(state, existing).DefaultProvider);
    }

    [Fact]
    public void BuildSettings_MatchingPreset_CarriesItsExtraHeaders()
    {
        // OpenRouter's attribution headers are part of the preset, not the kind, so they can only be
        // recovered by matching kind + baseUrl.
        var state = new WizardState
        {
            InstanceName = "openrouter-main", Kind = "openai-compatible",
            ApiKey = "sk-or", BaseUrl = "https://openrouter.ai/api/v1", Model = "a/b",
        };

        var headers = SetupWizard.BuildSettings(state).Providers["openrouter-main"].ExtraHeaders;
        Assert.NotNull(headers);
        Assert.Equal("cxagent", headers!["X-Title"]);
    }

}
