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

    [Fact]
    public async Task RunAsync_TimeoutKillsProcess_AndReturnsPromptly()
    {
        var ctx = new CollectingContext();
        var start = DateTimeOffset.UtcNow;
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec("/bin/sh", new[] { "-c", "sleep 30" }, TimeoutSeconds: 1), ctx, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(result.TimedOut);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"timeout should kill promptly, took {elapsed.TotalSeconds}s");
    }

    [Fact]
    public async Task RunAsync_CancellationKillsProcess()
    {
        var ctx = new CollectingContext();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var start = DateTimeOffset.UtcNow;
        // Cancellation kills the process; RunAsync returns (TimedOut false — it was cancelled, not timed out).
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec("/bin/sh", new[] { "-c", "sleep 30" }), ctx, cts.Token);
        var elapsed = DateTimeOffset.UtcNow - start;
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"cancel should kill promptly, took {elapsed.TotalSeconds}s");
        Assert.False(result.TimedOut);
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
                SpillDir: dir),
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
            new ProcessSpec("/bin/sh", new[] { "-c", "echo hello" }, SpillDir: dir),
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
