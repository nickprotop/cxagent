using System.Text.Json;
using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A plugin whose start never finishes — a language server that never answers — is unloaded once the
/// session's start limit passes, rather than holding every plugin queued after it, and the startup
/// summary, forever.
/// </summary>
public class PluginStartLimitTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-start-" + Guid.NewGuid().ToString("N"));

    public PluginStartLimitTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TempDirectory.Delete(_dir);

    private static PluginManifest Manifest() =>
        new("stuck", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("stuck_tool", "a fixture tool", JsonSerializer.SerializeToElement(new { type = "object" }))]);

    /// <summary>Starts and never finishes, whatever its token says — the case a timeout must cover.</summary>
    private sealed class NeverStarts : IPlugin
    {
        public bool Stopped { get; private set; }

        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("the registry is handed an already-loaded plugin in these tests");

        public Task Start(CancellationToken ct) => new TaskCompletionSource().Task;

        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true });

        public Task Stop(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }

    [Fact]
    public async Task APluginThatNeverStarts_IsUnloadedAtTheLimit_AndSaysWhy()
    {
        var chat = new BufferedChatSink();
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = chat, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        session.PluginStartLimit = TimeSpan.FromMilliseconds(200);
        var plugin = new NeverStarts();

        var load = session.LoadPlugin(plugin, Manifest(), _dir);
        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(load, finished);
        Assert.Equal(CommandStatus.Reported, await load);
        Assert.DoesNotContain("stuck", session.Plugins.LoadedPluginNames);
        Assert.True(plugin.Stopped, "the plugin is stopped, so whatever it spawned is reaped");
        Assert.Contains("did not start within", chat.Transcript);
    }
}
