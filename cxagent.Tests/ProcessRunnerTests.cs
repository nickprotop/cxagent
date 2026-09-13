using System.Collections.Concurrent;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

// A minimal IJobContext that just collects logged lines, for exec tests.
internal sealed class CollectingContext : IJobContext
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ConcurrentQueue<ResourceSnapshot> Resources { get; } = new();
    public void WorkStarting() { }

    /// <summary>Recorded rather than ignored, so a test can assert a job reported itself blocked on
    /// a prompt — the signal a parent's row turns into "waiting for permission".</summary>
    public List<bool> PermissionWaits { get; } = [];
    public void ReportPermissionWait(bool waiting) => PermissionWaits.Add(waiting);

    /// <summary>Recorded rather than ignored, mirroring PermissionWaits above — a test can assert
    /// whether "reviewing…" was raised for this call.</summary>
    public List<bool> ReviewingReports { get; } = [];
    public void ReportReviewing(bool reviewing) => ReviewingReports.Add(reviewing);

    public string? Requester => null;
    /// <summary>Settable so a test can exercise a path-less file call, which resolves against the
    /// agent's working directory. Null by default — most tests pass absolute paths and care about
    /// neither.</summary>
    public string? WorkingDirectory { get; init; }
    public string? DecidedBy { get; set; }

    public void ReportProgress(double percent, string? message = null) { }
    public void Log(string line) => Lines.Enqueue(line);
    public void Log(JobLogLevel level, string line) => Lines.Enqueue(line);
    public void ReportResources(ResourceSnapshot snapshot) => Resources.Enqueue(snapshot);
    public void ReportToolCall(string toolName, string summary) { }
    public void ReportTextDelta(string delta) { }
    public IReadOnlyDictionary<string, JobResult> CompletedJobOutputs { get; } = new Dictionary<string, JobResult>();
    public IReadOnlyDictionary<string, string> CompletedJobNames { get; } = new Dictionary<string, string>();
}

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CapturesStdout_AndZeroExit()
    {
        var ctx = new CollectingContext();
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec("/bin/sh", new[] { "-c", "echo hello-stdout" }), ctx, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(ctx.Lines, l => l.Contains("hello-stdout"));
    }

    [Fact]
    public async Task RunAsync_CapturesStderr()
    {
        var ctx = new CollectingContext();
        await ProcessRunner.RunAsync(
            new ProcessSpec("/bin/sh", new[] { "-c", "echo oops 1>&2" }), ctx, CancellationToken.None);
        Assert.Contains(ctx.Lines, l => l.Contains("oops"));
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZeroExitCode()
    {
        var ctx = new CollectingContext();
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec("/bin/sh", new[] { "-c", "exit 3" }), ctx, CancellationToken.None);
        Assert.Equal(3, result.ExitCode);
    }

    /// <summary>
    /// THE DEADLINE RETURNS PROMPTLY AND LEAVES THE COMMAND RUNNING.
    ///
    /// <para>Both halves, because either alone is satisfied by a bug. "Returns promptly" alone is
    /// what killing did; "still running" alone would pass for a call that never had a deadline at
    /// all. The pid check is against the OS, not against the result — the result says what the runner
    /// BELIEVES, and this test exists to find out whether the process is actually there.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_Timeout_HandsTheCommandOverStillRunning()
    {
        var registry = new DetachedProcessRegistry();
        try
        {
            var ctx = new CollectingContext();
            var start = DateTimeOffset.UtcNow;
            var result = await ProcessRunner.RunAsync(
                new ProcessSpec("/bin/sh", new[] { "-c", "sleep 30" }, new RunOptions(TimeoutSeconds: 1)),
                ctx, CancellationToken.None, registry);
            var elapsed = DateTimeOffset.UtcNow - start;

            Assert.True(elapsed < TimeSpan.FromSeconds(10),
                $"a deadline must answer the caller promptly, took {elapsed.TotalSeconds}s");

            // STILL REPORTED AS A TIMEOUT: the deadline was a real outcome and the caller has to be
            // able to say so. What changed is what happened to the process, not whether it is named.
            Assert.True(result.TimedOut);

            Assert.NotNull(result.Detached);
            Assert.True(ProcessExists(result.Detached!.Pid),
                $"the command must still be running; pid {result.Detached.Pid} is gone");

            // AND THE REGISTRY HOLDS IT, which is what makes it reapable at shutdown. A handover that
            // registered nothing would leave exactly the orphan this feature risks introducing.
            Assert.Contains(result.Detached, registry.Live);
        }
        finally { registry.ReapAll(); }
    }

    /// <summary>
    /// A COMMAND THAT FINISHES BEFORE ITS DEADLINE IS NOT DETACHED, however generous the deadline.
    ///
    /// <para>The complement of the test above, and it is the one that would catch a handover wired to
    /// fire unconditionally: every ordinary command has a deadline (the executor supplies 120s by
    /// default), so a runner that handed over on the way out of every call would register a
    /// DetachedProcess per shell command and hit the cap in sixteen calls.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_CommandThatBeatsItsDeadline_IsNotDetached()
    {
        var registry = new DetachedProcessRegistry();
        try
        {
            var result = await ProcessRunner.RunAsync(
                new ProcessSpec("/bin/sh", new[] { "-c", "echo quick" },
                    new RunOptions(TimeoutSeconds: 30)),
                new CollectingContext(), CancellationToken.None, registry);

            Assert.False(result.TimedOut);
            Assert.Null(result.Detached);
            Assert.Empty(registry.Live);
            Assert.Contains("quick", result.Stdout);
        }
        finally { registry.ReapAll(); }
    }

    /// <summary>
    /// DetachOnTimeout FALSE STILL KILLS AT THE DEADLINE — the behaviour a machine running unattended
    /// jobs may require, and the only way to get it once the default changed.
    /// </summary>
    [Fact]
    public async Task RunAsync_TimeoutWithoutDetach_KillsTheProcess()
    {
        var ctx = new CollectingContext();
        var registry = new DetachedProcessRegistry();
        try
        {
            var start = DateTimeOffset.UtcNow;
            var result = await ProcessRunner.RunAsync(
                new ProcessSpec("/bin/sh", new[] { "-c", "echo mypid $$; sleep 30" },
                    new RunOptions(TimeoutSeconds: 1, DetachOnTimeout: false)),
                ctx, CancellationToken.None, registry);
            var elapsed = DateTimeOffset.UtcNow - start;

            Assert.True(result.TimedOut);
            Assert.Null(result.Detached);
            Assert.Empty(registry.Live);
            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"a kill must be prompt, took {elapsed.TotalSeconds}s");

            // THE SHELL'S OWN PID, PRINTED BY THE COMMAND, so this asserts against the OS rather than
            // against a field the runner filled in. `entireProcessTree` is what the kill promises and
            // a result field cannot evidence.
            Assert.False(ProcessExists(PidFrom(result.Stdout)),
                "a deadline with DetachOnTimeout false must leave no process behind");
        }
        finally { registry.ReapAll(); }
    }

    /// <summary>
    /// AN EXTERNAL CANCEL KILLS, EVEN THOUGH THE DEADLINE NOW DOES NOT.
    ///
    /// <para>THE ASYMMETRY IS THE DECISION. Escape means the user wants the command stopped; a
    /// deadline only means the caller stopped waiting. A handover on cancellation would leave a
    /// process running that somebody explicitly asked to end — so this test asserts against the OS
    /// that nothing is left, not merely that TimedOut is false.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_CancellationKillsProcess_EvenThoughTheDeadlineWouldNot()
    {
        var ctx = new CollectingContext();
        var registry = new DetachedProcessRegistry();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));
            var start = DateTimeOffset.UtcNow;

            // A DEADLINE IS SET AND DETACHING IS ON — the defaults — so the only thing that can make
            // this a kill is the cancel being treated differently. Without that, this test passes
            // trivially for want of anything to detach on.
            var result = await ProcessRunner.RunAsync(
                new ProcessSpec("/bin/sh", new[] { "-c", "echo mypid $$; sleep 30" },
                    new RunOptions(TimeoutSeconds: 30)),
                ctx, cts.Token, registry);
            var elapsed = DateTimeOffset.UtcNow - start;

            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"cancel should kill promptly, took {elapsed.TotalSeconds}s");

            // THE OS CHECK FIRST, because it is the assertion that matters and a cheaper one above it
            // would shadow it: a runner that handed a cancelled command over reports TimedOut true,
            // fails on that line, and the question of whether a process is still running never gets
            // asked at all.
            Assert.False(ProcessExists(PidFrom(result.Stdout)),
                "Escape means stop: a cancelled command must leave no process behind");
            Assert.Null(result.Detached);
            Assert.Empty(registry.Live);
            Assert.False(result.TimedOut);
        }
        finally { registry.ReapAll(); }
    }

    /// <summary>The pid a command printed with <c>$$</c>, so a test asserts against the process the
    /// OS knows rather than against a number the runner reported.</summary>
    private static int PidFrom(string stdout)
    {
        var line = stdout.Split('\n').First(l => l.StartsWith("mypid "));
        return int.Parse(line["mypid ".Length..].Trim());
    }

    /// <summary>
    /// Whether a pid is a live process — <c>HasExited</c>, never a <c>pgrep</c> on a pattern.
    ///
    /// <para>A PATTERN FOR `sleep 30` MATCHES THE TEST'S OWN SHELL, the harness that launched it and
    /// any other test running the same command. And HasExited rather than bare presence matters on
    /// Linux: a killed child stays in the table as a zombie until its parent reaps it, so "is the pid
    /// there" would report a killed process as alive.</para>
    /// </summary>
    private static bool ProcessExists(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }          // not in the process table at all
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// LARGE OUTPUT KEEPS THE TAIL, NOT THE HEAD.
    ///
    /// <para>Truncation always bets on which end matters, and for a command the end is where the
    /// answer is: a failing test's assertion and summary are last, while a head shows the compiler
    /// banner. The file removes the bet for anything the tail did not carry.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_LargeOutput_KeepsTheTailAndWritesTheRest()
    {
        var dir = NewSpillDir();
        var ctx = new CollectingContext();

        // A marker at each end, so which end survived is unambiguous. 4,000 padded lines is ~76 KB
        // against an 8,192 cap — comfortably over, without the test's own runtime depending on how
        // close to the cap it lands.
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec("/bin/sh",
                new[] { "-c", "echo FIRSTLINE; for i in $(seq 1 4000); do echo padpadpadpadpadpad; done; echo LASTLINE" },
                new RunOptions(SpillDir: dir)),
            ctx, CancellationToken.None);

        Assert.Contains("LASTLINE", result.Stdout);
        Assert.DoesNotContain("FIRSTLINE", result.Stdout);
        Assert.True(result.Stdout.Length <= ProcessRunner.MaxCapturedChars + 200,
            $"the inline extract must stay near the cap, was {result.Stdout.Length}");

        // THE MARKER LEADS, because the elision happened at the START. Trailing, it would tell a
        // model the output ENDS mid-stream — the opposite of what a tail means.
        Assert.StartsWith("[...", result.Stdout);

        Assert.NotNull(result.Spill);
        Assert.True(File.Exists(result.Spill!.Path), $"no spill file at {result.Spill.Path}");

        // THE WHOLE OUTPUT, not just the part the tail dropped: the file is the thing the agent is
        // pointed at, so reading it must not require stitching it back onto the inline extract.
        var spilled = await File.ReadAllTextAsync(result.Spill.Path);
        Assert.Contains("FIRSTLINE", spilled);
        Assert.Contains("LASTLINE", spilled);
        Assert.True(result.Spill.TotalBytes > result.Stdout.Length,
            $"the spill's size must describe the whole output, got {result.Spill.TotalBytes}");
    }

    /// <summary>Small output is whole and needs no file — a spill for everything would litter for
    /// nothing.</summary>
    [Fact]
    public async Task RunAsync_SmallOutput_IsWholeAndUnspilled()
    {
        var dir = NewSpillDir();
        var ctx = new CollectingContext();
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec("/bin/sh", new[] { "-c", "echo hello" }, new RunOptions(SpillDir: dir)),
            ctx, CancellationToken.None);

        Assert.Contains("hello", result.Stdout);
        Assert.Null(result.Spill);

        // NOT MERELY "no Spill on the result": a file written and then not reported is still a file
        // left behind, and the whole point of the small case is that nothing is.
        Assert.Empty(Directory.Exists(dir) ? Directory.GetFiles(dir) : []);
    }

    /// <summary>A directory of its own per test, so one test's spill cannot satisfy another's
    /// "nothing was written" assertion.</summary>
    private static string NewSpillDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-spill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
