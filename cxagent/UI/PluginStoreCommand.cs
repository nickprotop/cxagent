using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>What <c>/plugin store</c> was asked, and where the config and caches live.</summary>
/// <param name="Arguments">Everything after <c>/plugin</c> — <c>store add acme https://…</c>.</param>
/// <param name="Client">Null for the shared client; a test passes its own.</param>
public sealed record StoreCommandRequest(string Arguments, string ConfigPath, string ConfigDir, HttpClient? Client = null);

/// <summary>The replies that are fixed text, so a caller can tell a refusal from a result.</summary>
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
    public async Task HandleAsync(string arguments)
    {
        var reply = await RunAsync(new StoreCommandRequest(
            arguments, Path.Combine(paths.ConfigDir, "config.json"), paths.ConfigDir), CancellationToken.None);

        var refused = reply.StartsWith("usage:", StringComparison.Ordinal)
                   || reply.StartsWith("no store", StringComparison.Ordinal)
                   || reply.Contains(" was not added", StringComparison.Ordinal)
                   || reply.Contains(" is already added", StringComparison.Ordinal);
        system.EnqueueOnUIThread(() => ChatTranscriptSink.Post(window.Chat, ChatTranscriptSink.Row(
            new CxAgent.Core.Commands.Message(reply, refused ? CxAgent.Core.Commands.Severity.Warning : CxAgent.Core.Commands.Severity.Info))),
            "pluginStoreCommand.HandleAsync");
    }

    /// <summary>Runs the command and returns its reply; the only effects are config.json and a store's cache.</summary>
    public static async Task<string> RunAsync(StoreCommandRequest request, CancellationToken ct)
    {
        var words = request.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var stores = PluginStores.ReadFile(request.ConfigPath);

        switch (words)
        {
            case ["store", "add", var name, var url]:
            {
                if (PluginStores.Problem(name, url) is { } problem) return $"store '{name}' was not added: {problem}.";
                if (stores.FirstOrDefault(s => s.Name == name) is { } existing)
                    return $"store '{name}' is already added ({existing.Url}).";

                // INTO THE STORE'S OWN CACHE, so the plugin manager opened next lists it without a
                // second request, and offline later from this copy.
                var catalog = await new CatalogReader(request.Client, CatalogSources.StoreCache(request.ConfigDir, name), url)
                    .ReadAsync(ct).ConfigureAwait(false);
                if (catalog.Error is { } error) return $"store '{name}' was not added: {error}";

                PluginConfigWriter.SetStores(request.ConfigPath, [.. stores, new PluginStore(name, url)]);
                return catalog.Plugins.Count == 0
                    ? $"store '{name}' added — it lists no plugins yet."
                    : $"store '{name}' added — {Count(catalog.Plugins.Count)}: {Names(catalog.Plugins)}.";
            }

            case ["store", "remove", var name]:
                if (!stores.Any(s => s.Name == name)) return $"no store named '{name}'.";
                PluginConfigWriter.SetStores(request.ConfigPath, [.. stores.Where(s => s.Name != name)]);
                return $"store '{name}' removed. Its installed plugins stay installed and stop getting updates.";

            case ["store", "list"]:
                if (stores.Count == 0) return "no stores — /plugin store add <name> <url> adds one.";
                return string.Join("\n", stores.Select(s => $"{s.Name} — {s.Url} — {LastRead(request, s)}"));

            default:
                return StoreCommandText.Usage;
        }
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
