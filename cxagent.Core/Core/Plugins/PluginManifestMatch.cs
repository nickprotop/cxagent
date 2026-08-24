using System.Text.Json;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Compares a sidecar manifest against what a plugin actually reported running — shared by
/// <see cref="ManagedPluginLoader"/> (sidecar vs. <c>IPlugin.Load</c>'s return) and
/// <c>AbiPluginLoader</c> (sidecar vs. <c>cxagent_plugin_describe</c>'s return), because both loaders
/// enforce the identical rule from PLUGINS.md: the file a user was asked to approve must describe
/// what actually runs, regardless of which loader loaded it. One comparison, not two that could
/// silently drift apart on which fields they check.
/// </summary>
internal static class PluginManifestMatch
{
    /// <summary>
    /// Names the first difference between <paramref name="sidecar"/> and <paramref name="actual"/>,
    /// or null when they agree. Compares every field the sidecar can declare — a match on name and
    /// tool names alone would let a plugin's description or gating policy drift from what the user
    /// was shown at approval time without tripping this check.
    /// </summary>
    /// <param name="sidecar">The manifest read from the sidecar file, before any binary loaded.</param>
    /// <param name="actual">What the running plugin itself reported.</param>
    /// <param name="actualSource">Names where <paramref name="actual"/> came from, in the error
    /// message — <c>"Load"</c> for a managed plugin, <c>"describe"</c> for an ABI one, so a mismatch
    /// report reads correctly for either loader without this method needing to know which one called it.</param>
    public static string? Mismatch(PluginManifest sidecar, PluginManifest actual, string actualSource)
    {
        if (sidecar.Name != actual.Name)
            return $"sidecar names '{sidecar.Name}', {actualSource} returned '{actual.Name}'.";
        if (sidecar.Version != actual.Version)
            return $"sidecar declares version '{sidecar.Version}', {actualSource} returned '{actual.Version}'.";
        if (sidecar.Instructions != actual.Instructions)
            return $"sidecar and {actualSource} disagree on 'instructions'.";
        if (sidecar.Spawns != actual.Spawns)
            return $"sidecar declares spawns={sidecar.Spawns}, {actualSource} returned spawns={actual.Spawns}.";

        var sidecarTools = sidecar.Tools.ToDictionary(t => t.Name);
        var actualTools = actual.Tools.ToDictionary(t => t.Name);

        var sidecarOnly = sidecarTools.Keys.Except(actualTools.Keys).ToList();
        if (sidecarOnly.Count > 0)
            return $"sidecar declares tool(s) {actualSource} did not return: {string.Join(", ", sidecarOnly)}.";

        var actualOnly = actualTools.Keys.Except(sidecarTools.Keys).ToList();
        if (actualOnly.Count > 0)
            return $"{actualSource} returned tool(s) the sidecar did not declare: {string.Join(", ", actualOnly)}.";

        foreach (var (name, sidecarTool) in sidecarTools)
        {
            var actualTool = actualTools[name];
            if (sidecarTool.Description != actualTool.Description)
                return $"tool '{name}': sidecar and {actualSource} disagree on 'description'.";
            if (sidecarTool.Gated != actualTool.Gated)
                return $"tool '{name}': sidecar declares gated={sidecarTool.Gated}, {actualSource} returned gated={actualTool.Gated}.";
            if (!SchemaEquals(sidecarTool.InputSchema, actualTool.InputSchema))
                return $"tool '{name}': sidecar and {actualSource} disagree on 'inputSchema'.";
        }

        return null;
    }

    /// <summary>
    /// Structural equality for two JSON Schema documents, ignoring formatting and object-key order.
    ///
    /// <para>NOT <c>GetRawText()</c>. The sidecar comes from a human-edited file and a running
    /// plugin's own description typically builds its schema in code — <c>{ "type": "object" }</c>
    /// from the sidecar and a compact <c>{"type":"object"}</c> from <see cref="JsonSerializer"/> are
    /// the SAME schema, and comparing raw text would refuse every plugin whose sidecar was merely
    /// pretty-printed. <see cref="JsonElement"/> carries no built-in deep-equals, so this walks both
    /// trees by hand.</para>
    /// </summary>
    private static bool SchemaEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().ToList();
                var bProps = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                if (aProps.Count != bProps.Count) return false;
                return aProps.All(p => bProps.TryGetValue(p.Name, out var bv) && SchemaEquals(p.Value, bv));

            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToList();
                var bItems = b.EnumerateArray().ToList();
                return aItems.Count == bItems.Count
                    && aItems.Zip(bItems, SchemaEquals).All(equal => equal);

            case JsonValueKind.String:
                return a.GetString() == b.GetString();

            case JsonValueKind.Number:
                // COMPARED AS TEXT, not as a parsed double: a JSON Schema never needs float
                // tolerance, and text comparison avoids 1 vs 1.0 silently passing as equal when a
                // plugin author would reasonably expect them written identically.
                return a.GetRawText() == b.GetRawText();

            default:
                // True, False, Null, Undefined — ValueKind equality above already decided these.
                return true;
        }
    }
}
