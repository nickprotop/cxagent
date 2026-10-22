using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>What to read: the user's stores, where the caches live, and the official catalog's URL.</summary>
/// <param name="Client">Null for the shared client; a test passes its own.</param>
public sealed record CatalogSetRequest(
    IReadOnlyList<PluginStore> Stores, string ConfigDir, string OfficialUrl, HttpClient? Client = null)
{
    /// <summary>How long each catalog's read may take; null for <see cref="CatalogReader.ReadTimeout"/>. A test shortens it.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// Every catalog's plugins in one list — the official catalog's first, then each store's in config
/// order — with one note per store that could not be read.
/// </summary>
/// <param name="OfficialCachedAt">When the official catalog's cached copy was written, when that is what was shown.</param>
/// <param name="OfficialError">Why the official catalog could not be fetched, or null.</param>
public sealed record CatalogSet(
    IReadOnlyList<CatalogEntry> Plugins,
    IReadOnlyList<string> Notes,
    DateTimeOffset? OfficialCachedAt,
    string? OfficialError);

/// <summary>
/// Reads the official catalog and every store's with the same reader, each from its own cache file.
///
/// <para>ONE STORE'S FAILURE IS ITS OWN. An unreachable store, an HTTP error or a body that is not a
/// catalog costs that store's fresh entries and nothing else: its last good copy is still listed, and
/// its note names it, so the user can tell which catalog is behind and why.</para>
///
/// <para>A CACHE PER STORE, named after it, which is what lets a store work offline from its last good
/// copy — and keeps one store's bad body from overwriting another's good one.</para>
/// </summary>
public static class CatalogSources
{
    /// <summary>The official catalog's cache — the one the plugin manager and <c>/plugin get</c> have always shared.</summary>
    public static string OfficialCache(string configDir) => Path.Combine(configDir, "catalog-cache.json");

    /// <summary>A store's own cache file.</summary>
    public static string StoreCache(string configDir, string store) => Path.Combine(configDir, $"catalog-cache-{store}.json");

    public static async Task<CatalogSet> ReadAllAsync(CatalogSetRequest request, CancellationToken ct)
    {
        // CONCURRENTLY, AND EACH UNDER ITS OWN TIMEOUT: the listing waits for every catalog, so it can be
        // held up only as long as one read may take — CatalogReader.ReadTimeout, not HttpClient's hundred
        // seconds — and a store that does not answer then shows its cache and a note like any other
        // failure. Gathered back in config order, so the listing does not reorder by latency.
        var official = new CatalogReader(request.Client, OfficialCache(request.ConfigDir), request.OfficialUrl)
            .ReadAsync(ct, request.Timeout);
        var stores = request.Stores
            .Select(s => new CatalogReader(request.Client, StoreCache(request.ConfigDir, s.Name), s.Url).ReadAsync(ct, request.Timeout))
            .ToList();
        await Task.WhenAll(stores.Prepend(official)).ConfigureAwait(false);

        var plugins = new List<CatalogEntry>(official.Result.Plugins);
        var notes = new List<string>();
        for (var i = 0; i < stores.Count; i++)
        {
            var store = request.Stores[i].Name;
            var read = stores[i].Result;
            plugins.AddRange(read.Plugins.Select(p => p with { Store = store }));
            if (read.Error is { } error) notes.Add($"store {store}: {error}");
        }

        return new CatalogSet(plugins, notes, official.Result.CachedAt, official.Result.Error);
    }
}
