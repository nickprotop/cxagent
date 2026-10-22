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

    // The installer's plugin-name rule: no '.', so `..` and a dotted staging folder are not names.
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex PluginName();

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

    /// <summary>Whether a directory is a store's folder: the one that holds <see cref="Marker"/>, which
    /// is what tells it apart from an ordinary plugin folder that happens to hold subfolders. One
    /// definition, so resolving, listing, uninstalling and registering agree on what a store is.</summary>
    public static bool IsStoreFolder(string? directory) =>
        directory is not null && File.Exists(Path.Combine(directory, Marker));

    /// <summary>
    /// The name a session registers a loaded plugin under: <c>store/folder</c> when its load set sits
    /// directly in a store's folder, its manifest name otherwise.
    ///
    /// <para>THE NAME THE USER REACHES IT BY. A store plugin is spelled <c>acme/calculator</c> in config,
    /// in F2 and in <c>/plugin load</c>; registered as its manifest's <c>calculator</c>, the session would
    /// list a plugin nobody named, call the one they did name inactive, and let <c>/plugin unwire
    /// calculator</c> stop the store's copy rather than the official one.</para>
    ///
    /// <para>DECIDED FROM THE LOAD SET, NOT FROM HOW IT WAS ASKED FOR, so a path typed into
    /// <c>plugins/acme/calculator/</c> registers the same name as <c>acme/calculator</c> does: it is the
    /// same copy either way, and two spellings of one copy would be two names for one plugin.</para>
    ///
    /// <para>FOLDER NAMES, NOT THE MANIFEST'S, because the folders are what <c>store/name</c> resolves
    /// through and what F2 lists — a store whose folder and manifest disagree is still reached by its
    /// folder.</para>
    ///
    /// <para>UNLESS THE FOLDERS ARE NOT A store/name. A marked <c>Acme/</c> or a plugin folder
    /// <c>calc.v2</c> is made by hand — the installer writes neither — and <c>Acme/calc.v2</c> is a
    /// spelling no command accepts, so a plugin registered under it could be listed but never named
    /// again to unwire it. The manifest name is one that can be.</para>
    /// </summary>
    public static string RegisteredName(string manifestName, string loadSetDirectory)
    {
        var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loadSetDirectory));
        var store = Path.GetDirectoryName(home);
        var qualified = $"{Path.GetFileName(store)}/{Path.GetFileName(home)}";
        return IsStoreFolder(store) && TrySplit(qualified, out _, out _) ? qualified : manifestName;
    }

    /// <summary>
    /// <c>store/name</c> split in two; false for anything else. BOTH SEGMENTS ARE CHECKED, because they
    /// become folder names under a plugins folder: <c>acme/..</c> or <c>../x</c> would otherwise name a
    /// path outside the store, and a typed name is not trusted to be a folder.
    /// </summary>
    public static bool TrySplit(string qualified, out string store, out string name)
    {
        var parts = qualified.Split('/');
        var ok = parts is [var s, var n] && StoreName().IsMatch(s) && PluginName().IsMatch(n);
        (store, name) = ok ? (parts[0], parts[1]) : ("", "");
        return ok;
    }
}
