using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// The Plugins rail's rows: a mark that says an installed plugin's state before its name is read, the
/// name in the normal foreground, and the state muted beside it — or under it when the two do not fit.
/// </summary>
public static class PluginRailText
{
    /// <summary>
    /// ● active, ○ installed but not active, ↑ an update, ! needs attention; a space for a plugin not
    /// installed, so every name starts in the same column.
    /// </summary>
    public static string Mark(PluginRowSection section, bool active) => section switch
    {
        PluginRowSection.NeedsAttention => $"[{ColorScheme.DangerMarkup}]![/]",
        PluginRowSection.Updates => $"[{ColorScheme.CautionMarkup}]↑[/]",
        PluginRowSection.Installed => active ? $"[{ColorScheme.AccentMarkup}]●[/]" : $"[{ColorScheme.MutedMarkup}]○[/]",
        _ => " ",
    };

    /// <summary>One row: on one line when name and state fit in <paramref name="cells"/>, on two otherwise.</summary>
    public static string Row(PluginRow row, bool active, int cells)
    {
        var mark = Mark(row.Section, active);
        var name = MarkupParser.Escape(row.Name);
        if (row.State.Length == 0) return $"{mark} {name}";
        var state = MarkupParser.Escape(row.State);
        return row.Name.Length + 2 + row.State.Length <= cells - 2
            ? $"{mark} {name}  [{ColorScheme.MutedMarkup}]{state}[/]"
            : $"{mark} {name}\n  [{ColorScheme.MutedMarkup}]{state}[/]";
    }

    /// <summary>A section's heading in muted capitals, with how many rows it holds.</summary>
    public static string Heading(string label, int count) => $"[{ColorScheme.MutedMarkup}]{label} · {count}[/]";
}
