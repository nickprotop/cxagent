using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>
/// Reads from disk what the rail is computed from.
///
/// <para>APART FROM <see cref="PluginManagerRows"/> BECAUSE THIS HALF TOUCHES THE FILESYSTEM.
/// Splitting them is what lets the placement rules be a pure function and lets this be checked
/// against a real directory.</para>
///
/// <para>NOTHING HERE LOADS AN ASSEMBLY. A sidecar is data, and reading it is how a plugin's claims
/// are known without running it — the same guarantee the load gate depends on.</para>
/// </summary>
public static class PluginManagerState
{
    public static PluginManagerInputs Gather(
        IReadOnlyDictionary<string, PluginConfig> configured,
        IReadOnlyList<string> loaded,
        IReadOnlyList<string> searchFolders,
        IReadOnlyList<CatalogEntry> catalog,
        string projectDirectory)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var contracts = new Dictionary<string, int>(StringComparer.Ordinal);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);

        var unconfigured = PluginDiscovery.FindUnconfigured(configured, searchFolders);

        // CONFIGURED AND DISCOVERED ALIKE. A plugin config never named can still be built against a
        // contract this host cannot speak, and reading only the configured ones would leave that row
        // saying "not configured" for something configuring would not fix.
        var found = configured
            .Select(c => (Name: c.Key, Home: PluginDiscovery.FindLoadSetDirectory(c.Value.File, searchFolders),
                          c.Value.File))
            .Concat(unconfigured.Select(u => (u.Name, Home: (string?)u.Folder, u.File)));

        foreach (var (name, home, file) in found)
        {
            if (home is null) { missing.Add(name); continue; }

            folders[name] = LabelFor(home, searchFolders, projectDirectory);

            var sidecar = Path.Combine(home, Path.GetFileNameWithoutExtension(file) + ".plugin.json");
            if (!File.Exists(sidecar)) { unreadable.Add(name); continue; }

            try
            {
                // Parse returns (null, errors) on bad JSON rather than throwing, so a broken sidecar
                // is a null manifest here — not an exception.
                var manifest = PluginManifest.Parse(File.ReadAllText(sidecar)).Manifest;
                if (manifest is null) { unreadable.Add(name); continue; }

                if (!string.IsNullOrEmpty(manifest.Version)) versions[name] = manifest.Version;
                if (manifest.Contract is { } c) contracts[name] = c;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UNREADABLE IS NOT ABSENT, and a permissions problem must not stop the rail
                // rendering — the dialog is how a user finds out about it.
                unreadable.Add(name);
            }
        }

        return new PluginManagerInputs(
            configured, loaded, unconfigured, catalog, versions, contracts, missing, unreadable, folders);
    }

    /// <summary>
    /// Which folder this is, in one word.
    ///
    /// <para>COMPARED AGAINST THE FOLDERS WE WERE GIVEN, never guessed from the path text. A
    /// substring test for "config" labels the Windows global directory —
    /// <c>%APPDATA%\Roaming\cxagent\plugins</c> — as neither global nor project, and a row that
    /// called it "path" would be lying on every Windows machine.</para>
    ///
    /// <para>THREE WORDS, NOT TWO: a <c>pluginPaths</c> entry can point anywhere, and calling it
    /// either of the other two would be wrong.</para>
    /// </summary>
    private static string LabelFor(string home, IReadOnlyList<string> searchFolders, string projectDirectory)
    {
        var full = Path.GetFullPath(home);
        var project = Path.GetFullPath(Path.Combine(projectDirectory, ".cxagent", "plugins"));

        if (Inside(full, project)) return "project";

        // The LAST search folder is the global one — PluginDiscovery.SearchFolders appends the
        // project's folder, then the global config directory's, after any configured paths.
        var global = searchFolders.Count > 0 ? Path.GetFullPath(searchFolders[^1]) : null;
        return global is not null && Inside(full, global) ? "global" : "path";
    }

    /// <summary>Whether a directory IS a folder or sits directly inside it — the same one-level
    /// relationship the plugin searches use.</summary>
    private static bool Inside(string candidate, string folder) =>
        string.Equals(candidate, folder, StringComparison.Ordinal)
        || string.Equals(Path.GetDirectoryName(candidate), folder, StringComparison.Ordinal);
}
