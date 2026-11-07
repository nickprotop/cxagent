using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;
using CxAgent.UI;
using PluginRow = CxAgent.UI.PluginRow;

namespace CxAgent.Web;

/// <summary>
/// The plugin marketplace for a browser, behind <c>web.allow.plugins</c>: the terminal's F2 — what is installed, what
/// has an update, what needs attention, what else the catalogs offer — and the same actions on each.
///
/// <para>F2'S RULES, NOT A COPY OF THEM. The rows are <see cref="PluginManagerRows.Build"/> over
/// <see cref="PluginManagerState.Gather"/>, the installs <see cref="PluginInstaller"/>, the removals
/// <see cref="PluginRemover"/>, and loading is the session's own /plugin verbs — so a plugin still shows the hash of
/// everything it would load and asks before any of it runs, and a download still asks first, both as prompts the
/// browser answers.</para>
/// </summary>
internal sealed class WebPlugins(AppPaths paths, SessionManager manager)
{
    private readonly PluginInstaller _installer = new();
    private CatalogSet? _catalog;
    private DateTimeOffset _readAt;

    private string ConfigPath => Path.Combine(paths.ConfigDir, "config.json");

    private IReadOnlyList<string> SearchFolders(Session session) =>
        PluginDiscovery.SearchFolders(manager.Config.PluginPaths, session.WorkingDirectory, paths.ConfigDir);

    /// <summary>The catalogs, read once and again on a refresh: the official one and each configured store's.</summary>
    private async Task<CatalogSet> CatalogAsync(bool refresh)
    {
        if (_catalog is { } read && !refresh) return read;
        var url = Environment.GetEnvironmentVariable("CXAGENT_PLUGIN_CATALOG") is { Length: > 0 } custom ? custom : CatalogReader.PublishedUrl;
        var set = await CatalogSources.ReadAllAsync(
            new CatalogSetRequest(PluginStores.ReadFile(ConfigPath), paths.ConfigDir, url), CancellationToken.None).ConfigureAwait(false);
        _catalog = set;
        _readAt = DateTimeOffset.UtcNow;
        return set;
    }

    private async Task<(PluginManagerInputs Inputs, IReadOnlyList<PluginRow> Rows, CatalogSet Catalog)> StateAsync(Session session, bool refresh)
    {
        var catalog = await CatalogAsync(refresh).ConfigureAwait(false);
        var inputs = PluginManagerState.Gather(new GatherInputs(manager.Config.Plugins, session.Plugins.LoadedPluginNames,
            SearchFolders(session), catalog.Plugins, session.WorkingDirectory, paths.ConfigDir));
        return (inputs, PluginManagerRows.Build(inputs), catalog);
    }

