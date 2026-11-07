using CxAgent.Core.Export;

namespace CxAgent.UI;

/// <summary>One part an export can include, and what it is called where it is offered.</summary>
/// <param name="Part">The flag.</param>
/// <param name="Label">The checkbox's words.</param>
public sealed record ExportPartLabel(ExportParts Part, string Label);

/// <summary>
/// The words an export's choices are offered in: the parts under Advanced, what a preset holds, the size readout and
/// the warnings beside it.
///
/// <para>ONE SET FOR BOTH FRONT ENDS. The terminal's export dialog and the browser's export form read them here; a
/// second copy would drift, and the same export would be described two ways — or a part added to one would never be
/// offered by the other. Plain text, no markup, so a page can show it as it is.</para>
/// </summary>
public static class ExportText
{
    /// <summary>The parts, in the order they are offered: what a person reads first, then what only an agent needs.</summary>
    public static IReadOnlyList<ExportPartLabel> Parts { get; } =
    [
        new(ExportParts.ToolCalls, "Tool calls, one line each"),
        new(ExportParts.Diffs, "Edits as diffs"),
        new(ExportParts.Header, "Session header"),
        new(ExportParts.ToolResults, "Tool results, whole"),
        new(ExportParts.Thinking, "Thinking"),
        new(ExportParts.Compaction, "Compaction summaries"),
        new(ExportParts.SystemAndInjected, "System prompt & injected"),
        new(ExportParts.SubAgents, "Sub-agent conversations"),
    ];

    /// <summary>The Readable choice's words.</summary>
    public const string ReadableLabel = "Readable · for a person";

    /// <summary>The Full choice's words.</summary>
    public const string FullLabel = "Full · for an agent";

    /// <summary>What a preset — <see cref="ExportOptions.PresetName"/> — includes, in a line, under the choice.</summary>
    public static string Describe(string preset) => preset switch
    {
        "Readable" => "What was said and done — tool calls as lines, edits as diffs.",
        "Full" => "All the model saw — tool output, thinking, the system prompt.",
        _ => "Custom — your own mix, set under Advanced.",
    };

    /// <summary>What a live session's export leaves out while a turn is still running.</summary>
    public const string RunningNote = "A turn is running; its unfinished answer is not included.";

    /// <summary>
    /// The warning <paramref name="parts"/> earn, or null. TOOL OUTPUT IS WHAT LEAKS: a file read or a command's output
    /// can hold a key or a customer's data the conversation itself never showed.
    /// </summary>
    public static string? PartsNote(ExportParts parts) =>
        (parts & ExportParts.ToolResults) != 0 ? "Full exports include tool output — check before sharing." : null;

    /// <summary>What an export will be: its size, its messages and, for a session, its sub-agents. An agent's own
    /// export has none of its own to count.</summary>
    public static string SizeLine(ExportStats stats, bool isAgent)
    {
        var line = $"≈ {ExportTarget.Size(stats.Bytes)} · {stats.Messages} message{(stats.Messages == 1 ? "" : "s")}";
        if (!isAgent) line += $" · {stats.SubAgents} sub-agent{(stats.SubAgents == 1 ? "" : "s")}";
        return line;
    }

    /// <summary>The words beside the size line when <see cref="LargeForClipboard"/> holds.</summary>
    public const string ClipboardWarning = "large for the clipboard";

    /// <summary>True when an export is big enough that a clipboard may truncate or refuse it.</summary>
    public static bool LargeForClipboard(ExportStats stats) => stats.Bytes > ExportTarget.ClipboardWarnBytes;
}
