using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The Providers rail: one row per instance, with what marks it and who is using it.</summary>
public class ProviderRowsTests
{
    private static ProviderSettings Settings() => new(
        new Dictionary<string, ProviderInstanceConfig>
        {
            ["local"] = new("ollama", "qwen", null, "http://localhost:11434", null),
            ["claude"] = new("anthropic", "claude-opus-5-5", "k", null, null),
        }, "local", [], new Dictionary<string, RoutingTarget>()) { Classifier = "claude" };

    [Fact]
    public void Rows_AreByName_AndCarryTheMarkers_AndTheTabCount()
    {
        var rows = ProviderRows.For(Settings(), new Dictionary<string, int> { ["local"] = 2 }, unsaved: null);

        Assert.Equal(["claude", "local"], rows.Select(r => r.Name));
        Assert.True(rows.Single(r => r.Name == "local").IsDefault);
        Assert.Equal(2, rows.Single(r => r.Name == "local").Tabs);
        Assert.True(rows.Single(r => r.Name == "claude").IsReviewer);
        Assert.StartsWith("Ollama", rows.Single(r => r.Name == "local").Preset);
    }

    [Fact]
    public void TheUnsavedRow_IsMarked() =>
        Assert.True(ProviderRows.For(Settings(), new Dictionary<string, int>(), "claude")
            .Single(r => r.Name == "claude").Unsaved);
}
