using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The stress tests' collection: run alone, never beside another class.
///
/// <para>NOT IN PARALLEL because what these do — many threads, many timers — is exactly what makes a
/// timing-sensitive process test in another class flake when they share the machine. The tests also
/// keep themselves gentle (no spinning; every loop awaits a delay), but isolation is what makes that
/// a property of the suite rather than of the scheduler on the day.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackgroundStressCollection
{
    public const string Name = "BackgroundStress";
}

/// <summary>
/// Background runs under load: many children, random stops, many delivering threads. Each test ends in
/// a few hundred milliseconds; the invariants are the point, not the volume.
/// </summary>
[Collection(BackgroundStressCollection.Name)]
public class BackgroundStressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bg-stress-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;

    public BackgroundStressTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _manager?.Dispose();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    /// <summary>
    /// THIRTY BACKGROUND SPAWNS UNDER A CAP OF FOUR, answered in random order, a third of them stopped at
    /// random moments — queued, running, or already answered. Every run ends; every claim is given back;
    /// the cap is never exceeded; and every report is consumed exactly once, whether it was told, left,
    /// or refused and then read.
    ///
    /// <para>Guards ResultHandoff.ReadLocked's <c>_state = State.Taken</c>: a refused report read without
    /// being taken is read again.</para>
    ///
    /// <para>SEEDED, so a failure names a seed to rerun. Thread timing still varies run to run, which is
    /// the point: the invariants must hold for every interleaving the seed's choices meet.</para>
    /// </summary>
    [Fact]
    public async Task ManyChildrenRandomStops_EveryRunEndsOnce_NoClaimLeaks()
    {
        const int Children = 30;
        var rng = new Random(20261016);
        var provider = new OneByOneProvider();
        var delivery = new RefusingEveryFourth();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 50, CompressAbove = 40_000, ContextWindow = 200_000,
            Delivery = delivery, MaxConcurrentAgents = 4,
        });
        var store = new SubAgentStore();
        var spawner = new SubAgentSpawner(factory, store: store);

        var names = new List<string>();
        for (var i = 0; i < Children; i++)
        {
            var receipt = await spawner.TryInvokeAsync(Calls.Spawn(id: $"c{i}", description: $"job {i}"), null,
                CancellationToken.None, parentAgentId: "parent");
            names.Add(SubAgentEnvelope.NameOf(receipt)!);
        }

        var toStop = names.OrderBy(_ => rng.Next()).Take(Children / 3).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && names.Any(n => !store.BackgroundOf(n)!.Handoff.Ended))
        {
            if (toStop.Count > 0 && rng.Next(3) == 0)
            {
                var name = toStop[^1];
                toStop.RemoveAt(toStop.Count - 1);
                store.Stop(name, byUser: rng.Next(2) == 0);
            }
            else provider.ReleaseOne(rng);

            // A DELAY, NOT A SPIN: each lap yields the thread for the runs it just released.
            await Task.Delay(2);
        }

        var runs = names.Select(n => store.BackgroundOf(n)!).ToList();
        Assert.All(runs, r => Assert.True(r.Handoff.Ended, $"run '{r.Start.Name}' never ended"));

        // ENDED IS NOT DELIVERED: a handoff marks itself ended under its lock and hands the report on after
        // letting go, so the last run can read Ended before its Tell or Leave has run. Waited for, per run:
        // either the delivery recorded it, or the refusal has been recorded as undelivered.
        Assert.True(await Poll.Until(() => delivery.Attempts == Children), "not every report was offered");
        Assert.True(await Poll.Until(() =>
        {
            var took = delivery.Accepted;
            return runs.All(r => r.Handoff.Undelivered || took.Contains(r.Start.Name));
        }), "a delivery never settled");
        Assert.All(runs, r => Assert.Contains(r.State, new[] { BackgroundState.Finished, BackgroundState.Stopped }));
        Assert.True(await Poll.Until(() => names.All(n => !store.IsBusy(n))), "a run kept its claim");
        Assert.InRange(provider.MaxInFlight, 1, 4);

        // EXACTLY ONCE: each name reaches the parent through one accepted delivery, or — refused — through
        // one read, and a second read finds it already taken.
        var accepted = delivery.Accepted;
        Assert.Equal(accepted.Count, accepted.Distinct().Count());
        var read = runs.Where(r => r.Handoff.Undelivered).ToList();
        foreach (var r in read)
        {
            var first = r.Handoff.Read();
            Assert.True(first is { Ended: true, AlreadySent: false, Result: not null }, $"'{r.Start.Name}' was not readable");
            Assert.True(r.Handoff.Read().AlreadySent, $"'{r.Start.Name}' could be read twice");
        }
        Assert.Equal(Children, accepted.Count + read.Count);
        Assert.Empty(accepted.Intersect(read.Select(r => r.Start.Name)));
        Assert.Equal(Children, delivery.Attempts);
    }

    /// <summary>
    /// TWO HUNDRED DELIVERIES FROM SIXTEEN THREADS into one session while its model answers in bursts:
    /// never two requests at once, and every message the session accepted reaches the model.
    ///
    /// <para>Guards Session.Send's turn-ownership test, as S7 does.</para>
    ///
    /// <para>A REFUSAL IS ALLOWED, A LOSS IS NOT. The mailbox is bounded, so a sender that finds it full is
    /// told so and may retry — what may never happen is an accepted message that no request carries.</para>
    /// </summary>
    [Fact]
    public async Task DeliveriesFromManyThreads_OneLoopAlways()
    {
        const int Messages = 200, Threads = 16;
        var provider = new BurstProvider();
        _manager = SessionManager.Create(new AppPaths(_dir));
        var session = _manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        var delivery = new SessionAgentDelivery(session, session.SubAgents);
        var id = session.SessionId!;

        var outcomes = new DeliveryOutcome[Messages];
        var senders = Enumerable.Range(0, Threads).Select(t => Task.Run(async () =>
        {
            for (var i = t; i < Messages; i += Threads)
            {
                outcomes[i] = delivery.Tell(id, $"message-{i:D3}");
                await Task.Delay(1);
            }
        })).ToList();

        // BURSTS: the model is let go every few milliseconds, so deliveries land in every phase of a
        // turn — mid-request, between laps, in the tail.
        var all = Task.WhenAll(senders);
        while (!all.IsCompleted)
        {
            provider.ReleaseBurst();
            await Task.Delay(5);
        }
        await all;
        Assert.True(await Poll.Until(() =>
        {
            provider.ReleaseBurst();
            return !session.IsBusy && !session.TurnOwned;
        }), "the session never settled");

        Assert.Equal(1, provider.MaxInFlight);
        var seen = provider.All;
        var acceptedButLost = Enumerable.Range(0, Messages)
            .Where(i => outcomes[i] is DeliveryOutcome.Woke or DeliveryOutcome.Injected)
            .Where(i => !seen.Contains($"message-{i:D3}"))
            .ToList();
        Assert.Empty(acceptedButLost);
        Assert.All(outcomes, o => Assert.Contains(o, new[] { DeliveryOutcome.Woke, DeliveryOutcome.Injected, DeliveryOutcome.Refused }));
    }
}

