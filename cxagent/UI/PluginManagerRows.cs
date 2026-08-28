using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>Which group a row belongs to. Order matters: the rail renders sections in this order.</summary>
public enum PluginRowSection
{
    /// <summary>What you have — configured, or found on disk and not yet configured.</summary>
    Installed,

    /// <summary>What changed. Its own section because it is the only state actionable without
    /// selecting the row: loaded and disabled are things the user already knows.</summary>
    Updates,

    /// <summary>What will not load, and why. Grouped apart because the ordinary actions do not
    /// apply to these rows.</summary>
    NeedsAttention,

    /// <summary>What you could have.</summary>
    Available,
}

/// <param name="State">The short word the rail prints. Where a state exists in both surfaces this is
/// <c>/plugin</c>'s own wording, so the dialog and the command never disagree.</param>
/// <param name="Detail">A longer explanation for the panel, or null when the state says it all.</param>
/// <param name="Catalog">The catalog entry, when there is one — carried so the panel renders without
/// a second lookup.</param>
/// <param name="Configured">Its config entry, when config names it.</param>
/// <param name="Folder">Which plugins folder holds it: "global", "project", "path", or null when it
/// is not installed. A path label is needed because pluginPaths can point anywhere, and "uninstall"
/// is ambiguous the moment two copies exist.</param>
public sealed record PluginRow(
    string Name,
    PluginRowSection Section,
    string State,
    string? Detail = null,
    CatalogEntry? Catalog = null,
    PluginConfig? Configured = null,
    string? Folder = null);

/// <summary>
/// Everything the rail is computed from. A record rather than seven parameters — AV1561, and these
/// are one thing: the state of plugins as this process currently sees them.
/// </summary>
/// <param name="MissingFiles">Config names these but no file resolves.</param>
/// <param name="Unreadable">Config names these, the file is there, but the sidecar is absent or will
/// not parse. A DIFFERENT bucket from <paramref name="MissingFiles"/> on purpose: "file missing"
/// would send a user looking for a binary that is sitting right there.</param>
/// <param name="InstalledContracts">Each plugin's declared contract, read from its sidecar WITHOUT
/// loading it. Covers UNCONFIGURED plugins too — a plugin config never named can still be built
/// against a contract this host cannot speak, and configuring it would not fix that.</param>
/// <param name="Folders">Which plugins folder each installed plugin resolved to, by name. The label
/// is computed against the known search folders rather than guessed from the path.</param>
public sealed record PluginManagerInputs(
    IReadOnlyDictionary<string, PluginConfig> Configured,
    IReadOnlyList<string> Loaded,
    IReadOnlyList<PluginDiscovery.UnconfiguredPlugin> Unconfigured,
    IReadOnlyList<CatalogEntry> Catalog,
    IReadOnlyDictionary<string, string> InstalledVersions,
    IReadOnlyDictionary<string, int> InstalledContracts,
    IReadOnlySet<string> MissingFiles,
    IReadOnlySet<string> Unreadable,
    IReadOnlyDictionary<string, string> Folders);

