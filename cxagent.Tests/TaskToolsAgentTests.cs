using CxAgent.Core.Agents;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class TaskToolsAgentTests
{
    private sealed record Rig(TaskTools Tools, SubAgentStore Store, SubAgentSpawner Spawner, RecordingDelivery Delivery);

    private static Rig Wired(ILlmProvider child, int? cap = null)
    {
        var delivery = new RecordingDelivery();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = child, Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 50, CompressAbove = 40_000, ContextWindow = 200_000, Delivery = delivery,
            MaxConcurrentAgents = cap,
        });
        var store = new SubAgentStore();
        return new Rig(new TaskTools(new TaskSources(DetachedProcessRegistry.Default, new FinishedShells(), "session", store)),
            store, new SubAgentSpawner(factory, store: store), delivery);
    }

    private static Task<string?> Spawn(Rig r, bool background = true, string description = "find thing") =>
        r.Spawner.TryInvokeAsync(Calls.Spawn(description: description, background: background), null,
            CancellationToken.None, parentAgentId: "session");

    private static Task<string> Call(Rig r, string tool, object args, string caller = "session",
        CancellationToken ct = default) => r.Tools.InvokeAsync(caller, Calls.Named(tool, args), ct);

    [Fact]
    public async Task TaskList_ShowsEveryKeptChild_WithItsState()
    {
        var held = new HeldProvider("a", "b");
        var r = Wired(held);
        await Spawn(r, background: true, description: "runner");
        held.Release();
        await r.Delivery.Next();
        await Spawn(r, background: true, description: "still going");

        var list = await Call(r, Tool.TaskList, new { });
        Assert.Contains("runner", list);
        Assert.Contains("finished", list);
        Assert.Contains("still-going", list);
        held.Release();
    }

    [Fact]
    public async Task TaskList_ShowsAnUndeliveredReport()
    {
        var held = new HeldProvider("r");
        var r = Wired(held);
        r.Delivery.Answer = DeliveryOutcome.Refused;
        await Spawn(r);
        held.Release();
        await r.Delivery.Next();
        Assert.True(await Poll.Until(() => r.Store.BackgroundOf("find-thing")!.Handoff.Undelivered));

        Assert.Contains("report not delivered", await Call(r, Tool.TaskList, new { }));
    }

    [Fact]
    public async Task TaskList_ForAChild_ShowsNoSubAgents()
    {
        var held = new HeldProvider("r");
        var r = Wired(held);
        await Spawn(r);
        Assert.DoesNotContain("find-thing", await Call(r, Tool.TaskList, new { }, caller: "some-child"));
        held.Release();
    }

    [Fact]
    public async Task TaskStop_StopsABackgroundAgent_AndItReportsCancelled()
    {
        var held = new HeldProvider("never");
        var r = Wired(held);
        await Spawn(r);
        Assert.True(await Poll.Until(() => held.Calls == 1));

        Assert.Contains("stopping 'find-thing'", await Call(r, Tool.TaskStop, new { id = "find-thing" }));
        var d = await r.Delivery.Next();
        Assert.True(d.Left);
        Assert.Contains("state=\"cancelled\"", d.Notice.ForModel);
        Assert.False(r.Store.BackgroundOf("find-thing")!.StoppedByUser);
    }

    [Fact]
    public async Task TaskStop_RefusesAFinishedAgent()
    {
        var held = new HeldProvider("done");
        var r = Wired(held);
        await Spawn(r);
        held.Release();
        await r.Delivery.Next();
        Assert.Contains("'find-thing' has already finished; task_output", await Call(r, Tool.TaskStop, new { id = "find-thing" }));
    }

    [Fact]
    public async Task TaskStop_RefusesAForegroundChild_WithTheReason()
    {
        var r = Wired(new HeldProvider("x"));
        r.Store.Keep(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
        }).Create(), "fg");
        r.Store.TryClaim("fg");
        Assert.Contains("not running in the background", await Call(r, Tool.TaskStop, new { id = "fg" }));
        r.Store.Release("fg");
    }

    [Fact]
    public async Task TaskStop_ByAChild_IsRefused()
    {
        var held = new HeldProvider("x");
        var r = Wired(held);
        await Spawn(r);
        Assert.Contains("only the session agent", await Call(r, Tool.TaskStop, new { id = "find-thing" }, caller: "child"));
        held.Release();
    }

    [Fact]
    public async Task TaskStop_AnUnknownHandle_NamesTaskList() =>
        Assert.Contains("task_list", await Call(Wired(new HeldProvider()), Tool.TaskStop, new { id = "ghost" }));

    [Fact]
    public async Task TaskOutput_ShowsProgress_WhileItRuns()
    {
        var held = new HeldProvider("x");
        var r = Wired(held);
        await Spawn(r);
        Assert.True(await Poll.Until(() => held.Calls == 1));
        Assert.Contains("is running", await Call(r, Tool.TaskOutput, new { id = "find-thing" }));
        held.Release();
    }

    [Fact]
    public async Task TaskOutput_Wait_ReturnsTheReport_WhenItEnds_AndNothingIsSent()
    {
        var held = new HeldProvider("found it");
        var r = Wired(held);
        await Spawn(r);
        var wait = Call(r, Tool.TaskOutput, new { id = "find-thing", wait = true, timeout = 10 });
        await Task.Delay(100);
        held.Release();

        Assert.Contains("found it", await wait);
        await Task.Delay(200);
        Assert.Empty(r.Delivery.Got);
    }

    [Fact]
    public async Task TaskOutput_Wait_TimesOut_WithProgress()
    {
        var held = new HeldProvider("x");
        var r = Wired(held);
        await Spawn(r);
        Assert.Contains("Still running after 1s", await Call(r, Tool.TaskOutput, new { id = "find-thing", wait = true, timeout = 1 }));
        held.Release();
        var d = await r.Delivery.Next();   // the report then arrives as a message
        Assert.Contains("x", d.Notice.ForModel);
    }

    [Fact]
    public async Task TaskOutput_WaitCancelled_ThenTheEnd_IsLeftForTheNextTurn()
    {
        var held = new HeldProvider("found it");
        var r = Wired(held);
        await Spawn(r);
        using var cts = new CancellationTokenSource();
        var wait = Call(r, Tool.TaskOutput, new { id = "find-thing", wait = true, timeout = 10 }, ct: cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        held.Release();

        var d = await r.Delivery.Next();
        Assert.False(d.Left);                      // the wait was gone before the end: an ordinary report
        Assert.Contains("found it", d.Notice.ForModel);
    }

    [Fact]
    public async Task TaskOutput_AfterTheReportWasSent_SaysSo()
    {
        var held = new HeldProvider("found it");
        var r = Wired(held);
        await Spawn(r);
        held.Release();
        await r.Delivery.Next();
        Assert.Contains("also sent this as a message", await Call(r, Tool.TaskOutput, new { id = "find-thing" }));
    }

    [Fact]
    public async Task AnUndeliveredReport_IsShownByTaskList_AndReturnedByTaskOutput()
    {
        var held = new HeldProvider("found it");
        var r = Wired(held);
        r.Delivery.Answer = DeliveryOutcome.Refused;
        await Spawn(r);
        held.Release();
        await r.Delivery.Next();
        Assert.True(await Poll.Until(() => r.Store.BackgroundOf("find-thing")!.Handoff.Undelivered));

        Assert.Contains("report not delivered", await Call(r, Tool.TaskList, new { }));
        var output = await Call(r, Tool.TaskOutput, new { id = "find-thing" });
        Assert.Contains("found it", output);
        Assert.DoesNotContain("also sent", output);
    }

    [Fact]
    public async Task TheTimeout_IsClampedToTheMaximum()
    {
        Assert.Equal(TaskTools.MaxWaitSeconds, TaskTools.WaitSeconds(99_999));
        Assert.Equal(1, TaskTools.WaitSeconds(0));
        Assert.Equal(TaskTools.DefaultWaitSeconds, TaskTools.WaitSeconds(null));
    }

    [Theory]
    [InlineData("find-thing")]
    public async Task TheIdArrivesAsAString(string id)
    {
        var held = new HeldProvider("x");
        var r = Wired(held);
        await Spawn(r);
        Assert.True(await Poll.Until(() => held.Calls == 1));
        Assert.Contains("is running", await Call(r, Tool.TaskOutput, new { id }));
        held.Release();
    }

    [Fact]
    public async Task AMissingId_NamesTheField() =>
        Assert.Contains("'id' is required", await Call(Wired(new HeldProvider()), Tool.TaskOutput, new { }));

    [Fact]
    public async Task AForegroundChild_PointsAtAgentSend()
    {
        var r = Wired(new HeldProvider());
        r.Store.Keep(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
        }).Create(), "fg");
        Assert.Contains("agent_send", await Call(r, Tool.TaskOutput, new { id = "fg" }));
    }

    // --- Describe takes nothing --------------------------------------------------------------

    [Fact]
    public async Task Describe_ShowsAnUndeliveredReport_WithoutTakingIt()
    {
        var held = new HeldProvider("found it");
        var r = Wired(held);
        r.Delivery.Answer = DeliveryOutcome.Refused;
        await Spawn(r);
        held.Release();
        await r.Delivery.Next();
        var run = r.Store.BackgroundOf("find-thing")!;
        Assert.True(await Poll.Until(() => run.Handoff.Undelivered));

        Assert.Contains("found it", r.Tools.Describe("find-thing"));
        Assert.True(run.Handoff.Undelivered);

        var output = await Call(r, Tool.TaskOutput, new { id = "find-thing" });
        Assert.Contains("found it", output);
        Assert.DoesNotContain("also sent", output);
        Assert.False(run.Handoff.Undelivered);

        // Taken once, so a second read says it was handed over already.
        Assert.Contains("also sent this as a message", await Call(r, Tool.TaskOutput, new { id = "find-thing" }));
    }

    // --- the id ------------------------------------------------------------------------------

    [Fact]
    public async Task ANumericId_IsAPid_AndReachesTheShellHalf()
    {
        var r = Wired(new HeldProvider());
        var number = await r.Tools.InvokeAsync("session", Calls.Named(Tool.TaskOutput, new { id = 4242 }), default);
        Assert.Contains("no background command with pid 4242", number);
    }

    [Fact]
    public async Task ANumericStringId_IsAPid_AndReachesTheShellHalf()
    {
        var r = Wired(new HeldProvider());
        var text = await Call(r, Tool.TaskStop, new { id = "4242" });
        Assert.Contains("no background command with pid 4242", text);
    }

    [Fact]
    public async Task AHandleId_IsNotTakenForAPid()
    {
        var held = new HeldProvider("x");
        var r = Wired(held);
        await Spawn(r);
        var text = await Call(r, Tool.TaskStop, new { id = "find-thing" });
        Assert.DoesNotContain("pid", text);
        Assert.Contains("stopping 'find-thing'", text);
        held.Release();
    }

    [Fact]
    public async Task ANonIntegerNumberId_IsRefusedClearly_NotAsMissing()
    {
        var text = await Call(Wired(new HeldProvider()), Tool.TaskOutput, new { id = 1.5 });
        Assert.DoesNotContain("is required", text);
        Assert.Contains("whole number", text);
    }

    // --- states ------------------------------------------------------------------------------

    [Fact]
    public async Task TaskList_NamesEachState()
    {
        var held = new HeldProvider("a", "b");
        var r = Wired(held, cap: 1);
        await Spawn(r, description: "first");
        Assert.True(await Poll.Until(() => held.Calls == 1));
        await Spawn(r, description: "second");
        Assert.Contains("'second' (", await Call(r, Tool.TaskList, new { }));
        Assert.Matches(@"'first'.*— running", await Call(r, Tool.TaskList, new { }));
        Assert.Matches(@"'second'.*— queued", await Call(r, Tool.TaskList, new { }));

        held.Release();
        await r.Delivery.Next();
        await r.Delivery.Next();
        Assert.True(await Poll.Until(() => r.Store.BackgroundOf("second")!.Handoff.Ended));
        Assert.Matches(@"'first'.*— finished", await Call(r, Tool.TaskList, new { }));
    }

    [Fact]
    public async Task TaskList_ShowsAStoppedRun_AndStoppingAQueuedRunWorks()
    {
        var held = new HeldProvider("a");
        var r = Wired(held, cap: 1);
        await Spawn(r, description: "first");
        Assert.True(await Poll.Until(() => held.Calls == 1));
        await Spawn(r, description: "second");

        Assert.Contains("stopping 'second'", await Call(r, Tool.TaskStop, new { id = "second" }));
        Assert.True(await Poll.Until(() => r.Store.BackgroundOf("second")!.Handoff.Ended));
        Assert.Matches(@"'second'.*— stopped", await Call(r, Tool.TaskList, new { }));
        held.Release();
    }

    [Fact]
    public async Task TaskList_NamesABusyAndAnIdleForegroundChild()
    {
        var r = Wired(new HeldProvider());
        r.Store.Keep(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
        }).Create(), "fg");
        Assert.Matches(@"'fg'.*— idle", await Call(r, Tool.TaskList, new { }));
        r.Store.TryClaim("fg");
        Assert.Matches(@"'fg'.*— busy", await Call(r, Tool.TaskList, new { }));
        r.Store.Release("fg");
    }

    [Fact]
    public async Task AResumedForegroundChild_IsBusy_NotFinished()
    {
        var held = new HeldProvider("done");
        var r = Wired(held);
        await Spawn(r);
        held.Release();
        await r.Delivery.Next();
        Assert.True(await Poll.Until(() => r.Store.BackgroundOf("find-thing")!.Handoff.Ended));

        Assert.True(r.Store.TryClaim("find-thing"));
        Assert.Matches(@"'find-thing'.*— busy", await Call(r, Tool.TaskList, new { }));
        Assert.Contains("not running in the background", await Call(r, Tool.TaskStop, new { id = "find-thing" }));
        r.Store.Release("find-thing");
    }

    // --- an ended run ------------------------------------------------------------------------

    [Fact]
    public async Task Wait_OnAnEndedRun_ReturnsTheReportAtOnce()
    {
        var held = new HeldProvider("found it");
        var r = Wired(held);
        await Spawn(r);
        held.Release();
        await r.Delivery.Next();

        var started = DateTime.UtcNow;
        Assert.Contains("found it", await Call(r, Tool.TaskOutput, new { id = "find-thing", wait = true, timeout = 10 }));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
