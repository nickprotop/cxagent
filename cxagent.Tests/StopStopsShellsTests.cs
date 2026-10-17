using CxAgent.Core.Agents;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Stopping a background agent stops the shells it started — the user's stop, the parent's, and StopAll
/// (a tab closed, the app quit, a headless run ending).
///
/// <para>A SHELL LEFT RUNNING OUTLIVES THE DECISION TO STOP ITS WORK, and when it exits its notice is
/// addressed to the stopped agent. Each test therefore checks both halves: the process is dead, and its
/// record says it was stopped rather than leaving finished-shell bookkeeping to disagree with the kill.</para>
/// </summary>
public sealed class StopStopsShellsTests : IDisposable
{
    private readonly DetachedProcessRegistry _registry = new();
    private readonly FinishedShells _finished = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stop-shells-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _registry.ReapAll();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private static SubAgent Child() => new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
    {
        Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
        MaxTurns = 50, CompressAbove = 40_000, ContextWindow = 200_000,
    }).Create();

    /// <summary>
    /// A long-running shell owned by this agent, tracked as the shell executor tracks one — with a handoff
    /// whose send counts, so a test can prove the kill told nobody.
    /// </summary>
    private (DetachedProcess Process, Func<int> Sent) Shell(string ownerAgentId)
    {
        var process = ProcessRunner.DetachAsync(
            new ProcessSpec("/bin/sh", ["-c", "sleep 30"], new RunOptions(WorkingDir: TestProcesses.WorkingDir, SpillDir: _dir)),
            new CollectingContext(), _registry).GetAwaiter().GetResult();
        var job = new BackgroundJob(process.Pid, ownerAgentId, "sleep 30", DateTimeOffset.UtcNow, process.OutputPath);
        _registry.Describe(process, job);
        var handoff = _finished.Track(job, _ => true);
        var sent = 0;
        process.Exited += code => handoff.Complete($"exit {code}", _ => { Interlocked.Increment(ref sent); return true; });
        return (process, () => Volatile.Read(ref sent));
    }

    private static BackgroundRun Begin(SubAgentStore store, string name) =>
        store.BeginBackground(new BackgroundStart(name, "parent", "do it", "find thing", IsResume: false),
            new ResultHandoff(_ => true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoppingAnAgent_KillsItsRunningShells_AndRecordsThemStopped(bool byUser)
    {
        var store = new SubAgentStore(_registry, _finished);
        var child = Child();
        var name = store.Keep(child, "find thing");
        Begin(store, name);
        var (shell, sent) = Shell(child.Agent.Id);

        Assert.True(store.Stop(name, byUser));

        Assert.True(shell.Finished);
        Assert.True(shell.Killed);
        Assert.True(_finished.Find(shell.Pid)!.Killed);
        Assert.Equal(0, sent());   // its exit is told to nobody: the agent it would reach is the one stopped
    }

    [Fact]
    public void StopAll_KillsTheShellsOfEveryAgentItStops()
    {
        var store = new SubAgentStore(_registry, _finished);
        var a = Child();
        var b = Child();
        Begin(store, store.Keep(a, "first"));
        Begin(store, store.Keep(b, "second"));
        var (shellA, _) = Shell(a.Agent.Id);
        var (shellB, _) = Shell(b.Agent.Id);

        Assert.Equal(2, store.StopAll().Count);

        Assert.True(shellA.Killed);
        Assert.True(shellB.Killed);
    }

    /// <summary>
    /// A SESSION ENDING ENDS EVERY SHELL ITS CHILDREN STARTED, NOT ONLY THE RUNNING ONES'. A child that already
    /// answered has no run to stop, but a shell it left behind would outlive the tab and, on exit, address a
    /// notice to an agent in a conversation nobody can see. Both kinds of finished child are covered: one
    /// whose background run answered, and one that only ever ran in the foreground.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopAll_KillsTheShellsOfAChildThatHadAlreadyFinished(bool ranInTheBackground)
    {
        var store = new SubAgentStore(_registry, _finished);
        var child = Child();
        var name = store.Keep(child, "find thing");
        if (ranInTheBackground) Begin(store, name).MarkAnswered();
        var (shell, sent) = Shell(child.Agent.Id);

        Assert.Empty(store.StopAll());   // no run was going, so nothing is named as stopped

        Assert.True(shell.Killed);
        Assert.True(_finished.Find(shell.Pid)!.Killed);
        Assert.Equal(0, sent());
    }

    /// <summary>ONLY ITS OWN: the session's shells and a sibling's are not the stopped agent's to end.</summary>
    [Fact]
    public void StoppingAnAgent_LeavesOtherAgentsShellsRunning()
    {
        var store = new SubAgentStore(_registry, _finished);
        var stopped = Child();
        var sibling = Child();
        var name = store.Keep(stopped, "stopped one");
        store.Keep(sibling, "sibling");
        Begin(store, name);
        var (theSessions, _) = Shell("SESSION");
        var (theSiblings, _) = Shell(sibling.Agent.Id);

        store.Stop(name, byUser: true);

        Assert.False(theSessions.Finished);
        Assert.False(theSiblings.Finished);
    }

    /// <summary>A REFUSED STOP STOPS NOTHING: an agent that already answered keeps the shells it left running.</summary>
    [Fact]
    public void ARefusedStop_LeavesTheShellsRunning()
    {
        var store = new SubAgentStore(_registry, _finished);
        var child = Child();
        var name = store.Keep(child, "find thing");
        Begin(store, name).MarkAnswered();
        var (shell, _) = Shell(child.Agent.Id);

        Assert.False(store.Stop(name, byUser: true));
        Assert.False(shell.Finished);
    }

    /// <summary>
    /// Answers once released, IGNORING cancellation — a provider stream that does not notice the stop, so a
    /// stopped run is still in flight when the test starts a shell for it.
    /// </summary>
    private sealed class DeafProvider : ILlmProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Release() => _gate.TrySetResult();
        public string ProviderId => "deaf";
        public string ModelId => "deaf";
        public string DisplayName => "Deaf";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return new LlmResponse { Text = "done", StopReason = "end_turn" };
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    /// <summary>
    /// A SHELL DETACHED AFTER THE STOP'S SWEEP DIES WHEN THE STOPPED RUN ENDS. The child's last tool call can
    /// hand a command to the background in the instant after the stop; the sweep at the stop missed it, so
    /// the run's end sweeps again.
    /// </summary>
    [Fact]
    public async Task AShellStartedAfterTheStop_IsKilledWhenTheStoppedRunEnds()
    {
        var deaf = new DeafProvider();
        var delivery = new RecordingDelivery();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = deaf, Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 50, CompressAbove = 40_000, ContextWindow = 200_000,
            Delivery = delivery, AllowBackground = true,
        });
        var store = new SubAgentStore(_registry, _finished);
        var spawner = new SubAgentSpawner(factory, store: store);
        await spawner.TryInvokeAsync(Calls.Spawn(), null, CancellationToken.None, parentAgentId: "parent");
        Assert.True(await Poll.Until(() => deaf.Calls == 1));

        Assert.True(store.Stop("find-thing", byUser: true));
        var (late, _) = Shell(store.Find("find-thing")!.Agent.Agent.Id);
        Assert.False(late.Finished);

        deaf.Release();
        await delivery.Next();

        Assert.True(late.Killed);
        Assert.Equal(BackgroundState.Stopped, store.BackgroundOf("find-thing")!.State);
    }

    /// <summary>
    /// A TAB ENDS AS A WHOLE: the session agent's own shells die with its children's. Left running, one
    /// outlives the conversation it belongs to, and its exit submits a notice to a session that is gone.
    /// </summary>
    [Fact]
    public void StopAll_KillsTheSessionAgentsOwnShells_WhenToldWhoTheSessionAgentIs()
    {
        var store = new SubAgentStore(_registry, _finished);
        var (theSessions, sent) = Shell("SESSION");

        store.StopAll(isSessionAgent: id => id == "SESSION");

        Assert.True(theSessions.Killed);
        Assert.True(_finished.Find(theSessions.Pid)!.Killed);
        Assert.Equal(0, sent());
    }

    /// <summary>
    /// KEPT, THE SHELLS RUN ON AND TELL NOBODY. The user chose to leave a tab's commands running — a dev server,
    /// a watcher — so neither the session's nor a child's is killed; each is silenced, because the
    /// conversation its exit would report to is closing. The agents still stop: they have nowhere to report.
    /// </summary>
    [Fact]
    public void StopAll_KeepingShells_StopsTheAgents_ButLeavesEveryShellRunning_AndSilent()
    {
        var store = new SubAgentStore(_registry, _finished);
        var child = Child();
        var name = store.Keep(child, "find thing");
        Begin(store, name);
        var (childs, childSent) = Shell(child.Agent.Id);
        var (theSessions, sessionSent) = Shell("SESSION");

        Assert.Single(store.StopAll(isSessionAgent: id => id == "SESSION", keepShells: true));

        Assert.False(childs.Finished);
        Assert.False(theSessions.Finished);

        // THE RUNNER SWEEPS A STOPPED RUN'S SHELLS AGAIN WHEN IT ENDS; a kept shell survives that too.
        store.StopShellsOf(name);
        Assert.False(childs.Finished);

        childs.Kill();
        theSessions.Kill();
        Assert.Equal(0, childSent());
        Assert.Equal(0, sessionSent());
    }
}
