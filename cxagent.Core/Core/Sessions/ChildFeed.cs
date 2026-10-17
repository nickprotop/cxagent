using System.Text;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;

namespace CxAgent.Core.Sessions;

/// <summary>One watcher of a child: where its words go, where its tool rows go, and its finished calls.</summary>
/// <param name="Transcript">The child's words — a transcript sink.</param>
/// <param name="Tools">The child's tool rows — a job sink.</param>
/// <param name="Calls">
/// Each finished tool call, or null for a view that draws none. A session transcript draws a round's calls as one
/// row from these reports, not from the job events, so a view without them shows no tool rows at all.
/// </param>
public sealed record ChildView(ISessionObserver Transcript, IToolObserver Tools, Action<ToolCallReport>? Calls = null);

/// <summary>
/// A sub-agent's observer: its buffers hear every event, and a view attached at any moment is replayed what it
/// missed and then follows live.
///
/// <para>NOT <see cref="ObserverFanOut"/>. That type delivers lock-free over a snapshot and locks only to swap the
/// array, so a view that read the past before subscribing loses what was published in between, and one that read
/// it after shows those events twice. Here delivery, recording and attaching share one lock: an event is either in
/// the log the attach replays or delivered to the view after it, never both and never neither.</para>
///
/// <para>THE LOCK IS HELD WHILE WATCHERS ARE CALLED, which the fan-outs refuse to do. It is affordable because a
/// child's watchers are transcript and job sinks whose every member only enqueues onto the UI thread; a slow
/// watcher here would hold up the child's turn, so a watcher must hand off rather than draw inline.</para>
///
/// <para>THE BUFFERS ARE NOT WATCHERS. They are this child's record for the parent's row and the spawn result, are
/// told first, and never attach or detach — so a child nobody opens costs its buffers and this log.</para>
///
/// <para>THE LOG IS EVERY EVENT A LATE VIEW NEEDS, coalesced: consecutive text, thinking or output for the same
/// message or job is one entry, so a long answer replays as one append rather than ten thousand. It is unbounded,
/// for the reason the store keeps a child's context: a child lives for its session, and the log is of the same
/// order as the transcript text the buffer already holds.</para>
/// </summary>
public sealed class ChildFeed : ISessionObserver, IToolObserver
{
    private readonly object _gate = new();
    private readonly ISessionObserver _buffer;
    private readonly IToolObserver _jobs;
    private readonly List<Action<ChildView>> _log = [];
    private ChildView[] _views = [];

    /// <summary>The streaming run the last log entry is, or null when the last entry is not one.</summary>
    private Run? _run;

    /// <param name="buffer">The child's chat buffer, told every event first.</param>
    /// <param name="jobs">The child's job buffer, told every tool event first.</param>
    public ChildFeed(ISessionObserver buffer, IToolObserver jobs)
    {
        _buffer = buffer;
        _jobs = jobs;
    }

    /// <summary>How many views are attached.</summary>
    public int Watchers { get { lock (_gate) return _views.Length; } }

