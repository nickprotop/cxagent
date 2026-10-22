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
/// <summary>
/// What <see cref="PluginManagerState.Gather"/> reads from: config's plugins, what is loaded, where to
/// look, the catalog, and the two directories the built-in folders are named from.
/// </summary>
public sealed record GatherInputs(
    IReadOnlyDictionary<string, PluginConfig> Configured,
    IReadOnlyList<string> Loaded,
    IReadOnlyList<string> SearchFolders,
    IReadOnlyList<CatalogEntry> Catalog,
    string ProjectDirectory,
    string ConfigDir);

public static class PluginManagerState
{
    public static PluginManagerInputs Gather(GatherInputs inputs)
    {
        var (configured, loaded, searchFolders, catalog, projectDirectory, configDir) = inputs;
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
            .Select(c => (Name: c.Key, Home: PluginDiscovery.HomeOf(c.Key, c.Value, searchFolders),
                          c.Value.File))
            .Concat(unconfigured.Select(u => (u.Name, Home: (string?)u.Folder, u.File)));

        foreach (var (name, home, file) in found)
        {
            if (home is null) { missing.Add(name); continue; }

            folders[name] = LabelFor(home, projectDirectory, configDir);

            // BESIDE THE DLL, WHICH IS NOT ALWAYS `home`. A config entry may carry a subdirectory —
            // "clone-finder/clone-finder.dll" is what an install through the manager writes — and
            // FindLoadSetDirectory then returns the SEARCH FOLDER, because the file was found by
            // combining the two. Joining the sidecar's name onto `home` looks a level too high and
            // finds nothing, so a plugin that loaded perfectly reports "no usable manifest".
            var dll = Path.Combine(home, file);
            var sidecar = Path.Combine(Path.GetDirectoryName(dll) ?? home,
                                       Path.GetFileNameWithoutExtension(file) + ".plugin.json");
            if (!File.Exists(sidecar)) { unreadable.Add(name); continue; }

            try
            {
                // Parse returns (null, errors) on bad JSON rather than throwing, so a broken sidecar
                // is a null manifest here — not an exception.
                // READ AS LOADING READS IT — PluginResolver.ReadSidecar: inside its folder, capped, never a device — so a
                // sidecar a cloned repository linked elsewhere is unreadable here too, not read to its end.
                var manifest = PluginResolver.ReadSidecar(sidecar).Json is { } json ? PluginManifest.Parse(json).Manifest : null;
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
    private static string LabelFor(string home, string projectDirectory, string configDir)
    {
        var full = Path.GetFullPath(home);

        // BY NAME, NOT BY POSITION IN THE SEARCH ORDER: that order is about which copy wins, and the
        // global folder is not at either end of it once pluginPaths come first.
        if (Inside(full, Path.GetFullPath(PluginResolver.ProjectFolder(projectDirectory)))) return "project";
        return Inside(full, Path.GetFullPath(PluginResolver.GlobalFolder(configDir))) ? "global" : "path";
    }

    /// <summary>
    /// Whether a directory IS a folder or sits directly inside it — the same one-level relationship
    /// the plugin searches use — or sits inside a STORE's folder directly inside it.
    ///
    /// <para>A STORE'S PLUGIN IS TWO LEVELS DOWN (<c>&lt;folder&gt;/acme/repo-map</c>). The middle
    /// folder only counts when it carries the store marker, so an ordinary plugin folder that happens
    /// to hold subfolders is not read as a store and its contents mislabelled.</para>
    /// </summary>
    private static bool Inside(string candidate, string folder)
    {
        if (string.Equals(candidate, folder, StringComparison.Ordinal)) return true;
        var parent = Path.GetDirectoryName(candidate);
        if (string.Equals(parent, folder, StringComparison.Ordinal)) return true;
        return parent is not null
            && string.Equals(Path.GetDirectoryName(parent), folder, StringComparison.Ordinal)
            && PluginDiscovery.IsStoreFolder(parent);
    }
}
