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
/// A plugin's declared command, wired end to end: runnable once loaded, gone once unwired, and a
/// name collision refuses the whole plugin rather than the one command that collided — see
/// <see cref="PluginRegistry.Load"/>'s own doc for why the check runs before any registration.
/// </summary>
public class PluginCommandRegistrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-cmd-" + Guid.NewGuid().ToString("N"));

    public PluginCommandRegistrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static JsonElement EmptySchema() => JsonSerializer.SerializeToElement(new { type = "object" });

    /// <summary>One manifest, one declared command named <paramref name="commandName"/> — no
    /// leading slash, matching PluginCommandManifest's own shape.</summary>
    private static PluginManifest ManifestWith(string commandName) =>
        new("cmd", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("cmd_tool", "a fixture tool", EmptySchema())],
            [new PluginCommandManifest(commandName, "a fixture command")]);

    /// <summary>Implements the command handler so the load-time capability check
    /// (ManagedPluginLoader's own, exercised elsewhere) is not what this suite is testing — these
    /// tests hand the registry an already-loaded plugin, same as PluginLoadGateTests' FakePlugin.</summary>
    private sealed class CommandPlugin : IPlugin, IPluginCommandHandler
    {
        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("the registry is handed an already-loaded plugin in these tests");

        public Task Start(CancellationToken ct) => Task.CompletedTask;

        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
            CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true, Output = { ["tool"] = toolName } });

        public Task Stop(CancellationToken ct) => Task.CompletedTask;

        public Task<CommandResult> RunCommand(string name, string arguments, CancellationToken ct) =>
            Task.FromResult(new CommandResult($"hello, {arguments}", PluginCommandOutcome.Reported));
    }

    private Session Wired(out SessionManager manager)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        return manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    [Fact]
    public async Task APluginsCommandRunsAfterLoad()
    {
        var session = Wired(out var manager);
        using var _ = manager;

        var status = await session.LoadPlugin(new CommandPlugin(), ManifestWith("greet"), _dir);
        Assert.Equal(CommandStatus.Changed, status);

        Assert.Equal(CommandRegistry.Dispatch.Ran, manager.Commands.Run(session, "/greet world"));
    }

    /// <summary>/model is a built-in. The WHOLE plugin is refused — not just the colliding
    /// command — because the manifest is what the load prompt described and the user approved.</summary>
    [Fact]
    public async Task APluginDeclaringATakenCommandNameIsRefusedWhole()
    {
        var session = Wired(out var manager);
        using var _ = manager;

        var status = await session.LoadPlugin(new CommandPlugin(), ManifestWith("model"), _dir);

        Assert.Equal(CommandStatus.Reported, status);
        Assert.DoesNotContain("cmd", session.Plugins.LoadedPluginNames);
    }

    [Fact]
    public async Task APluginsCommandIsGoneAfterUnwire()
    {
        var session = Wired(out var manager);
        using var _ = manager;
        await session.LoadPlugin(new CommandPlugin(), ManifestWith("greet"), _dir);

        await session.UnwirePluginAsync("cmd", CancellationToken.None);

        Assert.Equal(CommandRegistry.Dispatch.NotACommand, manager.Commands.Run(session, "/greet world"));
    }
}
