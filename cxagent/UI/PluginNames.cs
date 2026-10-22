using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>What a typed plugin name came to: the catalog entry it means, or why it means none.</summary>
public abstract record NameLookup
{
    private NameLookup() { }

    public sealed record Found(CatalogEntry Entry) : NameLookup;

    public sealed record Refused(string Message) : NameLookup;
}

/// <summary>
/// The one reading of a typed plugin name, for every command that takes one.
///
/// <para>A BARE NAME IS ALWAYS THE OFFICIAL CATALOG'S, and <c>cxagent/name</c> is the same name
/// written out. A store's plugin is only ever <c>store/name</c>: if a bare name could fall through to
/// a store, adding a store would change what an existing command installs.</para>
/// </summary>
public static class PluginNames
{
    public const string Malformed = "a plugin name is `name` or `store/name`.";

    public static NameLookup Find(string typed, IReadOnlyList<CatalogEntry> plugins, IReadOnlyList<PluginStore> stores)
    {
        if (!typed.Contains('/'))
            return Match(plugins, null, typed) is { } official
                ? new NameLookup.Found(official)
                : new NameLookup.Refused($"no plugin named '{typed}' in the catalog.");

        if (!PluginStores.TrySplit(typed, out var store, out var name)) return new NameLookup.Refused(Malformed);

        // THE OFFICIAL NAME WRITTEN OUT: 'cxagent' is no store's, so it reads as the bare name.
        if (store == PluginStores.Official) return Find(name, plugins, stores);

        if (!stores.Any(s => s.Name == store)) return new NameLookup.Refused($"no store named {store} — /plugin store list");

        return Match(plugins, store, name) is { } entry
            ? new NameLookup.Found(entry)
            : new NameLookup.Refused($"{store} does not list {name}.");
    }

    /// <summary>
    /// The entry for this name in one catalog, matched case-insensitively against its name — and its
    /// display name, because the site shows one and the catalog keys on the other.
    /// </summary>
    private static CatalogEntry? Match(IReadOnlyList<CatalogEntry> plugins, string? store, string name) =>
        plugins.FirstOrDefault(p => p.Store == store
            && (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
             || string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)));
}
