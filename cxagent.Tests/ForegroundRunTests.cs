using CxAgent.Core.Agents;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A foreground child's own token, linked to the turn's: the turn's cancel still reaches it until it is moved, a move
/// unlinks it, and a stop cancels it alone (spec decisions 5–11).
/// </summary>
public sealed class ForegroundRunTests
{
    private static SubAgentStore Store(string name)
    {
        var store = new SubAgentStore();
        store.Keep(AgentTabTests.Child(AgentTabTests.Says()), name);
        return store;
    }

    [Fact]
    public void TheTurnsCancel_ReachesAChildThatWasNotMoved()
    {
        var store = Store("finder");
        using var turn = new CancellationTokenSource();
        var run = store.BeginForeground("finder", turn.Token);

        turn.Cancel();

        Assert.True(run.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AMove_UnlinksTheTurn_AndReleasesAsMove()
    {
        var store = Store("finder");
        using var turn = new CancellationTokenSource();
        var run = store.BeginForeground("finder", turn.Token);

        Assert.True(store.MoveToBackground("finder"));
        turn.Cancel();

        Assert.Equal(ForegroundRelease.Move, await run.Released);
        Assert.False(run.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AStop_CancelsTheChildAlone_AndMarksTheUsersStop()
    {
        var store = Store("finder");
        using var turn = new CancellationTokenSource();
        var run = store.BeginForeground("finder", turn.Token);

        Assert.True(store.StopForeground("finder"));

        Assert.Equal(ForegroundRelease.Stop, await run.Released);
        Assert.True(run.Token.IsCancellationRequested);
        Assert.False(turn.IsCancellationRequested);
        Assert.True(store.WasStoppedByUser("finder"));
    }

    [Fact]
    public void OnlyTheFirstRelease_Counts()
    {
        var store = Store("finder");
        store.BeginForeground("finder", CancellationToken.None);

        Assert.True(store.MoveToBackground("finder"));
        Assert.False(store.StopForeground("finder"));
        Assert.False(store.WasStoppedByUser("finder"));
    }

    [Fact]
    public void NothingInTheForeground_IsNotMovable()
    {
        var store = Store("finder");
        Assert.False(store.MoveToBackground("finder"));
        Assert.False(store.StopForeground("finder"));
        Assert.Empty(store.ForegroundNames());
        Assert.Null(store.ForegroundSince());
    }

    [Fact]
    public void AUsersStop_IsClearedByTheirOwnSend()
    {
        var store = Store("finder");
        store.BeginForeground("finder", CancellationToken.None);
        store.StopForeground("finder");
        store.EndForeground("finder");

        store.ClearUserStop("finder");

        Assert.False(store.WasStoppedByUser("finder"));
    }

    /// <summary>
    /// A STOP OR A MOVE THAT LANDS AFTER THE ANSWER IS REFUSED. The answer reaches the parent either way, so a stop
    /// accepted there would kill the child's shells and refuse the model's next send to a child that finished.
    /// </summary>
    [Fact]
    public void AfterTheAnswer_StopAndMoveAreRefused()
    {
        var store = Store("finder");
        using var turn = new CancellationTokenSource();
        var run = store.BeginForeground("finder", turn.Token);

        Assert.True(run.TryAnswer());

        Assert.False(store.StopForeground("finder"));
        Assert.False(store.MoveToBackground("finder"));
        Assert.False(store.WasStoppedByUser("finder"));
        Assert.False(run.Token.IsCancellationRequested);
    }

    /// <summary>A release that landed first wins over the answer: the caller answers as released.</summary>
    [Fact]
    public void AfterAStop_TheAnswerIsRefused()
    {
        var store = Store("finder");
        using var turn = new CancellationTokenSource();
        var run = store.BeginForeground("finder", turn.Token);

        Assert.True(store.StopForeground("finder"));

        Assert.False(run.TryAnswer());
    }
}