/// <summary>Answers each request only when the test releases that one — in whatever order it picks.</summary>
internal sealed class OneByOneProvider : ILlmProvider
{
    private readonly List<TaskCompletionSource> _waiting = [];
    private int _inFlight, _maxInFlight;

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Lets one waiting request, chosen at random, answer; does nothing when none waits.</summary>
    public void ReleaseOne(Random rng)
    {
        TaskCompletionSource? one = null;
        lock (_waiting)
        {
            if (_waiting.Count == 0) return;
            var at = rng.Next(_waiting.Count);
            one = _waiting[at];
            _waiting.RemoveAt(at);
        }
        one.TrySetResult();
    }

    public string ProviderId => "one-by-one";
    public string ModelId => "one-by-one";
    public string DisplayName => "One by one";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight))
               && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiting) _waiting.Add(mine);
        try
        {
            await mine.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return new LlmResponse { Text = "answer", StopReason = "end_turn" };
        }
        finally
        {
            // A STOPPED REQUEST LEAVES THE QUEUE, so a release is never spent on one nobody awaits.
            lock (_waiting) _waiting.Remove(mine);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        yield return new LlmStreamChunk(r.Text, null, true);
    }
}

/// <summary>
/// Holds every request until the next burst, then lets all of them through; records what each carried.
/// </summary>
internal sealed class BurstProvider : ILlmProvider
{
    private readonly object _gate = new();
    private readonly List<string> _requests = [];
    private TaskCompletionSource _burst = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight, _maxInFlight;

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public string All { get { lock (_gate) return string.Join("\n", _requests); } }

    /// <summary>Answers every request waiting now; later ones wait for the next burst.</summary>
    public void ReleaseBurst()
    {
        TaskCompletionSource now;
        lock (_gate)
        {
            now = _burst;
            _burst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        now.TrySetResult();
    }

    public string ProviderId => "burst";
    public string ModelId => "burst";
    public string DisplayName => "Burst";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight))
               && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        try
        {
            Task burst;
            lock (_gate)
            {
                _requests.Add(string.Join("\n", messages.Select(m => m.Content)));
                burst = _burst.Task;
            }
            await burst.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return new LlmResponse { Text = "ok", StopReason = "end_turn" };
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        yield return new LlmStreamChunk(r.Text, null, true);
    }
}

/// <summary>
/// A parent that refuses every fourth report — told or left — so the undelivered path runs under load
/// beside the ordinary ones. Records the handle of every report it accepted.
/// </summary>
internal sealed class RefusingEveryFourth : IAgentDelivery
{
    private readonly List<string> _accepted = [];
    private int _attempts;

    public IReadOnlyList<string> Accepted { get { lock (_accepted) return [.. _accepted]; } }

    public int Attempts => Volatile.Read(ref _attempts);

    public DeliveryOutcome Tell(string agentId, string text) => Take(text, DeliveryOutcome.Woke);

    public DeliveryOutcome Tell(string agentId, AgentNotice notice) => Take(notice.ForModel, DeliveryOutcome.Woke);

    public DeliveryOutcome Leave(string agentId, AgentNotice notice) => Take(notice.ForModel, DeliveryOutcome.Queued);

    private DeliveryOutcome Take(string text, DeliveryOutcome accepted)
    {
        if (Interlocked.Increment(ref _attempts) % 4 == 0) return DeliveryOutcome.Refused;
        lock (_accepted) _accepted.Add(SubAgentEnvelope.NameOf(text)!);
        return accepted;
    }
}
