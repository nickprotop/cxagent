using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;

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
public sealed class SessionEventLog(string sessionId, int capacity = 20_000) : ISessionObserver, IToolObserver, Core.Agents.IAsideObserver
{
    /// <summary>How much of one tool's output reaches a browser; the rest is in the session's logs.</summary>
    public const int MaxToolOutput = 64 * 1024;

    private readonly object _gate = new();
    private readonly LinkedList<JsonObject> _events = new();
    private readonly List<Action<JsonObject>> _subscribers = [];
    private readonly Dictionary<string, int> _toolOutput = [];
    private readonly Dictionary<string, string> _progress = [];
    private long _seq;
    private int _turns;
    private int _toolCalls;

    // ROWS WHOSE END IS SENT, so a row ends once. A view attaching late is replayed a row's start with the job as it is
    // NOW — finished — which ends it there, and then the update that finished it; a row reopened (a worker sent more
    // work) is running again and may end again.
    private readonly HashSet<string> _ended = new(StringComparer.Ordinal);

    /// <summary>The session these events belong to (<c>Session.Id</c>).</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>
    /// User turns taken since this log began — the panel's "turns". A replayed past is not counted: the terminal
    /// counts a resumed session from its reopening, and both should say the same.
    /// </summary>
    public int Turns
    {
        get { lock (_gate) return _turns; }
    }

    /// <summary>Tool calls that finished, as this log saw them — the panel's "tool calls".</summary>
    public int ToolCalls
    {
        get { lock (_gate) return _toolCalls; }
    }

    /// <summary>When this log began — the panel's session clock, for a session the browser has watched since.</summary>
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

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
    public Replay Subscribe(long seq, Action<JsonObject> live, out IDisposable handle) =>
        Subscribe(seq, live, _ => { }, out handle);

    /// <summary>
    /// As <see cref="Subscribe(long, Action{JsonObject}, out IDisposable)"/>, handing the replay to
    /// <paramref name="first"/> while still holding the lock — so a caller that queues it is certain the replay is
    /// queued before any live event.
    /// </summary>
    public Replay Subscribe(long seq, Action<JsonObject> live, Action<Replay> first, out IDisposable handle)
    {
        lock (_gate)
        {
            var replay = SinceLocked(seq);
            first(replay);
            _subscribers.Add(live);
            handle = new Unsubscribe(this, live);
            return replay;
        }
    }

    /// <summary>
    /// Shows a saved session's past before anything live — what a tab joining a resumed or already-running session sees
    /// of it: its turns, its tool calls and the workers it spawned, back through every resume.
    ///
    /// <para>NOT COUNTED. The panel's turns and tool calls are this run's, as the terminal's are.</para>
    /// </summary>
    /// <remarks>
    /// THROUGH ITS OWN VIEW, NOT A FLAG ON THE LOG: a flag set for the replay's length would also stop a live event that
    /// another thread delivers meanwhile from being counted.
    /// </remarks>
    public ReplayShown Replay(Core.Export.ExportSource past)
    {
        var view = new Uncounted(this);
        return SessionReplay.Play(past, new ReplayTarget(view, view)
        {
            Worker = w => Worker(w.JobId, w.AgentId, w.Name, w.Type, w.Task),
        });
    }

    /// <summary>The log as a replay sees it: every event appended, none counted as this run's.</summary>
    private sealed class Uncounted(SessionEventLog log) : ISessionObserver, IToolObserver
    {
        public void UserTurnAdded(ChatMessageId id, string text) => log.AddUserTurn(id, text, counted: false);
        public void AssistantTurnBegan(ChatMessageId id) => log.AssistantTurnBegan(id);
        public void AssistantTextAppended(ChatMessageId id, string token) => log.AssistantTextAppended(id, token);
        public void AssistantThinkingAppended(ChatMessageId id, string text) => log.AssistantThinkingAppended(id, text);
        public void AssistantThinkingEnded(ChatMessageId id, ThinkingSummary summary) => log.AssistantThinkingEnded(id, summary);
        public void AssistantTurnEnded(ChatMessageId id) => log.AssistantTurnEnded(id);
        public void AssistantLabelled(ChatMessageId id, string header) => log.AssistantLabelled(id, header);
        public void Said(Message message) => log.Said(message);
        public void ToolsChanged(IReadOnlyList<Job> jobs) => log.Changed(jobs, counted: false);
        public void ToolUpdated(Job job) => log.Updated(job, counted: false);
        public void ToolProgressed(Job job) => log.ToolProgressed(job);
        public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
        public void ToolOutputAppended(string jobId, string delta) => log.ToolOutputAppended(jobId, delta);
    }

