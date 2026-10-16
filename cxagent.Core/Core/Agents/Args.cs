using System.Text.Json;
using CxAgent.Core.Models;

namespace CxAgent.Core.Agents;

/// <summary>Reads a tool call's arguments where a plain string read is not enough.</summary>
internal static class Args
{
    /// <summary>
    /// A boolean argument, read leniently: JSON true/false, or the strings "true"/"false" in any case — a
    /// model that stringifies scalars meant the same thing (ShellArguments documents the same leniency).
    /// Anything else is null, which the caller turns into its default rather than an error.
    /// </summary>
    public static bool? ReadBool(ToolCall call, string name)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object || !call.Arguments.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    /// <summary>
    /// A whole-number argument, read leniently for <see cref="ReadBool"/>'s reason: a JSON number, or a
    /// string holding one. Anything else is null, which the caller turns into its default.
    /// </summary>
    public static int? ReadInt(ToolCall call, string name)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object || !call.Arguments.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }
}
