using CxAgent.Core.Execution;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The foreground shell calls a turn waits on, as Ctrl+B and the composer's hint count them.</summary>
public sealed class ForegroundShellsTests
{
    /// <summary>EACH CALL IS ONE TASK, so two shells in one turn read "waiting on 2 tasks", as two children do.</summary>
    [Fact]
    public void EachWaitingCall_CountsOnce()
    {
        var shells = new ForegroundShells();
        using var a = shells.Begin("g1");
        using var b = shells.Begin("g1");
        using var other = shells.Begin("g2");

        Assert.Equal(2, shells.Count(id => id == "g1"));
    }

    /// <summary>
    /// A CALL ALREADY MOVED IS NOT MOVED AGAIN. It stays registered until its call returns, and a second Ctrl+B —
    /// tmux needs two — would otherwise report it moved twice.
    /// </summary>
    [Fact]
    public void ACallAlreadyMoved_IsNotCountedAgain()
    {
        var shells = new ForegroundShells();
        using var a = shells.Begin("g1");

        Assert.Equal(1, shells.DetachWhere(_ => true));
        Assert.Equal(0, shells.DetachWhere(_ => true));
        Assert.Equal(0, shells.Count(_ => true));
    }
}