    /// <summary>
    /// A worker started for the row <paramref name="job"/>: who it is, so its card can name it and open it. Its type
    /// goes out as "workerType": every event already has a "type", which says it is an event.
    /// </summary>
    public void Worker(string job, string agentId, string name, string? type, string? task) =>
        Append("worker", new JsonObject { ["job"] = job, ["agentId"] = agentId, ["name"] = name, ["workerType"] = type, ["task"] = task });

    /// <inheritdoc/>
    public void UserTurnAdded(ChatMessageId id, string text) => AddUserTurn(id, text, counted: true);

    private void AddUserTurn(ChatMessageId id, string text, bool counted)
    {
        if (counted) lock (_gate) _turns++;
        Append("user", new JsonObject { ["turn"] = id.Value, ["text"] = text });
    }

    /// <summary>
    /// The images a user turn carried, each as a data URL the page shows on the message — the terminal's thumbnail
    /// and image button.
    ///
    /// <para>INLINE, NOT A LINK TO FETCH. The log replays to a tab that joins later, and a link would need a route
    /// that checks the login and the session for every image; the bytes are already the shrunk ones the model was
    /// sent, a few hundred kilobytes at most.</para>
    /// </summary>
    public void UserImagesAttached(ChatMessageId id, IReadOnlyList<AttachedImage> images) =>
        Append("images", new JsonObject
        {
            ["turn"] = id.Value,
            ["images"] = new JsonArray([.. images.Select(i => (JsonNode)new JsonObject
            {
                ["label"] = i.Label,
                ["src"] = i.Bytes is { } bytes ? $"data:{i.MediaType};base64,{Convert.ToBase64String(bytes)}" : null,
            })]),
        });

    // ---- asides: /btw, answered beside the conversation and never part of it -----------------------------------------

    /// <inheritdoc/>
    public void Asked(int aside, string question) => Append("asideAsked", new JsonObject { ["aside"] = aside, ["text"] = question });

    /// <inheritdoc/>
    public void Waiting(int aside, bool waiting) => Append("asideWaiting", new JsonObject { ["aside"] = aside, ["waiting"] = waiting });

    /// <inheritdoc/>
    public void AnswerAppended(int aside, string text) => Append("asideText", new JsonObject { ["aside"] = aside, ["text"] = text });

    /// <inheritdoc/>
    public void LookedUp(int aside, string row) => Append("asideLookedUp", new JsonObject { ["aside"] = aside, ["text"] = row });

    /// <inheritdoc/>
    public void ThinkingAppended(int aside, string text) => Append("asideThinking", new JsonObject { ["aside"] = aside, ["text"] = text });

    /// <inheritdoc/>
    public void ThinkingEnded(int aside, ThinkingSummary summary) =>
        Append("asideThinkingEnded", new JsonObject { ["aside"] = aside, ["words"] = summary.Words });

    /// <inheritdoc/>
    public void Finished(int aside, Core.Agents.AsideEnd end, string? detail) =>
        Append("asideEnded", new JsonObject { ["aside"] = aside, ["end"] = end.ToString(), ["detail"] = detail });

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
            ["seconds"] = summary.Untimed ? null : Math.Round(summary.Duration.TotalSeconds, 1),
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
    public void ToolsChanged(IReadOnlyList<Job> jobs) => Changed(jobs, counted: true);

