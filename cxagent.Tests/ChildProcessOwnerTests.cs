using System.Diagnostics;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Startup reaps what a CRASHED run left behind — not what another window, still running, owns.
///
/// <para>EVERY cxagent ON ONE CONFIG DIRECTORY SHARES ONE plugin-children.json: two TUI windows, or a
/// window and a headless <c>-p</c> run. Each record names the process that wrote it, and a startup
/// reap that killed every record would kill the other window's language server and background
/// commands while it is still using them.</para>
/// </summary>
public class ChildProcessOwnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("child-owner-").FullName;
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        Directory.Delete(_dir, recursive: true);
    }

    private Process Sleeper()
    {
        var p = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        _started.Add(p);
        return p;
    }

    private static ProcessIdentity IdentityOf(Process p) => new(p.Id, p.StartTime.ToUniversalTime());

    /// <summary>
    /// A run that no longer exists: this pid, but a start time no process here has. What a test of
    /// crash recovery records its children as — a store owned by the live test process would be a
    /// running neighbour's, which a reap rightly leaves alone.
    /// </summary>
    internal static readonly ProcessIdentity Crashed = new(Environment.ProcessId, DateTime.UnixEpoch);

    private static ChildProcessRecord ChildOf(Process child, string plugin = "lsp") =>
        new(child.Id, child.StartTime.ToUniversalTime(), plugin);

    [Fact]
    public void AnotherLiveWindowsChild_IsLeftRunning()
    {
        var otherWindow = Sleeper();
        var child = Sleeper();
        new ChildProcessStore(_dir, IdentityOf(otherWindow)).Add(ChildOf(child));

        var log = new List<string>();
        new ChildProcessStore(_dir).ReapOrphans(log.Add);

        Assert.False(child.WaitForExit(TimeSpan.FromMilliseconds(300)), "a live window's child was killed");
        Assert.Empty(log);
        Assert.Contains(child.Id.ToString(), File.ReadAllText(Path.Combine(_dir, "plugin-children.json")));
    }

    [Fact]
    public void ACrashedRunsChild_IsReaped()
    {
        var child = Sleeper();
        new ChildProcessStore(_dir, Crashed).Add(ChildOf(child));

        var log = new List<string>();
        new ChildProcessStore(_dir).ReapOrphans(log.Add);

        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)));
        Assert.Contains(log, line => line.Contains(child.Id.ToString()));
    }

    /// <summary>The reap's own save keeps the live window's records: it drops only what it reaped.</summary>
    [Fact]
    public void Reaping_KeepsTheLiveWindowsRecords_AndDropsOnlyTheReapedOnes()
    {
        var otherWindow = Sleeper();
        var live = Sleeper();
        var orphan = Sleeper();
        new ChildProcessStore(_dir, IdentityOf(otherWindow)).Add(ChildOf(live, "live"));
        new ChildProcessStore(_dir, Crashed).Add(ChildOf(orphan, "orphan"));

        new ChildProcessStore(_dir).ReapOrphans(_ => { });

        var file = File.ReadAllText(Path.Combine(_dir, "plugin-children.json"));
        Assert.Contains(live.Id.ToString(), file);
        Assert.DoesNotContain(orphan.Id.ToString(), file);
    }

    /// <summary>
    /// A RECORD WITH NO OWNER was written by a cxagent from before owners were recorded. It is reaped,
    /// as every record was then: it cannot say it belongs to anyone running.
    /// </summary>
    [Fact]
    public void ARecordWithNoOwner_IsReaped()
    {
        var child = Sleeper();
        File.WriteAllText(Path.Combine(_dir, "plugin-children.json"),
            $$"""[{"Pid":{{child.Id}},"StartTimeUtc":"{{child.StartTime.ToUniversalTime():O}}","Plugin":"lsp"}]""");

        new ChildProcessStore(_dir).ReapOrphans(_ => { });

        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Add_StampsTheStoresOwner()
    {
        var child = Sleeper();
        var owner = Sleeper();

        new ChildProcessStore(_dir, IdentityOf(owner)).Add(ChildOf(child));

        var file = File.ReadAllText(Path.Combine(_dir, "plugin-children.json"));
        Assert.Contains($"\"OwnerPid\": {owner.Id}", file);
    }

    /// <summary>
    /// AT QUIT, THIS RUN'S OWN CHILDREN GO WITH IT. Whatever a plugin's Stop did not end in the window
    /// it was given is killed on the way out, rather than outliving the app until the next launch —
    /// and a neighbour's children are left exactly where they are.
    /// </summary>
    [Fact]
    public void ReapOwn_KillsThisRunsChildren_AndLeavesANeighboursAlone()
    {
        var me = Sleeper();
        var neighbour = Sleeper();
        var mine = Sleeper();
        var theirs = Sleeper();
        var store = new ChildProcessStore(_dir, IdentityOf(me));
        store.Add(ChildOf(mine, "lsp"));
        new ChildProcessStore(_dir, IdentityOf(neighbour)).Add(ChildOf(theirs, "lsp"));

        store.ReapOwn(_ => { });

        Assert.True(mine.WaitForExit(TimeSpan.FromSeconds(5)), "this run's child outlived the quit");
        Assert.False(theirs.WaitForExit(TimeSpan.FromMilliseconds(300)), "a neighbour's child was killed");
        var file = File.ReadAllText(Path.Combine(_dir, "plugin-children.json"));
        Assert.DoesNotContain(mine.Id.ToString(), file);
        Assert.Contains(theirs.Id.ToString(), file);
    }

    /// <summary>
    /// A RECORD WHOSE PROCESS HAS EXITED IS DROPPED WHEN THE NEXT ONE IS ADDED. A plugin that runs a
    /// short command per call registers each one, and without this the file grows by one record per
    /// call for the whole session, each a pid with nothing behind it left to reap.
    /// </summary>
    [Fact]
    public void Add_DropsThisRunsRecordsWhoseProcessHasExited()
    {
        var me = Sleeper();
        // A SHORT SLEEP, NOT `true`: the start time is read before it exits, and `true` can be gone first.
        var done = Process.Start(new ProcessStartInfo("sleep", "0.3") { UseShellExecute = false })!;
        _started.Add(done);
        var record = new ChildProcessRecord(done.Id, done.StartTime.ToUniversalTime(), "worktree");
        done.WaitForExit();
        var running = Sleeper();
        var store = new ChildProcessStore(_dir, IdentityOf(me));
        store.Add(record);

        store.Add(ChildOf(running, "worktree"));

        var file = File.ReadAllText(Path.Combine(_dir, "plugin-children.json"));
        Assert.DoesNotContain($"\"Pid\": {done.Id},", file);
        Assert.Contains($"\"Pid\": {running.Id},", file);
    }

    /// <summary>
    /// A NEIGHBOUR'S RECORDS ARE ITS OWN TO CLEAR, exited or not: an add here drops only what this
    /// run recorded, the same line <see cref="ChildProcessStore.ReapOwn"/> draws.
    /// </summary>
    [Fact]
    public void Add_LeavesANeighboursExitedRecordsAlone()
    {
        var me = Sleeper();
        var neighbour = Sleeper();
        // A SHORT SLEEP, NOT `true`: the start time is read before it exits, and `true` can be gone first.
        var done = Process.Start(new ProcessStartInfo("sleep", "0.3") { UseShellExecute = false })!;
        _started.Add(done);
        var record = new ChildProcessRecord(done.Id, done.StartTime.ToUniversalTime(), "worktree");
        done.WaitForExit();
        new ChildProcessStore(_dir, IdentityOf(neighbour)).Add(record);

        new ChildProcessStore(_dir, IdentityOf(me)).Add(ChildOf(Sleeper(), "worktree"));

        var file = File.ReadAllText(Path.Combine(_dir, "plugin-children.json"));
        Assert.Contains($"\"Pid\": {done.Id},", file);
    }
}
