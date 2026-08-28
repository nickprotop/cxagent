using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Finds the assembly <c>/plugin load</c> means, by a configured name or by a path — the Core-side
/// counterpart to the front end's <c>PluginDiscovery</c>, which does the same search at startup for
/// every ENABLED plugin in one pass. This does it for ONE plugin, on demand, and accepts a name
/// config never declared: the plugin design's whole case for the path form is a plugin nobody has
/// configured yet, and a resolver that only understood configured names could not serve it.
/// </summary>
public static class PluginResolver
{
    private const string ProjectPluginsDirName = ".cxagent/plugins";

    private static string GlobalPluginsDir(string configDir) => Path.Combine(configDir, "plugins");

    /// <summary>The folders searched for a configured plugin's file — same order and reasoning as
    /// the front end's <c>PluginDiscovery.SearchFolders</c>: configured paths first, in the order
    /// written, then the project-local folder, then the global one.</summary>
    public static IReadOnlyList<string> SearchFolders(
        IReadOnlyList<string> configuredPaths, string projectDirectory, string configDir)
    {
        var folders = new List<string>();

        foreach (var raw in configuredPaths)
        {
            var expanded = ConfigVariable.Expand(raw);
            folders.Add(Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(projectDirectory, expanded));
        }

        folders.Add(Path.Combine(projectDirectory, ProjectPluginsDirName));
        folders.Add(GlobalPluginsDir(configDir));

        return folders;
    }

    /// <summary>What <c>/plugin load</c> resolved a target to — a file ready to load, or a reason
    /// nothing was found.</summary>
    public abstract record ResolveResult
    {
        private ResolveResult() { }

        /// <param name="AssemblyPath">The entry-point <c>.dll</c>.</param>
        /// <param name="LoadSetDirectory">The folder holding it — the identity boundary
        /// <see cref="PluginIdentity.HashLoadSet"/> hashes and the gate's subject.</param>
        public sealed record Found(string AssemblyPath, string LoadSetDirectory) : ResolveResult;

        /// <summary>Neither a configured name nor an existing path — reported, not a silent
        /// no-op and not an unhandled exception surfacing as a crash.</summary>
        public sealed record NotFound(string Reason) : ResolveResult;
    }

    /// <summary>
    /// Resolves <paramref name="target"/> to an assembly: first as a name in
    /// <paramref name="configured"/> (searched under <paramref name="searchFolders"/>, matching how
    /// every configured plugin is found at startup), then as a literal path — a bare filename
    /// resolved against <paramref name="searchFolders"/> the same way, or an absolute/relative path
    /// taken as given. THE GATE IS UNAFFECTED EITHER WAY: identity is a content hash over the load
    /// set, so config declaring a name was never approval and a path-loaded plugin asks exactly as a
    /// named one does.
    /// </summary>
    public static ResolveResult Resolve(string target,
        IReadOnlyDictionary<string, PluginConfig> configured, IReadOnlyList<string> searchFolders,
        string projectDirectory)
    {
        if (configured.TryGetValue(target, out var config))
        {
            var directory = FindLoadSetDirectory(config.File, searchFolders);
            return directory is null
                ? new ResolveResult.NotFound(
                    $"no file named '{config.File}' found under {string.Join(", ", searchFolders)}.")
                : new ResolveResult.Found(Path.Combine(directory, config.File), directory);
        }

        // A PATH, NOT A NAME. A rooted or relative-with-separators path is taken as the user wrote
        // it (relative to the project directory); a bare filename is searched the same folders a
        // configured plugin is, so `/plugin load lsp-rust.dll` works whether or not config knows it.
        var expanded = ConfigVariable.Expand(target);
        var candidate = Path.IsPathRooted(expanded) ? expanded : Path.Combine(projectDirectory, expanded);

        if (File.Exists(candidate))
            return new ResolveResult.Found(candidate, Path.GetDirectoryName(candidate) ?? projectDirectory);

        if (!expanded.Contains(Path.DirectorySeparatorChar) && !expanded.Contains(Path.AltDirectorySeparatorChar))
        {
            var directory = FindLoadSetDirectory(expanded, searchFolders);
            if (directory is not null)
                return new ResolveResult.Found(Path.Combine(directory, expanded), directory);
        }

        return new ResolveResult.NotFound(
            $"'{target}' is not a configured plugin and no file matches it under "
            + $"{string.Join(", ", searchFolders)}.");
    }

