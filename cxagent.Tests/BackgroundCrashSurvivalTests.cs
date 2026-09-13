using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A BACKGROUNDED COMMAND SURVIVES A CRASH, AND THE NEXT LAUNCH KILLS IT.
///
/// <para>THIS IS THE FAILURE BACKGROUNDING INTRODUCES. <c>DetachedProcessRegistry</c> is reaped from
/// <c>SessionManager.Dispose</c>, which a SIGKILL or a crash never reaches — and unlike a plugin's
/// child there was nothing on disk naming a detached command, because <c>RegisterChildProcess</c>
/// needs a plugin and a shell command has none. It got worse when a deadline stopped killing: the
/// timeout used to guarantee a dead process.</para>
///
/// <para>THE ASSERTIONS ARE ABOUT WHAT A REAL COMMAND PRODUCES, never about a record this test wrote.
/// PluginLifecycleTests already covers a store handed a record it constructed itself, and that proves
/// the store works while saying nothing about whether anything in the app puts a record in it. The
/// chain here is the executor detaching, the pid being written, and a SECOND store instance — the next
/// launch, since nothing in memory survives a crash — finding and killing it.</para>
/// </summary>
public class BackgroundCrashSurvivalTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("cxagent-bg-crash").FullName;
    private readonly DetachedProcessRegistry _registry = new();

    public void Dispose()
    {
        _registry.ReapAll();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static JobParameters P(params (string k, object? v)[] kv)
        => new(kv.ToDictionary(x => x.k, x => x.v));

    /// <summary>The executor wired the way a real session wires it, plus its own store and registry so
    /// one test cannot reap another's command.</summary>
    private (ShellJobExecutor Executor, ChildProcessStore Children, JobContext Context) Wire()
    {
        var paths = new AppPaths(_dir);
        paths.EnsureCreated();
        var children = new ChildProcessStore(_dir);

        var executor = new ShellJobExecutor(new ShellBackgrounding(_registry, Children: children));

        var context = new JobContext("agent-crash", "job-crash",
            new Dictionary<string, JobResult>(), new LogFileManager(paths))
        {
            WorkingDirectory = _dir,
        };

        return (executor, children, context);
    }

    /// <summary>
    /// A `background: true` COMMAND IS ON DISK WHILE IT RUNS, AND THE NEXT LAUNCH REAPS IT.
    ///
    /// <para>The kill is asserted through <c>WaitForExit</c> on the real process, not through the log
    /// line: a reap that logged its intention and killed nothing would satisfy the log.</para>
    /// </summary>
    [Fact]
    public async Task ABackgroundedCommandIsRecordedAndTheNextLaunchKillsIt()
    {
        var (executor, children, context) = Wire();

        var result = await executor.ExecuteAsync(
            P(("command", "sleep 60"), ("background", true)), context, CancellationToken.None);

        Assert.True(result.Success);
        var pid = Convert.ToInt32(result.Output!["pid"]);

        // THE RECORD EXISTS AND NAMES THIS PID. Read off the file rather than an in-memory list,
        // because the file is the only thing a crash leaves behind.
        Assert.Contains(pid.ToString(), Recorded(children));

        using var running = System.Diagnostics.Process.GetProcessById(pid);
        Assert.False(running.HasExited);

        // A SECOND STORE OVER THE SAME FILE — the next launch. Nothing of the crashed run's memory is
        // available to it, which is the whole point.
        var log = new List<string>();
        new ChildProcessStore(_dir).ReapOrphans(log.Add);

        Assert.True(running.WaitForExit(TimeSpan.FromSeconds(5)),
            "the next launch must kill a background command the crashed one left running");
        Assert.Contains(log, line => line.Contains(pid.ToString()));
    }

    /// <summary>
    /// A COMMAND HANDED OVER AT ITS DEADLINE IS RECORDED TOO, which is the path this whole change
    /// created — a model that never asked to background anything can now leave a process running.
    /// </summary>
    [Fact]
    public async Task ACommandHandedOverAtItsDeadlineIsRecorded()
    {
        var (executor, children, context) = Wire();

        var result = await executor.ExecuteAsync(
            P(("command", "sleep 60"), ("timeout_seconds", 1)), context, CancellationToken.None);

        Assert.False(result.Success);
        var pid = Convert.ToInt32(result.Output!["pid"]);
        Assert.Contains(pid.ToString(), Recorded(children));

        using var running = System.Diagnostics.Process.GetProcessById(pid);
        var log = new List<string>();
        new ChildProcessStore(_dir).ReapOrphans(log.Add);

        Assert.True(running.WaitForExit(TimeSpan.FromSeconds(5)),
            "a command left running by a deadline must be reapable by the next launch");
    }

    /// <summary>
    /// THE RECORD IS CLEARED WHEN THE COMMAND EXITS ON ITS OWN, so the file holds what is RUNNING
    /// rather than a log of everything ever backgrounded.
    ///
    /// <para>A STALE RECORD IS NOT MERELY UNTIDY. The next launch looks up its pid, and every stale
    /// entry is another chance for the OS to have reused that number for something the start-time
    /// match then has to rule out — one more process this app asks about for no reason.</para>
    ///
    /// <para>THE COMMAND FAILS, AND FAST. `exit 7` finishes before the executor has returned, so the
    /// clearing cannot rely on the <c>Exited</c> subscription — the event is raised once and never
    /// replayed. That is the exact shape that shipped a broken exit report on this feature's
    /// background path, caught then by a failing command rather than a clean one.</para>
    /// </summary>
    [Fact]
    public async Task AFastFailingCommandLeavesNoRecordBehind()
    {
        var (executor, children, context) = Wire();

        var result = await executor.ExecuteAsync(
            P(("command", "exit 7"), ("background", true)), context, CancellationToken.None);

        var pid = Convert.ToInt32(result.Output!["pid"]);

        // A brief window: the clearing happens on whichever thread notices the exit, and this asserts
        // the record is GONE rather than that it was never written.
        var cleared = await Eventually(() => !Recorded(children).Contains(pid.ToString()));
        Assert.True(cleared,
            $"pid {pid} is still recorded after the command exited: {Recorded(children)}");
    }

    /// <summary>
    /// AN ORDERLY SHUTDOWN CLEARS THE DISK RECORDS TOO, so the next launch does not sweep pids that
    /// were already killed on the way down.
    ///
    /// <para>THIS IS THE LINK BETWEEN THE TWO REAPERS, and it is not obvious that it exists: nothing
    /// in <c>ReapAll</c> touches a file. It works because <c>DetachedProcess.Kill</c> reaches the same
    /// completion path a natural exit does and raises <c>Exited</c> — which is what the record's
    /// removal is subscribed to. A shutdown that killed the processes and left the file full would
    /// leave the next launch asking the OS about sixteen dead pids, and every one of those is a pid the
    /// OS may have handed to something else.</para>
    /// </summary>
    [Fact]
    public async Task AnOrderlyShutdownClearsTheRecordsItKilled()
    {
        var (executor, children, context) = Wire();

        var result = await executor.ExecuteAsync(
            P(("command", "sleep 60"), ("background", true)), context, CancellationToken.None);
        var pid = Convert.ToInt32(result.Output!["pid"]);
        Assert.Contains(pid.ToString(), Recorded(children));

        // What SessionManager.Dispose does.
        _registry.ReapAll();

        var cleared = await Eventually(() => !Recorded(children).Contains(pid.ToString()));
        Assert.True(cleared,
            $"shutdown killed pid {pid} but left its record: {Recorded(children)}");
    }

    /// <summary>
    /// What the store has on disk, or an explicit marker when the file does not exist.
    ///
    /// <para>NOT File.ReadAllText DIRECTLY. Nothing recorded means no file at all, and a bare read
    /// then fails with a FileNotFoundException and a stack trace through the BCL — which tells a
    /// reader that a path was missing rather than that a backgrounded command went unrecorded. The
    /// marker turns the same failure into the assertion message it should have been.</para>
    /// </summary>
    private static string Recorded(ChildProcessStore store) =>
        File.Exists(store.FilePath) ? File.ReadAllText(store.FilePath) : "(nothing was recorded)";

    /// <summary>Polls a condition rather than sleeping a fixed time, so a fast machine does not wait
    /// and a slow one does not fail.</summary>
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }
}
