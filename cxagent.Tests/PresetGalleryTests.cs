using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Adding a provider starts from a card: the preset fills what it knows, and a free name.</summary>
public class PresetGalleryTests
{
    [Fact]
    public void SixPresets_InGalleryOrder() =>
        Assert.Equal(["anthropic", "openrouter", "openai", "ollama", "llamacpp", "openai-compatible"],
            ProviderKindCatalog.Presets.Select(p => p.Id));

    [Fact]
    public void EveryPreset_HasALineForItsCard() =>
        Assert.All(ProviderKindCatalog.Presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Blurb)));

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("llamacpp", "http://localhost:8080/v1")]
    [InlineData("ollama", "http://localhost:11434")]
    public void APreset_PrefillsItsEndpoint(string id, string url) =>
        Assert.Equal(url, PresetGallery.DraftFor(ProviderKindCatalog.PresetFor(id), id).BaseUrl);

    [Fact]
    public void OpenRouter_KeepsItsAttributionHeader() =>
        Assert.Equal("cxagent", PresetGallery.DraftFor(ProviderKindCatalog.PresetFor("openrouter"), "or").ExtraHeaders!["X-Title"]);

    [Fact]
    public void SuggestName_SkipsTakenNames() =>
        Assert.Equal("ollama-2", PresetGallery.SuggestName(["ollama"], ProviderKindCatalog.PresetFor("ollama")));

    // A NEW INSTANCE WRITES EVERYTHING IT HAS, its kind included — there is no file entry to differ from.
    [Fact]
    public void ANewDraft_WritesItsKind_AndWhatThePresetFilled()
    {
        var draft = PresetGallery.DraftFor(ProviderKindCatalog.PresetFor("openrouter"), "or");
        draft.Model = "anthropic/claude-sonnet-5-5";

        var fields = draft.ToFields();

        Assert.Equal("openai-compatible", fields.Kind);
        Assert.Equal("https://openrouter.ai/api/v1", fields.BaseUrl);
        Assert.Equal("anthropic/claude-sonnet-5-5", fields.Model);
        Assert.NotNull(fields.ExtraHeaders);
    }

    /// <summary>
    /// A NEW PROVIDER STARTS WHERE ITS KIND DEFAULTS, so the box shows what will happen, and unticking it is
    /// written: absent would load as the default, which is on for Anthropic and OpenRouter.
    /// </summary>
    [Theory]
    [InlineData("anthropic", true)]
    [InlineData("openrouter", true)]
    [InlineData("ollama", false)]
    [InlineData("llamacpp", false)]
    public void ANewProvider_StartsWithItsKindsCacheDefault(string id, bool expected) =>
        Assert.Equal(expected, PresetGallery.DraftFor(ProviderKindCatalog.PresetFor(id), id).CacheControl);

    [Fact]
    public void UntickingCachingOnANewAnthropic_IsWritten()
    {
        var draft = PresetGallery.DraftFor(ProviderKindCatalog.PresetFor("anthropic"), "claude");
        Assert.Null(draft.ToFields().CacheControl);
        draft.CacheControl = false;
        Assert.Equal(false, draft.ToFields().CacheControl);
    }
}
