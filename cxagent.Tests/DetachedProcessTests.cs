using System.Diagnostics;
using CxAgent.Core.Execution;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A command that outlives the call that started it.
///
/// <para>EVERY TEST HERE USES ITS OWN REGISTRY. The production one is process-wide, and a test that
/// reaped it would kill whatever another test in the same parallel run had just backgrounded — see
/// <see cref="DetachedProcessRegistry.Default"/>.</para>
/// </summary>
public class DetachedProcessTests
{
    /// <summary>
    /// A DETACHED PROCESS OUTLIVES THE CALL AND STILL REPORTS.
    ///
    /// <para>The point of detaching is that the caller stops waiting; the point of this test is that
    /// nothing else stops. Output keeps landing in the file after the call returned, and the exit is
    /// still announced — which is what the agent is told from.</para>
    /// </summary>
    [Fact]
    public async Task ADetachedProcess_KeepsRunningAndAnnouncesItsExit()
    {
        using var registry = NewRegistry();
        var exited = new TaskCompletionSource<int>();
        var detached = registry.Detach("echo started; sleep 2; echo finished; exit 3");
        detached.Exited += code => exited.TrySetResult(code);

        Assert.True(ProcessExists(detached.Pid), "the process must survive the call");

        // READ BEFORE THE EXIT, so "kept running" is observed rather than inferred from the file's
        // final contents — which a process that had already finished would also produce.
        Assert.False(exited.Task.IsCompleted, "the call must not have waited for the command");

        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(3, code);
        Assert.NotNull(detached.OutputPath);
        var text = await File.ReadAllTextAsync(detached.OutputPath!);
        Assert.Contains("started", text);
        Assert.Contains("finished", text);   // written AFTER the call returned
    }

    /// <summary>
    /// A COMMAND THAT FAILED BEFORE ANYONE SUBSCRIBED IS STILL REPORTED.
    ///
    /// <para>THE DEFECT THIS PINS, WHICH A LIVE DRIVE FOUND AND THE SUITE FLAKED ON: an agent that
    /// backgrounded `exit 9` was never told it finished. <c>Process.Exited</c> is raised from a
    /// thread-pool work item, so with the pool saturated it is not a notification but a request to be
    /// notified eventually — measured at over four seconds for a child that had already died, against
    /// a twenty-second delivery deadline the suite hit in full.</para>
    ///
    /// <para>SATURATION IS THE NORMAL CASE, NOT A CONTRIVED ONE. Backgrounding is what a model reaches
    /// for when the machine is busy, and a parallel test suite is busy in the same way — which is why
    /// this failed one run in two and never in isolation. And the reports lost are the ones carrying
    /// BAD NEWS: a rejected argument or a failed authentication returns in microseconds, so it depends
    /// entirely on the notification rather than on anyone still watching.</para>
    ///
    /// <para>THE STARVATION ITSELF IS NOT REPRODUCED HERE, DELIBERATELY. Doing so needs
    /// <c>ThreadPool.SetMaxThreads</c> — queueing blocking work does not suffice, the pool grows past
    /// it — and that setting is PROCESS-WIDE: applied in the test host it starved tests running in
    /// parallel, failing <c>RunAsync_Timeout_HandsTheCommandOverStillRunning</c> and
    /// <c>ProcessResourceMonitorTests</c> in most runs. Trading one flake for three is not a bargain,
    /// so what is left here is the end-to-end behaviour, and the starvation stays measured rather than
    /// asserted: with the pool capped at one busy thread, <c>Process.Exited</c> for an already-dead
    /// child had not arrived after four seconds, and the parameterless <c>WaitForExit()</c> — which
    /// also waits on the pool-driven readers — had not returned after six.</para>
    ///
    /// <para>WHAT IS ASSERTED IS THE EXIT CODE REACHING A LATE SUBSCRIBER, through the same
    /// subscribe-then-re-check pair <c>ArrangeTheReport</c> uses, because that is the sequence the
    /// agent's report actually depends on.</para>
    /// </summary>
    [Fact]
    public void AFastExit_ReachesASubscriberThatArrivedAfterTheCommandHadFinished()
    {
        using var registry = NewRegistry();
        var exited = new TaskCompletionSource<int>();

        var detached = registry.Detach("exit 9");
        detached.Exited += code => exited.TrySetResult(code);
        if (detached.Finished) exited.TrySetResult(detached.ExitCode);

        Assert.True(exited.Task.Wait(TimeSpan.FromSeconds(20)),
            "the agent is never told a fast-failing background command finished");
        Assert.Equal(9, exited.Task.Result);
    }

