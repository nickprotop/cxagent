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
            // THE SOURCES, F2'S "Stores and folders": catalogs beside the official one, and the folders searched.
            ["stores"] = new JsonArray([.. PluginStores.ReadFile(ConfigPath).Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["url"] = x.Url })]),
            ["folders"] = new JsonArray([.. manager.Config.PluginPaths.Select(f => (JsonNode)f)]),
            ["destinations"] = new JsonArray([.. Destinations(session).Select(d => (JsonNode)new JsonObject { ["label"] = d.Label, ["folder"] = d.Folder })]),
            ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode)Row(r, inputs, loaded.Contains(r.Name, StringComparer.Ordinal), ReadmeOf(r, inputs, session)))]),
        };
    }

    /// <summary>
    /// An installed plugin's own README, from beside its binary — what F2's Details shows. A plugin without one, or
    /// one only in the catalog, shows the catalog's description instead.
    /// </summary>
    private string? ReadmeOf(PluginRow row, PluginManagerInputs inputs, Session session)
    {
        if (HomeOf(row, inputs, session) is not { } home) return null;
        try
        {
            var readme = Path.Combine(home, "README.md");
            return File.Exists(readme) ? ReadmeTitle.Drop(File.ReadAllText(readme), row.Name) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static JsonObject Row(PluginRow row, PluginManagerInputs inputs, bool loaded, string? readme)
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
            ["readme"] = readme,

            // F2'S SETTINGS TAB: what the catalog says a configured plugin takes, in the author's order, and what
            // config holds for each — a string as itself, anything else as its JSON.
            ["settings"] = row.Configured is null || e is null || e.Settings.Count == 0 ? null
                : new JsonArray([.. e.Settings.Select(kv => (JsonNode)Setting(row, kv.Key, kv.Value))]),
            ["catalog"] = e is null ? null : new JsonObject
            {
                ["title"] = e.DisplayName, ["version"] = e.Version, ["description"] = e.Description, ["publisher"] = e.Publisher,
                ["license"] = e.License, ["repository"] = e.Repository, ["category"] = e.Category, ["store"] = e.Store,
                ["requires"] = e.RequiresDescription, ["requiresInstall"] = e.RequiresInstall,
                ["tools"] = new JsonArray([.. e.Tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["gated"] = t.Gated })]),
            },
        };
    }

    /// <summary>
    /// One setting for the page. A SECRET-NAMED ONE IS NOT SENT — an API token in a plugin's settings is as much a key
    /// as a provider's — only whether it is set and its last four characters; left empty, a save keeps it.
    /// </summary>
    internal static JsonObject Setting(PluginRow row, string key, string prose)
    {
        var text = SettingText(row, key);
        var secret = WebConfig.SecretName.IsMatch(key);
        return new JsonObject
        {
            ["key"] = key, ["prose"] = prose, ["secret"] = secret,
            ["value"] = secret ? "" : text,
            ["saved"] = secret ? CxAgent.UI.Config.SecretField.Describe(text) : null,
        };
    }

    private static string SettingText(PluginRow row, string key) =>
        row.Configured?.Settings is { ValueKind: System.Text.Json.JsonValueKind.Object } settings && settings.TryGetProperty(key, out var value)
            ? value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";

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
    public async Task<string> ActAsync(Session session, string name, string action, string? folder, JsonObject? values = null)
    {
        // THE SOURCES ARE NOT A ROW'S: stores and folders are changed whatever plugin is selected.
        switch (action)
        {
            case "addStore":
            {
                var url = (values?["url"] as JsonValue)?.GetValue<string>()?.Trim() ?? "";
                if (name.Trim().Length == 0 || url.Length == 0) return "Type the store's name and its catalog URL.";
                if (name.Any(char.IsWhiteSpace) || url.Any(char.IsWhiteSpace)) return "A store's name and URL have no spaces.";
                var added = await StoreAsync($"store add {name.Trim()} {url}", session).ConfigureAwait(false);
                _catalog = null;
                return added;
            }
            case "removeStore":
            {
                var removed = await StoreAsync($"store remove {name}", session).ConfigureAwait(false);
                _catalog = null;
                return removed;
            }
            case "addFolder":
            {
                var listed = manager.Config.PluginPaths;
                if (PluginFolders.Problem(name, listed) is { } problem) return problem;
                return Folders(session, [.. listed, PluginFolders.Clean(name)]) ?? $"{PluginFolders.Clean(name)} added to the folders searched.";
            }
            case "removeFolder":
                return Folders(session, [.. manager.Config.PluginPaths.Where(f => f != name)]) ?? $"{name} is no longer searched.";
            case "moveFolderUp":
            {
                var list = manager.Config.PluginPaths.ToList();
                var at = list.IndexOf(name);
                if (at <= 0) return "It is already first.";
                (list[at - 1], list[at]) = (list[at], list[at - 1]);
                return Folders(session, list) ?? $"{name} is searched earlier now.";
            }
            case "saveSettings":
                return await SaveSettingsAsync(session, name, values ?? []).ConfigureAwait(false);
        }

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

    private async Task<string> StoreAsync(string arguments, Session session)
    {
        try
        {
            var reply = await PluginStoreCommand.RunAsync(new StoreCommandRequest(arguments, ConfigPath, paths.ConfigDir)
                { ProjectDirectory = session.WorkingDirectory }, CancellationToken.None).ConfigureAwait(false);
            return reply.Text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"config.json could not be written: {e.Message}";
        }
    }

    /// <summary>The folders searched for plugins, written and made live as F2 sets them; null when it worked.</summary>
    private string? Folders(Session session, IReadOnlyList<string> folders)
    {
        _ = session;
        try { PluginConfigWriter.SetPluginPaths(ConfigPath, folders); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"config.json could not be written: {e.Message}";
        }
        manager.SetPluginPaths(folders);
        return null;
    }

    /// <summary>
    /// F2's settings Save: the catalog's keys from what the page sent — JSON where it reads as JSON, else the text —
    /// keys the catalog does not list kept as they were, and nothing at all clearing the entry's settings.
    /// </summary>
    private async Task<string> SaveSettingsAsync(Session session, string name, JsonObject values)
    {
        var (_, rows, _) = await StateAsync(session, refresh: false).ConfigureAwait(false);
        if (rows.FirstOrDefault(r => r.Name == name) is not { Configured: { } configured, Catalog: { } entry }) return $"'{name}' has no settings to save.";
        var settings = new JsonObject();
        if (configured.Settings is { ValueKind: System.Text.Json.JsonValueKind.Object } existing)
            foreach (var property in existing.EnumerateObject())
                if (!entry.Settings.ContainsKey(property.Name)) settings[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        foreach (var key in entry.Settings.Keys)
        {
            var text = ((values[key] as JsonValue)?.GetValue<string>() ?? "").Trim();

            // A SECRET LEFT EMPTY KEEPS WHAT CONFIG HOLDS: the page was never sent it, so empty is not a request to clear.
            if (text.Length == 0 && WebConfig.SecretName.IsMatch(key)
                && configured.Settings is { ValueKind: System.Text.Json.JsonValueKind.Object } held && held.TryGetProperty(key, out var kept))
            {
                settings[key] = JsonNode.Parse(kept.GetRawText());
                continue;
            }
            if (text.Length == 0) continue;
            JsonNode value;
            try { value = JsonNode.Parse(text) ?? JsonValue.Create(text)!; }
            catch (System.Text.Json.JsonException) { value = JsonValue.Create(text)!; }
            settings[key] = value;
        }
        PluginChangeResult result;
        if (settings.Count == 0) result = manager.SetPluginSettings(session, name, null);
        else
        {
            using var document = System.Text.Json.JsonDocument.Parse(settings.ToJsonString());
            result = manager.SetPluginSettings(session, name, document.RootElement.Clone());
        }
        if (result is PluginChangeResult.Refused refused) return refused.Reason;
        return PluginConfigPersistence.TrySync(ConfigPath, manager.Config.Plugins) ?? $"'{name}' settings saved.";
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
