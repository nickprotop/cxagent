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

            // A DOT-PREFIXED SUBDIRECTORY IS NOT A PLUGIN. The installer stages an extraction in
            // one, beside its destination so the final move cannot cross a filesystem; a hard kill
            // mid-install leaves that directory behind, and resolving a plugin out of a half-written
            // staging copy would load bytes no hash ever covered.
            foreach (var nested in Directory.EnumerateDirectories(folder)
                         .Where(d => !Path.GetFileName(d).StartsWith('.'))
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
    public static string? DeclaredName(string assemblyPath) => DeclaredManifest(assemblyPath)?.Name;

    /// <summary>
    /// The sidecar beside <paramref name="assemblyPath"/>, parsed — the same early read as
    /// <see cref="DeclaredName"/>, but keeping the whole manifest rather than only the name. A caller
    /// deciding whether to build an <see cref="IPluginClient"/> needs <see cref="PluginManifest.Client"/>
    /// from THIS read: the declaration is what the load prompt discloses and the user approves, so it
    /// has to gate whether a client is constructed at all, before <see cref="ManagedPluginLoader.Load"/>
    /// ever runs the plugin's own code — checking it only after Load returns would let an
    /// undeclared plugin stash a live reference during its own Load, which nothing afterward could
    /// take back.
    /// </summary>
    public static PluginManifest? DeclaredManifest(string assemblyPath)
    {
        var sidecarPath = Path.ChangeExtension(assemblyPath, null) + ".plugin.json";
        if (!File.Exists(sidecarPath)) return null;

        return PluginManifest.Parse(File.ReadAllText(sidecarPath)).Manifest;
    }

    /// <summary>What a runtime context needs to serve one plugin in one session.</summary>
    /// <param name="WorkingDirectory">Where the session works.</param>
    /// <param name="Settings">The plugin's own configuration.</param>
    /// <param name="Report">Where the plugin's log lines go.</param>
    /// <param name="Children">Where spawned processes are recorded.</param>
    /// <param name="PluginName">Which plugin this is, for child-process scoping and sever.</param>
    /// <param name="SessionId">Which session, so two in one folder are distinguishable.</param>
    /// <param name="Client">
    /// This plugin's handle on the session it is loaded into, or null — NULL WHEN THE SIDECAR DID NOT
    /// DECLARE IT. The declaration is what the load prompt disclosed and the user approved, so it is
    /// what gates whether a reference exists at all: a plugin that never asked must never be able to
    /// stash one during its own Load, before the caller has even checked its binary against the
    /// declaration. <see cref="ManagedPluginLoader"/>'s own check
    /// (<see cref="IPluginClientConsumer"/>, run AFTER Load returns) answers a DIFFERENT question —
    /// whether a binary that DID declare the capability can honestly use it — and is orthogonal to
    /// this: that check gates the binary, this parameter gates the reference.
    /// </param>
    public sealed record PluginRuntime(
        string WorkingDirectory, JsonElement Settings, Action<string> Report,
        ChildProcessStore Children, string PluginName, string? SessionId, IPluginClient? Client);

    /// <summary>
    /// <see cref="IPluginContext"/> for a plugin loaded at RUNTIME, through <c>/plugin load</c> —
    /// the counterpart to the front end's startup-only context: this one exists because a runtime
    /// load is not startup and nothing before this command needed one.
    /// </summary>
    public sealed class RuntimeContext(PluginRuntime runtime) : IPluginContext, IDisposable
    {
        public string WorkingDirectory { get; } = runtime.WorkingDirectory;
        public JsonElement Settings { get; } = runtime.Settings;
        public int HostContract => PluginContract.Version;
        // THIS ASSEMBLY'S VERSION, not the contract's. Core carries the release the workflow
        // stamped; the contract assembly's own version is frozen so a plugin's binding survives a
        // release, and asking it for a release number returns that frozen identity instead.
        public string HostVersion =>
            PluginContract.HostVersionOf(typeof(PluginResolver).Assembly);
        public IPluginLogger Logger { get; } = new ReportingLogger(runtime.Report);

        public IPluginClient? Client { get; } = runtime.Client;

        // CANCELLED AT STOP, AND ONLY AT STOP. A plugin that starts a timer needs a signal that its
        // session is done with it, or the timer outlives the plugin and keeps running against a
        // session nobody is watching — which matters more now that a plugin can submit work.
        private readonly CancellationTokenSource _lifetime = new();

        public CancellationToken Lifetime => _lifetime.Token;

        /// <summary>Fires <see cref="Lifetime"/>. Step one of cancel-drain-sever.</summary>
        public void Dispose()
        {
            // CANCEL, DON'T DISPOSE THE SOURCE. A plugin's Stop reads Lifetime to learn it WAS
            // cancelled — the exact moment Dispose runs — and CancellationTokenSource.Token throws
            // ObjectDisposedException once the source itself is torn down, which would turn "ask
            // whether my session ended" into a crash for any caller reading Lifetime after this.
            // Leaving the source undisposed costs one small object per plugin load; the alternative
            // costs a token nobody can safely read past the moment it matters.
            try { _lifetime.Cancel(); } catch (Exception) { }
        }

        public void RegisterChildProcess(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                // WHOSE CHILD IT IS, not only which plugin's. A plugin is loaded per session, so a
                // record naming the plugin alone made one session's unwire reap every session's
                // children — see ChildProcessRecord.Session.
                runtime.Children.Add(new ChildProcessRecord(
                    processId, process.StartTime.ToUniversalTime(), runtime.PluginName, runtime.SessionId));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                runtime.Report($"plugin '{runtime.PluginName}': process {processId} could not be recorded ({ex.Message}) — it may have already exited.");
            }
        }

        private sealed class ReportingLogger(Action<string> report) : IPluginLogger
        {
            public void Log(string message) => report(message);
        }
    }
}
