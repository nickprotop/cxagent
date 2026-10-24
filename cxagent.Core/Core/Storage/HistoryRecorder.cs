using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CxAgent.Core.Agents;
using CxAgent.Core.Models;

namespace CxAgent.Core.Storage;

/// <summary>
/// Appends every message an agent's context gains to its <c>history.jsonl</c>, once, in order.
///
/// <para>WHY A SECOND RECORD: <c>context.json</c> is the context as it is now, rewritten whole on every save,
/// so after a compaction the tool calls, their results and the thinking before it exist nowhere. The long
/// sessions most worth exporting are the ones that compact. This file only grows.</para>
///
/// <para>IT CATCHES UP RATHER THAN BEING TOLD. Messages join the context in more than a dozen places — the
/// prompt, mailbox drains, nudges, steers, tool results, the task list — and a hook at each would miss the
/// next one added. <see cref="Record"/> is called where the whole list is in hand (before each request, after
/// a run) and writes whatever it has not written yet.</para>
///
/// <para>A NEW OBJECT IS NOT ALWAYS A NEW MESSAGE. Thinking blocks are dropped by rewriting a message with
/// <c>with</c>, and an unchanged task list is removed and appended again; both put a fresh object where an
/// old one was. So a message counts as already written when an equal one left the list since the last call
/// — and only then: a user sending "continue" twice has its first "continue" still in the list, so the
/// second is written.</para>
///
/// <para>FAILURES ARE SWALLOWED, as the transcript's are: a full disk costs the record, never the turn.</para>
/// </summary>
public sealed class HistoryRecorder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    // WEAK, so a message compaction dropped is not kept alive for the rest of the session by being written.
    private readonly ConditionalWeakTable<ChatMessage, object> _written = new();
    private readonly ConditionalWeakTable<ChatMessage, StrongBox<Incoming>> _labels = new();
    private List<ChatMessage> _previous = [];
    private Incoming? _next;
    private string? _thinking;
    private long _seq;

    public HistoryRecorder(string path)
    {
        _path = path;
        _seq = CountLines(path);
    }

    /// <summary>Where this agent's history is written.</summary>
    public string Path => _path;

    /// <summary>Writes every message in <paramref name="context"/> not written yet, in list order.</summary>
    public void Record(IReadOnlyList<ChatMessage> context)
    {
        lock (_gate)
        {
            var present = new HashSet<ChatMessage>(context, ReferenceEqualityComparer.Instance);
            var left = _previous.Where(m => !present.Contains(m)).Select(m => (Key: Key(m), m.IsTaskList)).ToList();

            var fresh = new List<ChatMessage>();
            foreach (var message in context)
            {
                if (_written.TryGetValue(message, out _)) continue;
                _written.Add(message, Marker);

                var leftIndex = left.FindIndex(l => l.Key == Key(message));
                if (leftIndex >= 0)
                {
                    left.RemoveAt(leftIndex);
                    continue;
                }
                fresh.Add(message);
            }

            // WHAT LEFT AND CAME BACK AS NOTHING WAS COMPACTED AWAY — the fallback that truncates when it cannot
            // summarise leaves no summary to mark, so the drop itself is the mark. A task list is the exception: an
            // emptied plan is removed and not put back, and that is no compaction.
            if (left.Any(l => !l.IsTaskList) && !fresh.Any(IsSummary))
                Append(new HistoryLine(0, DateTimeOffset.UtcNow, HistoryLine.Kinds.Compacted));

            foreach (var message in fresh)
            {
                if (IsSummary(message))
                    Append(new HistoryLine(0, Now(message), HistoryLine.Kinds.Compacted));
                Append(new HistoryLine(0, Now(message), HistoryLine.Kinds.Message, message,
                    TakeThinking(message), FromOf(message)));
            }
            _previous = [.. context];
        }
    }

    /// <summary>Labels the next prompt this agent receives — see <see cref="Incoming"/>.</summary>
    public void NoteIncoming(Incoming from)
    {
        lock (_gate) _next = from;
    }

    /// <summary>
    /// The reasoning that came with the model's next reply, as text. A provider that sends its thinking back as blocks
    /// has it on the message; one that reasons in its text — llama.cpp's &lt;think&gt; — has it nowhere else.
    /// </summary>
    public void NoteThinking(string? text)
    {
        // NULL CLEARS: a reply with no reasoning must not inherit the last one's.
        lock (_gate) _thinking = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Labels one message, for a caller that creates several at once (a mailbox drain).</summary>
    public void Label(ChatMessage message, Incoming from) => _labels.AddOrUpdate(message, new StrongBox<Incoming>(from));

    /// <summary>Marks the conversation as cleared here; what came before stays in the file.</summary>
    public void NoteCleared()
    {
        lock (_gate)
        {
            Append(new HistoryLine(0, DateTimeOffset.UtcNow, HistoryLine.Kinds.Cleared));
            _previous = [];
        }
    }

    /// <summary>
    /// Starts this history as the continuation of <paramref name="previousAgentId"/>'s, with
    /// <paramref name="loaded"/> already in its context. Those messages are that agent's record, not this one's,
    /// so none of them is written again.
    /// </summary>
    public void ContinuesFrom(string previousAgentId, IReadOnlyList<ChatMessage> loaded)
    {
        lock (_gate)
        {
            Append(new HistoryLine(0, DateTimeOffset.UtcNow, HistoryLine.Kinds.Continues, Previous: previousAgentId));
            foreach (var message in loaded)
                _written.AddOrUpdate(message, Marker);
            _previous = [.. loaded];
        }
    }

    /// <summary>
    /// A history file's first line, or null — all a caller asking "does this continue another session?" needs, read
    /// without the rest: a long session's file runs to megabytes.
    /// </summary>
    public static HistoryLine? First(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadLines(path).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            return text is null ? null : JsonSerializer.Deserialize<HistoryLine>(text, Json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every line of a history file, in order. An unreadable line — a crash mid-append — is skipped.</summary>
    public static IReadOnlyList<HistoryLine> Read(string path)
    {
        var lines = new List<HistoryLine>();
        try
        {
            if (!File.Exists(path)) return lines;
            foreach (var text in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<HistoryLine>(text, Json) is { } line) lines.Add(line);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception) { }
        return lines;
    }

    private static readonly object Marker = new();

    private Incoming? FromOf(ChatMessage message)
    {
        if (message.Role != "user" || message.ToolCallId is not null) return null;
        if (_labels.TryGetValue(message, out var label)) return label.Value;
        var from = _next;
        _next = null;
        return from;
    }

    /// <summary>
    /// An assistant message's reasoning — its blocks, or else the text noted for it — USING UP the noted text either way,
    /// so it can only ever belong to the reply it came with.
    /// </summary>
    private IReadOnlyList<string>? TakeThinking(ChatMessage message)
    {
        if (message.Role != "assistant") return null;
        var noted = _thinking;
        _thinking = null;
        return ThinkingOf(message) ?? (noted is null ? null : [noted]);
    }

    private static IReadOnlyList<string>? ThinkingOf(ChatMessage message) =>
        message.ThinkingBlocks?.Where(b => !b.Redacted && !string.IsNullOrEmpty(b.Thinking)).Select(b => b.Thinking!)
            .ToList() is { Count: > 0 } thoughts ? thoughts : null;

    private static bool IsSummary(ChatMessage message) =>
        message.Role == "assistant" && message.Content.StartsWith(Sessions.SessionCompressor.SummaryMarker, StringComparison.Ordinal);

    // EVERYTHING A REWRITE KEEPS: the thinking blocks are exactly what a rewrite drops, so they are left out. THE TIME
    // TOO, because a `with` copy keeps it while a resend has its own — so a "continue" typed as compaction dropped an
    // older "continue" is not taken for it. Not for the task list, which is rebuilt with a fresh time every lap.
    private static string Key(ChatMessage m) =>
        $"{m.Role}\u0001{m.ToolCallId}\u0001{m.IsTaskList}\u0001{string.Join(",", m.ToolCalls?.Select(c => c.Id) ?? [])}"
        + $"\u0001{(m.IsTaskList ? default : m.Timestamp):O}\u0001{m.Content}";

    private static DateTimeOffset Now(ChatMessage message) =>
        message.Timestamp != default ? message.Timestamp : DateTimeOffset.UtcNow;

    private void Append(HistoryLine line)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var numbered = line with { Seq = ++_seq };
            File.AppendAllText(_path, JsonSerializer.Serialize(numbered, Json) + Environment.NewLine);
        }
        catch (Exception) { }
    }

    private static long CountLines(string path)
    {
        try { return File.Exists(path) ? File.ReadLines(path).LongCount() : 0; }
        catch (Exception) { return 0; }
    }
}
