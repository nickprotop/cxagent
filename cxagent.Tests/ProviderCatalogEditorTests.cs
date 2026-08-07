using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class ProviderCatalogEditorTests
{
    private static ProviderSettings Empty() => new(
        new Dictionary<string, ProviderInstanceConfig>(), null,
        Array.Empty<string>(), new Dictionary<string, RoutingTarget>());

    private static ProviderInstanceConfig Or(string model) =>
        new("openai-compatible", model, "sk-or", "https://openrouter.ai/api/v1", null);

    [Fact]
    public void AddOrReplace_KeepsExistingInstances()
    {
        var s = Empty();
        s = ProviderCatalogEditor.AddOrReplace(s, "openrouter-main", Or("anthropic/claude-sonnet-4-5"), makeDefault: true);
        s = ProviderCatalogEditor.AddOrReplace(s, "openrouter-alt", Or("openai/gpt-4o-mini"), makeDefault: false);
        s = ProviderCatalogEditor.AddOrReplace(s, "local",
            new ProviderInstanceConfig("openai-compatible", "qwen", null, "http://localhost:8771/v1", null), makeDefault: false);

        // Two instances of the SAME kind coexist — the user's stated requirement.
        Assert.Equal(3, s.Providers.Count);
        Assert.Equal("openrouter-main", s.DefaultProvider);
        Assert.Equal("openai/gpt-4o-mini", s.Providers["openrouter-alt"].Model);
    }

    [Fact]
    public void AddOrReplace_SameName_Overwrites_WithoutDuplicating()
    {
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "openrouter-main", Or("a/b"), true);
        s = ProviderCatalogEditor.AddOrReplace(s, "openrouter-main", Or("c/d"), false);
        Assert.Single(s.Providers);
        Assert.Equal("c/d", s.Providers["openrouter-main"].Model);
        Assert.Equal("openrouter-main", s.DefaultProvider);   // makeDefault:false must not clear it
    }

    [Fact]
    public void AddOrReplace_FirstInstance_BecomesDefaultEvenIfNotRequested()
    {
        // Otherwise the very first provider added leaves defaultProvider null and the loader
        // rejects the file the UI just wrote.
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "only", Or("a/b"), makeDefault: false);
        Assert.Equal("only", s.DefaultProvider);
    }

    [Fact]
    public void RemoveInstance_RepointsDefault_WhenDefaultRemoved()
    {
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "a", Or("m"), true);
        s = ProviderCatalogEditor.AddOrReplace(s, "b", Or("m"), false);

        s = ProviderCatalogEditor.RemoveInstance(s, "a");
        Assert.Single(s.Providers);
        Assert.Equal("b", s.DefaultProvider);
    }

    [Fact]
    public void RemoveInstance_LastOne_LeavesNullDefault()
    {
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "a", Or("m"), true);
        s = ProviderCatalogEditor.RemoveInstance(s, "a");
        Assert.Empty(s.Providers);
        Assert.Null(s.DefaultProvider);
    }

    [Fact]
    public void RemoveInstance_RepointsDefault_Deterministically()
    {
        // Ordinal-first, not dictionary enumeration order — so which instance inherits the default is
        // reproducible rather than an accident of insertion.
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "zeta", Or("m"), true);
        s = ProviderCatalogEditor.AddOrReplace(s, "beta", Or("m"), false);
        s = ProviderCatalogEditor.AddOrReplace(s, "alpha", Or("m"), false);

        Assert.Equal("alpha", ProviderCatalogEditor.RemoveInstance(s, "zeta").DefaultProvider);
    }

    [Fact]
    public void DescribeRows_KeepTheInstanceName_EvenWhenItContainsTheSeparator()
    {
        // The editor selects on these rows, so a name containing the " — " display separator must
        // still resolve to the right instance. Parsing the name back out of the line would not.
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "we — ird", Or("m"), true);

        var row = Assert.Single(ProviderCatalogEditor.DescribeRows(s));
        Assert.Equal("we — ird", row.Name);
        Assert.Contains("we — ird", row.Line);
    }

    [Fact]
    public void RemoveInstance_AlsoDropsRoutingEntriesAndAllowedProviders()
    {
        // routing/allowedProviders naming a removed instance fail the loader's validation exactly the
        // same way a role binding does, so they are cleaned up on the same path.
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "a", Or("m"), true);
        s = ProviderCatalogEditor.AddOrReplace(s, "b", Or("m2"), false);
        s = s with
        {
            AllowedProviders = new[] { "a", "b" },
            Routing = new Dictionary<string, RoutingTarget>
            {
                ["cheap"] = new("a", "m"),
                ["smart"] = new("b", "m2"),
            },
        };

        s = ProviderCatalogEditor.RemoveInstance(s, "a");
        Assert.Equal(new[] { "b" }, s.AllowedProviders);
        Assert.False(s.Routing.ContainsKey("cheap"));
        Assert.True(s.Routing.ContainsKey("smart"));
    }

    [Fact]
    public void RemoveInstance_UnknownName_IsANoOp()
    {
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "a", Or("m"), true);
        var after = ProviderCatalogEditor.RemoveInstance(s, "nope");
        Assert.Same(s, after);
    }

    [Fact]
    public void SuggestName_AvoidsCollisions()
    {
        var preset = ProviderKindCatalog.PresetFor("openrouter");
        var s = Empty();
        Assert.Equal("openrouter", ProviderCatalogEditor.SuggestName(s, preset));

        s = ProviderCatalogEditor.AddOrReplace(s, "openrouter", Or("m"), true);
        Assert.Equal("openrouter-2", ProviderCatalogEditor.SuggestName(s, preset));

        s = ProviderCatalogEditor.AddOrReplace(s, "openrouter-2", Or("m"), false);
        Assert.Equal("openrouter-3", ProviderCatalogEditor.SuggestName(s, preset));
    }

    [Fact]
    public void SetDefault_RepointsOnlyWhenTheNameExists()
    {
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "a", Or("m"), true);
        s = ProviderCatalogEditor.AddOrReplace(s, "b", Or("m"), false);

        Assert.Equal("b", ProviderCatalogEditor.SetDefault(s, "b").DefaultProvider);
        Assert.Equal("a", ProviderCatalogEditor.SetDefault(s, "missing").DefaultProvider);
    }

    [Fact]
    public void Describe_UsesThePresetDisplayName_WhenKindAndBaseUrlMatch()
    {
        // Presentational inference only (option (a) of the brief): no schema change, and a miss
        // degrades to showing the raw kind.
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "openrouter-main", Or("a/b"), true);
        s = ProviderCatalogEditor.AddOrReplace(s, "mystery",
            new ProviderInstanceConfig("openai-compatible", "m", "k", "https://example.invalid/v1", null), false);

        var lines = ProviderCatalogEditor.Describe(s);

        Assert.Contains(lines, l => l.Contains("openrouter-main") && l.Contains("OpenRouter") && l.Contains("(default)"));
        Assert.Contains(lines, l => l.Contains("mystery") && l.Contains("openai-compatible"));
    }

    [Fact]
    public void Describe_ListsEveryInstanceIncludingSameKindDuplicates()
    {
        var s = ProviderCatalogEditor.AddOrReplace(Empty(), "openrouter-main", Or("a/b"), true);
        s = ProviderCatalogEditor.AddOrReplace(s, "openrouter-alt", Or("c/d"), false);

        Assert.Equal(2, ProviderCatalogEditor.Describe(s).Count);
    }

    [Fact]
    public void MultiInstanceCatalog_SurvivesAWriteReadRoundTrip()
    {
        // The end-to-end claim of this task: the UI can now produce a catalog the loader accepts with
        // TWO instances of the same kind plus a local one.
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new AppPaths(dir);

            var s = Empty();
            s = ProviderCatalogEditor.AddOrReplace(s, "openrouter-main", Or("anthropic/claude-sonnet-4-5"), true);
            s = ProviderCatalogEditor.AddOrReplace(s, "openrouter-alt", Or("openai/gpt-4o-mini"), false);
            s = ProviderCatalogEditor.AddOrReplace(s, "local",
                new ProviderInstanceConfig("ollama", "llama3.1", null, "http://localhost:11434", null), false);

            ProviderConfigWriter.Write(paths, s);
            var loaded = ProviderConfigLoader.LoadAndValidate(paths, new Dictionary<string, string>());

            Assert.Equal(3, loaded.Providers.Count);
            Assert.Equal("openrouter-main", loaded.DefaultProvider);
            Assert.Equal("openai/gpt-4o-mini", loaded.Providers["openrouter-alt"].Model);
            Assert.Equal("ollama", loaded.Providers["local"].Kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
