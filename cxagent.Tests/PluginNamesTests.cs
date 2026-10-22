using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>What a typed plugin name means — one row per row of the spec's Names table.</summary>
public class PluginNamesTests
{
    private static CatalogEntry Entry(string name, string? store = null) =>
        new(name, name, "1.0.0", "", "", "", "", "", name + ".dll", "managed", 2, [], null, null, null, null) { Store = store };

    private static readonly IReadOnlyList<CatalogEntry> Plugins = [Entry("repo-map"), Entry("repo-map", "acme")];

    [Theory]
    [InlineData("repo-map", "repo-map", null)]
    [InlineData("REPO-MAP", "repo-map", null)]
    [InlineData("cxagent/repo-map", "repo-map", null)]
    [InlineData("acme/repo-map", "acme/repo-map", null)]
    [InlineData("acme/", null, "a plugin name is `name` or `store/name`.")]
    [InlineData("/repo-map", null, "a plugin name is `name` or `store/name`.")]
    [InlineData("x/y/z", null, "a plugin name is `name` or `store/name`.")]
    [InlineData("nosuch/repo-map", null, "no store named nosuch — /plugin store list")]
    [InlineData("acme/missing", null, "acme does not list missing.")]
    [InlineData("missing", null, "no plugin named 'missing' in the catalog.")]
    public void ATypedName_MeansWhatTheNamesTableSays(string typed, string? found, string? refused)
    {
        var lookup = PluginNames.Find(typed, Plugins, [new PluginStore("acme", "https://a/c.json")]);
        if (found is not null) Assert.Equal(found, Assert.IsType<NameLookup.Found>(lookup).Entry.QualifiedName);
        else Assert.Equal(refused, Assert.IsType<NameLookup.Refused>(lookup).Message);
    }

    [Fact]
    public void ABareName_NeverReachesAStoresPlugin()
    {
        var lookup = PluginNames.Find("jira", [Entry("jira", "acme")], [new PluginStore("acme", "https://a/c.json")]);
        Assert.IsType<NameLookup.Refused>(lookup);
    }
}
