using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;

namespace CxAgent.UI;

/// <summary>
/// Finds a configured plugin's entry-point file on disk and loads it into a session — the piece
/// PLUGINS.md, "Configuration" puts on the APPLICATION rather than Core: "Core accepts 'here is a
/// plugin at this path' and does not care how it was found. Enumerating folders, presenting a picker
/// and deciding what to load is orchestration — Core is the infrastructure underneath it."
///
/// <para><see cref="Session.LoadPlugin"/> is the wiring nothing before this class provided: a loader
/// (<see cref="ManagedPluginLoader"/>) that can construct an <see cref="IPlugin"/> from disk existed,
/// a registry that can hold one existed, and config that names one existed — nothing yet called the
/// loader and handed its result to the session, and no production <see cref="IPluginContext"/> existed
/// either (only test fakes did).</para>
/// </summary>
public static class PluginDiscovery
{
    /// <summary>The folder searched under a project directory when <c>pluginPaths</c> says nothing —
    /// the project-local counterpart of <see cref="GlobalPluginsDir"/>, matching the <c>.cxagent/</c>
    /// prefix this app already uses for project-scoped state.</summary>
    private const string ProjectPluginsDirName = ".cxagent/plugins";

    private static string GlobalPluginsDir(string configDir) => Path.Combine(configDir, "plugins");

    /// <summary>
    /// The folders searched for a plugin's <see cref="PluginConfig.File"/>, nearest (most specific)
    /// first — PLUGINS.md, "Two locations, project wins": "A project overrides a globally installed
    /// plugin rather than colliding with it."
    ///
    /// <para>CONFIGURED PATHS COME FIRST, IN THE ORDER WRITTEN, because a user who bothered to list
    /// <c>pluginPaths</c> stated their own precedence and this must not silently reorder it. The two
    /// built-in defaults are appended after — project directory then global config directory — so an
    /// unconfigured install still finds anything dropped in either without editing config.json, with
    /// the same project-over-global precedence <see cref="ProjectInstructions.Find"/> already gives
    /// project instruction files over the global one.</para>
    ///
    /// <para>RELATIVE ENTRIES RESOLVE AGAINST THE PROJECT DIRECTORY, not the config directory —
    /// unlike <c>ProviderConfigLoader</c>'s config-time collision check, which has no project
    /// directory to resolve against and falls back to the config directory instead. This is the
    /// richer resolution that check's own doc says the runtime load has and it does not: PLUGINS.md's
    /// example <c>.cxagent/plugins</c> is meant to be read relative to the repo being worked in, and
    /// a plugin only findable that way is exactly the case config-time validation cannot see.</para>
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

        folders.Add(Path.Combine(projectDirectory, ProjectPluginsDirName));
        folders.Add(GlobalPluginsDir(configDir));

