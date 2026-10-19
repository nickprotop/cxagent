using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Plugins.Abi;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A native plugin's progress, log lines and streamed text reach the call they belong to — through
/// the real plugin host, from a real library built with the reporting fixture.
/// </summary>
public class AbiReportingTests
{
    /// <summary>What a call was told, in order.</summary>
    private sealed class Recording : IJobContext
    {
        public List<string> Seen { get; } = [];
        public void ReportProgress(double percent, string? message = null) { lock (Seen) Seen.Add($"progress {percent} {message}"); }
        public string? Requester => null;
        public string? WorkingDirectory => null;
        public void Log(string line) { lock (Seen) Seen.Add($"log {line}"); }
        public void Log(JobLogLevel level, string line) { lock (Seen) Seen.Add($"log {level} {line}"); }
        public void ReportTextDelta(string delta) { lock (Seen) Seen.Add($"stream {delta}"); }
    }

    private sealed class Throwing : IJobContext
    {
        public void ReportProgress(double percent, string? message = null) => throw new InvalidOperationException();
        public string? Requester => null;
        public string? WorkingDirectory => null;
        public void Log(string line) => throw new InvalidOperationException();
        public void Log(JobLogLevel level, string line) => throw new InvalidOperationException();
        public void ReportTextDelta(string delta) => throw new InvalidOperationException();
    }

    private sealed class Logger(List<string> lines) : IPluginLogger
    {
        public void Log(string message) { lock (lines) lines.Add(message); }
    }

    private sealed class Context(IPluginLogger logger) : IPluginContext
    {
        public string WorkingDirectory { get; } = AppContext.BaseDirectory;
        public JsonElement Settings { get; } = JsonSerializer.SerializeToElement(new { });
        public int HostContract => PluginContract.Version;
        public string HostVersion => PluginContract.HostVersionOf(GetType().Assembly);
        public IPluginLogger Logger { get; } = logger;
        public CancellationToken Lifetime { get; } = CancellationToken.None;
        public void RegisterChildProcess(int processId) { }
    }