/// <summary>
/// What the plugin manager's rail shows, decided as a pure function.
///
/// <para>NO WINDOW, DELIBERATELY. The placement rules are thirteen states with a precedence order,
/// and deciding them inside a control would make each one cost a rendered dialog to check. The
/// control renders what this returns.</para>
/// </summary>
public static class PluginManagerRows
{
    /// <summary>
    /// Every row, ordered by section then name.
    ///
    /// <para>PRECEDENCE: NeedsAttention &gt; Updates &gt; Installed &gt; Available, and a plugin has
    /// exactly one row. A plugin you have AND the catalog lists appears once — two rows for one
    /// plugin is where the update story would be lost.</para>
    /// </summary>
    public static IReadOnlyList<PluginRow> Build(PluginManagerInputs inputs)
    {
        // FIRST WINS, RATHER THAN THROWING. ToDictionary would raise on a duplicate name, so one
        // malformed published catalog would make F2 crash instead of showing a rail.
        var catalog = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var e in inputs.Catalog) catalog.TryAdd(e.Name, e);
        var loaded = new HashSet<string>(inputs.Loaded, StringComparer.Ordinal);
        var rows = new List<PluginRow>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, config) in inputs.Configured)
        {
            catalog.TryGetValue(name, out var entry);
            rows.Add(Installed(name, config, entry, loaded.Contains(name), inputs));
            placed.Add(name);
        }

        foreach (var found in inputs.Unconfigured)
        {
            if (!placed.Add(found.Name)) continue;

            catalog.TryGetValue(found.Name, out var entry);
            var contract = inputs.InstalledContracts.TryGetValue(found.Name, out var c) ? c : PluginContract.Version;
            // Gather reads sidecars for UNCONFIGURED plugins too, so this branch is reachable —
            // it would be dead if contracts were only collected for configured entries.

            // A CONTRACT PROBLEM OUTRANKS "not configured": configuring it would not make it load.
            rows.Add(Mismatch(contract) is { } why
                ? new PluginRow(found.Name, PluginRowSection.NeedsAttention, why, Catalog: entry, Folder: Folder(found.Name, inputs))
                : new PluginRow(found.Name, PluginRowSection.Installed, "not configured",
                                Catalog: entry, Folder: Folder(found.Name, inputs)));
        }

        // LOADED BY PATH, CONFIG NEVER NAMED IT — the case `/plugin load <path>` creates. /plugin
        // lists these, so a rail that omitted them would disagree with the command about what is
        // running, which is the one thing this dialog must never do.
        foreach (var name in inputs.Loaded)
            if (placed.Add(name))
                rows.Add(new PluginRow(name, PluginRowSection.Installed, "loaded",
                                       Folder: Folder(name, inputs)));

        foreach (var entry in inputs.Catalog)
        {
            if (!placed.Add(entry.Name)) continue;

            // NOT INSTALLED, SO IT NEEDS NOTHING FROM THE USER — an unusable catalog entry stays
            // listed with its reason rather than hidden, or a reader wonders where it went.
            //
            // NO SOURCE FOR THIS MACHINE IS A REASON TOO. An ABI plugin ships one artifact per
            // runtime identifier (CatalogEntry.Sources), and an entry whose map has none for the
            // running RID must SAY so — offering an install button that cannot work is worse than
            // saying the plugin is not available here.
            var why = Mismatch(entry.PluginContract)
                   ?? (entry.Sources.Count > 0 && !entry.Sources.ContainsKey(RuntimeIdentifier)
                       ? "not available for this machine" : "");

            rows.Add(new PluginRow(entry.Name, PluginRowSection.Available, why, Catalog: entry));
        }

        return [.. rows.OrderBy(r => r.Section).ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The rows whose name or state matches, case-insensitively. Empty text matches everything.
    ///
    /// <para>A SECTION WITH NOTHING LEFT IS NOT RENDERED — the caller drops a header with no rows
    /// under it, so filtering never leaves a heading standing over nothing.</para>
    /// </summary>
    public static IReadOnlyList<PluginRow> Filter(IReadOnlyList<PluginRow> rows, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return rows;

        return [.. rows.Where(r =>
            r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || r.State.Contains(text, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// The running machine's identifier, for matching a catalog entry's per-RID sources.
    ///
    /// <para>NOT A GUESS FROM THE OS: RuntimeInformation.RuntimeIdentifier is what the publish that
    /// produced those artifacts used, so comparing anything else would disagree with the names in
    /// the catalog.</para>
    /// </summary>
    private static string RuntimeIdentifier =>
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    /// <summary>Which folder holds it, in one word, or null when it is not installed.</summary>
    private static string? Folder(string name, PluginManagerInputs inputs) =>
        inputs.Folders.TryGetValue(name, out var label) ? label : null;

    private static PluginRow Installed(
        string name, PluginConfig config, CatalogEntry? entry, bool isLoaded, PluginManagerInputs inputs)
    {
        if (inputs.MissingFiles.Contains(name))
            return new PluginRow(name, PluginRowSection.NeedsAttention, "file missing",
                                 Catalog: entry, Configured: config, Folder: Folder(name, inputs));

        // ITS OWN WORDS, not "file missing". The binary is there; what is absent or broken is the
        // sidecar — and a user sent looking for a missing file would be looking for the wrong thing.
        if (inputs.Unreadable.Contains(name))
            return new PluginRow(name, PluginRowSection.NeedsAttention, "no usable manifest",
                                 Catalog: entry, Configured: config, Folder: Folder(name, inputs));

        var contract = inputs.InstalledContracts.TryGetValue(name, out var c) ? c : PluginContract.Version;
        if (Mismatch(contract) is { } why)
            return new PluginRow(name, PluginRowSection.NeedsAttention, why, Catalog: entry, Configured: config);

        var state = isLoaded ? "loaded" : config.Enabled ? "declared, not loaded" : "disabled";

        // ORDINAL, AND BOTH NUMBERS. A sidecar version is whatever an author wrote, so semver
        // parsing would have to decide what to do with one that is not — and a locally built plugin
        // ahead of the catalog would be offered an "update" that is a downgrade.
        if (entry is not null
            && inputs.InstalledVersions.TryGetValue(name, out var have)
            && !string.Equals(have, entry.Version, StringComparison.Ordinal))
            return new PluginRow(name, PluginRowSection.Updates,
                $"{state} · {have} → {entry.Version}",
                Catalog: entry, Configured: config, Folder: Folder(name, inputs));

        return new PluginRow(name, PluginRowSection.Installed, state,
                             Catalog: entry, Configured: config, Folder: Folder(name, inputs));
    }

    /// <summary>Why this contract cannot load here, or null when it can. Split by direction: the
    /// remedies are different and only one of them is in this dialog.</summary>
    private static string? Mismatch(int contract) => contract == PluginContract.Version ? null
        : contract < PluginContract.Version
            ? $"contract {contract} · needs a newer build"
            : $"contract {contract} · needs a newer cxagent";


}
