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

    private async Task<string> Run(string args, HttpClient? http = null) =>
        (await Reply(args, http)).Text;

    private Task<StoreReply> Reply(string args, HttpClient? http = null) =>
        PluginStoreCommand.RunAsync(new StoreCommandRequest(args, Config, _dir, http ?? Http()), default);

    /// <summary>
    /// A REFUSAL IS SAID BY THE COMMAND, NOT GUESSED FROM ITS WORDS: "no stores — /plugin store add …" is
    /// a plain answer that begins like the refusal "no store named …", and matching on text showed it as
    /// a warning.
    /// </summary>
    [Fact]
    public async Task EachReply_SaysWhetherItIsARefusal()
    {
        Assert.False((await Reply("store list")).Refused);
        Assert.True((await Reply("store remove acme")).Refused);
        Assert.True((await Reply("store frob")).Refused);
        Assert.True((await Reply("store add cxagent https://a/c.json")).Refused);
        Assert.True((await Reply("store add acme https://a/c.json")).Refused);
        Assert.False((await Reply("store add acme https://a/c.json", Http(("https://a/c.json", CatalogSetTests.Catalog("jira"))))).Refused);
        Assert.True((await Reply("store add acme https://a/c.json")).Refused);
        Assert.False((await Reply("store remove acme")).Refused);
    }

    /// <summary>
    /// A FIELD cxagent DOES NOT KNOW ON A STORE SURVIVES AN EDIT OF THE LIST: config.json is hand-edited,
    /// and the store record keeps room for more than a name and a URL.
    /// </summary>
    [Fact]
    public async Task AddingAndRemovingAStore_KeepsTheOtherStoresOwnFields()
    {
        File.WriteAllText(Config, """
            { "stores": [ { "name": "acme", "url": "https://a/c.json", "token": "keep-me-too" },
                          { "name": "beta", "url": "https://b/c.json" } ] }
            """);

        await Run("store add gamma https://g/c.json", Http(("https://g/c.json", CatalogSetTests.Catalog("jira"))));
        Assert.Contains("keep-me-too", File.ReadAllText(Config));
        await Run("store remove beta");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Config));
        var acme = doc.RootElement.GetProperty("stores")[0];
        Assert.Equal("keep-me-too", acme.GetProperty("token").GetString());
        Assert.Equal(["acme", "gamma"], PluginStores.ReadFile(Config).Select(s => s.Name));
    }

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

    /// <summary>
    /// A STORE'S PLUGINS GO INTO plugins/&lt;store&gt;/, the folder an official plugin of that name has,
    /// so a name already taken there — by an installed plugin's folder, in either plugins folder, or by
    /// a plugin the official catalog lists — is refused before anything is read or written.
    /// </summary>
    [Theory]
    [InlineData("global")]
    [InlineData("project")]
    public async Task Add_RefusesANameAnInstalledPluginsFolderHas(string scope)
    {
        var project = Path.Combine(_dir, "repo");
        var plugins = scope == "global" ? PluginResolver.GlobalFolder(_dir) : PluginResolver.ProjectFolder(project);
        Directory.CreateDirectory(Path.Combine(plugins, "repo-map"));
        File.WriteAllText(Path.Combine(plugins, "repo-map", "repo-map.plugin.json"), "{}");

        var reply = (await PluginStoreCommand.RunAsync(new StoreCommandRequest(
            "store add repo-map https://a/c.json", Config, _dir,
            Http(("https://a/c.json", CatalogSetTests.Catalog("jira-search")))) { ProjectDirectory = project }, default)).Text;

        Assert.StartsWith("store 'repo-map' was not added: ", reply);
        Assert.Contains(Path.Combine(plugins, "repo-map"), reply);
        Assert.Contains("another name", reply);
        Assert.Empty(PluginStores.ReadFile(Config));
    }

    /// <summary>A store folder already marked is that store's own, from an earlier add: adding it again is fine.</summary>
    [Fact]
    public async Task Add_AcceptsANameWhoseFolderIsAlreadyThatStores()
    {
        var store = Path.Combine(PluginResolver.GlobalFolder(_dir), "acme");
        Directory.CreateDirectory(Path.Combine(store, "jira-search"));
        File.WriteAllText(Path.Combine(store, PluginStores.Marker), "");
        File.WriteAllText(Path.Combine(store, "jira-search", "jira-search.plugin.json"), "{}");

        var reply = await Run("store add acme https://a/c.json",
            Http(("https://a/c.json", CatalogSetTests.Catalog("jira-search"))));

        Assert.StartsWith("store 'acme' added", reply);
    }

    /// <summary>The official catalog's last good copy is what is asked: no second request just to check a name.</summary>
    [Fact]
    public async Task Add_RefusesANameTheOfficialCatalogLists()
    {
        File.WriteAllText(CatalogSources.OfficialCache(_dir), CatalogSetTests.Catalog("repo-map"));

        var reply = await Run("store add repo-map https://a/c.json",
            Http(("https://a/c.json", CatalogSetTests.Catalog("jira-search"))));

        Assert.StartsWith("store 'repo-map' was not added: ", reply);
        Assert.Contains("official catalog", reply);
        Assert.Contains("another name", reply);
        Assert.Empty(PluginStores.ReadFile(Config));
    }
}