    private static string HostDll()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "cxagent.PluginHost", "bin", configuration, "net10.0", "cxagent-plugin-host.dll");
    }

    /// <summary>Loads and starts the library the way a session does, handing back the plugin.</summary>
    private static async Task<IPlugin> StartAsync(string library, List<string>? pluginLog = null)
    {
        var loaded = Assert.IsType<AbiPluginLoadResult.Loaded>(await AbiPluginLoader.Load(
            HostDll(), library, new Context(new Logger(pluginLog ?? [])), CancellationToken.None));
        await loaded.Instance.Start(CancellationToken.None);
        return loaded.Instance;
    }

    private static readonly JobParameters NoArguments = new(new Dictionary<string, object?>());

    [Fact]
    public async Task ReportsFromInsideACall_ReachThatCall_InOrder()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var plugin = await StartAsync(lib);
        try
        {
            var call = new Recording();

            var result = await plugin.Invoke("echo", NoArguments, call, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(["progress 50 half", "log Info working", "stream one ", "stream two"], call.Seen);
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>Review focus 3: two calls in flight each get only their own reports.</summary>
    [Fact]
    public async Task TwoCallsInFlight_EachGetsItsOwnReports()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var plugin = await StartAsync(lib);
        try
        {
            var a = new Recording();
            var b = new Recording();

            await Task.WhenAll(
                plugin.Invoke("echo", NoArguments, a, CancellationToken.None),
                plugin.Invoke("echo", NoArguments, b, CancellationToken.None));

            Assert.Equal(["progress 50 half", "log Info working", "stream one ", "stream two"], a.Seen);
            Assert.Equal(["progress 50 half", "log Info working", "stream one ", "stream two"], b.Seen);
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>
    /// The ABI lets invokes run concurrently, and the plugin host must not serialize them: one slow
    /// call would hold up every other call to the same plugin. "pair" succeeds only when a second
    /// call is inside it at the same time, so serialized invokes fail it.
    /// </summary>
    [Fact]
    public async Task TwoInvokes_RunAtTheSameTime()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var plugin = await StartAsync(lib);
        try
        {
            var results = await Task.WhenAll(
                plugin.Invoke("pair", NoArguments, new Recording(), CancellationToken.None),
                plugin.Invoke("pair", NoArguments, new Recording(), CancellationToken.None));

            Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>Review focus 2: a report naming a call that already returned goes nowhere, and breaks nothing.</summary>
    [Fact]
    public async Task AReportAfterItsCallEnded_IsDropped()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var plugin = await StartAsync(lib);
        try
        {
            var late = new Recording();
            var next = new Recording();

            await plugin.Invoke("echo_dynamic", NoArguments, late, CancellationToken.None);
            await Task.Delay(600);
            await plugin.Invoke("echo", NoArguments, next, CancellationToken.None);

            Assert.Empty(late.Seen);
            Assert.Equal(4, next.Seen.Count);
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>Review focus 4: a log outside any call reaches the plugin's own logger.</summary>
    [Fact]
    public async Task ALogWithNoCall_GoesToThePluginsLogger()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var pluginLog = new List<string>();
        var plugin = await StartAsync(lib, pluginLog);
        try
        {
            for (var i = 0; i < 50 && !pluginLog.Contains("started"); i++) await Task.Delay(20);

            lock (pluginLog) Assert.Contains("started", pluginLog);
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>
    /// The plugin's own log keeps a level worth noticing: a warning from start must not read like
    /// an ordinary line. Info stays bare, as a managed plugin's own lines are.
    /// </summary>
    [Fact]
    public async Task APluginsOwnWarning_KeepsItsLevel()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var pluginLog = new List<string>();
        var plugin = await StartAsync(lib, pluginLog);
        try
        {
            for (var i = 0; i < 50 && !pluginLog.Contains("[Warning] careful"); i++) await Task.Delay(20);

            lock (pluginLog)
            {
                Assert.Contains("[Warning] careful", pluginLog);
                Assert.Contains("started", pluginLog);
            }
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>Review focus 2's other half: a report naming an id the host never issued goes nowhere.</summary>
    [Fact]
    public async Task AReportForAnIdNeverIssued_ReachesNoCall()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var plugin = await StartAsync(lib);
        try
        {
            var call = new Recording();

            var result = await plugin.Invoke("stray", NoArguments, call, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Empty(call.Seen);
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>
    /// "No call" is a NULL id outside an invoke, and nothing else: a log line whose id does not parse
    /// is a plugin bug, and sending it to the plugin's own log would hide that bug behind a line
    /// that looks deliberate.
    /// </summary>
    [Fact]
    public async Task ALogWithAnUnreadableCallId_IsDropped_NotSentToThePluginsLog()
    {
        if (NativeFixtures.Path("fixture-reports") is not { } lib) return;
        var pluginLog = new List<string>();
        var plugin = await StartAsync(lib, pluginLog);
        try
        {
            await plugin.Invoke("stray", NoArguments, new Recording(), CancellationToken.None);
            await plugin.Invoke("echo", NoArguments, new Recording(), CancellationToken.None);

            lock (pluginLog) Assert.DoesNotContain(pluginLog, line => line.Contains("unplaceable"));
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }

    /// <summary>
    /// A line the reader cannot place costs that line and nothing else: the reader is the only one
    /// this host has, and every other call's reply waits on it.
    /// </summary>
    [Theory]
    [InlineData("""{"event":5,"id":3}""")]
    [InlineData("""{"event":"log","id":"x","text":"t"}""")]
    [InlineData("""{"event":"progress","id":3,"percent":"lots"}""")]
    [InlineData("""{"event":"teleport","id":3}""")]
    [InlineData("not json")]
    public void AMalformedReport_IsDropped_WithoutThrowing(string line)
    {
        var call = new Recording();

        AbiHostProcess.RouteReport(line, id => id == 3 ? call : null, (_, _) => throw new InvalidOperationException());

        Assert.Empty(call.Seen);
    }

    /// <summary>A context that throws while taking a report must not take the reader down with it.</summary>
    [Fact]
    public void AContextThatThrows_CostsOnlyThatReport()
    {
        AbiHostProcess.RouteReport("""{"event":"progress","id":3,"percent":5}""", _ => new Throwing(), null);
    }

    /// <summary>The export is optional: a library without attach loads, runs, and reports nothing.</summary>
    [Fact]
    public async Task ALibraryWithoutAttach_StillRuns()
    {
        if (NativeFixtures.Path("fixture-wellformed") is not { } lib) return;
        var plugin = await StartAsync(lib);
        try
        {
            var call = new Recording();

            var result = await plugin.Invoke("echo", NoArguments, call, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Empty(call.Seen);
        }
        finally { await plugin.Stop(CancellationToken.None); }
    }
}
