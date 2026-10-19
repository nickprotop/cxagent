using System.Text.Json;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Testing;

/// <summary>
/// What a harness hands a plugin at Load: the folder, its settings, a logger that keeps every line,
/// and a lifetime cancelled when the harness is done. The children it registers are reaped then.
/// </summary>
internal sealed class HarnessContext(string workingDirectory, JsonElement settings, IPluginClient? client)
    : IPluginContext, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _logged = [];
    private readonly List<int> _children = [];

    public string WorkingDirectory { get; } = workingDirectory;
    public JsonElement Settings { get; } = settings;
    public int HostContract => PluginContract.Version;
    public string HostVersion => PluginContract.HostVersionOf(typeof(ManagedPluginLoader).Assembly);
    public IPluginLogger Logger => new Lines(this);
    public IPluginClient? Client { get; } = client;
    public CancellationToken Lifetime => _lifetime.Token;

    public IReadOnlyList<string> Logged { get { lock (_logged) return [.. _logged]; } }

    public void RegisterChildProcess(int pid) { lock (_children) _children.Add(pid); }

    /// <summary>
    /// Ends the plugin's lifetime — BEFORE its Stop is awaited, as a session does, so a Stop that waits
    /// for a loop which exits on the lifetime returns rather than waiting on itself.
    /// </summary>
    public void EndLifetime() => _lifetime.Cancel();

    /// <summary>
    /// Ends the lifetime if it has not ended, and kills whatever the plugin registered and left
    /// running — a test that leaves a language server behind leaks it into every later test on the
    /// machine.
    ///
    /// <para>THE TOKEN SOURCE IS NOT DISPOSED: a plugin may still read <see cref="Lifetime"/> from a
    /// thread of its own after Stop, and reading a disposed source's token throws. It holds no timer,
    /// so leaving it to the collector costs nothing.</para>
    /// </summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        int[] pids;
        lock (_children) pids = [.. _children];
        foreach (var pid in pids)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // ALREADY GONE — Stop reaped it, or it exited on its own. Nothing left to kill.
            }
        }
    }

    private sealed class Lines(HarnessContext owner) : IPluginLogger
    {
        public void Log(string message) { lock (owner._logged) owner._logged.Add(message); }
    }
}

/// <summary>Keeps every report a call makes, in order, and passes each to the caller as it happens.</summary>
internal sealed class RecordingJobContext(string workingDirectory, Action<HarnessReport>? onReport) : CxAgent.Core.Jobs.IJobContext
{
    private readonly List<HarnessReport> _reports = [];

    public IReadOnlyList<HarnessReport> Reports { get { lock (_reports) return [.. _reports]; } }
    public string? Requester => "cxagent plugin call";
    public string? WorkingDirectory { get; } = workingDirectory;

    public void ReportProgress(double percent, string? message = null) => Add(new HarnessReport.Progress(percent, message));
    public void Log(string line) => Add(new HarnessReport.Log(CxAgent.Core.Jobs.JobLogLevel.Info, line));
    public void Log(CxAgent.Core.Jobs.JobLogLevel level, string line) => Add(new HarnessReport.Log(level, line));
    public void ReportTextDelta(string delta) => Add(new HarnessReport.Stream(delta));

    private void Add(HarnessReport report)
    {
        lock (_reports) _reports.Add(report);

        // THE CALLER'S PRINTER MUST NOT FAIL THE PLUGIN'S CALL: for a native plugin this runs on the
        // one thread every reply waits on.
        try
        {
            onReport?.Invoke(report);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>A client that starts nothing: each goal is kept, and accepted, so the plugin carries on.</summary>
internal sealed class RecordingClient : IPluginClient
{
    private readonly List<string> _goals = [];

    public IReadOnlyList<string> Goals { get { lock (_goals) return [.. _goals]; } }

    public Task<SubmitResult> Submit(string goal, bool wantResult = false, CancellationToken ct = default)
    {
        lock (_goals) _goals.Add(goal);
        return Task.FromResult(new SubmitResult(true, null, null));
    }
}
