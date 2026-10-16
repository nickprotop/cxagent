using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

public class BackgroundSpawnTests
{
    private static (SubAgentSpawner Spawner, SubAgentStore Store, RecordingDelivery Delivery, SubAgentFactory Factory)
        Wired(ILlmProvider provider, int? cap = null, bool allow = true)
    {
        var delivery = new RecordingDelivery();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 50, CompressAbove = 40_000, ContextWindow = 200_000,
            Delivery = delivery, MaxConcurrentAgents = cap, AllowBackground = allow,
        });
        var store = new SubAgentStore();
        return (new SubAgentSpawner(factory, store: store), store, delivery, factory);
    }

    private static MockLlmProvider Says(params string[] texts)
    {
        var p = new MockLlmProvider();
        foreach (var t in texts) p.EnqueueResponse(new LlmResponse { Text = t, StopReason = "end_turn" });
        return p;
    }

    private static Task<string?> Spawn(SubAgentSpawner s, ToolCall? call = null) =>
        s.TryInvokeAsync(call ?? Calls.Spawn(), null, CancellationToken.None, parentAgentId: "parent");

    [Fact]
    public async Task ABackgroundSpawn_ReturnsAReceipt_BeforeTheChildAnswers()
    {
        var held = new HeldProvider("found it");
        var (spawner, store, _, _) = Wired(held);

        var receipt = await Spawn(spawner);

        Assert.Equal(SubAgentEnvelope.Running, SubAgentEnvelope.StateOf(receipt));
        Assert.Equal("find-thing", SubAgentEnvelope.NameOf(receipt));
        Assert.True(store.IsBusy("find-thing"));
        Assert.Contains("task_output", receipt);
        Assert.Contains("task_stop", receipt);
        held.Release();
    }

    [Fact]
    public async Task ItsReport_IsToldToTheParent_SelfContained()
    {
        var held = new HeldProvider("found it");
        var (spawner, _, delivery, _) = Wired(held);
        await Spawn(spawner);

        held.Release();
        var d = await delivery.Next();

        Assert.Equal("parent", d.To);
        Assert.False(d.Left);
        Assert.Contains("Its brief was: find the thing", d.Notice.ForModel);
        Assert.Contains("You spawned it to: find thing", d.Notice.ForModel);
        Assert.Contains("found it", d.Notice.ForModel);
        Assert.Contains("state=\"completed\"", d.Notice.ForModel);
        Assert.Contains("find-thing", d.Notice.Shown);
        Assert.Contains("completed", d.Notice.Shown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData("false")]
    [InlineData("False")]
    public async Task BackgroundFalse_WaitsForTheAnswer(object flag)
    {
        var (spawner, _, delivery, _) = Wired(Says("found it"));
        var call = Calls.Named(Tool.Agent, new { description = "find thing", prompt = "p", background = flag });

        Assert.Equal("completed", SubAgentEnvelope.StateOf(await Spawn(spawner, call)));
        Assert.Empty(delivery.Got);
    }

    [Fact]
    public async Task AnUnreadableFlag_TakesTheDefault()
    {
        var held = new HeldProvider("x");
        var (spawner, _, _, _) = Wired(held);
        var call = Calls.Named(Tool.Agent, new { description = "d", prompt = "p", background = "maybe" });
        Assert.Equal(SubAgentEnvelope.Running, SubAgentEnvelope.StateOf(await Spawn(spawner, call)));
        held.Release();
    }

    [Fact]
    public async Task WithoutAParentId_ItSpawnsInTheForeground()
    {
        var (spawner, _, _, _) = Wired(Says("found it"));
        Assert.Equal("completed", SubAgentEnvelope.StateOf(
            await spawner.TryInvokeAsync(Calls.Spawn(), null, CancellationToken.None)));
    }

    [Fact]
    public async Task WhenBackgroundIsNotAllowed_ItSpawnsInTheForeground()
    {
        var (spawner, _, _, _) = Wired(Says("found it"), allow: false);
        Assert.Equal("completed", SubAgentEnvelope.StateOf(await Spawn(spawner)));
    }

    [Fact]
    public async Task WithoutAStore_ItSpawnsInTheForeground()
    {
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = Says("found it"), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 50, CompressAbove = 40_000, ContextWindow = 200_000, Delivery = new RecordingDelivery(),
        });
        Assert.Equal("completed", SubAgentEnvelope.StateOf(await Spawn(new SubAgentSpawner(factory))));
    }

    [Fact]
    public async Task AStoppedRun_IsLeftAsCancelled_NotWoken()
    {
        var held = new HeldProvider("never");
        var (spawner, store, delivery, _) = Wired(held);
        await Spawn(spawner);
        Assert.True(await Poll.Until(() => held.Calls == 1));

        Assert.True(store.Stop("find-thing", byUser: true));
        var d = await delivery.Next();

        Assert.True(d.Left);
        Assert.Contains("state=\"cancelled\"", d.Notice.ForModel);
        Assert.Contains("stopped", d.Notice.Shown);
        Assert.False(store.IsBusy("find-thing"));
        Assert.Equal(BackgroundState.Stopped, store.BackgroundOf("find-thing")!.State);
    }

    [Fact]
    public async Task AStopWhileQueued_ReportsCancelled_AndAnnouncesNothing()
    {
        var (spawner, store, delivery, factory) = Wired(new HeldProvider("never"), cap: 1);
        var began = 0;
        store.SendBegan += _ => Interlocked.Increment(ref began);
        await factory.ConcurrencySlot!.WaitAsync();
        await Spawn(spawner);

        store.Stop("find-thing", byUser: true);
        var d = await delivery.Next();

        Assert.True(d.Left);
        Assert.Contains("state=\"cancelled\"", d.Notice.ForModel);
        Assert.Equal(0, Volatile.Read(ref began));
        Assert.False(store.IsBusy("find-thing"));
        Assert.Equal(0, factory.ConcurrencySlot.CurrentCount);   // the slot it never took is untouched
        factory.ConcurrencySlot.Release();
    }

    /// <summary>
    /// A MESSAGE SENT WHILE IT QUEUED WAS SENT AFTER ITS BRIEF, and is read after it — the loop's own
    /// drain puts it there; draining before the brief would put a correction ahead of the task.
    /// </summary>
    [Fact]
    public async Task AMessageSentWhileQueued_IsReadAfterTheBrief()
    {
        var provider = Says("ok");
        var (spawner, store, delivery, factory) = Wired(provider, cap: 1);
        await factory.ConcurrencySlot!.WaitAsync();
        await Spawn(spawner);

        store.Find("find-thing")!.Agent.Agent.Mailbox.TryEnqueue("the schema changed", out _);
        factory.ConcurrencySlot.Release();
        await delivery.Next();

        var contents = provider.LastMessages!.Select(m => m.Content ?? "").ToList();
        var brief = contents.FindIndex(c => c.Contains("find the thing"));
        var message = contents.FindIndex(c => c.Contains("the schema changed"));
        Assert.True(brief >= 0 && brief < message);
    }

    [Fact]
    public async Task AFailingChild_ReportsAnError_ToTheParent()
    {
        var (spawner, _, delivery, _) = Wired(new MockLlmProvider());   // empty queue: the first request throws
        await Spawn(spawner);
        var d = await delivery.Next();
        Assert.Contains("state=\"error\"", d.Notice.ForModel);
        Assert.False(d.Left);
    }

    [Fact]
    public async Task AParentThatRefusesTheReport_LeavesItUndelivered_AndReadable()
    {
        var held = new HeldProvider("found it");
        var (spawner, store, delivery, _) = Wired(held);
        delivery.Answer = DeliveryOutcome.Refused;
        await Spawn(spawner);
        held.Release();
        await delivery.Next();

        var run = store.BackgroundOf("find-thing")!;
        Assert.True(await Poll.Until(() => run.Handoff.Undelivered));
        Assert.Contains("found it", run.Handoff.Read().Result);
    }

    [Fact]
    public async Task TwoBackgroundSpawnsWithOneLabel_GetTwoHandles_AndTwoReports()
    {
        var held = new HeldProvider("a", "b");
        var (spawner, _, delivery, _) = Wired(held);
        var first = await Spawn(spawner, Calls.Spawn(id: "c1"));
        var second = await Spawn(spawner, Calls.Spawn(id: "c2"));

        Assert.NotEqual(SubAgentEnvelope.NameOf(first), SubAgentEnvelope.NameOf(second));
        held.Release();
        await delivery.Next(1);
        Assert.Equal(2, delivery.Got.Count);
    }

    [Fact]
    public async Task TheCap_BoundsBackgroundChildren()
    {
        var held = new HeldProvider("a", "b", "c");
        var (spawner, _, delivery, _) = Wired(held, cap: 2);
        for (var i = 0; i < 3; i++) await Spawn(spawner, Calls.Spawn(id: $"c{i}", description: $"d{i}"));
        Assert.True(await Poll.Until(() => held.Calls == 2));
        await Task.Delay(150);
        Assert.Equal(2, held.MaxInFlight);
        held.Release();
        await delivery.Next(2);
    }

    /// <summary>A RENDER THAT THROWS still ends the run and reports — the runner's tail cannot leave it Running.</summary>
    [Fact]
    public async Task ARunnerFailureAfterTheAnswer_StillFinishesTheRun()
    {
        var (_, store, delivery, factory) = Wired(Says("x"));
        var child = factory.Create(label: "boom");
        var name = store.Keep(child, "boom");
        store.TryClaim(name);
        var run = store.BeginBackground(
            new BackgroundStart(Name: name, ParentAgentId: "parent", Brief: "p", Description: "boom", IsResume: false),
            new ResultHandoff(_ => true));

        await BackgroundRunner.Start(new BackgroundWork(store, store.Find(name)!, run, "p", null, delivery,
            _ => throw new InvalidOperationException("render failed")));

        Assert.True(run.Handoff.Ended);
        Assert.Equal(BackgroundState.Finished, run.State);
        Assert.False(store.IsBusy(name));
        var d = Assert.Single(delivery.Got);
        Assert.Contains("render failed", d.Notice.ForModel);
    }
}