    /// <summary>The page: every row, the actions F2 offers on it, and where an install may go.</summary>
    public async Task<JsonObject> ListAsync(Session session, bool refresh)
    {
        var (inputs, rows, catalog) = await StateAsync(session, refresh).ConfigureAwait(false);
        var loaded = session.Plugins.LoadedPluginNames;
        return new JsonObject
        {
            ["type"] = "plugins",
            ["sessionId"] = session.Id,
            ["readAt"] = _readAt.ToString("O"),
            ["error"] = catalog.OfficialError,
            ["notes"] = new JsonArray([.. catalog.Notes.Select(n => (JsonNode)n)]),
            ["destinations"] = new JsonArray([.. Destinations(session).Select(d => (JsonNode)new JsonObject { ["label"] = d.Label, ["folder"] = d.Folder })]),
            ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode)Row(r, inputs, loaded.Contains(r.Name, StringComparer.Ordinal)))]),
        };
    }

    private static JsonObject Row(PluginRow row, PluginManagerInputs inputs, bool loaded)
    {
        var actions = new JsonArray();
        switch (row.Section)
        {
            case PluginRowSection.Available:
                if (row.State.Length == 0) actions.Add("install");
                break;
            case PluginRowSection.Updates:
            case PluginRowSection.Installed:
                if (row.Section == PluginRowSection.Updates && row.CannotUpdate is null) actions.Add("update");
                actions.Add(loaded ? "deactivate" : "activate");
                if (row.Configured is { } config) actions.Add(config.Enabled ? "disable" : "enable");
                else if (FileOf(row, inputs) is not null) actions.Add("addToConfig");
                if (row.Folder is not null) actions.Add("uninstall");
                break;
            case PluginRowSection.NeedsAttention:
                if (row.Folder is not null || row.Configured is not null) actions.Add("uninstall");
                break;
        }
        var e = row.Catalog;
        return new JsonObject
        {
            ["name"] = row.Name,
            ["section"] = row.Section.ToString(),
            ["state"] = row.State,
            ["detail"] = row.Detail,
            ["folder"] = row.Folder,
            ["cannotUpdate"] = row.CannotUpdate,
            ["loaded"] = loaded,
            ["autoLoad"] = row.Configured?.Enabled,
            ["actions"] = actions,
            ["catalog"] = e is null ? null : new JsonObject
            {
                ["title"] = e.DisplayName, ["version"] = e.Version, ["description"] = e.Description, ["publisher"] = e.Publisher,
                ["license"] = e.License, ["repository"] = e.Repository, ["category"] = e.Category, ["store"] = e.Store,
                ["requires"] = e.RequiresDescription, ["requiresInstall"] = e.RequiresInstall,
                ["tools"] = new JsonArray([.. e.Tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["gated"] = t.Gated })]),
            },
        };
    }

    // CONFIG'S WORD FOR A CONFIGURED ROW, THE SIDECAR'S FOR ONE FOUND ON DISK.
    private static string? FileOf(PluginRow row, PluginManagerInputs inputs) =>
        row.Configured?.File ?? inputs.Unconfigured.FirstOrDefault(u => u.Name == row.Name)?.File;

    private string? HomeOf(PluginRow row, PluginManagerInputs inputs, Session session) =>
        row.Configured is { } config
            ? PluginDiscovery.HomeOf(row.Name, config, SearchFolders(session))
            : inputs.Unconfigured.FirstOrDefault(u => u.Name == row.Name)?.Folder;

    /// <summary>Where an install may go: this machine's plugins folder, the project's, and any configured folder.</summary>
    private IReadOnlyList<(string Label, string Folder)> Destinations(Session session)
    {
        var global = PluginResolver.GlobalFolder(paths.ConfigDir);
        var project = PluginResolver.ProjectFolder(session.WorkingDirectory);
        var list = new List<(string, string)> { ($"global — {global}", global), ($"project — {project}", project) };
        foreach (var folder in SearchFolders(session).Where(f => f != global && f != project)) list.Add(($"pluginPaths — {folder}", folder));
        return list;
    }

    /// <summary>
    /// One action on one row, answered with a sentence for the page. Every action re-reads the state first: the page's
    /// rows may be seconds old, and an action on a row that has since changed is refused rather than guessed at.
    /// </summary>
    public async Task<string> ActAsync(Session session, string name, string action, string? folder)
    {
        var (inputs, rows, catalog) = await StateAsync(session, refresh: false).ConfigureAwait(false);
        if (rows.FirstOrDefault(r => r.Name == name) is not { } row) return $"'{name}' is no longer listed.";
        switch (action)
        {
            case "install":
                if (row.Catalog is not { } entry) return $"'{name}' is not in a catalog.";
                if (folder is null || !Destinations(session).Any(d => d.Folder == folder)) return "Choose where it goes.";
                return await InstallAsync(session, entry, folder, updating: false).ConfigureAwait(false);
            case "update":
                if (row.Catalog is not { } latest || row.CannotUpdate is { } why) return row.CannotUpdate ?? $"'{name}' has no update.";
                if (Held(name, session) is { } held) return held;
                if (HomeOf(row, inputs, session) is not { } home) return $"'{name}' is not on disk.";
                return await InstallAsync(session, latest, PluginManagerRows.UpdateFolder(home, SearchFolders(session), latest.Store is not null), updating: true).ConfigureAwait(false);
            case "activate":
            {
                var once = row.Configured is { Enabled: false } ? " --once" : "";
                var load = row.Configured is not null
                    ? $"load {row.Name}{once}"
                    : $"load {PluginManagerRows.LoadTarget(row.Name, FileOf(row, inputs), inputs.Unconfigured.FirstOrDefault(u => u.Name == row.Name)?.Folder)}";
                return await RunAsync(session, load, $"'{name}' is active in this session.").ConfigureAwait(false);
            }
            case "deactivate":
                return await RunAsync(session, $"unwire {name}", $"'{name}' is no longer active in this session.").ConfigureAwait(false);
            case "enable":
                return await RunAsync(session, $"enable {name}", $"'{name}' will load at start — config.json updated.").ConfigureAwait(false);
            case "disable":
                return await RunAsync(session, $"disable {name}", $"'{name}' will not load at start — config.json updated.").ConfigureAwait(false);
            case "addToConfig":
                if (FileOf(row, inputs) is not { } file) return $"'{name}' has no file to name.";
                if (manager.AddPlugin(session, name, new PluginConfig(file)) is PluginChangeResult.Refused refused) return refused.Reason;
                return PluginConfigPersistence.TrySync(ConfigPath, manager.Config.Plugins) ?? $"'{name}' added to config.json — it loads at every start.";
            case "uninstall":
                return Uninstall(session, row, inputs, confirmed: folder == "confirmed");
            default:
                return "Unknown action.";
        }
    }

    /// <summary>
    /// The uninstall's confirmation, then the uninstall: <paramref name="confirmed"/> false answers with the sentence
    /// F2 asks before deleting, prefixed so the page knows to ask it.
    /// </summary>
    private string Uninstall(Session session, PluginRow row, PluginManagerInputs inputs, bool confirmed)
    {
        if (Held(row.Name, session) is { } held) return held;
        var home = HomeOf(row, inputs, session);
        var file = FileOf(row, inputs);
        if (home is null || file is null)
        {
            if (row.Configured is null) return $"'{row.Name}' has nothing to remove.";
            if (!confirmed) return $"confirm:'{row.Name}': no file resolves for it — remove the config entry?";
            return RemoveEntry(session, row.Name) ?? $"'{row.Name}' removed from config.json.";
        }
        var plan = PluginRemover.Plan(row.Name, file, home, SearchFolders(session), manager.Config.Plugins);
        if (plan is RemovalPlan.Blocked(var reason)) return reason;
        var removable = (RemovalPlan.Removable)plan;
        if (!confirmed) return "confirm:" + PluginRemover.Confirmation(removable);
        var failed = PluginRemover.Remove(removable);
        var entry = row.Configured is null ? null : RemoveEntry(session, row.Name);
        return failed.Count > 0 ? $"'{row.Name}': {failed.Count} file(s) could not be deleted." : entry ?? $"'{row.Name}' uninstalled.";
    }

    private string? RemoveEntry(Session session, string name) =>
        manager.RemovePlugin(session, name) is PluginChangeResult.Refused refused
            ? refused.Reason
            : PluginConfigPersistence.TrySync(ConfigPath, manager.Config.Plugins);

    /// <summary>
    /// Why a plugin's files cannot be replaced or removed now: a file this process ever loaded stays mapped until it
    /// exits, so deleting it under a session would break that session. Null when none holds it.
    /// </summary>
    private string? Held(string name, Session session)
    {
        var holder = manager.Sessions.FirstOrDefault(s => s.Plugins.EverLoadedNames.Contains(name));
        if (holder is null) return null;
        var where = ReferenceEquals(holder, session) ? "this session" : $"another session ({holder.WorkingDirectory})";
        return $"'{name}' was loaded in {where}, and its files stay in use until cxagent exits. Restart, then try again.";
    }

    private async Task<string> InstallAsync(Session session, CatalogEntry entry, string folder, bool updating)
    {
        // THE DOWNLOAD ASKS FIRST, through the session's own gate — a prompt this browser answers — stamped with the
        // session's policy, which the gate judges by.
        if (session.Services?.Gate is not { } gate) return "No permission gate is wired, so nothing can be downloaded.";
        var source = entry.DownloadFor(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)?.Url;
        var outcome = await gate.RequestAsync(new PermissionRequest(PermissionKind.Http,
            $"download '{entry.QualifiedName}' {entry.Version} from {source}", AlwaysRule: null) { Policy = session.Policy },
            CancellationToken.None).ConfigureAwait(false);
        if (!outcome.Allowed) return "Not downloaded.";

        return await _installer.InstallAsync(entry, folder, CancellationToken.None).ConfigureAwait(false) switch
        {
            InstallResult.Installed(var directory, var files) => updating
                ? $"'{entry.QualifiedName}' updated to {entry.Version}."
                : $"installed:'{entry.QualifiedName}' installed — {files.Count} file(s) in {directory}.",
            InstallResult.HashMismatch(var expected, var actual) =>
                $"Not installed — the catalog and the release disagree about this file (catalog sha256 {expected}, download sha256 {actual}). Nothing was written.",
            InstallResult.Refused(var reason) => $"Not installed: {reason} Nothing was written.",
            _ => "Not installed.",
        };
    }

    /// <summary>A /plugin verb, as F2 runs it: and only a verb that changes an entry writes config.json.</summary>
    private async Task<string> RunAsync(Session session, string argument, string done)
    {
        var status = await session.RunPluginCommand(argument, CancellationToken.None).ConfigureAwait(false);
        var changesAnEntry = PluginCommand.Parse(argument) is PluginRequest.Enable or PluginRequest.Disable;
        var failure = status is CommandStatus.Changed && changesAnEntry
            ? PluginConfigPersistence.TrySync(ConfigPath, manager.Config.Plugins)
            : null;
        return failure ?? (status is CommandStatus.Changed ? done : status is CommandStatus.Refused ? "Refused — the reason is in the conversation." : "Nothing changed — the reason is in the conversation.");
    }
}
