using System.Net;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The published catalog, and the copy kept so the dialog opens without a network.
///
/// <para>A DIALOG THAT SHOWS NOTHING IS INDISTINGUISHABLE FROM AN EMPTY CATALOG, which is a
/// different and wrong answer. Every failure here therefore produces a Catalog carrying an Error
/// rather than throwing, so a caller can say what went wrong beside whatever it could still show.
/// </para>
/// </summary>
public class CatalogReaderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "catalog-reader-" + Guid.NewGuid().ToString("N"));

    public CatalogReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string CachePath => Path.Combine(_dir, "catalog.json");

    private const string Published = """
        {
          "schemaVersion": 2,
          "plugins": [{
            "name": "csharp-lsp",
            "displayName": "C# Language Server",
            "version": "0.9.5",
            "description": "Go-to-definition for C#.",
            "publisher": "nickprotop",
            "license": "MIT",
            "repository": "https://github.com/nickprotop/cxagent",
            "category": "code-intelligence",
            "file": "csharp-lsp.dll",
            "kind": "managed",
            "compatibility": { "pluginContract": 2, "platforms": ["any"] },
            "tools": [{ "name": "csharp_definition", "gated": "dynamic" }],
            "settings": {
              "server": "The language server command. Defaults to csharp-ls.",
              "args": "Arguments for it. OmniSharp needs [\"-lsp\"]; csharp-ls needs none."
            },
            "source": { "kind": "release", "sha256": "abc123", "latest": "https://example/csharp-lsp.zip" },
            "requires": { "description": "csharp-ls on PATH", "default": "csharp-ls", "install": "dotnet tool install -g csharp-ls" }
          }]
        }
        """;

    /// <summary>Answers with a fixed response, so no test reaches the network.</summary>
    private sealed class Canned(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task APublishedCatalogIsRead()
    {
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.OK, Published)), CachePath);

        var catalog = await reader.ReadAsync(CancellationToken.None);

        Assert.Null(catalog.Error);
        var entry = Assert.Single(catalog.Plugins);
        Assert.Equal("csharp-lsp", entry.Name);
        Assert.Equal("code-intelligence", entry.Category);
        Assert.Equal(2, entry.PluginContract);
        Assert.Equal("abc123", entry.Sha256);
        Assert.Equal("https://example/csharp-lsp.zip", entry.DownloadUrl);
        Assert.Equal("dynamic", Assert.Single(entry.Tools).Gated);
    }

    /// <summary>
    /// THE CACHE IS WHAT MAKES THE DIALOG USEFUL OFFLINE. It is written on a good read so a later
    /// failure has something to fall back to.
    /// </summary>
    [Fact]
    public async Task AGoodReadIsCached()
    {
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.OK, Published)), CachePath);

        await reader.ReadAsync(CancellationToken.None);

        Assert.True(File.Exists(CachePath), "a successful read must leave a cache behind.");
    }

    /// <summary>
    /// A FAILED FETCH FALLS BACK, AND SAYS SO. The entries are the cached ones; Error names why they
    /// may be stale, and CachedAt says how stale.
    /// </summary>
    [Fact]
    public async Task AFailedFetchServesTheCacheAndNamesTheFailure()
    {
        File.WriteAllText(CachePath, Published);
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.InternalServerError, "nope")), CachePath);

        var catalog = await reader.ReadAsync(CancellationToken.None);

        Assert.Single(catalog.Plugins);
        Assert.NotNull(catalog.Error);
        Assert.Contains("500", catalog.Error);
        Assert.NotNull(catalog.CachedAt);
    }

    /// <summary>No network and no cache is not a crash — it is an empty catalog that explains itself.</summary>
    [Fact]
    public async Task NoNetworkAndNoCacheIsAnEmptyCatalogWithAReason()
    {
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.NotFound, "")), CachePath);

        var catalog = await reader.ReadAsync(CancellationToken.None);

        Assert.Empty(catalog.Plugins);
        Assert.NotNull(catalog.Error);
    }

    /// <summary>
    /// MALFORMED JSON IS A FAILED READ, NOT AN EXCEPTION. A truncated download is the ordinary way
    /// this happens, and it must behave exactly as an unreachable host does.
    /// </summary>
    [Fact]
    public async Task MalformedJsonIsReportedRatherThanThrown()
    {
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.OK, "{ not json")), CachePath);

        var catalog = await reader.ReadAsync(CancellationToken.None);

        Assert.Empty(catalog.Plugins);
        Assert.NotNull(catalog.Error);
    }

    /// <summary>
    /// THE SETTINGS PROSE IS THE FORM'S LABELS. The manager shows one input per documented key with
    /// this text beside it — cxagent cannot validate a plugin's settings, so what it CAN do is say
    /// what the plugin's own catalog entry claims they mean.
    /// </summary>
    [Fact]
    public async Task SettingsProseIsParsedPerKey()
    {
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.OK, Published)), CachePath);

        var entry = Assert.Single((await reader.ReadAsync(CancellationToken.None)).Plugins);

        Assert.Equal("The language server command. Defaults to csharp-ls.", entry.Settings["server"]);
        Assert.Equal(2, entry.Settings.Count);
    }

    /// <summary>An entry documenting no settings gets an empty map, never null — a caller renders a
    /// form from it without checking.</summary>
    [Fact]
    public async Task AnEntryWithNoSettingsGetsAnEmptyMap()
    {
        var noSettings = Published.Replace("\"settings\"", "\"unusedSettings\"");
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.OK, noSettings)), CachePath);

        var entry = Assert.Single((await reader.ReadAsync(CancellationToken.None)).Plugins);

        Assert.Empty(entry.Settings);
    }

    /// <summary>
    /// PER-RID SOURCES, FOR AN ABI PLUGIN. A native plugin ships one artifact per platform, so the
    /// catalog offers a map; a row whose map has no entry for the running RID must say the plugin is
    /// unavailable here rather than offering a button that cannot work.
    /// </summary>
    [Fact]
    public async Task PerPlatformSourcesAreParsed()
    {
        var abi = Published.Replace(
            "\"source\": {",
            "\"sources\": { \"linux-x64\": { \"latest\": \"https://example/p-linux.zip\", \"sha256\": \""
          + new string('a', 64) + "\" } }, \"unusedSource\": {");

        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.OK, abi)), CachePath);
        var entry = Assert.Single((await reader.ReadAsync(CancellationToken.None)).Plugins);

        Assert.Equal("https://example/p-linux.zip", entry.Sources["linux-x64"]);
    }

    /// <summary>A single-source entry has no per-RID map, and that is not an error — most plugins are
    /// managed and run anywhere.</summary>
    [Fact]
    public async Task AManagedEntryHasNoPerPlatformSources()
    {
        var reader = new CatalogReader(new HttpClient(new Canned(HttpStatusCode.OK, Published)), CachePath);

        var entry = Assert.Single((await reader.ReadAsync(CancellationToken.None)).Plugins);

        Assert.Empty(entry.Sources);
        Assert.Equal("csharp-ls", entry.RequiresDefault);
    }
}
