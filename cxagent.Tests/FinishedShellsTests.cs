using CxAgent.Core.Execution;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Background shells after they end: remembered, bounded, and found by owner.</summary>
public class FinishedShellsTests
{
    private static BackgroundJob Job(int pid, string owner = "session") =>
        new(pid, owner, $"cmd{pid}", DateTimeOffset.UtcNow, $"/tmp/out{pid}");

    [Fact]
    public void AFinishedShell_IsRemembered_WithItsExitAndPath()
    {
        var shells = new FinishedShells();
        shells.Track(Job(10), _ => true);
        shells.Finished(10, exitCode: 3, killed: false);

        var f = shells.Find(10)!;
        Assert.Equal(3, f.ExitCode);
        Assert.False(f.Killed);
        Assert.Equal("/tmp/out10", f.Job.OutputPath);
    }

    [Fact]
    public void ARunningShell_IsTrackedButNotFinished()
    {
        var shells = new FinishedShells();
        shells.Track(Job(11), _ => true);
        Assert.NotNull(shells.HandoffFor(11));
        Assert.Null(shells.Find(11));
    }

    [Fact]
    public void AnUntrackedPid_IsUnknown()
    {
        var shells = new FinishedShells();
        Assert.Null(shells.Find(99));
        Assert.Null(shells.HandoffFor(99));
    }

    /// <summary>BOUNDED: a list nobody prunes in a long session is a leak.</summary>
    [Fact]
    public void OnlyTheNewestFinishedAreKept()
    {
        var shells = new FinishedShells();
        for (var pid = 1; pid <= FinishedShells.Kept + 5; pid++)
        {
            shells.Track(Job(pid), _ => true);
            shells.Finished(pid, 0, false);
        }

        Assert.Null(shells.Find(1));
        Assert.Null(shells.HandoffFor(1));
        Assert.NotNull(shells.Find(FinishedShells.Kept + 5));
        Assert.Equal(FinishedShells.Kept, shells.Recent(_ => true).Count);
    }

    [Fact]
    public void Recent_FiltersByOwner_NewestFirst()
    {
        var shells = new FinishedShells();
        foreach (var (pid, owner) in new[] { (1, "a"), (2, "b"), (3, "a") })
        {
            shells.Track(Job(pid, owner), _ => true);
            shells.Finished(pid, 0, false);
        }

        Assert.Equal([3, 1], shells.Recent(o => o == "a").Select(f => f.Job.Pid));
    }

    /// <summary>A PID THE OS REUSES starts a new record rather than inheriting the old one's exit.</summary>
    [Fact]
    public void AReusedPid_StartsAFreshRecord()
    {
        var shells = new FinishedShells();
        shells.Track(Job(7), _ => true);
        shells.Finished(7, 1, false);
        var fresh = shells.Track(Job(7), _ => true);

        Assert.Null(shells.Find(7));
        Assert.Same(fresh, shells.HandoffFor(7));
    }
}
