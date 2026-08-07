using System.Text;
using CxAgent.Core.Plugins;

namespace CxAgent.UI;

/// <summary>
/// Shared schema-text generation for tool definitions that reference the plugin registry
/// (CreatePlanTool's `jobs`, DiagnoseTool's `jobs_to_insert`). Both need the same plugin type-name
/// enum and the same per-plugin params reference text; extracted here so the two tool schemas cannot
/// drift apart as plugins change.
/// </summary>
internal static class PluginSchemaText
{
    /// <summary>Plugins ordered deterministically, for stable schema output.</summary>
    public static IReadOnlyList<IJobPlugin> OrderedPlugins(PluginRegistry registry) =>
        registry.All.OrderBy(p => p.TypeName, StringComparer.Ordinal).ToList();

    public static string[] TypeNames(IReadOnlyList<IJobPlugin> plugins) =>
        plugins.Select(p => p.TypeName).ToArray();

    /// <summary>
    /// Renders each plugin's real JobSchema as "type (DisplayName): name (type, required|optional) —
    /// description" lines. Used both as a `params` property's own description (so it appears in the
    /// schema JSON) and folded into a tool's top-level description for providers that surface tool
    /// descriptions more prominently than nested schema text. A JSON-Schema `oneOf` keyed on `type`
    /// would be the formally precise way to express "params depends on type", but not every
    /// OpenAI-compatible provider — including the local llama.cpp servers cxagent targets — handles
    /// `oneOf` well; a plain-text reference works identically everywhere.
    /// </summary>
    public static string BuildParamsPropertyDescription(IReadOnlyList<IJobPlugin> plugins)
    {
        var sb = new StringBuilder("Plugin-specific parameters — must match the reference for this job's `type`.");

        foreach (var plugin in plugins)
        {
            var schema = plugin.GetSchema();
            sb.Append($"\n{schema.TypeName} ({schema.DisplayName}):");

            if (schema.Params.Count == 0)
            {
                sb.Append(" no params.");
                continue;
            }

            foreach (var p in schema.Params)
            {
                var requiredness = p.Required ? "required" : "optional";
                sb.Append($" {p.Name} ({p.Type}, {requiredness})");
                if (!string.IsNullOrWhiteSpace(p.Description))
                    sb.Append($" — {p.Description};");
                else
                    sb.Append(';');
            }
        }

        return sb.ToString();
    }
}
