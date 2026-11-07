using CxAgent.Core.Helpers;
using CxAgent.Core.Sessions;

namespace CxAgent.UI;

/// <summary>
/// One finished call as a call table shows it: the outcome's mark, the time and the size, ready to draw.
///
/// <para>ONE SET OF CELLS FOR BOTH FRONT ENDS. The terminal's expanded worker row and a browser's worker card draw a
/// worker's calls from here, so a call cannot read ✓ in one and ✗ in the other: Core's outcome names are matched in
/// this one place.</para>
/// </summary>
public static class CallTable
{
    /// <summary>
    /// The outcome glyph, and FOUR of them rather than two.
    ///
    /// <para>✗ ran and broke; ⊘ never ran, because permission was refused; – never finished, because the user stopped
    /// it. Those are three different problems with three different fixes, and a column that renders them all as "failed"
    /// tells a reader to go and debug the wrong one.</para>
    /// </summary>
    public static string Mark(string outcome) => outcome switch
    {
        "succeeded" => "✓",
        "denied" => "⊘",
        "cancelled" => "–",
        _ => "✗",
    };

    /// <summary>How the mark is coloured: <c>ok</c>, <c>bad</c>, or <c>skip</c> for a call that never ran or never
    /// finished.</summary>
    public static string Tone(string outcome) => outcome switch
    {
        "succeeded" => "ok",
        "denied" or "cancelled" => "skip",
        _ => "bad",
    };

    /// <summary>The time column: grouped milliseconds, or nothing for a replayed call whose time history does not
    /// keep.</summary>
    public static string Ms(ToolCallReport call) => call.Untimed ? "" : DisplayNumber.Grouped(call.DurationMs);

    /// <summary>
    /// How much the result put into the child's context, at the scale a reader compares by.
    ///
    /// <para>CHARACTERS COUNTED AS BYTES, which the column header says by naming kB. ResultChars is what the loop
    /// measures — the figure that says whether a tool is cheap or is quietly filling the window — and re-encoding it
    /// here to be exact about multi-byte runes would spend real work on a digit nobody reads at this scale.</para>
    /// </summary>
    public static string Bytes(int chars) =>
        chars <= 0 ? "0"
        : chars < 1024 ? $"{DisplayNumber.Grouped(chars)} B"
        : chars < 1024 * 1024 ? $"{DisplayNumber.Fixed(chars / 1024.0, 1)} kB"
        : $"{DisplayNumber.Fixed(chars / (1024.0 * 1024.0), 1)} MB";

    /// <summary>
    /// The line over the table: "11 calls · across 3 tools · 6.2s in tools · 1 failed".
    ///
    /// <para>EVERY NUMBER THROUGH DisplayNumber, the duration especially: a bare ":0.0" takes the current culture's
    /// decimal separator, so the same run would read "6.2s" or "6,2s" depending on the machine.</para>
    /// </summary>
    public static string Summary(IReadOnlyList<ToolCallReport> calls)
    {
        var parts = new List<string> { $"{DisplayNumber.Grouped(calls.Count)} call{(calls.Count == 1 ? "" : "s")}" };

        if (calls.Count > 0)
        {
            // "ACROSS", NOT A SECOND COUNT. This is how many DISTINCT tools the calls used, and "11 calls · 3 tools"
            // reads as two tallies of the same thing that disagree — the table below it lists eleven rows.
            var tools = calls.Select(c => c.ToolName).Distinct(StringComparer.Ordinal).Count();
            parts.Add($"across {DisplayNumber.Grouped(tools)} tool{(tools == 1 ? "" : "s")}");

            // TIME SPENT IN TOOLS, WHICH A WORKER'S CLOCK IS NOT. That clock measures how long the worker has been
            // alive — thinking, waiting on the provider — and this only the calls, so the gap between them says where
            // the time went. SUMMED FROM THE CALLS, so it cannot disagree with the rows; NOT FOR A REPLAYED CALL,
            // whose time history does not keep: a total over it would be made up.
            if (!calls.Any(c => c.Untimed))
                parts.Add($"{DisplayNumber.Fixed(calls.Sum(c => c.DurationMs) / 1000.0, 1)}s in tools");
        }

        // ZERO COUNTS ARE OMITTED: the point of the line is that a bad run announces itself, and "0 failed · 0 denied"
        // on every run that went fine makes a reader check two words before discarding them.
        AddCount(parts, calls, "failed");
        AddCount(parts, calls, "denied");
        AddCount(parts, calls, "cancelled");

        // OUTSIDE THE SANDBOX is not an outcome — the call may well have succeeded — so it is counted on its own.
        if (calls.Count(c => c.Uncontained) is > 0 and var uncontained)
            parts.Add($"{DisplayNumber.Grouped(uncontained)} uncontained");

        return string.Join(" · ", parts);
    }

    private static void AddCount(List<string> parts, IReadOnlyList<ToolCallReport> calls, string outcome)
    {
        var n = calls.Count(c => string.Equals(c.Outcome, outcome, StringComparison.Ordinal));
        if (n > 0) parts.Add($"{DisplayNumber.Grouped(n)} {outcome}");
    }
}
