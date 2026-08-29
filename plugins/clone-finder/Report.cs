using System.Text;

namespace CxAgent.Plugins.CloneFinder;

/// <summary>Turns the detector's findings into the few lines a person (or a model) actually
/// reads. On a real repository the detector returns four figures of clones; the report's job is
/// to spend the reader's attention on the biggest ones and be honest about the rest.</summary>
public static class Report
{
    private const int PlacesShown = 3;
    private const int FingerprintShown = 2;

    public static string Render(IReadOnlyList<Clone> clones, int maxResults, int belowMinimum)
    {
        var text = new StringBuilder();

        if (clones.Count == 0)
        {
            text.Append("No duplication found");
            if (belowMinimum > 0)
                text.Append($" ({belowMinimum} repeats fell below the minimum size)");
            text.Append('.');
            return text.ToString();
        }

        // Lines x places, biggest first: the order a person would fix them in — a 33-line block
        // pasted 29 times outranks a 211-line block pasted twice because it accounts for more
        // duplicated source. Ties break on location so the same corpus always renders the same.
        var ranked = clones
            .OrderByDescending(c => c.Lines * c.Places.Count)
            .ThenBy(c => c.Places[0].Path, StringComparer.Ordinal)
            .ThenBy(c => c.Places[0].StartLine)
            .ToList();

        int shown = Math.Min(maxResults, ranked.Count);
        text.AppendLine($"{clones.Count} duplicated blocks. Top {shown} by size (lines x places):");
        text.AppendLine();

        for (int i = 0; i < shown; i++)
        {
            var clone = ranked[i];
            // The label column is fixed-width so the places align into a scannable list; each
            // place is named with its line range because the point is to send the reader to the
            // code, not to reproduce it here. No blank line between findings: the label at
            // column zero already marks each start, and twenty separators would be twenty lines
            // of whitespace in a report measured by the context it costs.
            string label = $"{clone.Lines}L x{clone.Places.Count}";
            string indent = new(' ', Math.Max(label.Length + 2, 11));

            text.Append(label.PadRight(indent.Length));
            text.AppendLine($"{clone.Places[0].Path}:{clone.Places[0].StartLine}-{clone.Places[0].EndLine}");
            // Three places, then a count. Deciding whether to act needs the pattern — which
            // files, and that it is everywhere — not the roll call: one 29-place finding listed
            // in full spends thirty lines, and a full report of them costs the very context this
            // plugin exists to save. Three is the least that still shows a cross-file pattern
            // (two names that differ plus proof it goes on); the count keeps the list honest,
            // the same rule the result cap follows below.
            foreach (var place in clone.Places.Skip(1).Take(PlacesShown - 1))
                text.AppendLine($"{indent}{place.Path}:{place.StartLine}-{place.EndLine}");
            if (clone.Places.Count > PlacesShown)
                text.AppendLine($"{indent}+ {clone.Places.Count - PlacesShown} more places");
            // A couple of quoted lines, never the block: enough to recognise the hit without
            // opening a file, cheap enough not to spend the context this plugin exists to
            // preserve. Two, not the detector's three: the third line rarely adds identity, and
            // across a twenty-finding report it alone costs twenty lines.
            foreach (string line in clone.Fingerprint.Take(FingerprintShown))
                text.AppendLine($"{indent}  | {line}");
        }

        // THE CAP ADMITS ITSELF. A truncated list that does not say so reads as the whole truth,
        // and "20 clones" on a repository with 1781 would be a lie by omission.
        int omitted = ranked.Count - shown;
        if (omitted > 0 || belowMinimum > 0) text.AppendLine();
        if (omitted > 0)
            text.AppendLine($"{omitted} smaller clones omitted by the result cap.");
        if (belowMinimum > 0)
            text.AppendLine($"{belowMinimum} repeats fell below the minimum size and are not listed.");

        return text.ToString().TrimEnd('\n');
    }
}
