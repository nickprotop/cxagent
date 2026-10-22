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
}
