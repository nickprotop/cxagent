using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Where a child runs: the type's listed providers, the first by default, the session's when it lists none.</summary>
public class AgentTypeProviderTests
{
    internal static ProviderRegistry Registry() => ProviderRegistry.FromProviders(
        new Dictionary<string, ILlmProvider>(StringComparer.Ordinal)
        {
            ["local"] = new MockLlmProvider("qwen-coder"),
            ["deepseek"] = new MockLlmProvider("deepseek/deepseek-v3.2"),
            ["opus"] = new MockLlmProvider("claude-opus-5-5"),
        },
        "local",
        new Dictionary<string, int?>(StringComparer.Ordinal) { ["deepseek"] = 163_840 },
        new Dictionary<string, string?>(StringComparer.Ordinal) { ["deepseek"] = "cheap, paid" });

    private static AgentType Type(AgentTypeConfig cfg) =>
        new AgentTypeCatalog(new Dictionary<string, AgentTypeConfig>(StringComparer.Ordinal) { ["scout"] = cfg }, Registry())
            .Resolve("scout")!;

    private static AgentType Listing(params string[] names) =>
        Type(new AgentTypeConfig("Search.", names[0]) { Providers = names });

    [Fact]
    public void NoList_RunsOnTheSessions_AndOffersNoChoice()
    {
        var t = Type(new AgentTypeConfig("Search."));

        Assert.Same(TypeRouting.Inherited, t.Pick(null).Routing);
        Assert.Equal("scout offers no choice of provider; omit 'provider'.", t.Pick("local").Refusal);
    }

    [Fact]
    public void NamingNone_RunsOnTheFirstListed()
    {
        var pick = Listing("deepseek", "local").Pick(null);

        Assert.Equal("deepseek", pick.Routing!.InstanceName);
        Assert.Equal(163_840, pick.Routing.ContextWindow);       // the window travels with the entry
        Assert.Equal("cheap, paid", pick.Routing.Description);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData("local:qwen-coder")]
    [InlineData(" local : QWEN-CODER ")]
    public void NamingAListedOne_RunsThere(string requested) =>
        Assert.Equal("local", Listing("deepseek", "local").Pick(requested).Routing!.InstanceName);

    [Fact]
    public void Pick_ModelIdsContainingSlashes_Match() =>
        Assert.Equal("deepseek", Listing("local", "deepseek").Pick("deepseek:deepseek/deepseek-v3.2").Routing!.InstanceName);

    [Theory]
    [InlineData("opus")]                       // configured, but not this type's
    [InlineData("deepseek:old-model")]         // the right name, a stale model
    [InlineData("loc")]                        // no prefix matching
    public void NamingAnythingElse_IsRefused_NamingTheChoices(string requested) =>
        Assert.Equal($"scout does not run on '{requested}'. Its providers: local:qwen-coder, deepseek:deepseek/deepseek-v3.2.",
            Listing("local", "deepseek").Pick(requested).Refusal);

    [Fact]
    public void AnExactCaseMatch_WinsOverACaseInsensitiveOne()
    {
        var registry = ProviderRegistry.FromProviders(new Dictionary<string, ILlmProvider>(StringComparer.Ordinal)
        {
            ["Local"] = new MockLlmProvider("a"), ["local"] = new MockLlmProvider("b"),
        }, "local", null);
        var t = new AgentTypeCatalog(new Dictionary<string, AgentTypeConfig>(StringComparer.Ordinal)
        {
            ["scout"] = new("Search.", "Local") { Providers = ["Local", "local"] },
        }, registry).Resolve("scout")!;

        Assert.Equal("local", t.Pick("local").Routing!.InstanceName);
    }

    [Fact]
    public void ADisabledType_RefusesEveryPick()
    {
        var t = Type(new AgentTypeConfig("Search.") { Disabled = "provider 'typo' is not a configured provider" });

        Assert.Equal("scout is disabled: provider 'typo' is not a configured provider.", t.Pick(null).Refusal);
    }

    [Fact]
    public void ANameTheRegistryLacks_DisablesTheType() =>
        Assert.Equal("provider 'ghost' is not a configured provider",
            Type(new AgentTypeConfig("Search.", "ghost") { Providers = ["ghost"] }).Disabled);

    [Fact]
    public void ALabel_IsNameColonModel() =>
        Assert.Equal("deepseek:deepseek/deepseek-v3.2", Listing("deepseek").Offers[0].Label);
}
