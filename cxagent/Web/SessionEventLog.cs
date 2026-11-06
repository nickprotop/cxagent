using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;

namespace CxAgent.Web;

/// <summary>What a reconnecting tab gets: the events it missed, or — when it missed more than is kept — all that is.</summary>
/// <param name="Events">The events, oldest first.</param>
/// <param name="Reset">True when these are not a continuation: the tab clears its view and draws them fresh.</param>
public sealed record Replay(IReadOnlyList<JsonObject> Events, bool Reset);

/// <summary>
/// One session as browser tabs see it: every report the session makes, numbered once and kept, so a tab that joins late
/// or drops off and comes back gets exactly what it has not seen.
///
/// <para>A SEQUENCE NUMBER PER EVENT IS THE WHOLE RECONNECT PROTOCOL. A phone that loses signal mid-reply asks for
/// "everything after 41" and gets 42 onwards — no repeated tokens, no gap — which a stream with no numbers cannot
/// promise.</para>
///
/// <para>NEVER BLOCKS THE SESSION. It is attached through the session's observer fan-out, which calls it on the
/// turn's own thread; it appends under a lock and hands each event to subscribers that only queue it.</para>
///
/// <para>PLAIN TEXT, NOT MARKUP, like every observer: the page escapes before it renders.</para>
/// </summary>
public sealed class SessionEventLog(string sessionId, int capacity = 20_000) : ISessionObserver, IToolObserver
{
    /// <summary>How much of one tool's output reaches a browser; the rest is in the session's logs.</summary>
    public const int MaxToolOutput = 64 * 1024;

    private readonly object _gate = new();
    private readonly LinkedList<JsonObject> _events = new();
    private readonly List<Action<JsonObject>> _subscribers = [];
    private readonly Dictionary<string, int> _toolOutput = [];
    private long _seq;

    /// <summary>The session these events belong to (<c>Session.Id</c>).</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>The number of the newest event.</summary>
    public long LastSeq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>Everything after <paramref name="seq"/>, or a fresh start when some of it is no longer kept.</summary>
    public Replay Since(long seq)
    {
        lock (_gate) return SinceLocked(seq);
    }

    /// <summary>
    /// Replays what came after <paramref name="seq"/> and, in the same step, starts sending <paramref name="live"/>
    /// everything newer — one lock, so nothing falls between the replay and the first live event.
    /// </summary>
    public Replay Subscribe(long seq, Action<JsonObject> live, out IDisposable handle)
    {
        lock (_gate)
        {
            _subscribers.Add(live);
            handle = new Unsubscribe(this, live);
            return SinceLocked(seq);
        }
    }

