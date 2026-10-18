using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Export;

/// <summary>What an export says about the conversation at its top.</summary>
/// <param name="AgentId">The newest agent of the session — the id /sessions lists.</param>
/// <param name="Title">The session's title, or null before it had one.</param>
/// <param name="WorkingDir">The folder it worked in.</param>
/// <param name="Started">When its first recorded entry was written.</param>
/// <param name="Ended">When its last was.</param>
/// <param name="InputTokens">Input spent, across every folder of the session.</param>
/// <param name="OutputTokens">Output spent, likewise.</param>
/// <param name="Model">The model it ran on, where the caller knows it — a past session does not record it.</param>
/// <param name="AgentName">For a sub-agent's own export: its name.</param>
/// <param name="AgentType">For a sub-agent's own export: its type.</param>
/// <param name="State">For a sub-agent's own export: how it stands — running, done, stopped.</param>
public sealed record ExportHeader(
    string AgentId,
    string? Title,
    string? WorkingDir,
    DateTimeOffset? Started,
    DateTimeOffset? Ended,
    int InputTokens,
    int OutputTokens,
    string? Model = null,
    string? AgentName = null,
    string? AgentType = null,
    string? State = null);

/// <summary>One point on an export's timeline.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Kind">A <see cref="HistoryLine.Kinds"/> value, or <see cref="Note"/>.</param>
/// <param name="Message">The message, for a message; the text, for a note.</param>
/// <param name="Thinking">The model's reasoning, for an assistant message that kept it.</param>
/// <param name="From">Who sent a received message, where that was recorded.</param>
/// <param name="Dir">
/// The agent folder this came from. A sub-agent's folder sits under the agent that SPAWNED it, which in a resumed
/// session may be an older folder than the newest — so a child is looked up beside the entry, not the session.
/// </param>
public sealed record ExportEntry(
    DateTimeOffset At,
    string Kind,
    ChatMessage? Message,
    IReadOnlyList<string>? Thinking,
    Incoming? From,
    string Dir)
{
    /// <summary>Something the export says about the record itself — what an older session did not keep.</summary>
    public const string Note = "note";
}

/// <summary>
/// One agent's conversation, loaded for export: the timeline and what to say at its top.
///
/// <para>FROM ITS HISTORY WHEREVER THERE IS ONE, across every folder the session has had — a resume and a re-wire
/// each mint a new agent, and its history begins by naming the one before.</para>
///
/// <para>A SESSION FROM BEFORE history.jsonl gives what its older files still hold. Its context.json is the whole
/// conversation only if it never compacted; if it did, the transcript — what the user was shown — is what remains,
/// and the export says that tool calls and sub-agents were not recorded rather than mixing guesses into it.</para>
/// </summary>
public sealed class ExportSource
{
    private const string NotRecorded = "Tool calls, their results and sub-agent conversations were not recorded for this "
        + "session (before cxagent 0.9.30); the conversation is as it was shown.";

    private const string HistoryFile = "history.jsonl";

    internal ExportSource(ExportHeader header, IReadOnlyList<ExportEntry> entries, bool recorded, string dir)
    {
        Header = header;
        Entries = entries;
        Recorded = recorded;
        Dir = dir;
    }

    public ExportHeader Header { get; }
    public IReadOnlyList<ExportEntry> Entries { get; }

    /// <summary>False for a session from before history.jsonl — its tool calls and sub-agents are not in it.</summary>
    public bool Recorded { get; }

    /// <summary>The newest agent folder.</summary>
    public string Dir { get; }

    /// <summary>
    /// A session, by the agent id /sessions lists. Null when there is no such folder.
    /// </summary>
    /// <param name="paths">Where cxagent keeps its files.</param>
    /// <param name="agentId">The session's newest agent.</param>
    /// <param name="model">The model, when the caller knows it; a session folder does not record it.</param>
    public static ExportSource? Load(AppPaths paths, string agentId, string? model = null)
    {
        var folder = new SessionFolder(paths, agentId);
        if (!Directory.Exists(folder.Dir)) return null;

        var header = SessionHeader.Read(folder);
        var history = Path.Combine(folder.Dir, HistoryFile);
        var (entries, recorded, tokens) = File.Exists(history)
            ? (Chain(paths, agentId, out var spent), true, spent)
            : (Legacy(folder), false, (header?.InputTokens ?? 0, header?.OutputTokens ?? 0));

        return new ExportSource(
            new ExportHeader(agentId, header?.Title, header?.WorkingDir,
                entries.Count > 0 ? entries[0].At : null,
                entries.Count > 0 ? entries[^1].At : header?.UpdatedAt,
                tokens.Item1, tokens.Item2, model),
            entries, recorded, folder.Dir);
    }

    /// <summary>A sub-agent's own folder, with what its caller knows about it.</summary>
    public static ExportSource? LoadDir(string dir, ExportHeader header)
    {
        var path = Path.Combine(dir, HistoryFile);
        if (!File.Exists(path)) return null;
        var entries = EntriesOf(HistoryRecorder.Read(path), dir);
        return new ExportSource(header with
        {
            Started = entries.Count > 0 ? entries[0].At : header.Started,
            Ended = entries.Count > 0 ? entries[^1].At : header.Ended,
        }, entries, true, dir);
    }

