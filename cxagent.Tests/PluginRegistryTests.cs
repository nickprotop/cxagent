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
/// The mutable registry the plugin design's whole design rests on, and the turn boundary that keeps it
/// correct — see the plugin design, "Loading is refused mid-turn" and "Unwire is one ordered operation".
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
    /// the whole plugin, not just the colliding tool. See the plugin design, "Name collisions": a
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

    /// <summary>
    /// A tool the manifest marks <c>gated</c> asks, and OFFERS "Always" — the user owns that
    /// decision, having already approved this binary at load against a hash of its whole load set.
    ///
    /// <para>THE RULE NAMES THE PLUGIN AS WELL AS THE TOOL. A bare <c>tool lsp_rename</c> would
    /// outlive this plugin: uninstall it, install a different one declaring the same tool name, and
    /// the newcomer inherits a grant the user gave someone else. A built-in can use the bare form
    /// because nothing else can ever claim its name; a plugin's name is only unique among what
    /// happens to be installed.</para>
    /// </summary>
    [Fact]
    public void AGatedToolsOwnGateOffersAlwaysScopedToThePlugin()
    {
        var manifest = new PluginManifest("lsp-rust", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("lsp_rename", "renames a symbol", EmptySchema(), Gated: PluginGating.Always)]);
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), manifest, isNameTaken: _ => false);

        var tool = registry.CurrentTools().Single();
        var request = tool.Gate(new JobParameters());

        Assert.NotNull(request);
        Assert.Equal(PermissionKind.Tool, request.Kind);
        Assert.Equal("plugin lsp-rust tool lsp_rename", request.AlwaysRule);
    }

    /// <summary>
    /// A gated tool the plugin marks <c>alwaysAskable: false</c> asks with NO "Always" — the one
    /// case where a standing grant is withheld, and the plugin is the party that decides it.
    ///
    /// <para>WHY THE PLUGIN AND NOT CORE: a language server's <c>definition</c> is a read that a
    /// user should be able to stop being asked about, while its <c>rename</c> rewrites files across
    /// a repository. Those want different answers, and nothing Core can see — a tool name, a JSON
    /// schema — distinguishes them. The author knows; this is how they say so.</para>
    /// </summary>
    [Fact]
    public void APluginCanWithholdAlwaysForOneOfItsTools()
    {
        var manifest = new PluginManifest("lsp-rust", "1.0.0", Instructions: null, Spawns: false,
        [
            new PluginToolManifest("lsp_definition", "finds a declaration", EmptySchema(), Gated: PluginGating.Always),
            new PluginToolManifest("lsp_rename", "rewrites every usage", EmptySchema(), Gated: PluginGating.Always,
                AlwaysAskable: false),
        ]);
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), manifest, isNameTaken: _ => false);

        var tools = registry.CurrentTools().ToDictionary(t => t.Definition.Name);

        // THE TWO TOOLS OF ONE PLUGIN DIFFER, which is the whole point of a per-tool flag.
        Assert.Equal("plugin lsp-rust tool lsp_definition",
            tools["lsp_definition"].Gate(new JobParameters())!.AlwaysRule);
        Assert.Null(tools["lsp_rename"].Gate(new JobParameters())!.AlwaysRule);

        // Both still ASK — alwaysAskable narrows how an answer generalises, it does not ungate.
        Assert.NotNull(tools["lsp_rename"].Gate(new JobParameters()));
    }

    /// <summary>A manifest that says nothing about <c>alwaysAskable</c> offers Always — the absent
    /// field means "the author did not think about this", and the answer matching every other
    /// permission in cxagent is to offer it.</summary>
    [Fact]
    public void AlwaysAskableDefaultsToTrueWhenTheManifestOmitsIt()
    {
        var json = """
        {
          "name": "lsp-rust", "version": "1.0.0", "spawns": false,
          "tools": [ { "name": "lsp_hover", "description": "d",
                       "inputSchema": { "type": "object" }, "gated": true } ]
        }
        """;

        var parsed = PluginManifest.Parse(json);

        Assert.True(parsed.IsSuccess);
        Assert.True(parsed.Manifest!.Tools.Single().AlwaysAskable);
    }

    /// <summary>An ungated tool does not ask at all — the other half of what <c>gated</c> selects
    /// between, asserted beside it so a change that gates everything fails here.</summary>
    [Fact]
    public void AnUngatedToolDoesNotAsk()
    {
        var manifest = new PluginManifest("lsp-rust", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("lsp_hover", "shows a type", EmptySchema(), Gated: PluginGating.Never)]);
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), manifest, isNameTaken: _ => false);

        Assert.Null(registry.CurrentTools().Single().Gate(new JobParameters()));
    }

    // ---- The system prompt --------------------------------------------------------------------

    /// <summary>
    /// A plugin's manifest instructions reach the prompt, WITH the tools they govern.
    ///
    /// <para>THE FIELD WAS PARSED AND NEVER READ. PluginManifest.Instructions is validated against
    /// the sidecar at load — a mismatch fails the load — and its own doc calls it "a block of
    /// system-prompt text", which nothing put in one. A plugin could say "positions are 1-based" and
    /// the model never saw it.</para>
    ///
    /// <para>THE TOOL NAMES TRAVEL WITH IT because a second language-server plugin would otherwise
    /// contribute a second block making overlapping claims about "positions" and "the server", and
    /// nothing would say which tools each governs.</para>
    /// </summary>
    [Fact]
    public void APluginsInstructionsReachThePromptWithItsToolNames()
    {
        var manifest = new PluginManifest("lsp-rust", "1.0.0",
            Instructions: "Positions are 1-based.", Spawns: false,
            [new PluginToolManifest("rust_definition", "finds a declaration", EmptySchema())]);
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), manifest, isNameTaken: _ => false);

        var block = Assert.Single(registry.InstructionsForPrompt());

        Assert.Equal("lsp-rust", block.Plugin);
        Assert.Equal("Positions are 1-based.", block.Text);
        Assert.Equal(["rust_definition"], block.Tools);
    }

    /// <summary>A plugin declaring no instructions contributes no block — a heading with nothing
    /// under it is prompt weight for no content.</summary>
    [Fact]
    public void APluginWithNoInstructionsContributesNothing()
    {
        var manifest = new PluginManifest("lsp-rust", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("rust_definition", "finds a declaration", EmptySchema())]);
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), manifest, isNameTaken: _ => false);

        Assert.Empty(registry.InstructionsForPrompt());
    }

    /// <summary>
    /// Unwiring removes the guidance along with the tools.
    ///
    /// <para>TEXT FOR TOOLS THAT ARE GONE IS WORSE THAN NO TEXT: it reads as actionable, so a model
    /// told "call rust_definition first" keeps trying a name nothing answers. The two must move
    /// together, which is why both are read per turn from this registry rather than cached.</para>
    /// </summary>
    [Fact]
    public async Task UnwiringRemovesTheInstructionsWithTheTools()
    {
        var manifest = new PluginManifest("lsp-rust", "1.0.0",
            Instructions: "Positions are 1-based.", Spawns: false,
            [new PluginToolManifest("rust_definition", "finds a declaration", EmptySchema())]);
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), manifest, isNameTaken: _ => false);

        Assert.Single(registry.InstructionsForPrompt());
        Assert.Single(registry.CurrentTools());

        await registry.UnwireAsync("lsp-rust", CancellationToken.None);

        Assert.Empty(registry.InstructionsForPrompt());
        Assert.Empty(registry.CurrentTools());
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

    /// <summary>Unwire deregisters without unloading — the managed loader uses no
    /// AssemblyLoadContext — so the name must survive in EverLoadedNames: an uninstall consulting
    /// only current state would delete a file this process still holds open on Windows.</summary>
    [Fact]
    public async Task AnUnwiredPluginStaysInEverLoadedNames()
    {
        var registry = new PluginRegistry();
        registry.Load(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), isNameTaken: _ => false);

        Assert.True(await registry.UnwireAsync("lsp-rust", CancellationToken.None));

        Assert.DoesNotContain("lsp-rust", registry.LoadedPluginNames);
        Assert.Contains("lsp-rust", registry.EverLoadedNames);
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
    /// still refuses (the plugin design, "Name collisions": a half-loaded plugin is unpredictable), proving
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

    // ---- per-call gates: the plugin decides from the arguments ----

    /// <summary>Gates on an argument: a path under the root passes, anything else asks.</summary>
    private sealed class GatingPlugin(Func<string, JobParameters, PluginGate?> gate)
        : IPlugin, IPluginGateSource
    {
        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("already loaded in these tests");
        public Task Start(CancellationToken ct) => Task.CompletedTask;
        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
            CancellationToken ct) => Task.FromResult(new JobResult { Success = true });
        public Task Stop(CancellationToken ct) => Task.CompletedTask;
        public PluginGate? Gate(string toolName, JobParameters call) => gate(toolName, call);
    }

    private static PluginManifest OneTool(PluginToolManifest tool) =>
        new("gp", "1.0.0", Instructions: null, Spawns: false, [tool]);

    private static IAgentTool ToolFor(IPlugin plugin, PluginToolManifest tool)
    {
        var registry = new PluginRegistry();
        registry.Load(plugin, OneTool(tool), isNameTaken: _ => false);
        return registry.CurrentTools().Single();
    }

    private static JobParameters WithFile(string file) =>
        new(new Dictionary<string, object?> { ["file"] = file });

    /// <summary>
    /// THE WHOLE POINT: one tool, two calls, two different answers. A static boolean cannot express
    /// this — it must either ask about every read or none of them.
    /// </summary>
    [Fact]
    public void ADynamicToolAsksAboutOneCallAndNotAnother()
    {
        var tool = ToolFor(
            new GatingPlugin((_, call) => call.Get("file", "").StartsWith('/')
                ? new PluginGate($"read '{call.Get("file", "")}', outside the workspace")
                : null),
            new PluginToolManifest("read_it", "reads", EmptySchema(), Gated: PluginGating.Dynamic));

        Assert.Null(tool.Gate(WithFile("inside.cs")));

        var request = tool.Gate(WithFile("/etc/outside.cs"));
        Assert.NotNull(request);
        Assert.Contains("/etc/outside.cs", request.Display);
    }

    /// <summary>
    /// A plugin cannot forge the SCOPE of what it asks for. It supplies wording; Core decides the
    /// kind and the rule, so a plugin prompt can never write a Shell grant into permissions.json.
    /// </summary>
    [Fact]
    public void ADynamicGatesKindAndRuleAreCoresNotThePlugins()
    {
        var tool = ToolFor(
            new GatingPlugin((_, _) => new PluginGate("do something alarming")),
            new PluginToolManifest("t", "does", EmptySchema(), Gated: PluginGating.Dynamic));

        var request = tool.Gate(new JobParameters())!;

        Assert.Equal(PermissionKind.Tool, request.Kind);
        Assert.Equal("plugin gp tool t", request.AlwaysRule);
    }

    /// <summary>A gate that throws asks anyway, and NEVER offers Always — a broken gate must not be
    /// able to accumulate a standing grant.</summary>
    [Fact]
    public void AThrowingGateAsksWithoutOfferingAlways()
    {
        var tool = ToolFor(
            new GatingPlugin((_, _) => throw new InvalidOperationException("boom")),
            new PluginToolManifest("t", "does", EmptySchema(), Gated: PluginGating.Dynamic));

        var request = tool.Gate(new JobParameters());

        Assert.NotNull(request);
        Assert.Null(request.AlwaysRule);
    }

    /// <summary>alwaysAskable is a floor: the sidecar's false cannot be widened by a runtime gate.</summary>
    [Fact]
    public void AManifestThatWithholdsAlwaysCannotBeWidenedByTheGate()
    {
        var tool = ToolFor(
            new GatingPlugin((_, _) => new PluginGate("ask", AlwaysAskable: true)),
            new PluginToolManifest("t", "does", EmptySchema(),
                Gated: PluginGating.Dynamic, AlwaysAskable: false));

        Assert.Null(tool.Gate(new JobParameters())!.AlwaysRule);
    }

    /// <summary>gated:true never consults the gate — the sidecar's promise stands whatever the code says.</summary>
    [Fact]
    public void AStaticallyGatedToolNeverConsultsTheGate()
    {
        var consulted = false;
        var tool = ToolFor(
            new GatingPlugin((_, _) => { consulted = true; return null; }),
            new PluginToolManifest("t", "does", EmptySchema(), Gated: PluginGating.Always));

        Assert.NotNull(tool.Gate(new JobParameters()));
        Assert.False(consulted);
    }

    /// <summary>gated:false never consults the gate either — "never ask" keeps meaning never ask.</summary>
    [Fact]
    public void AnUngatedToolNeverConsultsTheGate()
    {
        var consulted = false;
        var tool = ToolFor(
            new GatingPlugin((_, _) => { consulted = true; return new PluginGate("ask"); }),
            new PluginToolManifest("t", "does", EmptySchema(), Gated: PluginGating.Never));

        Assert.Null(tool.Gate(new JobParameters()));
        Assert.False(consulted);
    }
}
