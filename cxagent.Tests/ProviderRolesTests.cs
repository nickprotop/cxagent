using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The Roles block: both roles visible at once, and "none" a choice the reviewer can be.</summary>
public class ProviderRolesTests
{
    private static ProviderSettings Settings(string? def, string? reviewer, params string[] names) => new(
        names.ToDictionary(n => n, n => new ProviderInstanceConfig("ollama", "m", null, null, null)),
        def, [], new Dictionary<string, RoutingTarget>()) { Classifier = reviewer };

    [Fact]
    public void TheDefault_ListsEveryInstance_AndPointsAtTheDefault()
    {
        var s = Settings("local", null, "local", "claude");
        Assert.Equal(["claude", "local"], ProviderRoles.DefaultChoices(s));
        Assert.Equal(1, ProviderRoles.DefaultIndex(s));
    }

    [Fact]
    public void TheReviewer_OffersNoneFirst_AndPointsAtNoneWhenUnset()
    {
        var s = Settings("local", null, "local", "claude");
        Assert.Equal([ProviderRoles.NoReviewer, "claude", "local"], ProviderRoles.ReviewerChoices(s));
        Assert.Equal(0, ProviderRoles.ReviewerIndex(s));
    }

    [Fact]
    public void TheReviewer_PointsAtItsInstance()
    {
        var s = Settings("local", "claude", "local", "claude");
        Assert.Equal(1, ProviderRoles.ReviewerIndex(s));
    }

    [Fact]
    public void ChoosingNone_ClearsTheReviewer() =>
        Assert.Null(ProviderRoles.ReviewerFrom(ProviderRoles.NoReviewer));

    [Fact]
    public void ChoosingAnInstance_NamesIt() =>
        Assert.Equal("claude", ProviderRoles.ReviewerFrom("claude"));

    [Fact]
    public void NoProviders_GiveAnEmptyDefault_AndOnlyNoneForTheReviewer()
    {
        var s = Settings(null, null);
        Assert.Empty(ProviderRoles.DefaultChoices(s));
        Assert.Equal(-1, ProviderRoles.DefaultIndex(s));
        Assert.Equal([ProviderRoles.NoReviewer], ProviderRoles.ReviewerChoices(s));
        Assert.Equal(0, ProviderRoles.ReviewerIndex(s));
    }

    [Fact]
    public void AReviewerNamingAMissingInstance_ShowsAsNone()
    {
        var s = Settings("local", "gone", "local");
        Assert.Equal(0, ProviderRoles.ReviewerIndex(s));
    }
}
