using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What the panel lists: every described background job, read live from its process, filtered to the
/// session asking.
///
/// <para>ITS OWN REGISTRY, so a reap here cannot kill another test's process and another test's job
/// cannot appear on this board.</para>
/// </summary>
public class BackgroundJobBoardTests : IDisposable
{
    private readonly DetachedProcessRegistry _registry = new();
    private readonly BackgroundJobBoard _board;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-board-tests", Guid.NewGuid().ToString("N"));

    public BackgroundJobBoardTests() => _board = new BackgroundJobBoard(_registry);

    public void Dispose()
    {
        _registry.ReapAll();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private DetachedProcess Detach(string command) =>
        ProcessRunner.DetachAsync(
            new ProcessSpec("/bin/sh", ["-c", command], new RunOptions(SpillDir: _dir)),
            new CollectingContext(), _registry).GetAwaiter().GetResult();

    private DetachedProcess Start(string command, string agent, DateTimeOffset? started = null)
    {
        var process = Detach(command);
        _registry.Describe(process, new BackgroundJob(process.Pid, agent, command,
            started ?? DateTimeOffset.UtcNow, process.OutputPath));
        return process;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static Func<string, bool> Is(string agent) => id => id == agent;

    [Fact]
    public void ARunningJob_IsListedForItsOwner_AndNobodyElse()
    {
        Start("sleep 30", "A");

        var mine = Assert.Single(_board.For(Is("A")));
        Assert.Equal(BackgroundJobState.Running, mine.View().State);
        Assert.Empty(_board.For(Is("B")));
        Assert.Equal(1, _board.RunningElsewhere(Is("B")));
        Assert.Equal(0, _board.RunningElsewhere(Is("A")));
    }

    /// <summary>A command that fails at once is the result most worth showing, and the easiest to miss.</summary>
    [Fact]
    public async Task AFastFailure_IsSeenFinished_NotMissed()
    {
        Start("exit 9", "A");

        var entry = Assert.Single(_board.For(Is("A")));
        await Until(() => entry.Finished);

        var view = entry.View();
        Assert.Equal(BackgroundJobState.Exited, view.State);
        Assert.Equal(9, view.ExitCode);
        Assert.Equal(0, _board.RunningElsewhere(Is("B")));
    }

    [Fact]
    public void AKilledJob_ReadsKilled()
    {
        var process = Start("sleep 30", "A");
        process.Kill();

        Assert.Equal(BackgroundJobState.Killed, Assert.Single(_board.For(Is("A"))).View().State);
    }

    /// <summary>A finished row's clock stops: "0:40" an hour later, not "1:00:40".</summary>
    [Fact]
    public async Task AFinishedJobsElapsed_IsFrozen()
    {
        Start("exit 0", "A");
        var entry = Assert.Single(_board.For(Is("A")));
        await Until(() => entry.Finished);

        var first = entry.View().Elapsed;
        await Task.Delay(60);
        Assert.Equal(first, entry.View().Elapsed);
    }

    [Fact]
    public async Task RunningComesFirst_ThenNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        Start("exit 0", "A", now.AddSeconds(-30));
        Start("sleep 30", "A", now.AddSeconds(-20));
        Start("sleep 30", "A", now.AddSeconds(-10));
        await Until(() => _board.For(Is("A")).Count(e => e.Finished) == 1);

        var order = _board.For(Is("A")).Select(e => e.Job.Started).ToList();
        Assert.Equal([now.AddSeconds(-10), now.AddSeconds(-20), now.AddSeconds(-30)], order);
    }

    [Fact]
    public async Task ClearFinished_RemovesOnlyTheOwnersFinishedEntries()
    {
        Start("exit 0", "A");
        Start("sleep 30", "A");
        Start("exit 0", "B");
        await Until(() => _board.For(_ => true).Count(e => e.Finished) == 2);

        _board.ClearFinished(Is("A"));

        Assert.Equal(BackgroundJobState.Running, Assert.Single(_board.For(Is("A"))).View().State);
        Assert.Single(_board.For(Is("B")));
    }

    /// <summary>A session whose user never speaks again must not grow the board without bound.</summary>
    [Fact]
    public async Task FinishedEntries_AreCapped_OldestDroppedFirst()
    {
        var first = Start("exit 0", "A");
        for (var i = 0; i < BackgroundJobBoard.MaxFinished + 2; i++)
        {
            // SIXTEEN MAY RUN AT ONCE, and a seventeenth is refused rather than queued — so the
            // starts wait for the short-lived ones to clear rather than racing the bound.
            await Until(() => _registry.Live.Count < 8);
            Start("exit 0", "A");
        }

        await Until(() => _board.For(Is("A")).Count == BackgroundJobBoard.MaxFinished
                          && _board.For(Is("A")).All(e => e.Finished));

        Assert.DoesNotContain(_board.For(Is("A")), e => e.Process == first);
    }
}
