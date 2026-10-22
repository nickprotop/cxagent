using System.Net;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The official catalog and every store's, read together: each store's entries named by their store,
/// each cached on its own, and one store's failure costing only that store's plugins.
/// </summary>
public sealed class CatalogSetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("catalog-set-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Serves a body per URL; anything else is a 404.</summary>
    internal sealed class ByUrl : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
        public string this[string url] { set => _bodies[url] = value; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_bodies.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    internal static string Catalog(params string[] names) =>
        "{\"schemaVersion\":2,\"plugins\":["
        + string.Join(",", names.Select(n =>
            $$"""{"name":"{{n}}","version":"1.0.0","kind":"managed","file":"{{n}}.dll","source":{"kind":"url","url":"https://x/{{n}}.zip","sha256":"{{new string('a', 64)}}"},"compatibility":{"pluginContract":2,"platforms":["any"]},"tools":[]}"""))
        + "]}";

    [Fact]
    public async Task EveryCatalog_IsRead_AndAStoresEntriesCarryItsName()
    {
        var handler = new ByUrl { ["https://o/c.json"] = Catalog("repo-map"), ["https://a/c.json"] = Catalog("repo-map", "jira") };
        var set = await CatalogSources.ReadAllAsync(new CatalogSetRequest(
            [new PluginStore("acme", "https://a/c.json")], _dir, "https://o/c.json", new HttpClient(handler)), default);

        Assert.Equal(["repo-map", "acme/repo-map", "acme/jira"], set.Plugins.Select(p => p.QualifiedName));
        Assert.Empty(set.Notes);
        Assert.True(File.Exists(Path.Combine(_dir, "catalog-cache-acme.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "catalog-cache.json")));
    }

    [Fact]
    public async Task AStoreThatFails_LeavesTheOthersAndSaysWhich()
    {
        var handler = new ByUrl { ["https://o/c.json"] = Catalog("repo-map") };
        var set = await CatalogSources.ReadAllAsync(new CatalogSetRequest(
            [new PluginStore("acme", "https://a/c.json")], _dir, "https://o/c.json", new HttpClient(handler)), default);

        Assert.Equal(["repo-map"], set.Plugins.Select(p => p.QualifiedName));
        Assert.StartsWith("store acme: ", Assert.Single(set.Notes));
        Assert.Null(set.OfficialError);
    }

    [Fact]
    public async Task AStoreThatFails_StillListsItsCachedPlugins()
    {
        var handler = new ByUrl { ["https://o/c.json"] = Catalog("repo-map"), ["https://a/c.json"] = Catalog("jira") };
        var stores = new[] { new PluginStore("acme", "https://a/c.json") };
        await CatalogSources.ReadAllAsync(new CatalogSetRequest(stores, _dir, "https://o/c.json", new HttpClient(handler)), default);

        var offline = new ByUrl { ["https://o/c.json"] = Catalog("repo-map") };
        var set = await CatalogSources.ReadAllAsync(new CatalogSetRequest(stores, _dir, "https://o/c.json", new HttpClient(offline)), default);

        Assert.Contains("acme/jira", set.Plugins.Select(p => p.QualifiedName));
        Assert.Single(set.Notes);
    }

    /// <summary>Answers one URL never — a store's host that takes the connection and says nothing — and the rest from <c>Others</c>.</summary>
    private sealed class HangsAt(string url, ByUrl others) : DelegatingHandler(others)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.ToString() != url) return await base.SendAsync(request, ct);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// A STORE THAT NEVER ANSWERS COSTS ITS OWN TIMEOUT, NOT THE CLIENT'S: the official list arrives when
    /// the store's read gives up, with the store's note, rather than after HttpClient's hundred seconds.
    /// </summary>
    [Fact]
    public async Task AStoreThatNeverAnswers_GivesUpAtItsTimeout_AndTheOfficialListArrives()
    {
        var handler = new HangsAt("https://slow/c.json", new ByUrl { ["https://o/c.json"] = Catalog("repo-map") });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var set = await CatalogSources.ReadAllAsync(new CatalogSetRequest(
            [new PluginStore("slow", "https://slow/c.json")], _dir, "https://o/c.json", new HttpClient(handler))
            { Timeout = TimeSpan.FromMilliseconds(300) }, default);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal(["repo-map"], set.Plugins.Select(p => p.QualifiedName));
        Assert.Equal("store slow: no answer within 0.3 s.", Assert.Single(set.Notes));
    }

    /// <summary>The wait is a named constant, short enough that a store off the VPN does not stall F2 for long.</summary>
    [Fact]
    public void EachRead_WaitsTenSeconds() => Assert.Equal(TimeSpan.FromSeconds(10), CatalogReader.ReadTimeout);
}

/// <summary>Whether an entry's download is cxagent's own release rather than a file hosted with its hash.</summary>
public class CatalogReleaseSourceTests
{
    [Theory]
    [InlineData("""{"kind":"release","sha256":"ab","latest":"https://github.com/x/releases/latest/download/d.zip"}""", true)]
    [InlineData("""{"kind":"url","sha256":"ab","url":"https://x/d.zip"}""", false)]
    public async Task AReleaseSource_IsMarked(string source, bool fromRelease)
    {
        var body = $$"""{"schemaVersion":2,"plugins":[{"name":"d","version":"1","kind":"managed","file":"d.dll","source":{{source}},"compatibility":{"pluginContract":2},"tools":[]}]}""";
        var handler = new CatalogSetTests.ByUrl { ["https://o/c.json"] = body };
        var dir = Directory.CreateTempSubdirectory("release-src-").FullName;
        try
        {
            var catalog = await new CatalogReader(new HttpClient(handler), Path.Combine(dir, "c.json"), "https://o/c.json").ReadAsync(default);
            Assert.Equal(fromRelease, Assert.Single(catalog.Plugins).FromRelease);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
