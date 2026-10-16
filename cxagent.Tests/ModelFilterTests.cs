using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class ModelFilterTests
{
    private static readonly string[] Catalog =
    {
        "anthropic/claude-sonnet-4-5", "anthropic/claude-opus-4-1", "openai/gpt-4o-mini",
        "meta-llama/llama-3.1-70b", "qwen/qwen3-35b-a3b", "google/gemma-3-27b",
    };

    [Fact]
    public void Apply_WithNoQuery_ReturnsAllUpToLimit()
    {
        Assert.Equal(Catalog.Length, ModelFilter.Apply(Catalog, null).Count);
        Assert.Equal(2, ModelFilter.Apply(Catalog, null, limit: 2).Count);
    }

    [Fact]
    public void Apply_MatchesSubstring_CaseInsensitively()
    {
        var r = ModelFilter.Apply(Catalog, "CLAUDE");
        Assert.Equal(2, r.Count);
        Assert.All(r, m => Assert.Contains("claude", m, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_MatchesOnVendorPrefix()
    {
        var r = ModelFilter.Apply(Catalog, "qwen/");
        Assert.Single(r);
        Assert.Equal("qwen/qwen3-35b-a3b", r[0]);
    }

    [Fact]
    public void Apply_NoMatch_ReturnsEmptyNotAll()
    {
        // An empty result must NOT silently degrade to the full list — that would hand the user a
        // 400-entry list at the moment they were trying to narrow it.
        Assert.Empty(ModelFilter.Apply(Catalog, "zzzz"));
    }

    [Fact]
    public void Apply_WhitespaceQuery_TreatedAsNoQuery()
    {
        // Distinct from Apply_WithNoQuery: this pins that a whitespace query is NORMALISED to
        // "no query" rather than matched literally (which would return zero rows).
        Assert.Equal(Catalog.Length, ModelFilter.Apply(Catalog, "   ").Count);
        Assert.NotEmpty(ModelFilter.Apply(Catalog, "   "));
    }
}
