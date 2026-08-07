using System;
using System.Threading.Channels;
using CxAgent.Core.Models;
using OrchestratorNs = CxAgent.Core.Orchestrator;

namespace CxAgent.Core.Storage;

/// <summary>
/// Wires an Orchestrator's events to an IGoalStore: persists each job transition and
/// each goal-state change as the run proceeds. The scheduler/DAG stay persistence-
/// agnostic — this is the only integration point.
///
/// Writes run off the event thread via a serialized channel with error isolation (a DB
/// hiccup must not crash the run). Call DrainAsync() to await all outstanding writes
/// (used at end-of-run / in tests).
/// </summary>
public class PersistenceSubscriber : IAsyncDisposable
{
    private readonly IGoalStore _store;
    private readonly Channel<Func<Task>> _channel;
    private readonly Task _worker;

    public PersistenceSubscriber(IGoalStore store)
    {
        _store = store;
        _channel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        _worker = RunWorkerAsync();
    }

    public void Attach(OrchestratorNs.Orchestrator orchestrator, Goal goal)
    {
        orchestrator.JobStateChanged += (_, job) =>
        {
            // Remap job.GoalId to the provided goal.Id so jobs are persisted under the
            // known goal row (P1-friction seam: the orchestrator creates its own goal
            // internally; the subscriber bridges the two via the passed goal reference).
            var mapped = job.GoalId == goal.Id ? job : job with { GoalId = goal.Id };
            Enqueue(() => _store.SaveJobAsync(mapped));
        };
        orchestrator.GoalStateChanged += (_, state) =>
        {
            goal.State = state;
            if (state is GoalState.Completed or GoalState.Failed or GoalState.Cancelled)
                goal.CompletedAt = DateTimeOffset.UtcNow;
            // Capture snapshot for the closure.
            var snapshot = goal with { };
            Enqueue(() => _store.SaveGoalAsync(snapshot));
        };
    }

    /// <summary>Await all persistence writes issued so far.</summary>
    public async Task DrainAsync()
    {
        // Write a sentinel that completes a TCS, then await that TCS.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(() => { tcs.TrySetResult(); return Task.CompletedTask; }))
            tcs.TrySetResult();   // writer completed (disposed) — nothing left to drain
        await tcs.Task;
    }

    private void Enqueue(Func<Task> action) => _channel.Writer.TryWrite(action);

    /// <summary>Dispose the subscriber and clean up the worker task and channel.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        await _worker;
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var action in _channel.Reader.ReadAllAsync())
        {
            try { await action(); }
            catch { /* best-effort; a failure channel is a later concern */ }
        }
    }
}
