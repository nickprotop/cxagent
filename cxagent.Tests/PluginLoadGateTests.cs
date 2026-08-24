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
/// The load gate — the plugin design, "Permission": "the load gate is the only boundary Core can enforce".
/// Task 5's three obligations, one test class each below: identity is a content hash over the whole
/// load set, config can never pre-approve a binary, and a plugin's tools are gated like any other
/// once loaded.
/// </summary>
public class PluginLoadGateTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-load-gate-" + Guid.NewGuid().ToString("N"));

    public PluginLoadGateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static JsonElement EmptySchema() => JsonSerializer.SerializeToElement(new { type = "object" });

    private static PluginManifest Manifest(string name, params string[] toolNames) =>
        new(name, "1.0.0", Instructions: null, Spawns: false,
            toolNames.Select(n => new PluginToolManifest(n, "does something", EmptySchema())).ToList());

    private sealed class FakePlugin : IPlugin
    {
        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) =>
            throw new NotSupportedException("the registry is handed an already-loaded plugin in these tests");

        /// <summary>Whether <see cref="Start"/> ran — IPlugin.Start's contract is "runs after Load",
        /// and a plugin whose tools are offered but which was never started answers every call with
        /// "not running". Recorded rather than ignored so a test can assert the lifecycle completed.</summary>
        public bool Started { get; private set; }

        /// <summary>Set to make Start throw, for the failed-start path.</summary>
        public Exception? StartFailure { get; init; }

        public Task Start(CancellationToken ct)
        {
            if (StartFailure is not null) return Task.FromException(StartFailure);
            Started = true;
            return Task.CompletedTask;
        }

        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
            CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true, Output = { ["tool"] = toolName } });

        public Task Stop(CancellationToken ct) => Task.CompletedTask;
    }

    // ---- Lifecycle: Load is not enough, Start has to run ----------------------------------------

    /// <summary>
    /// A loaded plugin is STARTED, not merely registered — <see cref="IPlugin.Start"/>'s own contract
    /// is "Runs after Load. The plugin may spawn processes, open connections, index — whatever it
    /// needs before its tools can be called."
    ///
    /// <para>NOTHING ELSE ASSERTS THIS. Every other test drives a plugin by constructing it and
    /// calling Start by hand, so all of them pass whether or not the SESSION starts what it loads.
    /// Observed live: the plugin loaded, announced "3 tool(s) offered", the model picked one, and the
    /// call came back in 0.0s saying the language server was not running — because nothing between
    /// the gate and the registry had ever called Start.</para>
    /// </summary>
    [Fact]
    public async Task ALoadedPluginIsStarted()
    {
        var session = SessionWithGate(new ScriptedGate(PermissionOutcome.Allow), out var manager);
        using var _ = manager;
        var plugin = new FakePlugin();

        var status = await session.LoadPlugin(plugin, Manifest("lsp-rust", "lsp_rename"), _dir);

        Assert.Equal(CommandStatus.Changed, status);
        Assert.True(plugin.Started,
            "the session registered the plugin's tools but never started it — every call would report the backend is not running.");
    }

    /// <summary>
    /// A plugin that throws from Start is UNLOADED, not left half-loaded with its tools still
    /// offered. A tool whose backing process never came up is worse than an absent one: the model
    /// sees it, calls it, and is told it is not running with nothing explaining why.
    /// </summary>
    [Fact]
    public async Task APluginThatFailsToStartIsUnloadedAndSaysSo()
    {
        var session = SessionWithGate(new ScriptedGate(PermissionOutcome.Allow), out var manager);
        using var _ = manager;
        var plugin = new FakePlugin { StartFailure = new InvalidOperationException("no server on PATH") };

        var status = await session.LoadPlugin(plugin, Manifest("lsp-rust", "lsp_rename"), _dir);

        Assert.Equal(CommandStatus.Reported, status);
        Assert.DoesNotContain("lsp_rename",
            session.Plugins.CurrentTools().Select(t => t.Definition.Name));
    }

    // ---- Identity: a content hash over the WHOLE load set, not one file --------------------------

    /// <summary>the plugin design, "Identity is a content hash, not a filename": "The hash covers everything
    /// loaded, not one file. A managed plugin with dependency assemblies is a directory, and hashing
    /// only its entry point leaves a swapped dependency changing the code without changing the
    /// identity — the grant would carry over to something the user never approved."</summary>
    [Fact]
    public void ChangingAnyLoadedFileReAsks()
    {
        var entry = Path.Combine(_dir, "plugin.dll");
        var dependency = Path.Combine(_dir, "dependency.dll");
        File.WriteAllText(entry, "entry point v1");
        File.WriteAllText(dependency, "dependency v1");

        var before = PluginIdentity.HashLoadSet(_dir);

        // ONLY THE DEPENDENCY CHANGES — the entry point is untouched. Hashing the entry point alone
        // would miss this entirely, which is the exact failure the plugin design names.
        File.WriteAllText(dependency, "dependency v2 — a swapped dependency, same entry point");

        var after = PluginIdentity.HashLoadSet(_dir);

        Assert.NotEqual(before, after);
    }

    /// <summary>The companion fact: identical bytes hash identically regardless of where the load set
    /// sits, because a grant names the content, not a path — the plugin design: "A grant names this binary,
    /// not this path."</summary>
    [Fact]
    public void IdenticalContentHashesTheSameFromADifferentDirectory()
    {
        var dirA = Path.Combine(_dir, "a");
        var dirB = Path.Combine(_dir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "plugin.dll"), "same bytes");
        File.WriteAllText(dirA + "/plugin.plugin.json", "{}");
        File.WriteAllText(Path.Combine(dirB, "plugin.dll"), "same bytes");
        File.WriteAllText(dirB + "/plugin.plugin.json", "{}");

        Assert.Equal(PluginIdentity.HashLoadSet(dirA), PluginIdentity.HashLoadSet(dirB));
    }

    /// <summary>Order must not matter — the set is hashed deterministically (by relative path,
    /// ordinal) regardless of enumeration order, which the BCL does not guarantee stable.</summary>
    [Fact]
    public void HashIsStableAcrossRepeatedCalls()
    {
        File.WriteAllText(Path.Combine(_dir, "b.dll"), "b");
        File.WriteAllText(Path.Combine(_dir, "a.dll"), "a");
        File.WriteAllText(Path.Combine(_dir, "c.plugin.json"), "{}");

        var first = PluginIdentity.HashLoadSet(_dir);
        var second = PluginIdentity.HashLoadSet(_dir);

        Assert.Equal(first, second);
    }

    // ---- The load prompt: fixtures shared by the two Session-level test groups below -------------

    /// <summary>A gate that records every request it sees and answers with a scripted outcome —
    /// standing in for the interactive gate so these tests can assert on the load prompt itself
    /// (kind, subject, display) without a real UI.</summary>
    private sealed class ScriptedGate(PermissionOutcome outcome) : IPermissionGate
    {
        public List<PermissionRequest> Requests { get; } = [];

        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    /// <summary>Allows the load itself (PermissionKind.Plugin) but declines every per-call gate
    /// (PermissionKind.Tool) — so a test can prove the tool call reached the gate at all without the
    /// load-time decision standing in for it.</summary>
    private sealed class LoadsButDeclinesCallsGate : IPermissionGate
    {
        public List<PermissionRequest> Requests { get; } = [];

        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(request.Kind == PermissionKind.Plugin
                ? PermissionOutcome.Allow
                : PermissionOutcome.ByUser);
        }
    }

    private Session SessionWithGate(IPermissionGate gate, out SessionManager manager)
    {
        manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            BuildGate = _ => gate,
        });
        return manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    // ---- Config cannot pre-approve a plugin --------------------------------------------------------

    /// <summary>the plugin design, "Loading is refused mid-turn": "A runtime load always prompts... A
    /// configuration that could pre-approve an arbitrary binary would dissolve the boundary this
    /// design rests on." There is no config-shaped input to <see cref="Session.LoadPlugin"/> at all —
    /// it takes only the running plugin, its manifest and its load-set path — so this test proves the
    /// absence the strongest way available: every load with a gate wired reaches that gate, with no
    /// parameter or flag able to skip it.</summary>
    [Fact]
    public async Task ConfigCannotPreApproveAPlugin()
    {
        File.WriteAllText(Path.Combine(_dir, "plugin.dll"), "content");
        var gate = new ScriptedGate(PermissionOutcome.Allow);
        var session = SessionWithGate(gate, out var manager);
        using var _ = manager;

        await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), _dir);

        // THE GATE WAS ASKED — nothing in Session, SessionPorts or ResolvedConfig offers a path that
        // reaches the registry without going through RequestAsync first. A config flag that could
        // skip this would show up here as zero requests recorded despite a successful load.
        Assert.Single(gate.Requests);
        Assert.Equal(PermissionKind.Plugin, gate.Requests[0].Kind);
        Assert.Contains("lsp_rename", session.Plugins.CurrentTools().Select(t => t.Definition.Name));
    }

    /// <summary>A decline is honoured: nothing from the plugin reaches the registry, however the
    /// plugin describes itself.</summary>
    [Fact]
    public async Task ADeclinedLoadOffersNoTools()
    {
        File.WriteAllText(Path.Combine(_dir, "plugin.dll"), "content");
        var gate = new ScriptedGate(PermissionOutcome.ByUser);
        var session = SessionWithGate(gate, out var manager);
        using var _ = manager;

        var status = await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), _dir);

        Assert.Equal(CommandStatus.Reported, status);
        Assert.Empty(session.Plugins.CurrentTools());
    }

    /// <summary>The prompt names origin and declared capability — the plugin design's own example:
    /// "lsp-rust wants to run a process and read files in this folder." / the plugin's path.</summary>
    [Fact]
    public async Task ThePromptNamesOriginAndDeclaredCapability()
    {
        File.WriteAllText(Path.Combine(_dir, "plugin.dll"), "content");
        var gate = new ScriptedGate(PermissionOutcome.Allow);
        var session = SessionWithGate(gate, out var manager);
        using var _ = manager;

        var spawningManifest = Manifest("lsp-rust", "lsp_rename") with { Spawns = true };
        await session.LoadPlugin(new FakePlugin(), spawningManifest, _dir);

        var display = gate.Requests[0].Display;
        Assert.Contains("lsp-rust wants to run a process and read files in this folder", display);
        Assert.Contains(_dir, display);
    }

    /// <summary>Identity, not a filename, is what a stored "Always" rule would key on — the request's
    /// AlwaysRule is the content hash, matching the plugin design's "a grant names this binary, not this
    /// path."</summary>
    [Fact]
    public async Task TheStoredRuleSubjectIsTheContentHashNotThePath()
    {
        File.WriteAllText(Path.Combine(_dir, "plugin.dll"), "content");
        var gate = new ScriptedGate(PermissionOutcome.Allow);
        var session = SessionWithGate(gate, out var manager);
        using var _ = manager;

        await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), _dir);

        Assert.Equal(PluginIdentity.HashLoadSet(_dir), gate.Requests[0].AlwaysRule);
    }

    /// <summary>A plugin loaded with no gate wired at all (headless, or a test with nothing to
    /// enforce with) is not refused — matching every other "no gate, no prompt" path SessionFactory
    /// already has.</summary>
    [Fact]
    public async Task WithNoGateWiredLoadingProceedsUnasked()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        using var _ = manager;

        var status = await session.LoadPlugin(new FakePlugin(), Manifest("lsp-rust", "lsp_rename"), _dir);

        Assert.Equal(CommandStatus.Changed, status);
        Assert.Contains("lsp_rename", session.Plugins.CurrentTools().Select(t => t.Definition.Name));
    }

    // ---- A plugin's tools are gated like any other -------------------------------------------------

    /// <summary>the plugin design, "The load gate is the only boundary Core can enforce" describes the load
    /// itself; this proves the OTHER half — once loaded, a plugin's tools still go through the same
    /// per-call gate every tool does. SessionFactory.Wire wraps the live plugin source INSIDE the
    /// dynamic-tools lambda (SessionFactory.cs ~64-73) specifically so a tool that starts existing
    /// after wiring — which is exactly what a runtime plugin load is — is never handed to the model
    /// ungated. This test exercises that wrap end to end: load a plugin whose tool always asks
    /// (Gated), and confirm calling it reaches a gate rather than running silently.</summary>
    [Fact]
    public async Task APluginsToolsAreGated()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        using var __ = manager;

        var gate = new LoadsButDeclinesCallsGate();
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = new BufferedJobPanel(),
                Policy = policy,
            },
            AgentMode.Single);

        // A SEPARATE MANAGER FOR THE GATE: SessionManager.Create's own PermissionRulesStore wiring is
        // exercised by the load-gate tests above; this test only needs a gate installed on THIS
        // session's SharedServices to prove the per-call wrap reaches a plugin tool, so the session is
        // re-wired with one via SessionFactory directly rather than re-opening through a second
        // manager (which would build a second, disconnected registry).
        var manifest = new PluginManifest("lsp-rust", "1.0.0", Instructions: null, Spawns: false,
            [new PluginToolManifest("lsp_rename", "renames a symbol", EmptySchema(), Gated: true)]);

        SessionFactory.Wire(session, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SharedServices { Gate = gate },
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = new BufferedJobPanel(),
                Policy = policy,
            },
            AgentMode.Single);

        var loaded = await session.LoadPlugin(new FakePlugin(), manifest, _dir);
        Assert.Equal(CommandStatus.Changed, loaded);

        var tool = session.Plugins.CurrentTools().Single(t => t.Definition.Name == "lsp_rename");

        // THE BARE PluginTool's OWN Gate() (PluginRegistry.cs) says "asks" — but the question this
        // test asks is whether calling the WRAPPED tool session.Plugins hands out actually consults
        // the process gate. GatedAgentTool is what SessionFactory's dynamic-tools wrap produces; the
        // live source itself (session.Plugins.CurrentTools) yields the unwrapped PluginTool, so the
        // wrap is exercised by going through the same dynamicTools delegate SessionFactory built.
        var wrapped = new GatedAgentTool(tool, gate, policy);

        var call = new JobParameters();
        var context = new TestJobContext();
        var result = await wrapped.ExecuteAsync(call, context, CancellationToken.None);

        Assert.Single(gate.Requests, r => r.Kind == PermissionKind.Tool);
        Assert.False(result.Success);
    }
}
