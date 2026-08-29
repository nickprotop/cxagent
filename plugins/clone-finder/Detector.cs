using System.Text;

namespace CxAgent.Plugins.CloneFinder;

/// <summary>One file offered to the detector. Text carries the original source because the
/// fingerprint must quote lines a reader can recognise, and by the time tokens arrive here every
/// identifier is already folded to `_` — the stream cannot reproduce them. It defaults to empty so
/// detection-only callers stay two-argument; with no text the fingerprint is simply omitted.</summary>
public record CloneSource(string Path, IReadOnlyList<Token> Tokens, string Text = "");

/// <summary>Where one copy of a clone lives, in 1-based source lines.</summary>
public record Occurrence(string Path, int StartLine, int EndLine);

/// <summary>A repeated block: the lines it spans, every place it occurs, and a few lines of the
/// original source to recognise it by. Three copies are one Clone with three Places, never three
/// pairwise findings — the report exists to save context, not to spend it restating one fact.</summary>
public record Clone(int Lines, IReadOnlyList<Occurrence> Places, IReadOnlyList<string> Fingerprint);

/// <summary>Finds token blocks repeated across (or within) sources and merges them into maximal
/// blocks.</summary>
public static class Detector
{
    /// <summary>A window's position in the corpus: which source, and the index of its first
    /// token. A struct because a corpus produces one per token and they live only for the
    /// duration of one Find call.</summary>
    private readonly record struct Pos(int Source, int Start);

    public static IReadOnlyList<Clone> Find(IReadOnlyList<CloneSource> sources, int minLines)
    {
        // The window is measured in TOKENS but sized from the LINE threshold: a block worth
        // reporting carries at least one token per line it spans, so minLines tokens never
        // overshoot the smallest reportable clone. (A block stretched to minLines lines by blank
        // lines alone can slip under this; that block is below the interesting size anyway.)
        int window = Math.Max(1, minLines);

        // Pass 1 — hash every window and bucket the positions by hash.
        var buckets = new Dictionary<int, List<Pos>>();
        for (int s = 0; s < sources.Count; s++)
        {
            var tokens = sources[s].Tokens;
            for (int start = 0; start + window <= tokens.Count; start++)
            {
                int hash = HashWindow(tokens, start, window);
                if (!buckets.TryGetValue(hash, out var positions))
                    buckets[hash] = positions = new List<Pos>();
                positions.Add(new Pos(s, start));
            }
        }

        var clones = new List<Clone>();
        // Every seed window inside the same repeated block extends to the same maximal
        // occurrences; this key collapses them so the block is reported once, not once per seed.
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;

            // EQUAL HASHES ARE A CANDIDATE, NEVER A RESULT. The bucket is regrouped by the actual
            // normalised token text before anything is reported: a hash collision reported as a
            // clone would be a false finding presented as fact — worse than a miss, because a
            // user acts on it.
            foreach (var group in bucket.GroupBy(
                p => JoinWindow(sources[p.Source].Tokens, p.Start, window), StringComparer.Ordinal))
            {
                var places = group.ToList();
                if (places.Count < 2) continue;

                var extent = Extend(sources, places, window);

                // Sorted places give a stable report and a canonical dedupe key regardless of
                // which seed window reached this block first.
                var resolved = places
                    .Select(p => new Pos(p.Source, p.Start - extent.Back))
                    .OrderBy(p => sources[p.Source].Path, StringComparer.Ordinal)
                    .ThenBy(p => p.Start)
                    .ToList();

                string key = string.Join(";", resolved.Select(p => $"{p.Source}:{p.Start}:{extent.Length}"));
                if (!reported.Add(key)) continue;

                var occurrences = resolved
                    .Select(p =>
                    {
                        var tokens = sources[p.Source].Tokens;
                        return new Occurrence(
                            sources[p.Source].Path,
                            tokens[p.Start].Line,
                            tokens[p.Start + extent.Length - 1].Line);
                    })
                    .ToList();

                // Line count is presentation-dependent — a reformatted copy spans fewer lines
                // than its twin — so both the threshold and the reported size use the largest
                // copy: squeezing one occurrence onto fewer lines must not hide the block.
                int lines = occurrences.Max(o => o.EndLine - o.StartLine + 1);
                if (lines < minLines) continue;

                clones.Add(new Clone(lines, occurrences,
                    Fingerprint(sources[resolved[0].Source], occurrences[0])));
            }
        }

        // Buckets iterate in hash order, which is no order at all; sort so the same corpus
        // always yields the same report.
        return clones
            .OrderBy(c => c.Places[0].Path, StringComparer.Ordinal)
            .ThenBy(c => c.Places[0].StartLine)
            .ToList();
    }

    /// <summary>Polynomial hash over the window's token texts. Only a grouping key, never proof
    /// of equality — Find compares the actual text before reporting anything.</summary>
    private static int HashWindow(IReadOnlyList<Token> tokens, int start, int window)
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < window; i++)
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(tokens[start + i].Text);
            return hash;
        }
    }

    /// <summary>The window's token text as one comparable string. NUL as the separator: the
    /// tokeniser never emits a token containing an actual NUL (an escaped one inside a literal is
    /// the two characters backslash-zero), so two different token sequences can never join to the
    /// same string.</summary>
    private static string JoinWindow(IReadOnlyList<Token> tokens, int start, int window)
    {
        var text = new StringBuilder();
        for (int i = 0; i < window; i++)
        {
            if (i > 0) text.Append('\0');
            text.Append(tokens[start + i].Text);
        }
        return text.ToString();
    }

    /// <summary>How far a matched window group grew: Back tokens to the left of each seed, and
    /// the total token Length. One extent serves every place because growth stops at the first
    /// disagreement anywhere.</summary>
    private readonly record struct Extent(int Back, int Length);

    /// <summary>Grows the match while every place agrees on the neighbouring token, so a run of
    /// overlapping window matches collapses into one maximal block instead of many shifted
    /// near-duplicates of the same finding.</summary>
    private static Extent Extend(IReadOnlyList<CloneSource> sources, IReadOnlyList<Pos> places, int window)
    {
        // offset is relative to each place's seed start. Running past the edge of any file ends
        // the growth exactly like a disagreement: there is no token there to agree with.
        bool AllAgree(int offset)
        {
            string? expected = null;
            foreach (var p in places)
            {
                var tokens = sources[p.Source].Tokens;
                int index = p.Start + offset;
                if (index < 0 || index >= tokens.Count) return false;
                string text = tokens[index].Text;
                if (expected is null) expected = text;
                else if (!string.Equals(expected, text, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        int forward = 0;
        while (AllAgree(window + forward)) forward++;
        int back = 0;
        while (AllAgree(-back - 1)) back++;
        return new Extent(back, window + forward + back);
    }

    /// <summary>Up to three ORIGINAL source lines from the first place, trimmed because
    /// indentation carries no identity — enough for a reader to recognise the block without
    /// opening the file. Empty when the source text was not supplied.</summary>
    private static IReadOnlyList<string> Fingerprint(CloneSource source, Occurrence place)
    {
        if (source.Text.Length == 0) return [];
        var lines = source.Text.Split('\n');
        var result = new List<string>();
        for (int line = place.StartLine;
             line <= place.EndLine && line <= lines.Length && result.Count < 3;
             line++)
        {
            result.Add(lines[line - 1].Trim());
        }
        return result;
    }
}
