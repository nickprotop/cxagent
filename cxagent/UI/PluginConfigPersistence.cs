using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;

namespace CxAgent.UI;

/// <summary>
/// Writes the session's live plugin entries into <c>config.json</c>.
///
/// <para>THE HALF CORE DOES NOT DO, DELIBERATELY. <c>SessionManager</c>'s per-entry mutators change
/// what this process holds and stop there — Core never writes a file, because where configuration
/// LIVES is the host's business and cxagent's answer (a JSON file in a config directory) is only
/// one of them. This is cxagent's answer, and an embedder that wants another writes its own.</para>
///
/// <para>MUTATE THEN PERSIST, IN THAT ORDER, and they are separate calls rather than one: a change
/// that took effect only if the write succeeded would make a disk error silently undo something the
/// user watched happen.</para>
/// </summary>
public static class PluginConfigPersistence
{
    /// <summary>
    /// Makes the file's <c>plugins</c> block match <paramref name="live"/> — adding, updating and
    /// removing entries in ONE atomic write.
    ///
    /// <para>REMOVALS MATTER AS MUCH AS ADDITIONS. Writing only what is present would leave config
    /// naming a plugin the session no longer has, and the next start would bring it back — the user
    /// having watched it go.</para>
    ///
    /// <para>AN EXISTING ENTRY IS EDITED, NOT REPLACED. A user may have written keys this type does
    /// not model, and rebuilding the object from <see cref="PluginConfig"/> would drop them. Only
    /// the members that have a live value are set.</para>
    /// </summary>
    public static void Sync(string configPath, IReadOnlyDictionary<string, PluginConfig> live)
    {
        PluginConfigWriter.Mutate(configPath, plugins =>
        {
            foreach (var name in plugins.Select(p => p.Key).ToList())
                if (!live.ContainsKey(name))
                    plugins.Remove(name);

            foreach (var (name, entry) in live)
            {
                if (plugins[name] is not JsonObject node)
                {
                    node = new JsonObject();
                    plugins[name] = node;
                }

                node["file"] = entry.File;

                // ABSENT MEANS TRUE, matching how the reader treats it — so an enabled plugin has no
                // `enabled` key rather than an explicit `true` nobody needed to write.
                if (entry.Enabled) node.Remove("enabled");
                else node["enabled"] = false;

                if (entry.Settings is { } settings)
                    node["settings"] = JsonNode.Parse(settings.GetRawText());
            }
        });
    }

    /// <summary>
    /// Persists the manager's live entries after a change it just applied, reporting a write failure
    /// rather than throwing.
    ///
    /// <para>CALLED AFTER A MUTATOR, NOT FROM AN ANNOUNCE. <c>SessionChangeKind.Plugins</c> says the
    /// REGISTRY moved — a load or an unwire raises it too, and neither changes config. Persisting on
    /// that signal would rewrite the file at every startup and, worse, would run this diff against
    /// entries a user had hand-added since, deleting them.</para>
    ///
    /// <para>IT REPORTS RATHER THAN THROWS. A read-only file, a full disk or a config someone left
    /// malformed are all reachable, and the caller is usually on the UI thread where an escaping
    /// exception is fatal. The change already happened in memory; a failed write means saying so, not
    /// unwinding something the user watched work.</para>
    /// </summary>
    public static string? TrySync(string configPath, IReadOnlyDictionary<string, PluginConfig> live)
    {
        try
        {
            Sync(configPath, live);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or InvalidOperationException)
        {
            return $"the change is active for this session, but config.json could not be written: "
                 + $"{ex.Message}";
        }
    }
}
