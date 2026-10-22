using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.UI;

namespace CxAgent.Headless;

public sealed record PluginChoice(IReadOnlyDictionary<string, PluginConfig> Load,
    IReadOnlyList<string> Skipped, IReadOnlyList<string> RunApprovedHashes, string? Error);

public sealed record PluginSelection(IReadOnlyDictionary<string, PluginConfig> Configured,
    HeadlessOptions Options, IReadOnlyList<string> SearchFolders, PermissionRulesStore Rules, string WorkingDir);

/// <summary>
/// Which configured plugins a headless run loads.
///
/// <para>DECIDED BEFORE ANY PLUGIN CODE RUNS. Loading runs the plugin's own Load before approval is
/// asked, so a plugin nobody approved is left out here rather than declined after it ran.</para>
/// </summary>
public static class HeadlessPlugins
{
    public static PluginChoice Select(PluginSelection s)
    {
        foreach (var name in s.Options.Plugins)
            if (!s.Configured.ContainsKey(name))
                return new(new Dictionary<string, PluginConfig>(), [], [],
                    $"--plugin {name}: no such plugin in config. Configured: "
                    + (s.Configured.Count == 0 ? "none" : string.Join(", ", s.Configured.Keys)) + ".");

        if (s.Options.NoPlugins)
            return new(new Dictionary<string, PluginConfig>(), [], [], null);

        var load = new Dictionary<string, PluginConfig>();
        var skipped = new List<string>();
        var runApproved = new List<string>();

        foreach (var (name, config) in s.Configured)
        {
            var named = s.Options.Plugins.Contains(name);

            // NAMING IT IS THE INTENT. enabled:false keeps a plugin out of every run by default; a
            // --plugin naming it asks for this run anyway, the same way naming one approves it.
            if (!config.Enabled && !named) continue;

            if (PluginDiscovery.HomeOf(name, config, s.SearchFolders) is not { } dir)
            {
                skipped.Add($"{name} (not found)");
                continue;
            }

            // A LOAD SET THAT CANNOT BE IDENTIFIED — a link out of it, a loop — is skipped with the reason.
            var identity = PluginIdentity.InspectLoadSet(dir);
            if (identity.Hash is not { } hash)
            {
                skipped.Add($"{name} ({identity.Refusal})");
                continue;
            }

            if (named) runApproved.Add(hash);
            if (named || s.Rules.Matches(s.WorkingDir, PermissionKind.Plugin, hash))
                load[name] = config with { Enabled = true };
            else
                skipped.Add($"{name} (not approved for unattended runs — approve it once with \"Always allow\" in the TUI, or name it with --plugin)");
        }

        return new(load, skipped, runApproved, null);
    }
}
