using System.Diagnostics;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using Xunit;

namespace CxAgent.Tests;

public class ProcessResourceMonitorTests
{
    private static Process SpawnBusy(int ms) =>
        Process.Start(new ProcessStartInfo("/bin/sh", $"-c \"end=$(( $(date +%s%N) + {ms}000000 )); while [ $(date +%s%N) -lt $end ]; do :; done\"")
        { RedirectStandardOutput = true })!;

    [Fact]
    public async Task Polls_AProcess_AndAccumulatesHistory()
    {
        if (OperatingSystem.IsWindows()) return;   // /bin/sh
        using var proc = SpawnBusy(1500);
        using var mon = new ProcessResourceMonitor(proc, TimeSpan.FromMilliseconds(150));

        await Task.Delay(900);

        Assert.NotEmpty(mon.History);
        Assert.All(mon.History, s => Assert.True(s.MemoryBytes > 0, "working set should be positive"));
        proc.WaitForExit();
    }

    [Fact]
    public async Task History_IsCappedAtMaxHistory()
    {
        if (OperatingSystem.IsWindows()) return;
        using var proc = SpawnBusy(2000);
        using var mon = new ProcessResourceMonitor(proc, TimeSpan.FromMilliseconds(50), maxHistory: 5);

        await Task.Delay(900);

        Assert.True(mon.History.Count <= 5, $"expected <= 5, got {mon.History.Count}");
        proc.WaitForExit();
    }

    [Fact]
    public async Task ExitedProcess_StopsCleanly_WithoutThrowing()
    {
        if (OperatingSystem.IsWindows()) return;
        using var proc = SpawnBusy(50);
        using var mon = new ProcessResourceMonitor(proc, TimeSpan.FromMilliseconds(50));

        proc.WaitForExit();
        await Task.Delay(300);   // several polls AFTER exit — must not throw or spin

        Assert.True(true);       // reaching here without an unhandled exception is the assertion
    }
}
