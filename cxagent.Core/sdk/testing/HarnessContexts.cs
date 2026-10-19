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
    /// Cancels the lifetime and kills whatever the plugin registered and left running — a test that
    /// leaves a language server behind leaks it into every later test on the machine.
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

        _lifetime.Dispose();
    }

    private sealed class Lines(HarnessContext owner) : IPluginLogger
    {
        public void Log(string message) { lock (owner._logged) owner._logged.Add(message); }
    }
}
