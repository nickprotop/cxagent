using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Removing a provider: refused while a tab is on it, and otherwise says what becomes of each reference.</summary>
public class ProviderRemovalPlanTests
{
    private static ProviderSettings Settings() => new(
        new Dictionary<string, ProviderInstanceConfig>
        {
            ["a"] = new("ollama", "m", null, "http://x", null),
            ["b"] = new("ollama", "m", null, "http://x", null),
        }, "a", [], new Dictionary<string, RoutingTarget>())
    {
        Classifier = "a",
        AgentTypes = new Dictionary<string, AgentTypeConfig> { ["planner"] = new(Provider: "a") },
    };

    [Fact]
    public void InUse_IsRefused_NamingTheTabs()
    {
        var plan = ProviderRemovalPlan.For(Settings(), "a", ["shop"]);
        Assert.True(plan.Refused);
        Assert.Contains(plan.Lines, l => l.Contains("shop"));
    }

    [Fact]
    public void Unused_ListsEveryReference()
    {
        var plan = ProviderRemovalPlan.For(Settings(), "a", []);
        Assert.False(plan.Refused);
        Assert.Contains(plan.Lines, l => l.Contains("passes to 'b'"));
        Assert.Contains(plan.Lines, l => l.Contains("reviewer"));
        Assert.Contains(plan.Lines, l => l.Contains("planner"));
    }

    [Fact]
    public void ThePlainCase_SaysNothingElseNamesIt() =>
        Assert.Contains("Nothing else", Assert.Single(ProviderRemovalPlan.For(Settings(), "b", []).Lines));
}
