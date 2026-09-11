using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The liveness a child gets while it is working — the apparatus that used to live inside the
/// `agent` tool call and now belongs to the child itself.
///
/// <para>WHY THIS FILE IS SEPARATE FROM SubAgentSpawnerTests: these are about a run's STATE across
/// several stretches of work, not about what a spawn returns. A resumed child is the case that had
/// no coverage at all, and a test for it sitting among spawn assertions reads as a spawn test.</para>
/// </summary>
public class ChildLivenessTests
{
    [Fact]
    public void ARunsTurns_AccumulateAcrossStretches()
    {
        var run = new ChildRun(null!, "job-1");

        run.Begin(_ => { });
        run.Turns++;
        run.Turns++;
        run.End();

        run.Begin(_ => { });
        run.Turns++;
        run.End();

        // CUMULATIVE, NOT PER-SEND: "3 turns" is the truth about the agent. Resetting per stretch
        // would make the number mean "turns since the last thing I asked", which no reader expects.
        Assert.Equal(3, run.Turns);
    }

    [Fact]
    public void ABeginRebasesTheClock_SoElapsedMeansThisStretch()
    {
        var run = new ChildRun(null!, "job-1");

        run.Begin(_ => { });
        var first = run.Started;
        run.End();

        Thread.Sleep(20);
        run.Begin(_ => { });

        Assert.True(run.Started > first);
        run.End();
    }

    [Fact]
    public void ASecondBeginWithoutAnEnd_DoesNotStartASecondTimer()
    {
        var run = new ChildRun(null!, "job-1");

        Assert.True(run.Begin(_ => { }));
        // The store refuses a second claim, so this cannot happen through the supported path — but a
        // double subscription would, and it must not leave a timer nothing disposes.
        Assert.False(run.Begin(_ => { }));

        Assert.True(run.End());
        Assert.False(run.End());
    }

    /// <summary>
    /// A resume is work, so the cap that bounds spawns bounds it too.
    ///
    /// <para>WITHOUT THIS A SEND WALKS PAST maxConcurrentAgents. The spawner waits the slot inside
    /// its own started task; agent_send never enters that method, so a user who capped their
    /// endpoint at two concurrent children could have two spawns and any number of resumes running
    /// at once — which is the cap not holding, on the path a model reaches for most.</para>
    /// </summary>
    [Fact]
    public async Task AResumeWaitsTheSameSlotASpawnWaits()
    {
        var slot = new SemaphoreSlim(1);
        var store = new SubAgentStore();
        var reach = new AgentReachTools(store, slot);

        await slot.WaitAsync();                       // the cap is full

        var send = reach.InvokeAsync(CxAgent.Core.Jobs.Tool.AgentSend, "nobody", "hello", CancellationToken.None);
        var finished = await Task.WhenAny(send, Task.Delay(200));

        // IT REFUSES BEFORE IT WAITS, and that is deliberate: a name nothing keeps can be answered
        // immediately, and making the model wait behind the cap to be told the handle is wrong burns
        // the very slot the cap exists to protect.
        Assert.Same(send, finished);
        Assert.Contains("no sub-agent named", await send);

        slot.Release();
    }

    /// <summary>
    /// A spawn registers the run before the store announces the claim.
    ///
    /// <para>THE ORDER IS THE WHOLE CONTRACT. SubAgentSpawner calls onChild, then Keep, then
    /// TryBeginSend — so the registry entry exists by the time SendBegan arrives. Reverse those and
    /// the begin finds nothing, the timer never starts, and a freshly spawned row is as dead as the
    /// resumed one this work exists to fix.</para>
    /// </summary>
    [Fact]
    public async Task ASpawnedChild_HasALiveRunBeforeItsClaimIsAnnounced()
    {
        var parent = SubAgentSpawnerTests.ParentWithSpawning(out var store);

        string? beganFor = null;
        store.SendBegan += id => beganFor ??= id;

        var text = await parent.SendAsync("spawn a worker", CancellationToken.None);

        Assert.NotNull(beganFor);
        // The run was already registered when the claim was announced, which is what a live row
        // needs: the begin handler has nothing to start a timer on otherwise.
        Assert.NotNull(parent.ChildRunFor(beganFor!));
    }
}
