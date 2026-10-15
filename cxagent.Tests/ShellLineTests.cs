using CxAgent.Core.Skills;
using Xunit;

namespace CxAgent.Tests;

public class ShellLineTests
{
    private static readonly string Here = Path.GetTempPath();

    [Fact]
    public async Task Output_AndExitCode_AreCaptured()
    {
        if (OperatingSystem.IsWindows()) return;   // /bin/sh — the shell run_shell uses

        var r = await ShellLine.RunAsync("echo out; echo err 1>&2; exit 3", Here, TimeSpan.FromSeconds(10), default);

        Assert.Equal(3, r.ExitCode);
        Assert.Contains("out", r.Output);
        Assert.Contains("err", r.Output);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task AHangingLine_IsKilled_AtTheTimeout()
    {
        if (OperatingSystem.IsWindows()) return;

        var r = await ShellLine.RunAsync("sleep 30", Here, TimeSpan.FromMilliseconds(300), default);

        Assert.True(r.TimedOut);
    }

    [Fact]
    public async Task AShellLineReadingStdin_EndsAtOnce()
    {
        if (OperatingSystem.IsWindows()) return;

        var r = await ShellLine.RunAsync("read x; echo got:$x", Here, TimeSpan.FromSeconds(10), default);

        Assert.False(r.TimedOut);
        Assert.Contains("got:", r.Output);
    }

    [Fact]
    public async Task Output_IsCapped()
    {
        if (OperatingSystem.IsWindows()) return;

        var r = await ShellLine.RunAsync("head -c 100000 /dev/zero | tr '\\0' a", Here, TimeSpan.FromSeconds(10), default);

        Assert.True(r.Output.Length <= ShellLine.MaxOutputChars + 200);
        Assert.Contains("truncated", r.Output);
    }

    [Fact]
    public async Task Cancelling_KillsTheLine()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellLine.RunAsync("sleep 30", Here, TimeSpan.FromSeconds(60), cts.Token));
    }

    /// <summary>A child left running in the background holds the output pipe; the line still ends.</summary>
    [Fact]
    public async Task ABackgroundChild_HoldingThePipe_DoesNotHoldTheLine()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var r = await ShellLine.RunAsync("sleep 8 & echo started", Here, TimeSpan.FromSeconds(30), default);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
        Assert.Contains("started", r.Output);
    }

    /// <summary>Esc during such a line ends it too, rather than waiting on the pipe.</summary>
    [Fact]
    public async Task Cancelling_ALineWhosePipeIsHeld_EndsPromptly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(300);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellLine.RunAsync("sleep 8 & sleep 8", Here, TimeSpan.FromSeconds(30), cts.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
    }

    /// <summary>Endless output is cut at the cap and the line stopped, not collected until memory runs out.</summary>
    [Fact]
    public async Task EndlessOutput_IsCut_AndTheLineStopped()
    {
        if (OperatingSystem.IsWindows()) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var r = await ShellLine.RunAsync("yes", Here, TimeSpan.FromSeconds(30), default);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.True(r.Output.Length <= ShellLine.MaxOutputChars + 200);
        Assert.Contains("truncated", r.Output);
    }
}
