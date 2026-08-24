using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The mutable registry PLUGINS.md's whole design rests on, and the turn boundary that keeps it
/// correct — see PLUGINS.md, "Loading is refused mid-turn" and "Unwire is one ordered operation".
///
/// <para>NO LOADER YET. Every plugin here is <see cref="FakePlugin"/> — nothing in this task
/// constructs an <see cref="IPlugin"/> from disk, so the fixture is what Task 4 replaces.</para>
/// </summary>
public class PluginRegistryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-registry-" + Guid.NewGuid().ToString("N"));

    public PluginRegistryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static JsonElement EmptySchema() => JsonSerializer.SerializeToElement(new { type = "object" });

    private static PluginManifest Manifest(string name, params string[] toolNames) =>
        new(name, "1.0.0", Instructions: null, Spawns: false,
            toolNames.Select(n => new PluginToolManifest(n, "does something", EmptySchema())).ToList());

    /// <summary>Never spawns anything and Stops instantly — records whether Stop ran, which is what
    /// the ordering tests need to observe. Invoke records every call it received and echoes the
    /// tool name back in its result, so a dispatch test can tell one plugin tool's call from
    /// another's without needing a real LSP-shaped executor behind it.</summary>
    private sealed class FakePlugin : IPlugin
    {
        public bool Stopped { get; private set; }
        public List<string> InvokedToolNames { get; } = [];

        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("the registry is handed an already-loaded plugin in these tests");

        public Task Start(CancellationToken ct) => Task.CompletedTask;

        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
            CancellationToken ct)
        {
            InvokedToolNames.Add(toolName);
            return Task.FromResult(new JobResult { Success = true, Output = { ["tool"] = toolName } });
        }

        public Task Stop(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    // ---- Load: duplicate names refuse the whole plugin -----------------------------------------

    [Fact]
    public void LoadingATwoToolManifestOffersBothTools()
    {
        var registry = new PluginRegistry();
        var result = registry.Load(new FakePlugin(), Manifest("lsp-rust", "lsp_definition", "lsp_rename"),
            isNameTaken: _ => false);

        Assert.IsType<PluginLoadResult.Loaded>(result);
        Assert.Equal(["lsp_definition", "lsp_rename"],
            registry.CurrentTools().Select(t => t.Definition.Name).ToList());
    }

    /// <summary>A name already taken outside the registry — a built-in or an injected tool — refuses
    /// the whole plugin, not just the colliding tool. See PLUGINS.md, "Name collisions": a
    /// half-loaded plugin is unpredictable from its manifest.</summary>
    [Fact]
    public void ACollisionWithAnOutsideNameRefusesTheWholePluginNotJustOneTool()
    {
        var registry = new PluginRegistry();
        var result = registry.Load(new FakePlugin(),
            Manifest("lsp-rust", "lsp_definition", "read_file"),
            isNameTaken: name => name == "read_file");

        var collision = Assert.IsType<PluginLoadResult.NameCollision>(result);
        Assert.Equal("read_file", collision.ToolName);

        // NEITHER TOOL LOADED — lsp_definition did not collide, but the whole plugin is refused.
        Assert.Empty(registry.CurrentTools());
    }

    /// <summary>A second plugin naming a tool the first already claimed is refused, entirely —
    /// matrix row 2, plugin x plugin.</summary>
    [Fact]
    public void ACollisionWithAnotherLoadedPluginRefusesTheSecondPluginWhole()
    {
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), isNameTaken: _ => false);

        var result = registry.Load(new FakePlugin(), Manifest("lsp-python", "lsp_rename", "lsp_hover"),
            isNameTaken: _ => false);

        var collision = Assert.IsType<PluginLoadResult.NameCollision>(result);
        Assert.Equal("lsp_rename", collision.ToolName);

        // lsp-python contributed NOTHING — lsp_hover did not collide, but the plugin is refused whole.
        Assert.DoesNotContain(registry.CurrentTools(), t => t.Definition.Name == "lsp_hover");
        Assert.Equal(["lsp_rename"], registry.CurrentTools().Select(t => t.Definition.Name).ToList());
    }

    // ---- Dispatch: a tool call routes into its owning plugin by name -----------------------------

    /// <summary>The registry's IAgentTool adapter routes a call THROUGH the plugin instance rather
    /// than servicing it itself — one plugin is the executor behind every tool it declared, told
    /// apart by name, the same shape ToolBindings already has for several tools sharing one
    /// executor. Two tools from the same plugin dispatch to the SAME instance with different names.</summary>
    [Fact]
    public async Task ATwoToolPluginDispatchesEachCallToItsOwnToolName()
    {
        var registry = new PluginRegistry();
        var plugin = new FakePlugin();
        registry.Load(plugin, Manifest("lsp-rust", "lsp_definition", "lsp_rename"), isNameTaken: _ => false);

        var tools = registry.CurrentTools();
        var definition = tools.Single(t => t.Definition.Name == "lsp_definition");
        var rename = tools.Single(t => t.Definition.Name == "lsp_rename");

        var context = new TestJobContext();
        var first = await definition.ExecuteAsync(new JobParameters(), context, CancellationToken.None);
        var second = await rename.ExecuteAsync(new JobParameters(), context, CancellationToken.None);

        Assert.Equal(["lsp_definition", "lsp_rename"], plugin.InvokedToolNames);
        Assert.True(first.Success);
        Assert.Equal("lsp_definition", first.Output["tool"]);
        Assert.Equal("lsp_rename", second.Output["tool"]);
    }

    /// <summary>A tool whose manifest does not set <c>gated</c> asks nothing — GATE 1 in
    /// GatedAgentTool ("may this tool run at all") is a separate question this adapter's own Gate
    /// does not need to answer.</summary>
    [Fact]
    public void AnUngatedToolsOwnGateIsNull()
    {
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), Manifest("lsp-rust", "lsp_definition"), isNameTaken: _ => false);

        var tool = registry.CurrentTools().Single();
        Assert.Null(tool.Gate(new JobParameters()));
    }

    /// <summary>A tool the manifest marks <c>gated</c> asks EVERY call — a null AlwaysRule, so no
    /// stored rule can generalise it, because the plugin-declared policy that WOULD let it
    /// generalise is a later task's to build. Defaulting to "asks always" rather than "never asks"
    /// is the safe side of a policy not yet implemented.</summary>
    [Fact]
    public void AGatedToolsOwnGateAsksEveryCallWithNoAlwaysRule()
    {
        var manifest = new PluginManifest("lsp-rust", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("lsp_rename", "renames a symbol", EmptySchema(), Gated: true)]);
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), manifest, isNameTaken: _ => false);

        var tool = registry.CurrentTools().Single();
        var request = tool.Gate(new JobParameters());

        Assert.NotNull(request);
        Assert.Equal(PermissionKind.Tool, request.Kind);
        Assert.Null(request.AlwaysRule);
    }

    // ---- Unwire ordering -------------------------------------------------------------------------

    /// <summary>Unwiring is one ordered operation: deregister, drain, Stop, reap. Deregistering
    /// FIRST is what makes draining finite — a plugin still reachable can be handed new work.</summary>
    [Fact]
    public async Task UnwireDeregistersBeforeDraining()
    {
        var registry = new PluginRegistry();
        var plugin = new FakePlugin();
        registry.Load(plugin, Manifest("lsp-rust", "lsp_rename"), isNameTaken: _ => false);

        var release = new TaskCompletionSource();
        var held = registry.HoldCallOpenForTest("lsp-rust", release.Task);

        var unwire = registry.UnwireAsync("lsp-rust", CancellationToken.None);

        // DEREGISTERED ALREADY, while the held call is still open: CurrentTools must not offer this
        // plugin's tools to a turn that starts while the drain is still waiting.
        await Task.Delay(50);
        Assert.Empty(registry.CurrentTools());
        Assert.False(plugin.Stopped, "Stop ran before the in-flight call finished draining");
        Assert.False(unwire.IsCompleted, "unwire finished without waiting for the drain");

        // RELEASE THE HELD CALL: only now can the drain finish, then Stop runs, then unwire returns.
        release.SetResult();
        Assert.True(await unwire.WaitAsync(TimeSpan.FromSeconds(5)));
        await held;
        Assert.True(plugin.Stopped);
    }

    [Fact]
    public async Task UnwireRunsStopWhenNothingIsInFlight()
    {
        var registry = new PluginRegistry();
        var plugin = new FakePlugin();
        registry.Load(plugin, Manifest("lsp-rust", "lsp_rename"), isNameTaken: _ => false);

        Assert.True(await registry.UnwireAsync("lsp-rust", CancellationToken.None));
        Assert.True(plugin.Stopped);
        Assert.Empty(registry.CurrentTools());
    }

    [Fact]
    public async Task UnwiringAPluginThatIsNotLoadedReturnsFalse()
    {
        var registry = new PluginRegistry();
        Assert.False(await registry.UnwireAsync("nothing-here", CancellationToken.None));
    }

    [Fact]
    public async Task UnwireAllStopsEveryPlugin()
    {
        var registry = new PluginRegistry();
        var a = new FakePlugin();
        var b = new FakePlugin();
        registry.Load(a, Manifest("lsp-rust", "lsp_rename"), isNameTaken: _ => false);
        registry.Load(b, Manifest("lsp-python", "lsp_hover"), isNameTaken: _ => false);

        await registry.UnwireAllAsync(CancellationToken.None);

        Assert.True(a.Stopped);
        Assert.True(b.Stopped);
        Assert.Empty(registry.CurrentTools());
    }

    // ---- Session.LoadPlugin / UnwirePlugin: refused mid-turn -------------------------------------

    private Session Wired(out SessionManager manager, ILlmProvider provider, out BufferedChatSink sink)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        sink = new BufferedChatSink();
        return manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    /// <summary>A provider whose stream blocks until released, so a test can hold a real turn open
    /// long enough to assert something is refused while <see cref="Session.IsBusy"/> is true.</summary>
    private sealed class BlockingProvider(TaskCompletionSource arrived, Task release) : ILlmProvider
    {
        public string ProviderId => "blocking";
        public string DisplayName => "blocking";
        public string ModelId => "blocking";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => true;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct) => throw new NotSupportedException("streaming only");

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            arrived.TrySetResult();
            await release.WaitAsync(TimeSpan.FromSeconds(10), ct);
            yield return new LlmStreamChunk("done", null, IsFinal: true, StopReason: "end_turn");
        }
    }

    /// <summary>
    /// REFUSED MID-TURN, and the reason is Core's own invariant rather than a new rule: the tool
    /// list is fixed once a request begins, so a tool cannot appear or vanish between two turns of
    /// one request and leave the model chasing something that is no longer there.
    /// </summary>
    [Fact]
    public async Task LoadingDuringATurnIsRefusedAndSaysSo()
    {
        var arrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var session = Wired(out var manager, new BlockingProvider(arrived, release.Task), out var sink);
        using var _ = manager;

        var started = session.Submit("go");
        Assert.IsType<Session.SubmitOutcome.Started>(started);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsBusy);

        var status = await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), _dir);

        Assert.Equal(CommandStatus.Refused, status);
        Assert.Empty(session.Plugins.CurrentTools());
        Assert.Contains(sink.Notices, t => t.Contains("turn is running", StringComparison.OrdinalIgnoreCase));

        release.SetResult();
        await ((Session.SubmitOutcome.Started)started).Turn.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Unwiring is refused mid-turn for the identical reason as loading — a call already in
    /// flight for one of this plugin's tools would fail for a reason nobody could trace back.</summary>
    [Fact]
    public async Task UnwiringDuringATurnIsRefusedAndSaysSo()
    {
        var arrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var session = Wired(out var manager, new BlockingProvider(arrived, release.Task), out _);
        using var __ = manager;

        Assert.Equal(CommandStatus.Changed,
            await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), _dir));

        var started = session.Submit("go");
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsBusy);

        var status = await session.UnwirePluginAsync("lsp-rust", CancellationToken.None);

        Assert.Equal(CommandStatus.Refused, status);
        Assert.Contains("lsp_rename", session.Plugins.CurrentTools().Select(t => t.Definition.Name));

        release.SetResult();
        await ((Session.SubmitOutcome.Started)started).Turn.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Off the busy path entirely: a plugin loads and unwires cleanly at an ordinary turn
    /// boundary, and both report Changed.</summary>
    [Fact]
    public async Task LoadAndUnwireAtATurnBoundarySucceed()
    {
        var session = Wired(out var manager, new MockLlmProvider(), out _);
        using var __ = manager;

        var loaded = await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), _dir);
        Assert.Equal(CommandStatus.Changed, loaded);
        Assert.Contains("lsp_rename", session.Plugins.CurrentTools().Select(t => t.Definition.Name));

        var unwired = await session.UnwirePluginAsync("lsp-rust", CancellationToken.None);
        Assert.Equal(CommandStatus.Changed, unwired);
        Assert.Empty(session.Plugins.CurrentTools());
    }

    /// <summary>An injected tool a session was wired with — used only to occupy a name, so
    /// <see cref="EchoTool.ExecuteAsync"/> is never actually reached by these tests.</summary>
    private sealed class EchoTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("echo_tool", "echoes",
            JsonSerializer.SerializeToElement(new { type = "object" }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true });
    }

    /// <summary>
    /// Matrix rows 3 and 4 (plugin x injected): a plugin whose tool name collides with an injected
    /// tool's must not load — the gap Task 3 left, because Agent then exposed no session-reachable
    /// way to ask "does an injected tool own this name" outside an internal test-only method. See
    /// <see cref="AgentHost.KnowsInjectedTool"/> and <see cref="Session.LoadPlugin"/>'s composed
    /// predicate.
    /// </summary>
    [Fact]
    public async Task APluginCollidingWithAnInjectedToolRefusesToLoad()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = new BufferedJobPanel(),
                Tools = [new EchoTool()],
            },
            AgentMode.Single);
        using var _ = manager;

        var status = await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "echo_tool"), _dir);

        Assert.Equal(CommandStatus.Reported, status);
        Assert.Empty(session.Plugins.CurrentTools());
    }

    /// <summary>The same collision, but the plugin's OTHER tool name is free — the whole plugin
    /// still refuses (PLUGINS.md, "Name collisions": a half-loaded plugin is unpredictable), proving
    /// the injected check goes through the same all-or-nothing path as the built-in check.</summary>
    [Fact]
    public async Task APluginCollidingWithAnInjectedToolRefusesWholeEvenWithAnUncontestedToolAlso()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = new BufferedJobPanel(),
                Tools = [new EchoTool()],
            },
            AgentMode.Single);
        using var _ = manager;

        var status = await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename", "echo_tool"), _dir);

        Assert.Equal(CommandStatus.Reported, status);
        Assert.DoesNotContain(session.Plugins.CurrentTools(), t => t.Definition.Name == "lsp_rename");
    }

    /// <summary>Session close runs the same four steps as an explicit unwire — there is no separate
    /// teardown path, so a plugin loaded and never explicitly unwired still gets Stop called.</summary>
    [Fact]
    public async Task ClosingASessionUnwiresEveryPluginItLoaded()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        var plugin = new FakePlugin();
        await session.LoadPlugin(plugin, Manifest("lsp-rust", "lsp_rename"), _dir);

        manager.Close(session);

        Assert.True(plugin.Stopped);
    }
}