    private void Changed(IReadOnlyList<Job> jobs, bool counted)
    {
        foreach (var job in jobs)
        {
            bool known;
            lock (_gate) known = !_toolOutput.TryAdd(job.Id, 0);
            if (known) continue;
            // A WORKER'S ROW SAYS WHAT IT IS — its type and description are in the name, the model it runs on here,
            // and its progress line as the terminal shows it ("2 turns · 14% ctx · 6s").
            Append("toolStarted", new JsonObject
            {
                ["job"] = job.Id,
                ["name"] = job.DisplayName,
                ["agent"] = job.AgentId,
                ["jobType"] = job.JobType,
                ["runsOn"] = job.RunsOn,
                ["progress"] = job.ProgressMessage,
            });
            if (IsDone(job.State)) Ended(job, counted);
        }
    }

    /// <inheritdoc/>
    public void ToolUpdated(Job job) => Updated(job, counted: true);

    private void Updated(Job job, bool counted)
    {
        bool known;
        lock (_gate) known = _toolOutput.ContainsKey(job.Id);
        if (!known) Changed([job], counted);
        else if (IsDone(job.State)) Ended(job, counted);
        else lock (_gate) _ended.Remove(job.Id);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ONLY WHAT CHANGED is appended: a worker reports progress every few seconds, often the same line, and every one
    /// would otherwise be kept and sent to every tab.
    /// </remarks>
    public void ToolProgressed(Job job)
    {
        var line = (job.ProgressMessage ?? "") + "\u0000" + (job.ProgressBody ?? "");
        lock (_gate)
        {
            if (_progress.TryGetValue(job.Id, out var last) && last == line) return;
            _progress[job.Id] = line;
        }
        Append("toolProgress", new JsonObject
        {
            ["job"] = job.Id,
            ["progress"] = job.ProgressMessage,
            ["body"] = job.ProgressBody,
            ["runsOn"] = job.RunsOn,
        });
    }

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

    private void Ended(Job job, bool counted)
    {
        bool streamed;
        lock (_gate)
        {
            if (!_ended.Add(job.Id)) return;
            if (counted) _toolCalls++;
            streamed = _toolOutput.GetValueOrDefault(job.Id) > 0;
        }
        var output = job.Result?.Output;

        // THE RESULT, WHEN NOTHING STREAMED. Most tools (a read, a grep, a quick command) never stream, and their row
        // would open on nothing; this is the terminal's own expanded body, capped like streamed output. Not for a
        // worker: its result is prose its own tab shows whole.
        var body = streamed || job.JobType == "llm_agent" ? null : UI.InlineJobSink.BodyFor(job);
        if (body is { Length: > MaxToolOutput }) body = body[..MaxToolOutput] + "\n… output continues in the session's logs";
        Append("toolEnded", new JsonObject
        {
            ["job"] = job.Id,
            ["state"] = job.State.ToString(),
            ["sandbox"] = output?.GetValueOrDefault("sandbox") as string,
            ["sandboxWarning"] = output?.ContainsKey("sandbox_warning") == true,
            ["error"] = job.Result?.ErrorMessage,
            ["output"] = body,
            // THE JOB'S OWN TIMES, not the page's: a replay delivers a whole session in one burst.
            ["ms"] = job is { StartedAt: { } from, CompletedAt: { } to } ? (long)(to - from).TotalMilliseconds : null,
            // THE LAST LINE, AS IT ENDS. A worker sent to the background ends its row at launch with "started in
            // background" set and no progress call between, so the end is the only place that line arrives.
            ["progress"] = job.ProgressMessage,
            ["body"] = job.ProgressBody,
        });
    }

    private static bool IsDone(JobState state) =>
        state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Skipped;

    /// <summary>
    /// The conversation was cleared: what was kept goes, and a <c>cleared</c> event takes its place — so a tab watching
    /// empties its transcript as the terminal's does, a tab joining later starts from the clear, and one that saw the
    /// old conversation is told to redraw rather than keep it.
    /// </summary>
    public void Cleared()
    {
        lock (_gate)
        {
            _events.Clear();
            Append("cleared", new JsonObject());
        }
    }

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
        // A NUMBER THIS LOG NEVER GAVE comes from an earlier log — a server since restarted, or /web turned off and on.
        // The tab redraws from what is here rather than drop every event until the count passes its number.
        if (seq > _seq) return new Replay([.. _events], true);
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