        return folders;
    }

    /// <summary>
    /// The directory holding <paramref name="file"/> and its sidecar, searched for in
    /// <see cref="SearchFolders"/> order — first match wins, matching every other search-path
    /// resolution in this codebase. Null when no folder holds it.
    /// </summary>
    public static string? FindLoadSetDirectory(string file, IReadOnlyList<string> searchFolders)
    {
        foreach (var folder in searchFolders)
            if (File.Exists(Path.Combine(folder, file)))
                return folder;

        return null;
    }

    /// <summary>
    /// Loads every ENABLED configured plugin whose file resolves against
    /// <paramref name="searchFolders"/>, in name order for a deterministic prompt sequence when more
    /// than one needs the user's approval.
    ///
    /// <para>ONE MESSAGE PER OUTCOME, THROUGH <paramref name="report"/> — a plugin that fails to
    /// resolve, fails to load, or is declined is not silently absent; it is exactly the kind of
    /// configured-but-not-working state <c>ProviderSettings.Warnings</c> already refuses to leave
    /// silent for MCP servers.</para>
    ///
    /// <para>SEQUENTIAL, NOT PARALLEL. Each load may prompt the user (<see cref="Session.LoadPlugin"/>'s
    /// load gate), and prompts arriving concurrently for several plugins would race for one dialog.</para>
    /// </summary>
    /// <param name="configDir">Where <see cref="ChildProcessStore"/> persists — the same file
    /// <see cref="SessionFactory.Wire"/> already attached to <c>session.Plugins</c>, so a process this
    /// call registers is reapable exactly like one registered mid-session.</param>
    public static async Task LoadConfiguredAsync(Session session,
        IReadOnlyDictionary<string, PluginConfig> plugins, IReadOnlyList<string> searchFolders,
        string configDir, Action<string> report, CancellationToken ct)
    {
        var children = new ChildProcessStore(configDir);

        foreach (var (name, config) in plugins.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // THE GATE, NOT A FILTER — PLUGINS.md, "Overriding is forbidden": false means no
            // process, no tools, no prompt, nothing. A disabled plugin is skipped before anything
            // about it is even looked up.
            if (!config.Enabled) continue;

            var loadSetDirectory = FindLoadSetDirectory(config.File, searchFolders);
            if (loadSetDirectory is null)
            {
                report($"plugin '{name}': no file named '{config.File}' found under "
                     + $"{string.Join(", ", searchFolders)}.");
                continue;
            }

            var assemblyPath = Path.Combine(loadSetDirectory, config.File);

            // THE SIDECAR'S OWN NAME, READ BEFORE Load() — a child process the plugin registers
            // DURING its own Load() must be recorded under the name ChildProcessStore.ReapPlugin and
            // PluginRegistry.UnwireAsync key on later (the manifest name), and Load() has not
            // returned yet to confirm it. ManagedPluginLoader.Load already refuses a mismatch between
            // this and what Load() returns, so reading it here is reading the same name early rather
            // than risking a second, different one.
            var sidecarPath = Path.ChangeExtension(assemblyPath, null) + ".plugin.json";
            var declaredName = File.Exists(sidecarPath)
                ? PluginManifest.Parse(File.ReadAllText(sidecarPath)).Manifest?.Name
                : null;
            if (string.IsNullOrEmpty(declaredName))
            {
                report($"plugin '{name}': no usable sidecar manifest at '{sidecarPath}'.");
                continue;
            }

            // THE SESSION'S FOLDER, NOT THE PLUGIN'S OWN. IPluginContext.WorkingDirectory is "where
            // the plugin should root itself — an LSP plugin starts its server here", which is the
            // folder being worked in; loadSetDirectory is where the plugin's FILES live, and rooting
            // a language server there points it at a directory holding one DLL and a sidecar. It
            // indexes nothing and every lookup returns empty, with no error to explain why.
            var context = new StartupPluginContext(session.WorkingDirectory,
                config.Settings ?? JsonDocument.Parse("{}").RootElement,
                report, children, declaredName);

            var result = await ManagedPluginLoader.Load(assemblyPath, context, ct);
            if (result is ManagedPluginLoadResult.Failed failed)
            {
                report($"plugin '{name}': {failed.Reason}");
                continue;
            }

            var loaded = (ManagedPluginLoadResult.Loaded)result;
            await session.LoadPlugin(loaded.Instance, loaded.Manifest, loadSetDirectory, ct);
        }
    }

    /// <summary>
    /// <see cref="IPluginContext"/> for a plugin loaded at startup, before any turn exists to hand it
    /// a per-call token — <see cref="Lifetime"/> is <see cref="CancellationToken.None"/> here, the
    /// same "nothing has cancelled this yet" starting point every session-scoped resource in
    /// <c>AppBootstrap</c> gets before its owning session begins tearing down.
    /// </summary>
    private sealed class StartupPluginContext(
        string workingDirectory, JsonElement settings, Action<string> report,
        ChildProcessStore children, string pluginName) : IPluginContext
    {
        public string WorkingDirectory { get; } = workingDirectory;
        public JsonElement Settings { get; } = settings;
        public IPluginLogger Logger { get; } = new ReportingLogger(report);
        public CancellationToken Lifetime { get; } = CancellationToken.None;

        /// <summary>
        /// Records the process by the SAME store <c>SessionFactory.Wire</c> already attached to
        /// <c>session.Plugins</c> — so a pid registered during this plugin's own <c>Load()</c> is
        /// reaped exactly like one registered after the session considers the plugin loaded. The
        /// START TIME IS READ BACK FROM THE OS, not stamped as <c>DateTime.UtcNow</c> — see
        /// <see cref="ChildProcessRecord.StartTimeUtc"/>'s own doc: only the OS's own value can later
        /// prove a pid was not reused by an unrelated process.
        /// </summary>
        public void RegisterChildProcess(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                children.Add(new ChildProcessRecord(processId, process.StartTime.ToUniversalTime(), pluginName));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // THE PROCESS ALREADY EXITED BETWEEN SPAWNING AND REGISTERING IT. Nothing to record
                // and nothing to reap — the same "already gone" case ChildProcessStore.Kill treats as
                // fine rather than an error.
                report($"plugin '{pluginName}': process {processId} could not be recorded ({ex.Message}) — it may have already exited.");
            }
        }

        private sealed class ReportingLogger(Action<string> report) : IPluginLogger
        {
            public void Log(string message) => report(message);
        }
    }
}
