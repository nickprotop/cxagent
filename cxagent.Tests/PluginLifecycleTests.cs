using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The pid record and reaping — PLUGINS.md, "Lifecycle": "Whatever cannot be closed on the way
/// down must be collectable on the way up." A plugin that crashed cannot clean up after itself,
/// which is why Core records and reaps rather than trusting the plugin's own bookkeeping.
///
/// <para>A REAL PROCESS, DELIBERATELY. Reaping means killing a pid, and the only honest way to prove
/// that is to spawn something and check whether it is actually gone afterwards — a fake "process"
/// object would only prove the store's own arithmetic, not that <see cref="Process.Kill"/> was ever
/// reached. <c>sleep</c> is used because it is present on every Linux runner this suite runs on,
/// exits instantly if killed, and never needs cleanup beyond the kill itself.</para>
/// </summary>
public class PluginLifecycleTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-lifecycle-" + Guid.NewGuid().ToString("N"));

    public PluginLifecycleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static JsonElement EmptySchema() => JsonSerializer.SerializeToElement(new { type = "object" });

    private static PluginManifest Manifest(string name, params string[] toolNames) =>
        new(name, "1.0.0", Instructions: null, Spawns: true,
            toolNames.Select(n => new PluginToolManifest(n, "does something", EmptySchema())).ToList());

    /// <summary>Starts a real, short-lived child process this test controls end to end — never left
    /// running past the assertions that need it alive.</summary>
    private static System.Diagnostics.Process StartSleeper(double seconds = 30)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sleep",
            Arguments = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UseShellExecute = false,
        })!;
        // Give the OS a moment to hand back a StartTime that has actually settled — reading it
        // immediately after Start can occasionally race the process table on a loaded machine.
        _ = process.StartTime;
        return process;
    }

    // ---- Part 1 + 2: CORE RECORDS AND REAPS, not the plugin ---------------------------------------

    /// <summary>
    /// CORE RECORDS AND REAPS, not the plugin. A plugin that crashed cannot clean up after itself,
    /// which is the entire scenario — so the obligation cannot rest on the plugin's own bookkeeping.
    ///
    /// <para>Simulates a crash by writing a pid record directly (what a real
    /// <see cref="IPluginContext.RegisterChildProcess"/> implementation would have done before the
    /// process died) and never calling Stop or unwire — then constructs a fresh store, the way the
    /// NEXT run's <see cref="Sessions.SessionManager.Create"/> would, and reaps.</para>
    /// </summary>
    [Fact]
    public void AChildRecordedByAPluginIsReapedAtStartup()
    {
        using var sleeper = StartSleeper();

        var store = new ChildProcessStore(_dir);
        store.Add(new ChildProcessRecord(sleeper.Id, sleeper.StartTime.ToUniversalTime(), "lsp-rust"));

        // A SECOND STORE INSTANCE, reading the same file — this is the "next run" in the scenario:
        // nothing in memory survives a crash, only what was written to disk.
        var nextRun = new ChildProcessStore(_dir);
        var log = new List<string>();
        nextRun.ReapOrphans(log.Add);

        Assert.True(sleeper.WaitForExit(TimeSpan.FromSeconds(5)));
        Assert.Contains(log, line => line.Contains("lsp-rust") && line.Contains(sleeper.Id.ToString()));

        // THE RECORD IS CLEARED, so a THIRD run does not try to reap a pid the OS may have long
        // since reassigned to something else.
        Assert.DoesNotContain(sleeper.Id.ToString(), File.ReadAllText(store.FilePath));
    }

    /// <summary>A pid that is no longer running at all (the plugin's own Stop already reaped it, the
    /// ordinary case) is silently dropped — nothing to kill, nothing to log as an orphan.</summary>
    [Fact]
    public void APidThatAlreadyExitedIsDroppedSilently()
    {
        var exited = StartSleeper(seconds: 0.1);
        exited.WaitForExit();

        var store = new ChildProcessStore(_dir);
        store.Add(new ChildProcessRecord(exited.Id, exited.StartTime.ToUniversalTime(), "lsp-rust"));

        var log = new List<string>();
        store.ReapOrphans(log.Add);

        Assert.Empty(log);
    }

    /// <summary>
    /// A BARE PID IS REUSED BY THE OS — the reason <see cref="ChildProcessRecord.StartTimeUtc"/>
    /// exists at all. A record whose start time no longer matches the live process at that pid must
    /// be left alone rather than killed, or reaping becomes "kill a stranger's process".
    /// </summary>
    [Fact]
    public void APidWhoseStartTimeDoesNotMatchIsLeftAlone()
    {
        using var alive = StartSleeper();

        var store = new ChildProcessStore(_dir);
        // A start time that is NOT this process's real one — simulating the OS having reused this
        // pid for a different process since the record was written.
        store.Add(new ChildProcessRecord(alive.Id, alive.StartTime.ToUniversalTime().AddHours(-1), "lsp-rust"));

        var log = new List<string>();
        store.ReapOrphans(log.Add);

        Assert.False(alive.HasExited, "reaping killed a process whose start time did not match the record");
        Assert.Contains(log, line => line.Contains("reused by the OS"));
    }

    // ---- Part 3: Stop has a timeout, and the remedy differs by loader -----------------------------

    private sealed class HangingPlugin : IPlugin
    {
        private readonly TaskCompletionSource _stopCalled = new();
        public Task StopCalled => _stopCalled.Task;

        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("the registry is handed an already-loaded plugin in these tests");

        public Task Start(CancellationToken ct) => Task.CompletedTask;

        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
            CancellationToken ct) => Task.FromResult(new JobResult { Success = true });

        // NEVER RETURNS. A managed plugin's Stop that hangs — the case PLUGINS.md says can only be
        // abandoned, since there is no host process to kill for an in-process plugin.
        public async Task Stop(CancellationToken ct)
        {
            _stopCalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
        }
    }

    /// <summary>A hung Stop must not hang the application's exit. The remedy differs by loader: an
    /// ABI host is a process and is killed; a managed plugin can only be abandoned and the hang
    /// logged loudly enough to name it.</summary>
    [Fact]
    public async Task AHungStopTimesOutRatherThanBlockingExit()
    {
        // A SHORT TIMEOUT, INJECTED — proving the timeout actually fires must not cost this suite
        // DefaultStopTimeout's real ten seconds; see PluginRegistry's own constructor doc.
        var stopTimeout = TimeSpan.FromMilliseconds(200);
        var registry = new PluginRegistry(stopTimeout);
        var messages = new List<string>();
        registry.AttachChildProcessStore(new ChildProcessStore(_dir), messages.Add);

        var plugin = new HangingPlugin();
        registry.Load(plugin, Manifest("lsp-hangs", "lsp_rename"), isNameTaken: _ => false);

        // WaitAsync THROWS TimeoutException if unwire itself does not finish promptly — the outer
        // bound exists only to fail this test fast if the abandon logic regresses into a real wait.
        var unwired = await registry.UnwireAsync("lsp-hangs", CancellationToken.None)
            .WaitAsync(stopTimeout + TimeSpan.FromSeconds(5));

        // UNWIRE STILL COMPLETES — the whole point: a hung Stop is abandoned, not waited out.
        Assert.True(unwired);
        await plugin.StopCalled;
        Assert.Contains(messages, m => m.Contains("lsp-hangs") && m.Contains("did not return"));

        // DEREGISTERED REGARDLESS OF THE HANG: the tool must not still be offered.
        Assert.Empty(registry.CurrentTools());
    }

    /// <summary>An ordinary Stop that returns well inside the timeout is not treated as a hang, and
    /// nothing is logged about it.</summary>
    [Fact]
    public async Task AStopThatReturnsPromptlyIsNotReportedAsHung()
    {
        var registry = new PluginRegistry();
        var messages = new List<string>();
        registry.AttachChildProcessStore(new ChildProcessStore(_dir), messages.Add);

        var plugin = new PluginRegistryTestsFriend.PromptPlugin();
        registry.Load(plugin, Manifest("lsp-fast", "lsp_rename"), isNameTaken: _ => false);

        Assert.True(await registry.UnwireAsync("lsp-fast", CancellationToken.None));
        Assert.True(plugin.Stopped);
        Assert.DoesNotContain(messages, m => m.Contains("did not return"));
    }
}

/// <summary>A minimal well-behaved <see cref="IPlugin"/>, kept out of the test class body only so
/// <see cref="PluginLifecycleTests"/>'s own fixtures above stay focused on the hanging case.</summary>
internal static class PluginRegistryTestsFriend
{
    public sealed class PromptPlugin : IPlugin
    {
        public bool Stopped { get; private set; }

        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("the registry is handed an already-loaded plugin in these tests");

        public Task Start(CancellationToken ct) => Task.CompletedTask;

        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
            CancellationToken ct) => Task.FromResult(new JobResult { Success = true });

        public Task Stop(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }
}
