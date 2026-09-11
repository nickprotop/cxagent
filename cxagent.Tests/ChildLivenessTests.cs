using CxAgent.Core.Agents;
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
}