    /// <summary>
    /// The sub-agent <paramref name="childAgentId"/> spawned by the agent <paramref name="at"/> came from.
    ///
    /// <para>READ ONCE PER EXPORT: the dialog renders again on every change of options, and a child's history is
    /// re-parsed for each call that mentions it otherwise. Concurrent, because those renders run on the pool.</para>
    /// </summary>
    public ExportSource? Child(ExportEntry at, string childAgentId) =>
        _children.GetOrAdd(Path.Combine(at.Dir, childAgentId), dir =>
            LoadDir(dir, new ExportHeader(childAgentId, Header.Title, Header.WorkingDir, null, null, 0, 0, Header.Model)));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExportSource?> _children = new(StringComparer.Ordinal);

    private static List<ExportEntry> Chain(AppPaths paths, string newest, out (int, int) spent)
    {
        // NEWEST FIRST WHILE WALKING, oldest first in the result; a folder seen twice ends the walk, so a damaged
        // chain that loops back still loads every folder once.
        var parts = new List<List<ExportEntry>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = newest; id is not null && seen.Add(id);)
        {
            var folder = new SessionFolder(paths, id);
            var path = Path.Combine(folder.Dir, HistoryFile);

            // A FOLDER FROM BEFORE history.jsonl ENDS THE CHAIN WITH WHAT IT STILL HOLDS: a session resumed after the
            // upgrade continues one that never recorded, and its conversation is exported rather than dropped.
            if (!File.Exists(path))
            {
                if (Directory.Exists(folder.Dir)) parts.Add(Legacy(folder));
                break;
            }
            var lines = HistoryRecorder.Read(path);
            parts.Add(EntriesOf(lines, folder.Dir));
            id = lines.Count > 0 && lines[0].Kind == HistoryLine.Kinds.Continues ? lines[0].Previous : null;
        }

        // THE NEWEST FOLDER'S SPEND IS THE SESSION'S: a resumed agent's ledger starts from the totals before it, so each
        // header already holds everything up to it, and a sum would count the early turns once per resume.
        var newestHeader = SessionHeader.Read(new SessionFolder(paths, newest));
        spent = (newestHeader?.InputTokens ?? 0, newestHeader?.OutputTokens ?? 0);

        parts.Reverse();
        return parts.SelectMany(p => p).ToList();
    }

    // THE CONTINUES LINE IS PLUMBING: it joins folders, and says nothing to a reader.
    private static List<ExportEntry> EntriesOf(IReadOnlyList<HistoryLine> lines, string dir) =>
        lines.Where(l => l.Kind != HistoryLine.Kinds.Continues)
            .Select(l => new ExportEntry(l.At, l.Kind, l.Message, l.Thinking, l.From, dir))
            .ToList();

    private static List<ExportEntry> Legacy(SessionFolder folder)
    {
        var context = ReadContext(folder.ContextPath);
        if (context is not null && !context.Any(Compacted))
        {
            var at = DateTimeOffset.MinValue;
            return context.Select(m =>
            {
                if (m.Timestamp != default) at = m.Timestamp;
                return new ExportEntry(at, HistoryLine.Kinds.Message, m, null, null, folder.Dir);
            }).ToList();
        }

        var entries = new List<ExportEntry>
        {
            new(DateTimeOffset.MinValue, ExportEntry.Note, new ChatMessage { Role = "system", Content = NotRecorded },
                null, null, folder.Dir),
        };
        entries.AddRange(Transcript(folder));
        return entries;
    }

    private static bool Compacted(ChatMessage m) =>
        m.Role == "assistant" && m.Content.StartsWith(Sessions.SessionCompressor.SummaryMarker, StringComparison.Ordinal);

    private static List<ChatMessage>? ReadContext(string path)
    {
        try
        {
            return SessionFolder.ReadOrNull(path) is { } text ? JsonSerializer.Deserialize<List<ChatMessage>>(text) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The transcript's rows, one per (session, seq) keeping the last write — a streamed answer is written once per
    /// token — in the order they were written.
    /// </summary>
    private static IEnumerable<ExportEntry> Transcript(SessionFolder folder)
    {
        var rows = new Dictionary<(string, long), TranscriptLine>();
        try
        {
            if (!File.Exists(folder.TranscriptPath)) return [];
            foreach (var text in File.ReadLines(folder.TranscriptPath))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<TranscriptLine>(text) is { } line) rows[(line.SessionId, line.Seq)] = line;
                }
                catch (JsonException) { }
            }
        }
        catch (IOException)
        {
            return [];
        }

        return rows.Values.OrderBy(r => r.At)
            .Where(r => !string.IsNullOrEmpty(r.Body))
            // A SYSTEM ROW IS A NOTE THE APP SHOWED, never a system prompt — so it is a note here too.
            .Select(r => new ExportEntry(r.At, r.Kind is "user" or "assistant" ? HistoryLine.Kinds.Message : ExportEntry.Note,
                new ChatMessage { Role = r.Kind is "user" or "assistant" ? r.Kind : "system", Content = r.Body!, Timestamp = r.At },
                null, r.Kind == "user" ? Incoming.You : null, folder.Dir));
    }
}
