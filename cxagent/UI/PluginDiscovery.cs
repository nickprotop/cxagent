using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Abi;
using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;

namespace CxAgent.UI;

/// <summary>
/// Finds a configured plugin's entry-point file on disk and loads it into a session — the piece the
/// APPLICATION owns rather than Core: Core accepts "here is a plugin at this path" and does not care
/// how it was found. Enumerating folders, presenting a picker and deciding what to load is
/// orchestration; Core is the infrastructure underneath it.
///
/// <para><see cref="Session.LoadPlugin"/> is the wiring nothing before this class provided: a loader
/// (<see cref="ManagedPluginLoader"/>) that can construct an <see cref="IPlugin"/> from disk existed,
/// a registry that can hold one existed, and config that names one existed — nothing yet called the
/// loader and handed its result to the session, and no production <see cref="IPluginContext"/> existed
/// either (only test fakes did).</para>
/// </summary>
public static class PluginDiscovery
{
    /// <summary>The folders searched for a configured plugin's file, in the order searched — Core's
    /// <see cref="PluginResolver.SearchFolders"/>, which says why the global folder comes before the
    /// project's. One copy, so startup and <c>/plugin load</c> cannot find different files.</summary>
    public static IReadOnlyList<string> SearchFolders(
        IReadOnlyList<string> configuredPaths, string projectDirectory, string configDir) =>
        PluginResolver.SearchFolders(configuredPaths, projectDirectory, configDir);

    /// <summary>
    /// The directory holding <paramref name="file"/> and its sidecar, searched for in
    /// <see cref="SearchFolders"/> order — first match wins, matching every other search-path
    /// resolution in this codebase. Null when no folder holds it.
    ///
    /// <para>ONE LEVEL, NOT A WALK, and the folder before its subdirectories in ordinal order —
    /// identical to <c>PluginResolver.FindLoadSetDirectory</c>, which is the Core-side counterpart
    /// this duplicates. THIS is the one that runs at startup: a change to only the other leaves a
    /// nested plugin loadable by /plugin load and invisible when cxagent starts.</para>
    /// </summary>
    public static string? FindLoadSetDirectory(string file, IReadOnlyList<string> searchFolders)
    {
        foreach (var folder in searchFolders)
        {
            if (File.Exists(Path.Combine(folder, file)))
                return folder;

            if (!Directory.Exists(folder)) continue;

            // DOT-PREFIXED DIRECTORIES SKIPPED — the installer's staging area is one, and a plugin
            // resolved out of a half-written extraction is bytes no hash covered. See PluginResolver.
            foreach (var nested in Directory.EnumerateDirectories(folder)
                         .Where(d => !Path.GetFileName(d).StartsWith('.'))
                         .OrderBy(d => d, StringComparer.Ordinal))
                if (File.Exists(Path.Combine(nested, file)))
                    return nested;
        }

        return null;
    }

    /// <summary>
    /// One plugin found in a search folder that <c>config.json</c> says nothing about — see
    /// <see cref="FindUnconfigured"/>.
    /// </summary>
    /// <param name="Name">From its sidecar's own <c>name</c>, which is what <c>/plugin load</c> takes.</param>
    /// <param name="File">The entry-point filename, for the <c>config.json</c> entry a user would write.</param>
    /// <param name="Folder">Where it was found, so the report can say which of several folders.</param>
    /// <param name="ToolCount">How many tools it declares — enough for a user to tell a real find
    /// from a stale file without loading anything.</param>
    public sealed record UnconfiguredPlugin(string Name, string File, string Folder, int ToolCount);

