using System.Text.Json;

namespace CxAgent.Core.Plugins;

/// <summary>
/// The one place a manifest's <c>"gated"</c> value becomes a <see cref="PluginGating"/>.
///
/// <para>SHARED BY BOTH LOADERS ON PURPOSE. A managed plugin's sidecar and an ABI plugin's
/// <c>describe</c> JSON are the same shape by design — see <see cref="PluginManifest"/> — and a
/// second copy of this rule is how they would drift, with a string one loader refuses becoming
/// "never ask" in the other. A permission hole spelled as a spelling mistake is exactly what the
/// refusal below exists to prevent.</para>
/// </summary>
public static class PluginGatingJson
{
    /// <summary>
    /// Reads one tool's <c>"gated"</c>. <paramref name="error"/> is non-null for a value this build
    /// does not understand, and the caller refuses the load with it rather than continuing — an
    /// unrecognised value silently meaning "never ask" would be the least safe reading available.
    /// </summary>
    public static PluginGating Parse(JsonElement value, string toolName, out string? error)
    {
        error = null;
        switch (value.ValueKind)
        {
            // ABSENT AND FALSE AGREE: a tool that says nothing about gating does not ask. The load
            // gate already approved the binary; this flag is the author marking their own sharp
            // edges, and silence is not a claim of danger.
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
            case JsonValueKind.False:
                return PluginGating.Never;

            case JsonValueKind.True:
                return PluginGating.Always;

            case JsonValueKind.String
                when string.Equals(value.GetString(), "dynamic", StringComparison.OrdinalIgnoreCase):
                return PluginGating.Dynamic;

            default:
                error = $"tool '{toolName}': unknown 'gated' value {value} — "
                      + "expected true, false or \"dynamic\".";
                return PluginGating.Never;
        }
    }
}
