using System.Text;

namespace CxAgent.Plugins.CloneFinder;

/// <summary>Turns the detector's findings into the few lines a person (or a model) actually
/// reads. On a real repository the detector returns four figures of clones; the report's job is
/// to spend the reader's attention on the biggest ones and be honest about the rest.</summary>
public static class Report
{
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

        for (int i = 0; i < shown; i++)
        {
            var clone = ranked[i];
            // The label column is fixed-width so the places align into a scannable list; every
            // place is named with its line range because the point is to send the reader to the
            // code, not to reproduce it here.
            string label = $"{clone.Lines}L x{clone.Places.Count}";
            string indent = new(' ', Math.Max(label.Length + 2, 11));

            text.AppendLine();
            text.Append(label.PadRight(indent.Length));
            text.AppendLine($"{clone.Places[0].Path}:{clone.Places[0].StartLine}-{clone.Places[0].EndLine}");
            foreach (var place in clone.Places.Skip(1))
                text.AppendLine($"{indent}{place.Path}:{place.StartLine}-{place.EndLine}");
            // A few quoted lines, never the block: enough to recognise the hit without opening a
            // file, cheap enough not to spend the context this plugin exists to preserve.
            foreach (string line in clone.Fingerprint)
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