    /// <summary>
    /// The one startup notice for everything <see cref="FindUnconfigured"/> found, or null for none.
    ///
    /// <para>ONE BLOCK, NOT ONE PER PLUGIN. Each notice used to explain how to add a plugin, so four
    /// finds meant four five-line "System" blocks repeating the same instructions and pushing the
    /// first screen off the top. What differs between plugins is a name, a tool count and a folder;
    /// the how-to is identical, so it is said once.</para>
    ///
    /// <para>A SINGLE FIND KEEPS ITS EXACT ENTRY, because there the config line can be written out
    /// in full at no cost. With several, the entry is shown as a pattern — naming each plugin's
    /// file would repeat the lines this exists to stop repeating.</para>
    ///
    /// <para>CAPPED, with the rest counted: a plugins folder with thirty entries must not become
    /// thirty lines at startup. <c>/plugin</c> and F2 list every one.</para>
    /// </summary>
    public static string? FormatUnconfiguredNotice(IReadOnlyList<UnconfiguredPlugin> found)
    {
        if (found.Count == 0) return null;

        // The settings caveat comes from a plugin that failed live (csharp-lsp loaded by hand, then
        // refused: "requires a 'server' string in its settings"). A /plugin load carries no
        // settings, so the config route is first and the command is hedged.
        const string Caveat = "settings cannot be passed that way, so a plugin needing any will say so and unload.";

        if (found.Count == 1)
        {
            var only = found[0];
            // WHAT IS LOST RATHER THAN WHAT IS MISSING — "not configured" reads as misconfigured,
            // which this plugin is not: it is installed and fine, and what it lacks is automatic
            // loading. Same wording as the manager's row.
            return $"plugin '{only.Name}' found in {only.Folder} ({only.ToolCount} tool(s)), no auto load. "
                + "Add it under \"plugins\" in config.json:\n"
                + $"    \"{only.Name}\": {{ \"file\": \"{only.File}\" }}\n"
                // THE FILENAME, NOT THE MANIFEST NAME: PluginResolver.Resolve looks a target up in
                // config first and otherwise treats it as a FILE.
                + $"    Or `/plugin load {only.File}` to try it for this session — {Caveat}";
        }

        const int Shown = 6;
        var lines = new List<string>
        {
            $"{found.Count} plugins found, not in config (no auto load):",
        };
        foreach (var plugin in found.Take(Shown))
            lines.Add($"    {plugin.Name} — {plugin.ToolCount} tool(s), {plugin.Folder}");
        if (found.Count > Shown) lines.Add($"    … and {found.Count - Shown} more (F2 lists them all)");

        lines.Add("Add one under \"plugins\" in config.json: \"<name>\": { \"file\": \"<file>\" }");
        lines.Add($"Or `/plugin load <file>` to try one for this session — {Caveat}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Plugins present in <paramref name="searchFolders"/> that no <c>config.json</c> entry names —
    /// what a user gets after dropping a file into the plugins folder, or after an installer put one
    /// there.
    ///
    /// <para>FOUND IS NOT LOADED, AND NOT OFFERED TO BE LOADED. This returns something to SAY, never
    /// something to run: dropping a file into a well-known directory is a weaker statement of intent
    /// than naming it in config, and a startup that prompted "found a plugin, run it?" would turn a
    /// file appearing in a fixed path into a request to execute code. The caller reports these; the
    /// user decides, through <c>/plugin load</c> or by writing the config entry.</para>
    ///
    /// <para>READS SIDECARS, LOADS NO ASSEMBLY. A sidecar is data — the whole reason it ships beside
    /// the binary rather than being baked into it is that a plugin's claims can be read without
    /// running it. This method never touches the DLL.</para>
    ///
    /// <para>NEAREST FOLDER WINS, matching <see cref="FindLoadSetDirectory"/>: a project's copy of a
    /// plugin shadows a global one rather than being reported twice under one name.</para>
    /// </summary>
    /// <param name="configured">Config's own plugins, by name — a sidecar naming one of these is
    /// already the user's business and is not reported as a find. MATCHED ON THE SIDECAR'S NAME, not
    /// the filename: config keys the plugin by name, and the two need not agree.</param>
    public static IReadOnlyList<UnconfiguredPlugin> FindUnconfigured(
        IReadOnlyDictionary<string, PluginConfig> configured, IReadOnlyList<string> searchFolders)
    {
        var found = new List<UnconfiguredPlugin>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in searchFolders)
        {
            if (!Directory.Exists(folder)) continue;

            // THE FOLDER AND ITS IMMEDIATE SUBDIRECTORIES, because a plugin installed into a folder
            // of its own is invisible to a top-level scan — and this scan is the only thing that
            // tells a user an installed-but-unconfigured plugin exists at all. One level, matching
            // FindLoadSetDirectory: deeper is a plugin's own dependencies, not another plugin.
            var scanned = new List<string> { folder };
            scanned.AddRange(Directory.EnumerateDirectories(folder)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => d, StringComparer.Ordinal));

            foreach (var scan in scanned)
            foreach (var sidecar in Directory.EnumerateFiles(scan, "*.plugin.json")
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                PluginManifest? manifest;
                try
                {
                    manifest = PluginManifest.Parse(File.ReadAllText(sidecar)).Manifest;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // AN UNREADABLE FILE IS NOT A FIND. This whole method is a courtesy on the
                    // startup path; a permissions problem in a plugins folder must not stop a
                    // session from opening.
                    continue;
                }

                if (manifest is null || string.IsNullOrEmpty(manifest.Name)) continue;
                if (configured.ContainsKey(manifest.Name)) continue;
                if (!seen.Add(manifest.Name)) continue;

                // THE ENTRY-POINT FILE MUST ACTUALLY BE THERE. A sidecar whose binary was deleted is
                // a leftover, and reporting it would send a user to `/plugin load` for something
                // that cannot load. Same stem pairing every other sidecar lookup here uses.
                var stem = sidecar[..^".plugin.json".Length];
                var file = Directory.EnumerateFiles(scan, Path.GetFileName(stem) + ".*")
                    .FirstOrDefault(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".so", StringComparison.OrdinalIgnoreCase));
                if (file is null) continue;

                found.Add(new UnconfiguredPlugin(
                    manifest.Name, Path.GetFileName(file), scan, manifest.Tools.Count));
            }
        }

