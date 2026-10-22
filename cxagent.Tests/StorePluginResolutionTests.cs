using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;
using static CxAgent.Core.Plugins.PluginResolver;

namespace CxAgent.Tests;

public sealed class StorePluginResolutionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("store-resolve-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Place(string relativeFolder)
    {
        var dir = Path.Combine(_root, relativeFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "repo-map.dll"), "x");
        File.WriteAllText(Path.Combine(dir, "repo-map.plugin.json"),
            """{"pluginContract":2,"name":"repo-map","version":"1.0.0","tools":[]}""");
        return dir;
    }

    private void MarkStore(string store) => File.WriteAllText(Path.Combine(_root, store, PluginStores.Marker), "");

    [Fact]
    public void AStorePlugin_IsFoundByItsQualifiedName_AndItsLoadSetIsItsOwnFolder()
    {
        var home = Place("acme/repo-map"); MarkStore("acme");
        Assert.Equal(home, PluginResolver.FindStoreLoadSet(new StorePlugin("acme", "repo-map"), "repo-map.dll", [_root]));
        var result = PluginResolver.Resolve("acme/repo-map", new Dictionary<string, PluginConfig>(), [_root], _root);
        var found = Assert.IsType<ResolveResult.Found>(result);
        Assert.Equal(home, found.LoadSetDirectory);
    }

    [Fact]
    public void ABareFileName_NeverReachesAStoreFolder()
    {
        var official = Place("repo-map");
        Place("acme/repo-map"); MarkStore("acme");
        var configured = new Dictionary<string, PluginConfig>
        {
            ["repo-map"] = new("repo-map.dll"),
            ["acme/repo-map"] = new("repo-map.dll"),
        };
        Assert.Equal(official, PluginDiscovery.HomeOf("repo-map", configured["repo-map"], [_root]));
        Assert.Equal(Path.Combine(_root, "acme", "repo-map"), PluginDiscovery.HomeOf("acme/repo-map", configured["acme/repo-map"], [_root]));
        var bare = Assert.IsType<ResolveResult.Found>(PluginResolver.Resolve("repo-map.dll", new Dictionary<string, PluginConfig>(), [_root], _root));
        Assert.StartsWith(official, bare.AssemblyPath);
    }

    [Fact]
    public void AnInstalledStorePlugin_NotInConfig_IsFoundUnderItsQualifiedName()
    {
        Place("acme/repo-map"); MarkStore("acme");
        var found = PluginDiscovery.FindUnconfigured(new Dictionary<string, PluginConfig>(), [_root]);
        Assert.Equal("acme/repo-map", Assert.Single(found).Name);
    }

    [Fact]
    public void AFolderWithoutTheMarker_IsNotAStore()
    {
        Place("notastore/repo-map");
        Assert.Empty(PluginDiscovery.FindUnconfigured(new Dictionary<string, PluginConfig>(), [_root]));
    }

    [Fact]
    public void ADotDotSegment_IsNeverAStorePlugin()
    {
        Place("acme/repo-map"); MarkStore("acme");
        Assert.IsNotType<ResolveResult.Found>(
            PluginResolver.Resolve("acme/..", new Dictionary<string, PluginConfig>(), [_root], _root));
    }

    [Fact]
    public void AStorePlugin_IsReportedUnderItsFolderName_NotItsManifestName()
    {
        var home = Place("acme/repo-map"); MarkStore("acme");
        File.WriteAllText(Path.Combine(home, "repo-map.plugin.json"),
            """{"pluginContract":2,"name":"repomap","version":"1.0.0","tools":[]}""");
        var found = PluginDiscovery.FindUnconfigured(new Dictionary<string, PluginConfig>(), [_root]);
        Assert.Equal("acme/repo-map", Assert.Single(found).Name);
    }

    /// <summary>
    /// A STORE FOLDER WHOSE NAMES ARE NOT store/name IS LISTED, AS NEEDING ATTENTION — never dropped, and
    /// never offered "Add to config": no config key can reach it (a bare name never looks inside a store's
    /// folder, and its folders are not a store/name), so an entry written for it would be one startup
    /// reports missing. The reason says which rename makes it reachable; Uninstall still removes it.
    /// </summary>
    [Theory]
    [InlineData("Acme", "repo-map")]
    [InlineData("acme", "repo.map")]
    public void AStorePluginWhoseFoldersAreNotAName_NeedsAttention_WithTheRename(string store, string folder)
    {
        var home = Place($"{store}/{folder}"); MarkStore(store);
        var found = Assert.Single(PluginDiscovery.FindUnconfigured(new Dictionary<string, PluginConfig>(), [_root]));
        Assert.Equal(home, found.Folder);
        Assert.NotNull(found.Problem);

        var rows = PluginManagerRows.Build(PluginManagerState.Gather(new GatherInputs(
            new Dictionary<string, PluginConfig>(), [], [_root], [], _root, _root)));
        var row = Assert.Single(rows);
        Assert.Equal(PluginRowSection.NeedsAttention, row.Section);
        Assert.Contains("rename", row.State);
        Assert.Contains($"{store}/{folder}", row.State);
    }

    [Fact]
    public void AMarkedStoreFolder_TopLevel_IsNotSearchedAsAPluginFolder()
    {
        Place("acme"); MarkStore("acme");   // a loose repo-map.dll directly in the store folder
        Assert.Null(PluginDiscovery.FindLoadSetDirectory("repo-map.dll", [_root]));
        Assert.IsNotType<ResolveResult.Found>(
            PluginResolver.Resolve("repo-map.dll", new Dictionary<string, PluginConfig>(), [_root], _root));
        Assert.Empty(PluginDiscovery.FindUnconfigured(new Dictionary<string, PluginConfig>(), [_root]));
    }

    [Fact]
    public void AStorePluginWithTwoSidecars_IsNeitherResolvedNorListed()
    {
        var home = Place("acme/repo-map"); MarkStore("acme");
        File.WriteAllText(Path.Combine(home, "other.dll"), "x");
        File.WriteAllText(Path.Combine(home, "other.plugin.json"),
            """{"pluginContract":2,"name":"other","version":"1.0.0","tools":[]}""");
        Assert.IsNotType<ResolveResult.Found>(
            PluginResolver.Resolve("acme/repo-map", new Dictionary<string, PluginConfig>(), [_root], _root));
        Assert.Empty(PluginDiscovery.FindUnconfigured(new Dictionary<string, PluginConfig>(), [_root]));
    }

    /// <summary>
    /// A NAME WITH A '/' THAT IS NOT A store/name NEVER FALLS BACK TO THE BARE SEARCH, which would find
    /// the official copy and run it under a key that looks like a store's. Config refuses such a key;
    /// this holds for one that arrives some other way (an embedder's own config).
    /// </summary>
    [Theory]
    [InlineData("Acme/repo-map")]
    [InlineData("a/b/c")]
    public void AMalformedSlashName_FindsNothing_NotTheOfficialCopy(string key)
    {
        Place("repo-map");
        var configured = new Dictionary<string, PluginConfig> { [key] = new("repo-map.dll") };
        Assert.Null(PluginDiscovery.HomeOf(key, configured[key], [_root]));
        Assert.IsNotType<ResolveResult.Found>(PluginResolver.Resolve(key, configured, [_root], _root));
    }

    [Fact]
    public void TheOfficialStoreName_ResolvesAsTheBareName()
    {
        var official = Place("repo-map");
        var found = Assert.IsType<ResolveResult.Found>(PluginResolver.Resolve("cxagent/repo-map",
            new Dictionary<string, PluginConfig> { ["repo-map"] = new("repo-map.dll") }, [_root], _root));
        Assert.Equal(official, found.LoadSetDirectory);
    }
}
