using System.Text;

namespace CxAgent.Core.Memory;

/// <summary>
/// The notes as the system prompt lists them: name and one-line description, the system notes first, then the project's.
///
/// <para>NAMES, NOT BODIES — the economics skills have: fifty notes in full would be permanent prefix in every request,
/// while their index is a few hundred tokens and the model reads the one that matters with <c>memory read</c>.</para>
///
/// <para>CAPPED, so a store that grew for a year cannot crowd out the conversation. Past the cap the rest are counted,
/// and <c>memory list</c> shows them all.</para>
/// </summary>
public static class MemoryIndex
{
    /// <summary>How long the list may run — about two thousand tokens.</summary>
    public const int DefaultCap = 8_000;

    /// <summary>The list, or empty when there are no notes.</summary>
    /// <param name="notes">In the store's order: system, then project, each by name.</param>
    /// <param name="cap">How many characters the list may take.</param>
    public static string Render(IReadOnlyList<MemoryNote> notes, int cap)
    {
        if (notes.Count == 0) return "";
        var ordered = notes.OrderBy(n => n.Scope).ThenBy(n => n.Name, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        MemoryScope? heading = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var note = ordered[i];
            var head = note.Scope != heading
                ? (note.Scope == MemoryScope.System ? "## System\n" : "## Project\n")
                : "";
            var line = note.Description.Length > 0 ? $"- {note.Name} — {note.Description}\n" : $"- {note.Name}\n";
            if (sb.Length + head.Length + line.Length > cap)
            {
                sb.Append($"\n{ordered.Count - i} more — memory list shows them all.\n");
                break;
            }
            sb.Append(head).Append(line);
            heading = note.Scope;
        }
        return sb.ToString();
    }
}
