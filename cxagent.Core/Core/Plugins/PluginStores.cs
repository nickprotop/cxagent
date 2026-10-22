using System.Text.Json;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Plugins;

/// <summary>One store: a name the user's plugins from it are known by, and its catalog's URL.</summary>
public sealed record PluginStore(string Name, string Url);

/// <summary>
/// Stores — other people's plugin catalogs, beside the official one — as config declares them, and
/// the one spelling of a plugin that comes from one: <c>store/name</c>.
///
/// <para>IN THE USER'S CONFIG ONLY. A store is somewhere plugins are listed from, and listing is the
/// first step to running one; a repository someone clones must not be able to add one.</para>
///
/// <para>A STORE PLUGIN IS ALWAYS QUALIFIED. A bare name always means the official catalog, so adding
/// a store can never change what an existing command or config entry reaches.</para>
/// </summary>
public static partial class PluginStores
{
    /// <summary>The official catalog's own name: <c>cxagent/repo-map</c> is <c>repo-map</c>.</summary>
    public const string Official = "cxagent";

    /// <summary>
    /// The file that marks a folder under a plugins folder as a store's, so its plugins — one level
    /// further down than any other — are found by name, and a stray folder of plugins is not taken for
    /// a store. Dot-named, so the one-level plugin search skips it.
    /// </summary>
    public const string Marker = ".cxagent-store";

    [GeneratedRegex("^[a-z0-9-]{1,32}$")] private static partial Regex StoreName();

    /// <summary>Why this name or URL cannot be a store, or null when it can.</summary>
    public static string? Problem(string name, string url)
    {
        if (!StoreName().IsMatch(name)) return "a store name is lowercase letters, digits and '-', 1-32 characters";
        if (name == Official) return "'cxagent' is the official catalog's name";
        var ok = Uri.TryCreate(url, UriKind.Absolute, out var u)
                 && (u.Scheme == Uri.UriSchemeHttps
                     || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));
        return ok ? null : "a store's URL must be https (or http://localhost for testing)";
    }

    /// <summary>The <c>stores</c> block of a config document; problems go to <paramref name="errors"/>.</summary>
    public static IReadOnlyList<PluginStore> Read(JsonElement root, List<string> errors)
    {
        var stores = new List<PluginStore>();
        if (!root.TryGetProperty("stores", out var block)) return stores;
        if (block.ValueKind != JsonValueKind.Array) { errors.Add("stores is not a list."); return stores; }
        foreach (var e in block.EnumerateArray())
        {
            var name = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
            var url = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
            if (Problem(name, url) is { } problem) { errors.Add($"stores.{name}: {problem}."); continue; }
            if (stores.Any(s => s.Name == name)) { errors.Add($"stores: '{name}' is listed twice."); continue; }
            stores.Add(new PluginStore(name, url));
        }
        return stores;
    }

    /// <summary>
    /// The stores config.json declares right now, read fresh — what F2 and <c>/plugin get</c> use, so a
    /// store added a moment ago by <c>/plugin store add</c> is there without a restart. A file that will
    /// not read gives no stores: startup already refused it, loudly.
    /// </summary>
    public static IReadOnlyList<PluginStore> ReadFile(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            return Read(doc.RootElement, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// <summary><c>store/name</c> split in two; false for anything else.</summary>
    public static bool TrySplit(string qualified, out string store, out string name)
    {
        var parts = qualified.Split('/');
        (store, name) = parts is [var s, var n] && s.Length > 0 && n.Length > 0 ? (s, n) : ("", "");
        return store.Length > 0;
    }
}
