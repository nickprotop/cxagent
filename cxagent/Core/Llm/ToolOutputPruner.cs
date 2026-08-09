using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

/// <summary>
/// Frees context by emptying tool results the conversation has already superseded.
///
/// <para>DEDUPLICATION, NOT AGE. An earlier version cleared the oldest tool output regardless of
/// what it contained, which is the aggressive reading of this idea and loses information: a
/// summariser at least READS what it discards and can carry a detail forward, while a tombstone
/// throws it away unread. That is presumably why opencode, whose implementation this was modelled
/// on, ships its equivalent OFF by default — its schema says so outright ("Enable pruning of old
/// tool outputs (default: false)"), a detail missed when only the implementation was read.</para>
///
/// <para>The rule here is narrower and loses nothing. A result is cleared only when the SAME file is
/// read again later in the conversation, so a fresher copy of exactly that content already exists
/// further down. The model cannot lose information it still holds — and on a file edited between the
/// two reads, the older copy was not merely redundant but WRONG, describing a state of the tree that
/// no longer exists. Cline reaches the same conclusion from the same pressure, deduplicating repeated
/// reads of a file before it will consider truncating anything.</para>
///
/// <para>Only SNAPSHOTS of a named file are deduplicated — reads, and the writes whose results echo
/// the file back. A search or a shell command names no single artefact that a later call supersedes
/// — two greps of one path are different questions — so their results are never touched, however
/// old.</para>
///
/// <para>THE BODY GOES, THE MESSAGE STAYS — and that is what makes this safe. A tool result is bound
/// to its call by <see cref="ChatMessage.ToolCallId"/>, and providers reject a result whose call is
/// missing (or a call whose result is). Any scheme that REMOVES messages has to reason about that
/// pairing; this one never can, because the message and its id remain exactly where they were. Only
/// the content is replaced with a tombstone, so the model still sees that it read the file and what
/// it asked for — the narrative survives, the superseded bytes do not.</para>
/// </summary>
public static class ToolOutputPruner
{
    /// <summary>
    /// Below this fraction of the tool output present, pruning does nothing.
    ///
    /// <para>Rewriting history invalidates the prompt cache from the first changed message onward, so
    /// a prune reclaiming a trivial amount costs more than it saves. A FRACTION rather than a fixed
    /// count, because the absolute constants this replaced — translated from opencode's 40,000/20,000
    /// TOKENS — were sized against a window-sized trigger and made pruning need more pressure than it
    /// takes to trigger summarisation. Measured on a live drive: no tombstone was ever written and two
    /// ~25-second summarising calls ran instead, on a context holding three tool results of 32k, 35k
    /// and 39k characters that were exactly what pruning is for.</para>
    /// </summary>
    public const double MinimumGainFraction = 0.10;

    /// <summary>What a cleared result leaves behind, so the model knows it is looking at a gap.</summary>
    public const string Tombstone = "[old tool result cleared to free context]";

    /// <summary>How much a prune freed, and how many results it emptied.</summary>
    public readonly record struct PruneResult(int CharsFreed, int ResultsCleared)
    {
        public bool Pruned => ResultsCleared > 0;
    }

    /// <summary>
    /// Clears superseded tool results in place — those whose file has since been read again.
    /// </summary>
    /// <param name="messages">The conversation, oldest first. Mutated.</param>
    /// <param name="minimumGain">Do nothing unless at least this much would be freed. Defaults to a
    /// fraction of the tool output present, so the rule works at any compaction threshold.</param>
    public static PruneResult Prune(List<ChatMessage> messages, int? minimumGain = null)
    {
        var toolChars = 0;
        foreach (var m in messages)
            if (m.ToolCallId is not null && m.Content != Tombstone)
                toolChars += m.Content?.Length ?? 0;

        var floor = minimumGain ?? (int)(toolChars * MinimumGainFraction);

        // WHICH CALL PRODUCED WHICH RESULT. The result carries only an id; the target lives on the
        // assistant message that made the call, so the two have to be joined before anything can be
        // said about what a result is a copy OF.
        var targetById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (m.ToolCalls is not { Count: > 0 }) continue;
            foreach (var call in m.ToolCalls)
            {
                if (call.Id is not { Length: > 0 } id) continue;
                if (TargetOf(call) is { } target) targetById[id] = target;
            }
        }

        // THE LAST READ OF EACH FILE WINS. Walking forward, every earlier read of a file that is read
        // again later is superseded — the model has a fresher copy of the same thing further down,
        // and on a file that was edited in between the older copy is not merely redundant but WRONG.
        var lastIndexFor = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.ToolCallId is null || m.Content == Tombstone) continue;
            if (!targetById.TryGetValue(m.ToolCallId, out var target)) continue;
            lastIndexFor[target] = i;
        }

        var candidates = new List<int>();
        var wouldFree = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.ToolCallId is null || m.Content == Tombstone) continue;
            var length = m.Content?.Length ?? 0;
            if (length <= Tombstone.Length) continue;
            if (!targetById.TryGetValue(m.ToolCallId, out var target)) continue;
            if (lastIndexFor[target] == i) continue;   // this IS the freshest copy — never clear it

            candidates.Add(i);
            wouldFree += length - Tombstone.Length;
        }

        if (wouldFree < floor) return new PruneResult(0, 0);

        foreach (var i in candidates)
            messages[i] = messages[i] with { Content = Tombstone };

        return new PruneResult(wouldFree, candidates.Count);
    }

    /// <summary>
    /// What a call READ, as a key, or null when it is not a read of an identifiable thing.
    ///
    /// <para>Only reads are deduplicated. A search or a shell command names no single artefact that a
    /// later call can be said to supersede — two greps of the same path are different questions —
    /// and clearing one because another followed it would discard an answer nothing else holds.</para>
    /// </summary>
    private static string? TargetOf(ToolCall call)
    {
        if (!SnapshotTools.Contains(call.Name)) return null;
        if (call.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!call.Arguments.TryGetProperty("path", out var path)) return null;
        if (path.ValueKind != System.Text.Json.JsonValueKind.String) return null;
        var value = path.GetString();
        // The PATH is the key, with no tool prefix: a read and a write of one file are snapshots of
        // the same thing, so an edit must supersede the read that preceded it.
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Calls whose result is a snapshot of one named file, so a later call on the same file replaces
    /// it.
    ///
    /// <para>WRITES COUNT, NOT JUST READS, and leaving them out was a real gap. A
    /// <c>replace_in_file</c> result echoes the file's new contents back ("…the file now reads: …"),
    /// which is a fresh copy of exactly what a read would have produced — so an edit supersedes an
    /// earlier read, and a second edit supersedes the first. Deduplicating only <c>read_file</c>
    /// meant an edit-heavy session accumulated a full copy per edit, which is precisely the session
    /// shape this exists for. Cline covers the same four paths (read, replace, write, and its file
    /// mentions) for the same reason.</para>
    ///
    /// <para>Keyed on the PATH alone rather than on path-plus-tool, because a read and a write of one
    /// file are copies of the same thing: after an edit, the read that preceded it is stale in the
    /// way that matters most — it describes a state of the tree that no longer exists.</para>
    /// </summary>
    private static readonly HashSet<string> SnapshotTools =
        new(StringComparer.Ordinal) { "read_file", "replace_in_file", "write_file" };
}
