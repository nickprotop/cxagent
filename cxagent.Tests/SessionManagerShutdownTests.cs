using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Quitting: every tab's plugins are asked to Stop within a short window, and whatever this run
/// recorded that is still running is killed on the way out.
/// </summary>
public class SessionManagerShutdownTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("shutdown-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Plugin(bool hang) : IPlugin
    {
        public bool StopCalled { get; private set; }

        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("handed to the registry already loaded");

        public Task Start(CancellationToken ct) => Task.CompletedTask;

        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
            CancellationToken ct) => Task.FromResult(new JobResult { Success = true });

        public Task Stop(CancellationToken ct)
        {
            StopCalled = true;
            return hang ? Task.Delay(Timeout.Infinite, CancellationToken.None) : Task.CompletedTask;
        }
    }

    private static PluginManifest Manifest(string name) =>
        new(name, "1.0.0", Instructions: null, Spawns: true,
            [new PluginToolManifest(name + "_x", "x", JsonSerializer.SerializeToElement(new { type = "object" }))]);

    private Session Open(SessionManager manager) =>
        manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

    /// <summary>
    /// EVERY TAB, NOT ONLY THE FIRST, gets its plugins stopped — and a Stop that hangs costs at most
    /// the window, after which what its plugin spawned is killed rather than left to outlive the app.
    /// </summary>
    [Fact]
    public void Shutdown_StopsEveryTabsPlugins_AndKillsWhatOutlivesTheWindow()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var first = Open(manager);
        var second = Open(manager);
        var fine = new Plugin(hang: false);
        var hung = new Plugin(hang: true);
        first.Plugins.Load(fine, Manifest("fine"), isNameTaken: _ => false);
        second.Plugins.Load(hung, Manifest("hung"), isNameTaken: _ => false);

        using var child = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        new ChildProcessStore(_dir).Add(new ChildProcessRecord(child.Id, child.StartTime.ToUniversalTime(), "hung"));

        // THE APP'S OWN WINDOW, TWO SECONDS. A short one measured the thread pool, not the behaviour:
        // on a slow runner the well-behaved plugin's teardown had not even been scheduled within
        // 300ms. The hung Stop still cannot hold the quit past the window, which the bound shows.
        var clock = Stopwatch.StartNew();
        manager.Shutdown(TimeSpan.FromSeconds(2));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"shutdown took {clock.Elapsed}");
        Assert.True(fine.StopCalled);
        Assert.True(hung.StopCalled);
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)), "a hung plugin's child outlived the quit");
    }

    /// <summary>A model that answers nothing and ignores cancellation: a turn on it stays busy.</summary>
    private sealed class StuckProvider : ILlmProvider
    {
        public string ProviderId => "stuck";
        public string ModelId => "stuck";
        public string DisplayName => "Stuck";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct) => new TaskCompletionSource<LlmResponse>().Task;

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await new TaskCompletionSource().Task;
            yield break;
        }
    }

    /// <summary>
    /// QUITTING MID-TURN STILL STOPS THE PLUGINS. A busy session cannot be closed the ordinary way,
    /// and ending it without unwiring meant its plugins never heard they were ending — the common
    /// case, since Ctrl+Q is often pressed while a model is still answering.
    /// </summary>
    [Fact]
    public void Shutdown_StopsTheBusySessionsPlugins_Too()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new StuckProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        var plugin = new Plugin(hang: false);
        session.Plugins.Load(plugin, Manifest("busy"), isNameTaken: _ => false);
        session.Submit("think forever");
        Assert.True(session.IsBusy);

        // TEN SECONDS, WHICH COSTS NOTHING: Shutdown returns as soon as the teardowns finish, and this
        // plugin stops at once. Half a second failed under a loaded suite, where the detached teardown
        // had not been scheduled yet — the window measured the thread pool, not the behaviour.
        manager.Shutdown(TimeSpan.FromSeconds(10));

        Assert.True(plugin.StopCalled, "a busy session's plugin was never stopped at quit");
    }
}