        return found;
    }

    /// <summary>A configured plugin that was asked for and is not running, and why.</summary>
    public sealed record PluginNotLoaded(string Name, string Why)
    {
        public override string ToString() => $"{Name} ({Why})";
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
    /// <returns>Each enabled plugin that did not end up loaded, and why — for a caller that lists
    /// what a run left out rather than reading the reports back.</returns>
    public static async Task<IReadOnlyList<PluginNotLoaded>> LoadConfiguredAsync(Session session,
        IReadOnlyDictionary<string, PluginConfig> plugins, IReadOnlyList<string> searchFolders,
        string configDir, Action<string> report, CancellationToken ct, bool allowClients = true)
    {
        var children = new ChildProcessStore(configDir);
        var notLoaded = new List<PluginNotLoaded>();

        // SAID ONCE, AT THE END — see Session.AnnouncePluginsLoaded. Each load runs with
        // announce: false and adds its manifest here instead of writing a block of its own.
        var loadedManifests = new List<PluginManifest>();

        foreach (var (name, config) in plugins.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // THE GATE, NOT A FILTER — false means no process, no tools, no prompt, nothing. A
            // disabled plugin is skipped before anything about it is even looked up.
            if (!config.Enabled) continue;

            var loadSetDirectory = FindLoadSetDirectory(config.File, searchFolders);
            if (loadSetDirectory is null)
            {
                report($"plugin '{name}': no file named '{config.File}' found under "
                     + $"{string.Join(", ", searchFolders)}.");
                notLoaded.Add(new(name, "not found"));
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
            var sidecarManifest = File.Exists(sidecarPath)
                ? PluginManifest.Parse(File.ReadAllText(sidecarPath)).Manifest
                : null;
            var declaredName = sidecarManifest?.Name;
            if (string.IsNullOrEmpty(declaredName))
            {
                report($"plugin '{name}': no usable sidecar manifest at '{sidecarPath}'.");
                notLoaded.Add(new(name, "no usable sidecar manifest"));
                continue;
            }

            // ASKED BEFORE ANY OF ITS CODE RUNS — the loader below constructs the plugin and runs its Load, so a
            // question after it would be about code already run. See Session.ApproveLoadAsync.
            if (!await session.ApproveLoadAsync(assemblyPath, loadSetDirectory, ct))
            {
                notLoaded.Add(new(name, "not approved"));
                continue;
            }

            // THE SESSION'S FOLDER, NOT THE PLUGIN'S OWN. IPluginContext.WorkingDirectory is "where
            // the plugin should root itself — an LSP plugin starts its server here", which is the
            // folder being worked in; loadSetDirectory is where the plugin's FILES live, and rooting
            // a language server there points it at a directory holding one DLL and a sidecar. It
            // indexes nothing and every lookup returns empty, with no error to explain why.
            // CORE'S CONTEXT, NOT A SECOND ONE. This path had its own near-identical copy —
            // same seven members, differing only in which assembly it read a version from, and the
            // doc for HostVersionOf names Core and the front end as interchangeable there. The
            // duplication cost a real bug: threading the session into child-process records was
            // applied to Core's context and missed this one, so an unwire went on reaping every
            // session's children while the fix looked complete.
            // CONSTRUCTED ONLY WHEN THE SIDECAR DECLARES IT. The declaration is what the load prompt
            // disclosed and what the user approved — a plugin that never asked must never hold a
            // client, or a binary that stashed the reference during its own Load keeps a live handle
            // on the session past a load the user went on to refuse or that Load itself never
            // returns from cleanly. ManagedPluginLoader's own check (IPluginClientConsumer, after
            // Load returns) is a DIFFERENT question — whether the binary can honestly use what it
            // asked for — and gates nothing about whether the reference exists at all.
            // `?.` RATHER THAN AN ASSERTION — sidecarManifest is null only for a plugin with no usable
            // sidecar, which the declaredName check above already refuses before this line is
            // reached; the guard here is defence for that invariant, and "no declaration read" has to
            // mean "no client" regardless, so the null-safe read is also the correct one.
            //
            // NO CLIENT IN A HEADLESS RUN (allowClients false): the run is exactly the user's goal, and
            // a plugin starting work of its own would be a second goal nobody asked for.
            var client = allowClients && sidecarManifest?.Client == true
                ? new SessionPluginClient(session, declaredName, session.Plugins.SubmitQueue)
                : null;

            var context = new PluginResolver.RuntimeContext(new PluginResolver.PluginRuntime(
                session.WorkingDirectory,
                config.Settings ?? JsonDocument.Parse("{}").RootElement,
                report, children, declaredName,
                // WHOSE PLUGIN THIS IS, so an unwire here reaps only what THIS session spawned.
                SessionId: session.Id, Client: client));

            // ROUTED BY THE SIDECAR'S KIND, OR THE FILE EXTENSION — THE ONE SHARED DECISION, see
            // PluginResolver.IsNative's own doc. This is the startup path's copy of the same routing
            // Session.RunLoadRequest does for /plugin load; both call the same PluginResolver method
            // rather than each growing its own test, for the reason FindLoadSetDirectory's own doc
            // already gives for this file.
            if (PluginResolver.IsNative(assemblyPath))
            {
                var hostDllPath = PluginResolver.PluginHostDllPath();
                var abiResult = await AbiPluginLoader.Load(hostDllPath, assemblyPath, context, ct);
                if (abiResult is AbiPluginLoadResult.Failed abiFailed)
                {
                    report($"plugin '{name}': {abiFailed.Reason}");
                    notLoaded.Add(new(name, abiFailed.Reason));
                    continue;
                }

                var abiLoaded = (AbiPluginLoadResult.Loaded)abiResult;
                if (await session.LoadPlugin(abiLoaded.Instance, abiLoaded.Manifest, loadSetDirectory, ct, context, client,
                        announce: false)
                    != CommandStatus.Changed)
                    notLoaded.Add(new(name, "not loaded — see warnings"));
                else
                    loadedManifests.Add(abiLoaded.Manifest);
                continue;
            }

            var result = await ManagedPluginLoader.Load(assemblyPath, context, ct);
            if (result is ManagedPluginLoadResult.Failed failed)
            {
                report($"plugin '{name}': {failed.Reason}");
                notLoaded.Add(new(name, failed.Reason));
                continue;
            }

            // NOT CHANGED MEANS NOT LOADED: declined, a name collision, or a Start that failed and
            // was unwound. The session has already said which.
            var loaded = (ManagedPluginLoadResult.Loaded)result;
            if (await session.LoadPlugin(loaded.Instance, loaded.Manifest, loadSetDirectory, ct, context, client,
                    announce: false)
                != CommandStatus.Changed)
                notLoaded.Add(new(name, "not loaded — see warnings"));
            else
                loadedManifests.Add(loaded.Manifest);
        }

        session.AnnouncePluginsLoaded(loadedManifests);
        return notLoaded;
    }

}
