using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>What <c>/plugin store</c> was asked, and where the config and caches live.</summary>
/// <param name="Arguments">Everything after <c>/plugin</c> — <c>store add acme https://…</c>.</param>
/// <param name="Client">Null for the shared client; a test passes its own.</param>
public sealed record StoreCommandRequest(string Arguments, string ConfigPath, string ConfigDir, HttpClient? Client = null)
{
    /// <summary>The folder being worked in, whose <c>.cxagent/plugins</c> a store name must not clash with; null for none.</summary>
    public string? ProjectDirectory { get; init; }
}

/// <summary>What <c>/plugin store</c> answered, and whether that answer is a refusal.</summary>
public sealed record StoreReply(string Text, bool Refused);

/// <summary>The replies that are fixed text, named once for the command and its tests.</summary>
public static class StoreCommandText
{
    public const string Usage = "usage: /plugin store add <name> <url> | remove <name> | list";
}

/// <summary>
/// <c>/plugin store add | remove | list</c> — the stores in the user's config, edited by command.
///
/// <para>ADD READS THE CATALOG ONCE FIRST. A mistyped URL saved silently would surface later as a note
/// in every listing; read now, the mistake is answered while the user still has the line in front of
/// them, and nothing is written. Reading downloads no plugin and runs nothing.</para>
///
/// <para>REMOVE LEAVES INSTALLED PLUGINS ALONE. They were downloaded, checked and approved; dropping
/// the store only stops new versions from appearing, which the reply says.</para>
/// </summary>
public sealed class PluginStoreCommand(ConsoleWindowSystem system, MainWindow window, AppPaths paths)
{
    /// <param name="projectDirectory">The session's folder, whose plugins folder a new store's name is checked against.</param>
    public async Task HandleAsync(string arguments, string projectDirectory)
    {
        var reply = await RunAsync(new StoreCommandRequest(
            arguments, Path.Combine(paths.ConfigDir, "config.json"), paths.ConfigDir)
            { ProjectDirectory = projectDirectory }, CancellationToken.None);

        system.EnqueueOnUIThread(() => ChatTranscriptSink.Post(window.Chat, ChatTranscriptSink.Row(
            new CxAgent.Core.Commands.Message(reply.Text, reply.Refused ? CxAgent.Core.Commands.Severity.Warning : CxAgent.Core.Commands.Severity.Info))),
            "pluginStoreCommand.HandleAsync");
    }

    /// <summary>
    /// Runs the command and returns its reply; the only effects are config.json and a store's cache.
    ///
    /// <para>EACH REPLY SAYS WHETHER IT IS A REFUSAL, at the line that decides it. Read back from the
    /// text, "no stores — /plugin store add …" begins like the refusal "no store named …", and a plain
    /// answer would show as a warning.</para>
    /// </summary>
    public static async Task<StoreReply> RunAsync(StoreCommandRequest request, CancellationToken ct)
    {
        static StoreReply Ok(string text) => new(text, Refused: false);
        static StoreReply No(string text) => new(text, Refused: true);

        var words = request.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var stores = PluginStores.ReadFile(request.ConfigPath);

        switch (words)
        {
            case ["store", "add", var name, var url]:
            {
                if (PluginStores.Problem(name, url) is { } problem) return No($"store '{name}' was not added: {problem}.");
                if (stores.FirstOrDefault(s => s.Name == name) is { } existing)
                    return No($"store '{name}' is already added ({existing.Url}).");
                if (NameClash(request, name) is { } clash) return No($"store '{name}' was not added: {clash}");

                // INTO THE STORE'S OWN CACHE, so the plugin manager opened next lists it without a
                // second request, and offline later from this copy.
                var catalog = await new CatalogReader(request.Client, CatalogSources.StoreCache(request.ConfigDir, name), url)
                    .ReadAsync(ct).ConfigureAwait(false);
                if (catalog.Error is { } error) return No($"store '{name}' was not added: {error}");

                PluginConfigWriter.SetStores(request.ConfigPath, [.. stores, new PluginStore(name, url)]);
                return Ok(catalog.Plugins.Count == 0
                    ? $"store '{name}' added — it lists no plugins yet."
                    : $"store '{name}' added — {Count(catalog.Plugins.Count)}: {Names(catalog.Plugins)}.");
            }

            case ["store", "remove", var name]:
                if (!stores.Any(s => s.Name == name)) return No($"no store named '{name}'.");
                PluginConfigWriter.SetStores(request.ConfigPath, [.. stores.Where(s => s.Name != name)]);
                return Ok($"store '{name}' removed. Its installed plugins stay installed and stop getting updates.");

            case ["store", "list"]:
                if (stores.Count == 0) return Ok("no stores — /plugin store add <name> <url> adds one.");
                return Ok(string.Join("\n", stores.Select(s => $"{s.Name} — {s.Url} — {LastRead(request, s)}")));

            default:
                return No(StoreCommandText.Usage);
        }
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be a store's here, or null: its plugins install into
    /// <c>plugins/&lt;name&gt;/</c>, the folder an official plugin of that name has, so the name must not
    /// already be an installed plugin's folder in either plugins folder, nor a plugin the official
    /// catalog lists — the installer refuses both clashes too, but only once someone tries an install.
    ///
    /// <para>THE OFFICIAL CATALOG'S CACHED COPY, NOT A FRESH READ: <c>add</c> already reads the store's
    /// catalog over the network, and a second request only to check a name would make adding a store
    /// depend on the official site being up. No cache yet means no official name is known to clash.</para>
    /// </summary>
    private static string? NameClash(StoreCommandRequest request, string name)
    {
        IEnumerable<string> folders = [PluginResolver.GlobalFolder(request.ConfigDir)];
        if (request.ProjectDirectory is { } project) folders = folders.Append(PluginResolver.ProjectFolder(project));
        foreach (var folder in folders.Select(f => Path.Combine(f, name)))
            if (PluginInstaller.HoldsUnmarkedPlugin(folder))
                return $"{folder} holds an installed plugin, and a store's plugins would be installed into that "
                     + "same folder. Choose another name for the store.";

        var official = new CatalogReader(request.Client, CatalogSources.OfficialCache(request.ConfigDir)).ReadCached();
        return official.Plugins.Any(p => p.Name == name)
            ? $"the official catalog lists a plugin named '{name}', which installs into the same folder a store's "
              + "plugins would. Choose another name for the store."
            : null;
    }

    /// <summary>
    /// What the store's last good copy held, from its cache — no request, so listing stores costs no
    /// round of network calls and says the same thing offline.
    /// </summary>
    private static string LastRead(StoreCommandRequest request, PluginStore store)
    {
        var cached = new CatalogReader(request.Client, CatalogSources.StoreCache(request.ConfigDir, store.Name), store.Url).ReadCached();
        return cached.CachedAt is { } at
            ? $"{Count(cached.Plugins.Count)}, read {at.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "not read yet";
    }

    private static string Count(int n) => n == 1 ? "1 plugin" : $"{n} plugins";

    /// <summary>The first five names, then how many more — a store of fifty would otherwise fill the transcript.</summary>
    private static string Names(IReadOnlyList<CatalogEntry> plugins) =>
        plugins.Count <= 5
            ? string.Join(", ", plugins.Select(p => p.Name))
            : string.Join(", ", plugins.Take(5).Select(p => p.Name)) + $" and {plugins.Count - 5} more";
}