    /// <summary>
    /// The folder holding <paramref name="file"/>: each search folder itself, then its immediate
    /// subdirectories.
    ///
    /// <para>ONE LEVEL, NOT A WALK. A plugin's own dependencies may sit in nested folders, and
    /// descending into those would find a dependency and hand it back as an entry point.</para>
    ///
    /// <para>THE FOLDER BEFORE ITS SUBDIRECTORIES, and subdirectories in ordinal order. A loose
    /// copy and a nested copy of one filename is a configuration a user built; which wins has to be
    /// the same answer on every run and every machine, and <c>Directory.EnumerateDirectories</c>
    /// returns filesystem order, which the BCL documents as unspecified.</para>
    ///
    /// <para>DUPLICATED IN <c>PluginDiscovery.FindLoadSetDirectory</c>, deliberately — see this
    /// type's own doc. The two must change together: that one is what loads configured plugins at
    /// startup, and a nested plugin found by only one of them is loadable by <c>/plugin load</c>
    /// and invisible when cxagent starts.</para>
    /// </summary>
    private static string? FindLoadSetDirectory(string file, IReadOnlyList<string> searchFolders)
    {
        foreach (var folder in searchFolders)
        {
            if (File.Exists(Path.Combine(folder, file)))
                return folder;

            if (!Directory.Exists(folder)) continue;

            foreach (var nested in Directory.EnumerateDirectories(folder)
                         .OrderBy(d => d, StringComparer.Ordinal))
                if (File.Exists(Path.Combine(nested, file)))
                    return nested;
        }

        return null;
    }

    /// <summary>
    /// The sidecar-declared name beside <paramref name="assemblyPath"/>, read before
    /// <see cref="ManagedPluginLoader.Load"/> — the same early read <c>PluginDiscovery</c> does, and
    /// for the same reason: a child process the plugin registers DURING its own <c>Load()</c> must be
    /// recorded under the name the registry and <see cref="ChildProcessStore"/> key on later, and
    /// <c>Load()</c> has not returned yet to confirm it.
    /// </summary>
    public static string? DeclaredName(string assemblyPath)
    {
        var sidecarPath = Path.ChangeExtension(assemblyPath, null) + ".plugin.json";
        if (!File.Exists(sidecarPath)) return null;

        return PluginManifest.Parse(File.ReadAllText(sidecarPath)).Manifest?.Name;
    }

    /// <summary>
    /// <see cref="IPluginContext"/> for a plugin loaded at RUNTIME, through <c>/plugin load</c> —
    /// the counterpart to the front end's startup-only context: this one exists because a runtime
    /// load is not startup and nothing before this command needed one.
    /// </summary>
    public sealed class RuntimeContext(
        string workingDirectory, JsonElement settings, Action<string> report,
        ChildProcessStore children, string pluginName) : IPluginContext
    {
        public string WorkingDirectory { get; } = workingDirectory;
        public JsonElement Settings { get; } = settings;
        public int HostContract => PluginContract.Version;
        // THIS ASSEMBLY'S VERSION, not the contract's. Core carries the release the workflow
        // stamped; the contract assembly's own version is frozen so a plugin's binding survives a
        // release, and asking it for a release number returns that frozen identity instead.
        public string HostVersion =>
            PluginContract.HostVersionOf(typeof(PluginResolver).Assembly);
        public IPluginLogger Logger { get; } = new ReportingLogger(report);

        // CANCELLED AT STOP, AND ONLY AT STOP — see IPluginContext.Lifetime's own doc. Nothing has
        // stopped this plugin yet at the moment /plugin load constructs its context, so
        // CancellationToken.None is the correct starting point, matching the startup path.
        public CancellationToken Lifetime { get; } = CancellationToken.None;

        public void RegisterChildProcess(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                children.Add(new ChildProcessRecord(processId, process.StartTime.ToUniversalTime(), pluginName));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                report($"plugin '{pluginName}': process {processId} could not be recorded ({ex.Message}) — it may have already exited.");
            }
        }

        private sealed class ReportingLogger(Action<string> report) : IPluginLogger
        {
            public void Log(string message) => report(message);
        }
    }
}
