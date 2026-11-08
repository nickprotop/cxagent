using System.Text;

namespace CxAgent.Core.Colleagues;

/// <summary>
/// A round of a colleague's past as text — ONE RENDERING for the distiller, which reads it to learn from, and for recall,
/// which hands it back to the colleague, so "round 9" names the same thing to both.
/// </summary>
internal static class PastRounds
{
    /// <summary>
    /// The round: who said what, and each tool call and result.
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="full">Tool results whole and the round uncut — recall's reading, which is asked for one round at a time
    /// and parts it itself. Otherwise each result is clipped to its first lines and a huge round to its two ends: the
    /// distiller reads many rounds in one call.</param>
    public static string Render(PastRound round, bool full = false)
    {
        var sb = new StringBuilder();
        sb.Append("## Round ").Append(round.Seq).Append(" — in ").Append(round.Folder).Append(", ")
            .Append(round.Ended.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        foreach (var m in round.Messages)
        {
            if (m.Role == "system") continue;
            if (m.ToolCallId is not null)
            {
                sb.Append("[tool result]\n").Append(full ? m.Content : Clip(m.Content)).Append('\n');
                continue;
            }
            if (m.Content.Length > 0) sb.Append('[').Append(m.Role).Append("]\n").Append(m.Content).Append('\n');
            foreach (var call in m.ToolCalls ?? [])
                sb.Append("[tool call] ").Append(call.Name).Append(' ')
                    .Append(call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : call.Arguments.GetRawText()).Append('\n');
        }
        var text = sb.ToString();
        if (full || text.Length <= Distiller.MaxRoundChars) return text;
        var half = Distiller.MaxRoundChars / 2;
        return text[..half] + $"\n[… {text.Length - Distiller.MaxRoundChars} characters of this round left out …]\n" + text[^half..];
    }

    private static string Clip(string result)
    {
        var lines = result.Split('\n');
        return lines.Length <= Distiller.ToolResultLines
            ? result
            : string.Join('\n', lines.Take(Distiller.ToolResultLines)) + $"\n[… {lines.Length - Distiller.ToolResultLines} more lines]";
    }
}