    /// <summary>
    /// Replays everything so far into the view, then delivers to it live; disposing the token detaches it.
    ///
    /// <para>ON THE CALLER'S THREAD AND UNDER THE LOCK, so the child's next event waits for the replay to finish
    /// and then reaches this view after it.</para>
    /// </summary>
    public IDisposable Attach(ChildView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            foreach (var entry in _log) Safely(() => entry(view));
            _views = [.. _views, view];
        }
        return new Registration(this, view);
    }

    public void UserTurnAdded(ChatMessageId id, string text) =>
        Publish(() => _buffer.UserTurnAdded(id, text), v => v.Transcript.UserTurnAdded(id, text));

    public void AssistantTurnBegan(ChatMessageId id) =>
        Publish(() => _buffer.AssistantTurnBegan(id), v => v.Transcript.AssistantTurnBegan(id));

    public void AssistantTextAppended(ChatMessageId id, string token) =>
        Stream(new Run($"text:{id.Value}", (v, text) => v.Transcript.AssistantTextAppended(id, text)), token,
            () => _buffer.AssistantTextAppended(id, token));

    public void AssistantThinkingAppended(ChatMessageId id, string text) =>
        Stream(new Run($"think:{id.Value}", (v, run) => v.Transcript.AssistantThinkingAppended(id, run)), text,
            () => _buffer.AssistantThinkingAppended(id, text));

    public void AssistantThinkingEnded(ChatMessageId id, Llm.ThinkingSummary summary) =>
        Publish(() => _buffer.AssistantThinkingEnded(id, summary), v => v.Transcript.AssistantThinkingEnded(id, summary));

    public void AssistantTurnEnded(ChatMessageId id) =>
        Publish(() => _buffer.AssistantTurnEnded(id), v => v.Transcript.AssistantTurnEnded(id));

    public void AssistantLabelled(ChatMessageId id, string header) =>
        Publish(() => _buffer.AssistantLabelled(id, header), v => v.Transcript.AssistantLabelled(id, header));

    public void Said(Message message) =>
        Publish(() => _buffer.Said(message), v => v.Transcript.Said(message));

    // A COPY OF THE LIST, because the loop may hand the same list again with more in it, and a replay must show
    // the plan as it was announced. The jobs inside are the live, mutated instances — a replay showing their state
    // now is what a late view wants.
    public void ToolsChanged(IReadOnlyList<Job> jobs)
    {
        var announced = jobs.ToArray();
        Publish(() => _jobs.ToolsChanged(jobs), v => v.Tools.ToolsChanged(announced));
    }

    public void ToolUpdated(Job job) =>
        Publish(() => _jobs.ToolUpdated(job), v => v.Tools.ToolUpdated(job));

    // LIVE ONLY. A tick or a sample describes a moment; a view attached later has nothing to tick until the next
    // one, and keeping them would grow the log by one entry a second for every running call.
    public void ToolProgressed(Job job) =>
        Publish(() => _jobs.ToolProgressed(job), v => v.Tools.ToolProgressed(job), keep: false);

    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) =>
        Publish(() => _jobs.ToolResourcesSampled(jobId, snapshot), v => v.Tools.ToolResourcesSampled(jobId, snapshot),
            keep: false);

    public void ToolOutputAppended(string jobId, string delta) =>
        Stream(new Run($"out:{jobId}", (v, text) => v.Tools.ToolOutputAppended(jobId, text)), delta,
            () => _jobs.ToolOutputAppended(jobId, delta));

    /// <summary>One finished tool call — wired to the child's <c>Agent.ToolCallFinished</c> by the factory.</summary>
    public void CallFinished(ToolCallReport report) =>
        Publish(static () => { }, v => v.Calls?.Invoke(report));

    private void Publish(Action toBuffer, Action<ChildView> toView, bool keep = true)
    {
        lock (_gate)
        {
            Safely(toBuffer);
            if (keep)
            {
                _run = null;
                _log.Add(toView);
            }
            foreach (var view in _views) Safely(() => toView(view));
        }
    }

    /// <summary>
    /// A piece of streamed text: joined onto the open run when it continues it, otherwise opening a new one.
    ///
    /// <para>THE LOG ENTRY READS THE RUN WHEN IT IS REPLAYED, not when it was opened, so a view attached mid-run is
    /// replayed what has arrived so far and then receives the rest live — under the same message id.</para>
    /// </summary>
    private void Stream(Run start, string piece, Action toBuffer)
    {
        lock (_gate)
        {
            Safely(toBuffer);
            var run = _run;
            if (run is null || run.Key != start.Key)
            {
                run = start;
                _run = run;
                var opened = run;
                _log.Add(view => opened.Deliver(view, opened.Text.ToString()));
            }
            run.Text.Append(piece);
            foreach (var view in _views) Safely(() => run.Deliver(view, piece));
        }
    }

    /// <summary>
    /// Runs one delivery, swallowing what it throws — see <see cref="ObserverFanOut"/>: a watcher's bug costs it
    /// that event, never the child's turn.
    /// </summary>
    private static void Safely(Action deliver)
    {
        try { deliver(); }
        catch (Exception) { }
    }

    private void Remove(ChildView view)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_views, view);
            if (index < 0) return;
            _views = [.. _views[..index], .. _views[(index + 1)..]];
        }
    }

    /// <summary>A stretch of streamed text for one message, thinking block or job.</summary>
    private sealed class Run(string key, Action<ChildView, string> deliver)
    {
        public string Key { get; } = key;
        public Action<ChildView, string> Deliver { get; } = deliver;
        public StringBuilder Text { get; } = new();
    }

    private sealed class Registration(ChildFeed owner, ChildView view) : IDisposable
    {
        private int _disposed;

        // ONCE, because a second dispose would remove a later attach of the same view.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(view);
        }
    }
}
