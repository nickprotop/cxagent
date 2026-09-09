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

        /// <summary>Which instance actually ran — the point of the cross-session dispatch test.</summary>
        public bool Ran { get; private set; }

        public Task<CommandResult> RunCommand(string name, string arguments, CancellationToken ct)
        {
            Ran = true;
            return Task.FromResult(new CommandResult($"hello, {arguments}", PluginCommandOutcome.Reported));
        }
    }

    private Session Wired(out SessionManager manager)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        return manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    /// <summary>
    /// A SECOND SESSION CAN LOAD THE SAME COMMAND-DECLARING PLUGIN.
    ///
    /// <para>The command table is the MANAGER's, shared by every session, so the first session to load
    /// a plugin registers its commands for the whole process. Without knowing who owns a name, the
    /// second session's load found its OWN commands taken and refused the whole plugin — tools
    /// included — reporting that they were "already offered by this session" about a registration
    /// another session made.</para>
    /// </summary>
    [Fact]
    public async Task TheSamePluginLoadsInASecondSession()
    {
        var first = Wired(out var manager);
        using var _ = manager;
        Assert.Equal(CommandStatus.Changed,
            await first.LoadPlugin(new CommandPlugin(), ManifestWith("greet"), _dir));

        var second = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        Assert.Equal(CommandStatus.Changed,
            await second.LoadPlugin(new CommandPlugin(), ManifestWith("greet"), _dir));
        Assert.Contains("cmd", second.Plugins.LoadedPluginNames);
    }

    /// <summary>
    /// AND THE COMMAND RUNS AGAINST THE SESSION THAT TYPED IT, not the one that loaded it first.
    ///
    /// <para>One registration serves every session, so the handler is given a plugin NAME and resolves
    /// the instance from the typing session. A handler closing over the loading session's instance
    /// would run session one's plugin for a command typed in session two — a plugin's reach is its own
    /// session, and dispatching across sessions breaks that through the command surface.</para>
    /// </summary>
    [Fact]
    public async Task ACommandRunsAgainstTheSessionThatTypedIt()
    {
        var first = Wired(out var manager);
        using var _ = manager;
        var firstPlugin = new CommandPlugin();
        await first.LoadPlugin(firstPlugin, ManifestWith("greet"), _dir);

        var second = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        var secondPlugin = new CommandPlugin();
        await second.LoadPlugin(secondPlugin, ManifestWith("greet"), _dir);

        manager.Commands.Run(second, "/greet world");
        await Task.Delay(200);

        Assert.True(secondPlugin.Ran, "the typing session's own instance should have run it");
        Assert.False(firstPlugin.Ran, "the loading session's instance must not run another's command");
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
