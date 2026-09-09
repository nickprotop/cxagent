using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxAgent.Core.Storage;

/// <summary>One line of the transcript file. The session id travels IN the line, not in the path.</summary>
internal sealed record TranscriptLine(
    [property: JsonPropertyName("s")] string SessionId,
    [property: JsonPropertyName("q")] long Seq,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("r")] string? Role,
    [property: JsonPropertyName("b")] string? Body);

/// <summary>
/// A session's scrollback, appended to one file per agent.
///
/// <para>APPEND-ONLY ON DISK, LAST-WRITE-WINS ON READ — and the write pattern is why. The row store's
/// <c>Append</c> was an upsert called ONCE PER STREAMED TOKEN, because "a message is ONE row that
/// grows". A file per message would mean a whole write per token; rewriting the file would mean
/// rewriting the entire transcript per token. Appending a line per token is what a file is good at,
/// and the reader keeps the LAST occurrence of each seq.</para>
///
/// <para>THE COST IS A FILE LARGER THAN ITS CONTENT, and it is paid on disk rather than on read: a
/// listing never opens this file, and a session's scrollback is deleted with its folder. A compaction
/// pass on close would rewrite one line per seq if it ever mattered — the reader's rule is already
/// "last wins", so nothing else would change.</para>
///
/// <para>THE FOLDER IS THE AGENT'S AND THE ENTRIES ARE THE SESSION'S, which is why
/// <see cref="BindAgent"/> exists: those two ids differ, and one agent's file may hold entries for
/// more than one session id across a re-wire.</para>
/// </summary>
public sealed class FolderTranscriptStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, string> _agentOf = new();

    /// <summary>Tells the store which agent's folder a session's entries belong in.</summary>
    public void BindAgent(string sessionId, string agentId) => _agentOf[sessionId] = agentId;

    private SessionFolder? FolderFor(string sessionId) =>
        _agentOf.TryGetValue(sessionId, out var agentId)
            ? new SessionFolder(paths, agentId)
            : null;

    /// <summary>
    /// Appends one entry. A seq written again supersedes what came before it.
    ///
    /// <para>SWALLOWS ITS OWN FAILURES, as the store it replaces did: no transcript means no replay,
    /// and that must not mean no app.</para>
    /// </summary>
    public void Append(string sessionId, long seq, string kind, string? role, string? body)
    {
        if (FolderFor(sessionId) is not { } folder) return;
        try
        {
            Directory.CreateDirectory(folder.Dir);
            var line = JsonSerializer.Serialize(
                new TranscriptLine(sessionId, seq, DateTimeOffset.UtcNow, kind, role, body), Json);
            File.AppendAllText(folder.TranscriptPath, line + Environment.NewLine);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// A window of a session's transcript, oldest first.
    ///
    /// <para>PAGED RATHER THAN CAPPED, as before: nothing is discarded when it is written; a client
    /// asks for what it can show and scrolls back for more.</para>
    /// </summary>
    public IReadOnlyList<TranscriptEntry> Window(string sessionId, long? before = null,
        int limit = 200)
    {
        var folded = Fold(sessionId);
        if (folded.Count == 0) return [];

        return folded.Values
            .Where(e => before is not { } b || e.Seq < b)
            .OrderBy(e => e.Seq)
            .TakeLast(limit)
            .ToList();
    }

    /// <summary>
    /// Every entry for this session, one per seq, keeping the last write of each.
    ///
    /// <para>A CORRUPT LINE IS SKIPPED, NEVER FATAL. A partial line at the end of the file is what a
    /// crash mid-append leaves, and one unreadable line must not cost the whole replay.</para>
    /// </summary>
    private Dictionary<long, TranscriptEntry> Fold(string sessionId)
    {
        var bySeq = new Dictionary<long, TranscriptEntry>();
        if (FolderFor(sessionId) is not { } folder) return bySeq;

        string[] lines;
        try
        {
            if (!File.Exists(folder.TranscriptPath)) return bySeq;
            lines = File.ReadAllLines(folder.TranscriptPath);
        }
        catch (Exception)
        {
            return bySeq;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            TranscriptLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<TranscriptLine>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (parsed is null || parsed.SessionId != sessionId) continue;
            bySeq[parsed.Seq] = new TranscriptEntry(parsed.Seq, parsed.At, parsed.Kind,
                parsed.Role, parsed.Body);
        }
        return bySeq;
    }

    /// <summary>
    /// Forgets a session, rewriting the file without its lines.
    ///
    /// <para>REWRITTEN RATHER THAN DELETED, because one agent's file may hold more than one session's
    /// entries — deleting it would forget a session nobody asked about.</para>
    /// </summary>
    public void Forget(string sessionId)
    {
        if (FolderFor(sessionId) is not { } folder) return;
        try
        {
            if (!File.Exists(folder.TranscriptPath)) return;
            var kept = File.ReadAllLines(folder.TranscriptPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Where(l =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<TranscriptLine>(l, Json)?.SessionId
                               != sessionId;
                    }
                    catch (JsonException)
                    {
                        return true;   // Unparseable is not this session's; keep it.
                    }
                })
                .ToList();
            SessionFolder.WriteAtomic(folder.TranscriptPath,
                string.Join(Environment.NewLine, kept)
                + (kept.Count > 0 ? Environment.NewLine : ""));
        }
        catch (Exception) { }
    }
}
