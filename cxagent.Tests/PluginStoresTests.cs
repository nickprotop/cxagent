using System.Text.Json;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class PluginStoresTests
{
    [Theory]
    [InlineData("acme", "https://acme.example/catalog.json", null)]
    [InlineData("acme-2", "http://localhost:8765/catalog.json", null)]
    [InlineData("acme", "http://127.0.0.1/catalog.json", null)]
    [InlineData("Acme", "https://a/c.json", "a store name is lowercase letters, digits and '-', 1-32 characters")]
    [InlineData("a/b", "https://a/c.json", "a store name is lowercase letters, digits and '-', 1-32 characters")]
    [InlineData("cxagent", "https://a/c.json", "'cxagent' is the official catalog's name")]
    [InlineData("acme", "http://acme.example/c.json", "a store's URL must be https (or http://localhost for testing)")]
    [InlineData("acme", "ftp://x/c.json", "a store's URL must be https (or http://localhost for testing)")]
    public void ANameAndUrl_AreCheckedTheSameWayEverywhere(string name, string url, string? problem) =>
        Assert.Equal(problem, PluginStores.Problem(name, url));

    [Fact]
    public void TheStoresBlock_IsRead_AndADuplicateIsAnError()
    {
        using var doc = JsonDocument.Parse("""
            { "stores": [ { "name": "acme", "url": "https://a/c.json" },
                          { "name": "acme", "url": "https://b/c.json" },
                          { "name": "Bad", "url": "https://c/c.json" } ] }
            """);
        var errors = new List<string>();
        var stores = PluginStores.Read(doc.RootElement, errors);
        Assert.Equal([new PluginStore("acme", "https://a/c.json")], stores);
        Assert.Contains("stores: 'acme' is listed twice.", errors);
        Assert.Contains(errors, e => e.StartsWith("stores.Bad: a store name is", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("acme/repo-map", true, "acme", "repo-map")]
    [InlineData("repo-map", false, "", "")]
    [InlineData("acme/", false, "", "")]
    [InlineData("/repo-map", false, "", "")]
    [InlineData("a/b/c", false, "", "")]
    [InlineData("acme/..", false, "", "")]
    [InlineData("../x", false, "", "")]
    [InlineData("Acme/x", false, "", "")]
    [InlineData("acme/a.b", false, "", "")]
    public void AQualifiedName_SplitsIntoStoreAndName(string typed, bool ok, string store, string name)
    {
        Assert.Equal(ok, PluginStores.TrySplit(typed, out var s, out var n));
        if (ok) { Assert.Equal(store, s); Assert.Equal(name, n); }
    }

    [Fact]
    public void ProviderConfig_ReadsStores_AndRefusesABadOne()
    {
        var ok = CxAgent.Core.Llm.ProviderConfigLoader.Parse("""
            { "providers": { "local": { "kind": "ollama", "model": "llama3.1" } },
              "defaultProvider": "local",
              "stores": [ { "name": "acme", "url": "https://a/c.json" } ] }
            """, new Dictionary<string, string>());
        Assert.Equal([new PluginStore("acme", "https://a/c.json")], ok.Stores);

        Assert.Throws<CxAgent.Core.Llm.ProviderConfigException>(() => CxAgent.Core.Llm.ProviderConfigLoader.Parse("""
            { "providers": { "local": { "kind": "ollama", "model": "llama3.1" } },
              "defaultProvider": "local",
              "stores": [ { "name": "cxagent", "url": "https://a/c.json" } ] }
            """, new Dictionary<string, string>()));
    }

    /// <summary>
    /// A CONFIG KEY WITH A '/' IS A store/name OR NOTHING. <c>Acme/repo-map</c> and <c>a/b/c</c> were
    /// searched by their bare file and loaded the official copy under a key that looks like a store's;
    /// <c>cxagent/repo-map</c> is a second spelling of <c>repo-map</c>, refused rather than read as it
    /// because every writer of config (F2's enable, remove, add) keys on the plain name and would miss it.
    /// </summary>
    [Theory]
    [InlineData("Acme/repo-map", "plugins.Acme/repo-map: a plugin name is 'name' or 'store/name'.")]
    [InlineData("a/b/c", "plugins.a/b/c: a plugin name is 'name' or 'store/name'.")]
    [InlineData("acme/", "plugins.acme/: a plugin name is 'name' or 'store/name'.")]
    [InlineData("cxagent/repo-map", "plugins.cxagent/repo-map: an official plugin is configured by its plain name — write 'repo-map'.")]
    public void ProviderConfig_RefusesAPluginKeyThatIsNeitherANameNorAStoreName(string key, string error)
    {
        var thrown = Assert.Throws<CxAgent.Core.Llm.ProviderConfigException>(() => CxAgent.Core.Llm.ProviderConfigLoader.Parse($$"""
            { "providers": { "local": { "kind": "ollama", "model": "llama3.1" } },
              "defaultProvider": "local",
              "plugins": { "{{key}}": { "file": "repo-map.dll" } } }
            """, new Dictionary<string, string>()));
        Assert.Contains(error, thrown.Errors);
    }

    [Theory]
    [InlineData("repo-map")]
    [InlineData("acme/repo-map")]
    public void ProviderConfig_AcceptsANameOrAStoreName(string key)
    {
        var ok = CxAgent.Core.Llm.ProviderConfigLoader.Parse($$"""
            { "providers": { "local": { "kind": "ollama", "model": "llama3.1" } },
              "defaultProvider": "local",
              "plugins": { "{{key}}": { "file": "repo-map.dll" } } }
            """, new Dictionary<string, string>());
        Assert.True(ok.Plugins.ContainsKey(key));
    }
}
