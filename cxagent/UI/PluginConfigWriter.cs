using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;

namespace CxAgent.UI;

/// <summary>
/// Writes a plugin's entry into <c>config.json</c>.
///
/// <para>FOLLOWS <see cref="ProviderConfigWriter"/>, which already round-trips this exact file
/// through <see cref="JsonNode"/> on every provider-settings save. One writer's habits for one
/// file: a second mechanism carefully preserving formatting would be preserving something the next
/// provider save destroys.</para>
///
/// <para>COMMENTS ARE NOT A CONSIDERATION. <c>ProviderConfig</c> parses with default options, which
/// reject them outright — a commented config never loads at all. What the sample file uses instead
/// is <c>"//"</c>-prefixed sibling keys, which are real keys and survive a round-trip as data.</para>
///
/// <para>READ FRESH ON EVERY CALL. A user with the file open in an editor is the ordinary case, and
/// holding a parsed copy would discard whatever they changed since.</para>
/// </summary>
public static class PluginConfigWriter
{
    /// <summary>Adds or replaces one plugin's entry.</summary>
    public static void Upsert(string configPath, string name, PluginConfig entry)
        => Mutate(configPath, plugins =>
        {
            var node = new JsonObject { ["file"] = entry.File };
            if (!entry.Enabled) node["enabled"] = false;
            if (entry.Settings is { } settings)
                node["settings"] = JsonNode.Parse(settings.GetRawText());

            plugins[name] = node;
        });

    /// <summary>Drops one plugin's entry, leaving the rest of the block alone.</summary>
    public static void Remove(string configPath, string name)
        => Mutate(configPath, plugins => plugins.Remove(name));

    /// <summary>
    /// Flips one entry's <c>enabled</c> without touching its file or settings — the difference
    /// between "do not load this" and "forget this plugin exists".
    /// </summary>
    public static void SetEnabled(string configPath, string name, bool enabled)
        => Mutate(configPath, plugins =>
        {
            if (plugins[name] is JsonObject existing) existing["enabled"] = enabled;
        });

    /// <summary>
    /// Remembers the chosen theme, so it survives a restart.
    ///
    /// <para>THE KEY THE READER ALREADY LOOKS FOR. <c>theme</c> is read at startup and applied
    /// before the window is built; nothing wrote it back, so a theme picked with F9 lasted exactly
    /// as long as the process and the picker looked broken rather than deliberately temporary.</para>
    /// </summary>
    public static void SetTheme(string configPath, string theme)
        => MutateRoot(configPath, root => root["theme"] = theme);

    /// <summary>
    /// One read-modify-write over the whole plugins block.
    ///
    /// <para>INTERNAL, NOT PRIVATE, so a caller syncing MANY entries does it in one write.
    /// Composing the public per-entry methods would rewrite the file once per plugin — several
    /// atomic writes to express one change, each a window a crash can land in.</para>
    /// </summary>
    internal static void Mutate(string configPath, Action<JsonObject> change)
        => MutateRoot(configPath, root =>
        {
            // CREATED WHEN ABSENT, which is the common case: a config from the setup wizard has
            // providers, classifier, mcp, agents and orchestrator, and nothing has ever written a
            // plugins block.
            if (root["plugins"] is not JsonObject plugins)
            {
                plugins = new JsonObject();
                root["plugins"] = plugins;
            }

            change(plugins);
        });

    /// <summary>
    /// One read-modify-write over the WHOLE document, for a key that is not inside the plugins
    /// block.
    ///
    /// <para>THE SAME WRITE, not a second one. Atomicity and the 0600 mode are properties of how
    /// this file is saved rather than of what is being changed, and a second writer that got either
    /// wrong would leave a world-readable config or a truncated one — the failures this method
    /// already handles.</para>
    ///
    /// <para>READ-MODIFY-WRITE, so a key nobody here knows about survives: config.json is a file
    /// people hand-edit, and a save that serialised only what cxagent models would silently delete
    /// the rest.</para>
    /// </summary>
    internal static void MutateRoot(string configPath, Action<JsonObject> change)
    {
        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        change(root);

        // ATOMIC, like ProviderConfigWriter: a config half-written by an interrupted save is one
        // cxagent will refuse to start from.
        var tmp = configPath + ".tmp";
        File.WriteAllText(tmp,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        // config.json holds API keys. The temp file is created under the caller's umask (commonly
        // 0022, giving 0644), so without this the rename would leave a world-readable secrets file
        // — and would silently widen an existing 0600 config the first time a plugin row is written.
        ChmodOwnerOnly(tmp);
        File.Move(tmp, configPath, overwrite: true);
    }

    private static void ChmodOwnerOnly(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best-effort: a filesystem that rejects chmod must not break setup */ }
    }
}
