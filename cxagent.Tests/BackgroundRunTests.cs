using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A background run's own life: its token, its states, and who may stop it.</summary>
public class BackgroundRunTests
{
    private static BackgroundStart Start(string name = "x") =>
        new(Name: name, ParentAgentId: "parent", Brief: "do it", Description: "x", IsResume: false);

    private static ResultHandoff Handoff() => new(_ => true);

    [Fact]
    public void ARun_StartsQueued_ThenRuns()
    {
        var store = new SubAgentStore();
        var run = store.BeginBackground(Start(), Handoff());
        Assert.Equal(BackgroundState.Queued, run.State);
        run.MarkRunning();
        Assert.Equal(BackgroundState.Running, store.BackgroundOf("x")!.State);
    }

    [Fact]
    public void Stop_CancelsTheRunsOwnToken_AndRemembersTheUser()
    {
        var store = new SubAgentStore();
        var run = store.BeginBackground(Start(), Handoff());
        Assert.True(store.Stop("x", byUser: true));
        Assert.True(run.Token.IsCancellationRequested);
        Assert.True(run.StoppedByUser);
    }

    [Fact]
    public void AParentsStop_IsNotTheUsers()
    {
        var store = new SubAgentStore();
        var run = store.BeginBackground(Start(), Handoff());
        Assert.True(store.Stop("x", byUser: false));
        Assert.False(run.StoppedByUser);
    }

    /// <summary>ONCE IT HAS ANSWERED IT CANNOT BE STOPPED: a stop then would say "stopped" over a report saying "completed".</summary>
    [Fact]
    public void AnAnsweredRun_RefusesAStop()
    {
        var store = new SubAgentStore();
        var run = store.BeginBackground(Start(), Handoff());
        Assert.True(run.MarkAnswered());
        Assert.False(store.Stop("x", byUser: true));
        Assert.False(run.StoppedByUser);
    }

    [Fact]
    public void AStoppedRun_CannotBeMarkedAnswered()
    {
        var store = new SubAgentStore();
        var run = store.BeginBackground(Start(), Handoff());
        store.Stop("x", byUser: true);
        Assert.False(run.MarkAnswered());
    }

    [Fact]
    public void AFinishedRun_CannotBeStopped()
    {
        var store = new SubAgentStore();
        var run = store.BeginBackground(Start(), Handoff());
        run.Finish(BackgroundState.Finished);
        Assert.False(store.Stop("x", byUser: true));
    }

    [Fact]
    public void StoppingAnUnknownHandle_IsFalse() => Assert.False(new SubAgentStore().Stop("nobody", byUser: true));

    [Fact]
    public void StopAll_StopsOnlyWhatIsGoing_AndSilences()
    {
        var store = new SubAgentStore();
        var sent = 0;
        var going = store.BeginBackground(Start("a"), Handoff());
        store.BeginBackground(Start("b"), Handoff()).Finish(BackgroundState.Finished);

        Assert.Equal(["a"], store.StopAll());
        going.Handoff.Complete("r", _ => { sent++; return true; });
        Assert.Equal(0, sent);
    }

    [Fact]
    public void ANewRun_ReplacesTheEndedOneForThatHandle()
    {
        var store = new SubAgentStore();
        store.BeginBackground(Start(), Handoff()).Finish(BackgroundState.Finished);
        var second = store.BeginBackground(Start() with { IsResume = true }, Handoff());
        Assert.Same(second, store.BackgroundOf("x"));
        Assert.Single(store.Background());
    }

    [Fact]
    public void ClearUserStop_LetsTheModelResumeAgain()
    {
        var store = new SubAgentStore();
        var run = store.BeginBackground(Start(), Handoff());
        store.Stop("x", byUser: true);
        run.ClearUserStop();
        Assert.False(run.StoppedByUser);
    }

    /// <summary>MANY STOPS AT ONCE CANCEL ONE TOKEN, and every one of them sees the run still going or not — none throws.</summary>
    [Fact]
    public void ConcurrentStops_CancelOnce_AndNoneThrows()
    {
        var store = new SubAgentStore();
        store.BeginBackground(Start(), Handoff());
        var wins = Enumerable.Range(0, 32).AsParallel().Count(_ => store.Stop("x", byUser: false));
        Assert.True(wins >= 1);
        Assert.True(store.BackgroundOf("x")!.Token.IsCancellationRequested);
    }

    /// <summary>A STOP RACING THE FINISH never throws on a disposed token source.</summary>
    [Fact]
    public async Task AStopRacingTheFinish_NeverThrows()
    {
        for (var i = 0; i < 200; i++)
        {
            var store = new SubAgentStore();
            var run = store.BeginBackground(Start(), Handoff());
            await Task.WhenAll(
                Task.Run(() => store.Stop("x", byUser: true)),
                Task.Run(() => run.Finish(BackgroundState.Finished)));
        }
    }
}
