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

    private static void Mutate(string configPath, Action<JsonObject> change)
    {
        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        // CREATED WHEN ABSENT, which is the common case: a config from the setup wizard has
        // providers, classifier, mcp, agents and orchestrator, and nothing has ever written a
        // plugins block.
        if (root["plugins"] is not JsonObject plugins)
        {
            plugins = new JsonObject();
            root["plugins"] = plugins;
        }

        change(plugins);

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
