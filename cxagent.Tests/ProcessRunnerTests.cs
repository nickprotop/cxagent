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
}