    /// <summary>
    /// Replays a session's saved transcript as events, before anything live — what a tab joining a resumed or
    /// already-running session sees of its past. Lines that cannot be read are skipped.
    /// </summary>
    public void Seed(string transcriptPath)
    {
        if (!File.Exists(transcriptPath)) return;
        string[] lines;
        try { lines = File.ReadAllLines(transcriptPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        var turn = -1L;
        foreach (var line in lines)
        {
            JsonNode? row;
            try { row = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (row?["k"]?.GetValue<string>() is not { } kind || row["b"]?.GetValue<string>() is not { } body) continue;
            switch (kind)
            {
                case "user":
                    Append("user", new JsonObject { ["text"] = body });
                    break;
                case "assistant":
                    var id = turn--;
                    Append("turnBegan", new JsonObject { ["turn"] = id });
                    Append("text", new JsonObject { ["turn"] = id, ["text"] = body });
                    Append("turnEnded", new JsonObject { ["turn"] = id });
                    break;
                default:
                    Append("said", new JsonObject { ["text"] = body, ["severity"] = row["r"]?.GetValue<string>() ?? "Info" });
                    break;
            }
        }
    }

    /// <inheritdoc/>
    public void UserTurnAdded(ChatMessageId id, string text) => Append("user", new JsonObject { ["turn"] = id.Value, ["text"] = text });

    /// <inheritdoc/>
    public void AssistantTurnBegan(ChatMessageId id) => Append("turnBegan", new JsonObject { ["turn"] = id.Value });

    /// <inheritdoc/>
    public void AssistantTextAppended(ChatMessageId id, string token) =>
        Append("text", new JsonObject { ["turn"] = id.Value, ["text"] = token });

    /// <inheritdoc/>
    public void AssistantThinkingAppended(ChatMessageId id, string text) =>
        Append("thinking", new JsonObject { ["turn"] = id.Value, ["text"] = text });

    /// <inheritdoc/>
    public void AssistantThinkingEnded(ChatMessageId id, ThinkingSummary summary) =>
        Append("thinkingEnded", new JsonObject
        {
            ["turn"] = id.Value,
            ["seconds"] = Math.Round(summary.Duration.TotalSeconds, 1),
            ["words"] = summary.Words,
            ["hidden"] = summary.Hidden,
        });

    /// <inheritdoc/>
    public void AssistantTurnEnded(ChatMessageId id) => Append("turnEnded", new JsonObject { ["turn"] = id.Value });

    /// <inheritdoc/>
    public void AssistantLabelled(ChatMessageId id, string header) =>
        Append("labelled", new JsonObject { ["turn"] = id.Value, ["text"] = header });

    /// <inheritdoc/>
    public void Said(Message message) =>
        Append("said", new JsonObject { ["text"] = message.Text, ["severity"] = message.Severity.ToString() });

    /// <inheritdoc/>
    public void ToolsChanged(IReadOnlyList<Job> jobs)
    {
        foreach (var job in jobs)
        {
            bool known;
            lock (_gate) known = !_toolOutput.TryAdd(job.Id, 0);
            if (known) continue;
            Append("toolStarted", new JsonObject
            {
                ["job"] = job.Id,
                ["name"] = job.DisplayName,
                ["agent"] = job.AgentId,
            });
            if (IsDone(job.State)) Ended(job);
        }
    }

    /// <inheritdoc/>
    public void ToolUpdated(Job job)
    {
        bool known;
        lock (_gate) known = _toolOutput.ContainsKey(job.Id);
        if (!known) ToolsChanged([job]);
        else if (IsDone(job.State)) Ended(job);
    }

    /// <inheritdoc/>
    public void ToolProgressed(Job job) { }

    /// <inheritdoc/>
    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }

    /// <inheritdoc/>
    public void ToolOutputAppended(string jobId, string delta)
    {
        // CAPPED PER TOOL. A build's output can run to megabytes, and every byte would be kept here and sent to every
        // tab — a phone included. The cap is said once, where the output stops.
        string? send;
        lock (_gate)
        {
            _toolOutput.TryGetValue(jobId, out var sent);
            if (sent >= MaxToolOutput) return;
            var room = MaxToolOutput - sent;
            send = delta.Length <= room ? delta : delta[..room] + "\n… output continues in the session's logs";
            _toolOutput[jobId] = delta.Length <= room ? sent + delta.Length : MaxToolOutput;
        }
        Append("toolOutput", new JsonObject { ["job"] = jobId, ["text"] = send });
    }

    private void Ended(Job job)
    {
        var output = job.Result?.Output;
        Append("toolEnded", new JsonObject
        {
            ["job"] = job.Id,
            ["state"] = job.State.ToString(),
            ["sandbox"] = output?.GetValueOrDefault("sandbox") as string,
            ["sandboxWarning"] = output?.ContainsKey("sandbox_warning") == true,
            ["error"] = job.Result?.ErrorMessage,
        });
    }

    private static bool IsDone(JobState state) =>
        state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

    private void Append(string kind, JsonObject fields)
    {
        lock (_gate)
        {
            fields["type"] = "event";
            fields["sessionId"] = SessionId;
            fields["seq"] = ++_seq;
            fields["kind"] = kind;
            _events.AddLast(fields);
            if (_events.Count > capacity) _events.RemoveFirst();

            // UNDER THE LOCK, SO ORDER HOLDS: two turns' threads appending at once must reach every tab in the order
            // they were numbered. Subscribers only queue.
            foreach (var s in _subscribers) s(fields);
        }
    }

    private Replay SinceLocked(long seq)
    {
        var oldest = _events.First?.Value["seq"]!.GetValue<long>() ?? _seq + 1;
        if (seq >= 0 && seq + 1 >= oldest) return new Replay([.. _events.Where(e => e["seq"]!.GetValue<long>() > seq)], false);
        return new Replay([.. _events], seq > 0);
    }

    private sealed class Unsubscribe(SessionEventLog log, Action<JsonObject> live) : IDisposable
    {
        public void Dispose()
        {
            lock (log._gate) log._subscribers.Remove(live);
        }
    }
}
