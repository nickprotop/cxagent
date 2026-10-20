using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Finds the assembly <c>/plugin load</c> means, by a configured name or by a path — the Core-side
/// counterpart to the front end's <c>PluginDiscovery</c>, which does the same search at startup for
/// every ENABLED plugin in one pass. This does it for ONE plugin, on demand, and accepts a name
/// config never declared: the whole case for the path form is a plugin nobody has configured yet,
/// and a resolver that only understood configured names could not serve it.
/// </summary>
public static class PluginResolver
{
    private const string ProjectPluginsDirName = ".cxagent/plugins";

    private static string GlobalPluginsDir(string configDir) => Path.Combine(configDir, "plugins");

    /// <summary>
    /// The folders searched for a configured plugin's file, first match winning: configured paths first, in
    /// the order written, then the global plugins folder, then the project's <c>.cxagent/plugins</c>.
    ///
    /// <para>THE GLOBAL FOLDER BEFORE THE PROJECT'S. A cloned repository is not the user's, and one shipping a
    /// file named like a plugin they configured would otherwise load in its place — a stranger's code under
    /// the name of something they installed. A plugin only the project has is still found, and asked about
    /// before it runs like any other.</para>
    ///
    /// <para>RELATIVE CONFIGURED ENTRIES RESOLVE AGAINST THE PROJECT DIRECTORY: <c>.cxagent/plugins</c>-style
    /// paths are meant to be read relative to the repo being worked in. The front end's
    /// <c>PluginDiscovery.SearchFolders</c> is this.</para>
    /// </summary>
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

