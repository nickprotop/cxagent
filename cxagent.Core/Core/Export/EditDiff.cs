using System.Text;
using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;

namespace CxAgent.Core.Export;

/// <summary>
/// A file change, as the body of a <c>```diff</c> fence: what a reader of an export wants to see of an edit.
///
/// <para>FROM THE CALL, NOT THE FILE: the export may be read long after the file moved on, and the call is the
/// change as it was made. <c>replace_in_file</c> names the exact text it replaced, so its lines are the removal;
/// <c>write_file</c> is all addition.</para>
/// </summary>
public static class EditDiff
{
    /// <summary>How much of a written file is shown; a reader wants its shape, not its whole body.</summary>
    public const int MaxWrittenLines = 60;

    /// <summary>The diff for a file-changing call, or null for any other call.</summary>
    public static string? For(ToolCall call)
    {
        var path = Arg(call, "path") ?? "";
        var text = new StringBuilder();
        switch (call.Name)
        {
            case Tool.ReplaceInFile:
                text.Append("--- ").Append(path).Append('\n').Append("+++ ").Append(path).Append('\n');
                foreach (var line in Lines(Arg(call, "pattern"))) text.Append('-').Append(line).Append('\n');
                foreach (var line in Lines(Arg(call, "replacement"))) text.Append('+').Append(line).Append('\n');
                break;

            case Tool.WriteFile:
                var written = Lines(Arg(call, "content"));
                text.Append("+++ ").Append(path).Append('\n');
                foreach (var line in written.Take(MaxWrittenLines)) text.Append('+').Append(line).Append('\n');
                if (written.Count > MaxWrittenLines) text.Append($"… {written.Count - MaxWrittenLines} more lines\n");
                break;

            default:
                return null;
        }
        return text.ToString().TrimEnd('\n');
    }

    private static List<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. text.Replace("\r\n", "\n").Split('\n')];

    private static string? Arg(ToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
