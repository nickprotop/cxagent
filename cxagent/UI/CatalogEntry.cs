namespace CxAgent.UI;

/// <summary>One tool a catalogued plugin offers, and whether calling it asks.</summary>
/// <param name="Name">The tool's name, as the model sees it.</param>
/// <param name="Gated">The manifest's own value — <c>"true"</c>, <c>"false"</c> or
/// <c>"dynamic"</c> — carried as written rather than parsed, because this is a catalogue entry
/// describing a plugin rather than the plugin's own manifest.</param>
public sealed record CatalogTool(string Name, string Gated);

/// <summary>
/// One plugin as the published catalog describes it — everything a reader needs to choose, before
/// anything is downloaded.
///
/// <para>NOT <see cref="PluginManifest"/>. That is what a plugin says about itself once its files
/// are on disk; this is what the catalog says about a plugin that may not be installed at all, and
/// it carries things a manifest has no business knowing: a publisher, a licence, a download URL and
/// the hash of the artifact behind it.</para>
/// </summary>
public sealed record CatalogEntry(
    string Name,
    string DisplayName,
    string Version,
    string Description,
    string Publisher,
    string License,
    string Repository,
    string Category,
    string File,
    string Kind,
    int PluginContract,
    IReadOnlyList<CatalogTool> Tools,
    string? DownloadUrl,
    string? Sha256,
    string? RequiresDescription,
    string? RequiresInstall)
{
    /// <summary>
    /// What each settings key means, by key — the catalog's own prose, empty when it documents none.
    ///
    /// <para>THE LABELS FOR A FORM cxagent CANNOT VALIDATE. A plugin's settings block is handed over
    /// verbatim and the host has no schema for it; what it can do is show what the plugin's entry
    /// says each key is for, beside the field that sets it.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The value <c>requires</c> suggests when the user has no preference, or null.</summary>
    public string? RequiresDefault { get; init; }

    /// <summary>
    /// Download URL per runtime identifier, for a plugin shipping one artifact per platform. Empty
    /// for a managed plugin, which has a single <see cref="DownloadUrl"/> and runs anywhere.
    ///
    /// <para>AN EMPTY MAP IS NOT "NO DOWNLOAD" — check <see cref="DownloadUrl"/> too. A map that is
    /// populated but lacks the running RID IS "not available for this machine", which a caller must
    /// say rather than offering an install that cannot work.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// The catalog as read, and how it went.
/// </summary>
/// <param name="Plugins">What was read — from the network, or from the cache when that failed.</param>
/// <param name="CachedAt">When the entries were fetched, set only when they came from the cache, so
/// a caller can say how stale they are.</param>
/// <param name="Error">Why the network read failed, or null. NOT an exception: a dialog that shows
/// nothing is indistinguishable from a catalog with nothing in it, so every failure still returns a
/// catalog and lets the caller say what happened beside whatever it could show.</param>
public sealed record Catalog(
    IReadOnlyList<CatalogEntry> Plugins,
    DateTimeOffset? CachedAt = null,
    string? Error = null);