        folders.Add(GlobalPluginsDir(configDir));
        folders.Add(Path.Combine(projectDirectory, ProjectPluginsDirName));

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
        internal sealed record NotFound(string Reason) : ResolveResult;
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
        return ReadSidecar(sidecarPath).Json is { } json ? PluginManifest.Parse(json).Manifest : null;
    }

    /// <summary>
    /// A sidecar's text, read the way every file that reaches a prompt is: only from inside the folder that holds
    /// it — a sidecar linked out of its load set describes something other than what is there — only a file with a
    /// size, at most a megabyte. Null text with no problem when there is no sidecar.
    ///
    /// <para>THE SIDECAR IS WHAT THE LOAD QUESTION SHOWS, read before any of the plugin's code runs, from a folder a
    /// cloned repository may have written — so it is read with the same care as an instruction file.</para>
    /// </summary>
    public static SidecarText ReadSidecar(string sidecarPath)
    {
        var read = Permissions.PathResolver.ReadConfined(new Permissions.ConfinedRead(sidecarPath,
            Path.GetDirectoryName(Path.GetFullPath(sidecarPath)), 1024 * 1024));
        return new SidecarText(read.Text is { Length: > 0 } text ? text : null, read.Problem);
    }

    /// <summary>
    /// Whether <paramref name="file"/> is a native shared library rather than a managed assembly —
    /// the one decision every load path needs before choosing <see cref="Abi.AbiPluginLoader"/> over
    /// <see cref="ManagedPluginLoader"/>, made HERE so it is made once. <c>PluginDiscovery.cs:176</c>
    /// already used <c>.so</c> for a narrower purpose (listing an unconfigured find); this is the
    /// same test, promoted to the one both loaders route on, so a plugin near-duplicate does not grow
    /// a second, driftable copy of the check <c>FindLoadSetDirectory</c>'s own doc already warns
    /// about for this file.
    ///
    /// <para>WITHOUT A DECLARED KIND, <c>.dll</c> MEANS MANAGED. A native Windows plugin is a
    /// <c>.dll</c> too, and nothing can tell the two apart by name alone, so a native plugin on Windows
    /// declares <c>"kind": "native"</c> in its sidecar — see <see cref="IsNative"/>, which is what the
    /// loaders route on. This is the extension half of that decision.</para>
    /// </summary>
    public static bool IsNativeLibrary(string file) =>
        file.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="file"/> is a .NET assembly — a PE image carrying CLI metadata — read
    /// from its header, not its name. A Windows native DLL is a PE image too but has no metadata, and
    /// an ELF or Mach-O library is not PE at all; both answer false, as does a file that cannot be
    /// read.
    ///
    /// <para>THE LOADERS ASK THIS because routing believes the sidecar's <c>"kind"</c>: a kind the file
    /// contradicts sends it to the loader that cannot read it, and only that loader can then say
    /// the two disagree, naming both, before the runtime or the plugin host fails on the file with
    /// an error that names neither.</para>
    /// </summary>
    public static bool IsDotNetAssembly(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.HasMetadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the plugin at <paramref name="pluginPath"/> loads through the native host — the one
    /// routing decision both load paths make.
    ///
    /// <para>ITS SIDECAR DECIDES WHEN IT NAMES A KIND, which is the only way a native library on
    /// Windows — a .dll — can say what it is. Otherwise the extension decides
    /// (<see cref="IsNativeLibrary"/>), as it always has. A sidecar that will not parse falls back to
    /// the extension: the loader it reaches reports the sidecar's own problem, which is more use than
    /// a routing guess.</para>
    /// </summary>
    public static bool IsNative(string pluginPath)
    {
        var sidecar = Path.ChangeExtension(pluginPath, null) + ".plugin.json";
        try
        {
            if (ReadSidecar(sidecar).Json is { } json && PluginManifest.Parse(json).Manifest?.Kind is { } kind)
                return kind == PluginKind.Native;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // UNREADABLE NOW IS THE LOADER'S TO REPORT, a moment later and in its own words.
        }

        return IsNativeLibrary(pluginPath);
    }

    /// <summary>
    /// Where <c>cxagent-plugin-host.dll</c> lives for THIS running process — the resolution
    /// <see cref="Abi.AbiPluginLoader.Load"/> deliberately leaves to its caller. Always in a fixed
    /// <c>plugin-host/</c> subfolder — sibling to, not inside, the user's own <c>plugins/</c> folder,
    /// because the host is infrastructure this build ships, not something a user configured — but
    /// the folder it is a subfolder OF differs by how cxagent was installed.
    ///
    /// <para>BESIDE THE EXECUTABLE FIRST, which is where <c>install.sh</c> places it for a release.
    /// That release is built with <c>PublishSingleFile</c>, and <see cref="Environment.ProcessPath"/>
    /// is the real executable's path however it was published, where <c>AppContext.BaseDirectory</c>
    /// can name a temporary extraction directory that changes between runs (the trap
    /// <c>cxagent/UI/Installation.ExecutableDirectory</c> documents for the app's own version
    /// lookup).</para>
    ///
    /// <para>THEN BESIDE THE APP'S OWN FILES. A <c>dotnet tool</c> runs through a shim in
    /// <c>~/.dotnet/tools</c> while the package — host included — is unpacked under
    /// <c>.store/</c>, so the executable's folder is the one place the host is NOT. The app's
    /// assemblies are loaded from <c>AppContext.BaseDirectory</c>, and the package puts the host
    /// beside them, as a dev build's output does.</para>
    ///
    /// <para>NEITHER: the executable's folder, so the "no plugin host executable at …" refusal names
    /// the location a user installing it by hand would put it.</para>
    /// </summary>
    public static string PluginHostDllPath() =>
        PluginHostDllPath(Environment.ProcessPath is { } exe ? Path.GetDirectoryName(exe) : null,
            AppContext.BaseDirectory);

    /// <summary><see cref="PluginHostDllPath()"/> over the two folders it searches, so each install
    /// layout can be tested without being that install.</summary>
    internal static string PluginHostDllPath(string? executableDirectory, string baseDirectory)
    {
        var besideExecutable = executableDirectory is null ? null : HostIn(executableDirectory);
        if (besideExecutable is not null && File.Exists(besideExecutable)) return besideExecutable;

        var besideAppFiles = HostIn(baseDirectory);
        if (File.Exists(besideAppFiles)) return besideAppFiles;

        return besideExecutable ?? besideAppFiles;
    }

    private static string HostIn(string folder) => Path.Combine(folder, "plugin-host", "cxagent-plugin-host.dll");

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

        // THE SAME ID THE CHILD-PROCESS RECORD IS SCOPED BY, handed to the plugin rather than only
        // used on its behalf: a plugin keying per-session state in a static needs the key Core
        // already has.
        public string? SessionId { get; } = runtime.SessionId;

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

/// <summary>A sidecar's JSON, or why it was not read — both null when there is no sidecar.</summary>
public sealed record SidecarText(string? Json, string? Problem);
