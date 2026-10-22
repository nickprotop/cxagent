using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/plugin store add | remove | list</c> — the stores list in the user's config, edited by command,
/// with a store's catalog read once before it is saved.
/// </summary>
public sealed class PluginStoreCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("store-cmd-").FullName;
    private string Config => Path.Combine(_dir, "config.json");

    public PluginStoreCommandTests() => File.WriteAllText(Config, """{ "theme": "keep-me" }""");
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HttpClient Http(params (string Url, string Body)[] served)
    {
        var handler = new CatalogSetTests.ByUrl();
        foreach (var (url, body) in served) handler[url] = body;
        return new HttpClient(handler);
    }

    private Task<string> Run(string args, HttpClient? http = null) =>
        PluginStoreCommand.RunAsync(new StoreCommandRequest(args, Config, _dir, http ?? Http()), default);

    [Fact]
    public async Task Add_ReadsTheCatalogOnce_ThenWritesTheStore()
    {
        var reply = await Run("store add acme https://a/c.json",
            Http(("https://a/c.json", CatalogSetTests.Catalog("jira-search", "deploy-check"))));

        Assert.Equal("store 'acme' added — 2 plugins: jira-search, deploy-check.", reply);
        Assert.Equal([new PluginStore("acme", "https://a/c.json")], PluginStores.ReadFile(Config));
        Assert.Contains("keep-me", File.ReadAllText(Config));
    }

    [Fact]
    public async Task Add_RefusesAnUnreadableCatalog_AndWritesNothing()
    {
        var reply = await Run("store add acme https://a/c.json");

        Assert.StartsWith("store 'acme' was not added: ", reply);
        Assert.Empty(PluginStores.ReadFile(Config));
    }

    [Theory]
    [InlineData("store add cxagent https://a/c.json", "store 'cxagent' was not added: 'cxagent' is the official catalog's name.")]
    [InlineData("store add acme http://a/c.json", "store 'acme' was not added: a store's URL must be https (or http://localhost for testing).")]
    [InlineData("store add acme", StoreCommandText.Usage)]
    [InlineData("store frob", StoreCommandText.Usage)]
    [InlineData("store", StoreCommandText.Usage)]
    public async Task BadInput_IsRefusedWithItsReason(string args, string reply) =>
        Assert.Equal(reply, await Run(args));

    [Fact]
    public async Task Add_RefusesADuplicate_RemoveRemoves_ListLists()
    {
        PluginConfigWriter.SetStores(Config, [new PluginStore("acme", "https://a/c.json")]);

        Assert.Equal("store 'acme' is already added (https://a/c.json).", await Run("store add acme https://b/c.json"));
        Assert.StartsWith("acme — https://a/c.json", await Run("store list"));
        Assert.Equal("store 'acme' removed. Its installed plugins stay installed and stop getting updates.",
            await Run("store remove acme"));
        Assert.Empty(PluginStores.ReadFile(Config));
        Assert.DoesNotContain("stores", File.ReadAllText(Config));
        Assert.Equal("no store named 'acme'.", await Run("store remove acme"));
        Assert.Equal("no stores — /plugin store add <name> <url> adds one.", await Run("store list"));
    }

    [Fact]
    public async Task List_SaysWhatEachStoresLastReadHeld()
    {
        await Run("store add acme https://a/c.json", Http(("https://a/c.json", CatalogSetTests.Catalog("jira-search"))));
        PluginConfigWriter.SetStores(Config,
            [new PluginStore("acme", "https://a/c.json"), new PluginStore("beta", "https://b/c.json")]);

        var list = await Run("store list");

        Assert.Contains("acme — https://a/c.json — 1 plugin, read ", list);
        Assert.Contains("beta — https://b/c.json — not read yet", list);
    }
}