    /// <summary>Killing a detached process stops it and is safe to call twice.</summary>
    [Fact]
    public void ADetachedProcess_CanBeKilled_Idempotently()
    {
        using var registry = NewRegistry();
        var detached = registry.Detach("sleep 60");
        detached.Kill();
        detached.Kill();                      // must not throw
        Assert.False(ProcessExists(detached.Pid));
    }

    /// <summary>
    /// SHUTDOWN REAPS WHAT IS STILL RUNNING. Nothing else will: a detached child has no plugin, so
    /// ChildProcessRecord cannot see it, and a process outliving the app is the failure this feature
    /// would otherwise introduce.
    /// </summary>
    [Fact]
    public void Shutdown_KillsEveryDetachedProcess()
    {
        using var registry = NewRegistry();
        var a = registry.Detach("sleep 60");
        var b = registry.Detach("sleep 60");

        registry.Registry.ReapAll();

        Assert.False(ProcessExists(a.Pid));
        Assert.False(ProcessExists(b.Pid));
    }

    /// <summary>
    /// WHETHER A PID IS RUNNING, ASKED OF THE PID.
    ///
    /// <para>NEVER A <c>pgrep</c> ON A COMMAND PATTERN. A pattern for `sleep 60` matches the test's
    /// own shell, the harness that launched it, and any other test running the same command — so a
    /// watcher ends up waiting on its own existence while the process it cares about has long
    /// exited.</para>
    ///
    /// <para>A zombie counts as gone: a killed child stays in the process table until its parent
    /// reaps it, and <c>HasExited</c> is what distinguishes "exited, not yet reaped" from
    /// "running".</para>
    /// </summary>
    private static bool ProcessExists(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }    // not in the process table at all
        catch (InvalidOperationException) { return false; }
    }

    private static Fixture NewRegistry() => new();

    /// <summary>
    /// One test's registry plus its own output directory, killed and deleted on the way out.
    ///
    /// <para>DISPOSABLE SO A FAILING ASSERTION STILL REAPS. A test that asserts before the kill and
    /// fails would otherwise leave a `sleep 60` running for a minute per failure, and a red suite
    /// re-run is exactly when that accumulates.</para>
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public DetachedProcessRegistry Registry { get; } = new();
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "cxagent-detached-tests", Guid.NewGuid().ToString("N"));

        public DetachedProcess Detach(string command) =>
            ProcessRunner.DetachAsync(
                new ProcessSpec("/bin/sh", ["-c", command], new RunOptions(SpillDir: _dir)),
                new CollectingContext(), Registry).GetAwaiter().GetResult();

        public void Dispose()
        {
            Registry.ReapAll();
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
        }
    }
    /// <summary>
    /// THE DEFECT THIS CATCHES WAS SHIPPED AND FOUND BY HAND. ReapAll is called by every test's own
    /// cleanup, so the suite exercised it constantly while nothing asserted the APP calls it — and it
    /// did not. A detached process survived `/exit`, reparented to init, running unowned.
    ///
    /// ANCHORED ON system.Run() BECAUSE THAT IS THE APP'S ONLY EXIT. Whatever follows that call is
    /// the shutdown path by definition, so a future teardown added there cannot quietly omit the
    /// reap: this fails naming the file, the way the policy/LogDir walk does.
    /// </summary>
    [Fact]
    public void TheAppReapsDetachedProcessesOnTheWayOut()
    {
        var bootstrap = Path.Combine(RepoRoot(), "cxagent", "UI", "AppBootstrap.cs");
        var text = File.ReadAllText(bootstrap);

        var run = text.IndexOf("system.Run()", StringComparison.Ordinal);
        Assert.True(run >= 0, $"{Path.GetFileName(bootstrap)}: no system.Run() — the anchor moved, so "
            + "this test no longer proves anything and needs repointing at the new exit path.");

        var shutdown = text[run..];
        Assert.True(shutdown.Contains("DetachedProcessRegistry.Default.ReapAll()", StringComparison.Ordinal),
            $"{Path.GetFileName(bootstrap)}: nothing after system.Run() reaps "
            + "DetachedProcessRegistry.Default, so a backgrounded command outlives the app.");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "cxagent.Core")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new DirectoryNotFoundException(
            "repository root not found from " + AppContext.BaseDirectory);
    }

}
