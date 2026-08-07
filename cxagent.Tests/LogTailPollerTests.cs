using System.Collections.Concurrent;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class LogTailPollerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-tail-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly LogFileManager _logs;
    public LogTailPollerTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
        _logs = new LogFileManager(_paths);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public async Task EmitsOnlyNewLines_AcrossPolls()
    {
        var emitted = new ConcurrentQueue<string>();
        await _logs.AppendAsync("g", "j", "log", "line1\nline2\nline3\n");

        using var cts = new CancellationTokenSource();
        var poller = new LogTailPoller(_logs, "g", "j",
            lines => { foreach (var l in lines) emitted.Enqueue(l); },
            tailLines: 20, pollIntervalMs: 20);
        var task = poller.RunAsync(cts.Token);

        // Let it emit the first 3 lines.
        await WaitUntil(() => emitted.Count >= 3, 2000);
        await _logs.AppendAsync("g", "j", "log", "line4\nline5\n");
        await WaitUntil(() => emitted.Count >= 5, 2000);

        cts.Cancel();
        await task;   // completes promptly

        Assert.Equal(new[] { "line1", "line2", "line3", "line4", "line5" }, emitted.ToArray());
    }

    [Fact]
    public async Task Cancellation_StopsLoop_Promptly()
    {
        using var cts = new CancellationTokenSource();
        var poller = new LogTailPoller(_logs, "g", "none", _ => { }, pollIntervalMs: 20);
        var task = poller.RunAsync(cts.Token);
        cts.Cancel();
        var completed = await Task.WhenAny(task, Task.Delay(2000)) == task;
        Assert.True(completed, "poller did not stop promptly on cancellation");
    }

    [Fact]
    public async Task MissingFile_IsSwallowed_LoopSurvives()
    {
        var emitted = new List<string>();
        using var cts = new CancellationTokenSource();
        // "ghost" job has no log file — ReadAsync will throw/return empty; the loop must not die.
        var poller = new LogTailPoller(_logs, "g", "ghost",
            lines => { lock (emitted) emitted.AddRange(lines); }, pollIntervalMs: 20);
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(200);   // several poll cycles with no file
        // Now the file appears:
        await _logs.AppendAsync("g", "ghost", "log", "appeared\n");
        await WaitUntil(() => { lock (emitted) return emitted.Contains("appeared"); }, 2000);
        cts.Cancel(); await task;
        Assert.Contains("appeared", emitted);
    }

    [Fact]
    public async Task TailLines_Window_IsRespected()
    {
        var emitted = new List<string>();
        // 5 lines already present, tailLines: 2 → only the last 2 emitted on first read.
        await _logs.AppendAsync("g", "j", "log", "a\nb\nc\nd\ne\n");
        using var cts = new CancellationTokenSource();
        var poller = new LogTailPoller(_logs, "g", "j",
            lines => { lock (emitted) emitted.AddRange(lines); }, tailLines: 2, pollIntervalMs: 20);
        var task = poller.RunAsync(cts.Token);
        await WaitUntil(() => { lock (emitted) return emitted.Count >= 2; }, 2000);
        cts.Cancel(); await task;
        Assert.Equal(new[] { "d", "e" }, emitted.ToArray());
    }

    /// <summary>
    /// Regression: once the log grows PAST the tail window the poller must keep emitting.
    /// ReadTailSafe returns a sliding window capped at tailLines, but _emittedCount was compared
    /// against that window's length — so after the log exceeded tailLines, tail.Count stayed pinned
    /// at the cap while the window slid forward, `tail.Count > _emittedCount` never held again, and
    /// the live tail silently froze. The existing TailLines_Window_IsRespected test missed this
    /// because it never appends after the poller starts.
    /// </summary>
    [Fact]
    public async Task KeepsEmitting_AfterLogExceedsTailWindow()
    {
        var emitted = new List<string>();
        using var cts = new CancellationTokenSource();
        var poller = new LogTailPoller(_logs, "g", "grow",
            lines => { lock (emitted) emitted.AddRange(lines); }, tailLines: 3, pollIntervalMs: 20);
        var task = poller.RunAsync(cts.Token);

        // Fill exactly to the window, then keep appending past it.
        await _logs.AppendAsync("g", "grow", "log", "l1\nl2\nl3\n");
        await WaitUntil(() => { lock (emitted) return emitted.Count >= 3; }, 2000);

        await _logs.AppendAsync("g", "grow", "log", "l4\n");
        await WaitUntil(() => { lock (emitted) return emitted.Contains("l4"); }, 2000);

        await _logs.AppendAsync("g", "grow", "log", "l5\n");
        await WaitUntil(() => { lock (emitted) return emitted.Contains("l5"); }, 2000);

        cts.Cancel(); await task;
        lock (emitted)
        {
            Assert.Contains("l4", emitted);
            Assert.Contains("l5", emitted);
            Assert.Equal(emitted.Distinct().Count(), emitted.Count);   // no duplicate re-emission
        }
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!cond() && Environment.TickCount64 - start < timeoutMs) await Task.Delay(10);
        Assert.True(cond(), "condition not met within timeout");
    }
}
