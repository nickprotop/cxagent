using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A PLUGIN IS ASKED ABOUT BEFORE ANY OF ITS CODE RUNS. Loading an assembly constructs its plugin and runs
/// its Load; a question asked after that is a question about code that has already run as the user — and a
/// repository that ships one in <c>.cxagent/plugins</c> runs it just by being opened. So the load set is
/// hashed and the question built from its sidecar, a file read without running anything, and only an
/// approval loads it.
///
/// <para>THE THROWING FIXTURE IS THE PROBE: its Load throws, so "threw from Load" in what the session says
/// is proof its code ran. Declined, that must never appear; approved, it must appear only after the
/// question.</para>
/// </summary>
[Collection("plugin-fixture-sidecar")]
public sealed class PluginAskBeforeLoadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-ask-first-" + Guid.NewGuid().ToString("N"));

    public PluginAskBeforeLoadTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task ADeclinedLoad_RunsNoneOfThePluginsCode()
    {
        var plugin = Drop("cxagent.Tests.PluginFixture.Throwing", "throwing.dll");
        var gate = new RecordingGate(PermissionOutcome.ByUser);
        var session = SessionWithGate(gate, out var manager, out var sink);
        using var owned = manager;

        await session.RunPluginCommand($"load {plugin}", CancellationToken.None);

        Assert.Single(gate.Requests, r => r.Kind == PermissionKind.Plugin);
        Assert.DoesNotContain(sink.Notices, n => n.Contains("threw from Load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnApprovedLoad_IsAskedAboutBeforeItsCodeRuns()
    {
        var plugin = Drop("cxagent.Tests.PluginFixture.Throwing", "throwing.dll");
        var gate = new RecordingGate(PermissionOutcome.Allow);
        var session = SessionWithGate(gate, out var manager, out var sink);
        using var owned = manager;

        await session.RunPluginCommand($"load {plugin}", CancellationToken.None);

        // ASKED, AND THEN RUN: the question came first, and the code ran only because it was approved.
        Assert.Single(gate.Requests, r => r.Kind == PermissionKind.Plugin);
        Assert.Contains(sink.Notices, n => n.Contains("threw from Load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupAsksBeforeItsCodeRuns_AndADeclineRunsNone()
    {
        var plugin = Drop("cxagent.Tests.PluginFixture.Throwing", "throwing.dll");
        var gate = new RecordingGate(PermissionOutcome.ByUser);
        var session = SessionWithGate(gate, out var manager, out _);
        using var owned = manager;
        var said = new List<string>();

        await PluginDiscovery.LoadConfiguredAsync(session,
            new Dictionary<string, PluginConfig> { ["throwing"] = new PluginConfig("throwing.dll") },
            [Path.GetDirectoryName(plugin)!], _dir, said.Add, CancellationToken.None);

        Assert.Single(gate.Requests, r => r.Kind == PermissionKind.Plugin);
        Assert.DoesNotContain(said, n => n.Contains("threw from Load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadPluginFrom_AsksWithWhatTheSidecarDeclares_ThenLoads()
    {
        var plugin = Drop("cxagent.Tests.PluginFixture", "well-formed.dll");
        var gate = new RecordingGate(PermissionOutcome.Allow);
        var session = SessionWithGate(gate, out var manager, out _);
        using var owned = manager;

        var status = await session.LoadPluginFrom(plugin, CancellationToken.None);

        Assert.Equal(CommandStatus.Changed, status);
        var asked = Assert.Single(gate.Requests, r => r.Kind == PermissionKind.Plugin);
        Assert.Contains("It adds 1 tool.", asked.Display);
        Assert.Contains("wf_tool", session.Plugins.CurrentTools().Select(t => t.Definition.Name));
    }

    /// <summary>
    /// AN INSTANCE ALREADY LOADED IS TOO LATE TO ASK ABOUT. Under a gate, LoadPlugin takes only a load set
    /// approved before it was loaded — so a host that loads first and registers after is refused, told
    /// which call asks first, and never asked a question whose answer could not matter.
    /// </summary>
    [Fact]
    public async Task AnInstanceLoadedBeforeItWasApproved_IsRefused_WithoutAsking()
    {
        var plugin = Drop("cxagent.Tests.PluginFixture", "well-formed.dll");
        var gate = new RecordingGate(PermissionOutcome.Allow);
        var session = SessionWithGate(gate, out var manager, out var sink);
        using var owned = manager;
        var loaded = (ManagedPluginLoadResult.Loaded)await ManagedPluginLoader.Load(plugin, Context(), CancellationToken.None);

        var status = await session.LoadPlugin(loaded.Instance, loaded.Manifest, Path.GetDirectoryName(plugin)!);

        Assert.NotEqual(CommandStatus.Changed, status);
        Assert.Empty(gate.Requests);
        Assert.Empty(session.Plugins.CurrentTools());
        Assert.Contains(sink.Notices, n => n.Contains("LoadPluginFrom", StringComparison.Ordinal));
    }

    /// <summary>A LOAD SET CHANGED AFTER ITS APPROVAL is not the one approved: the hash is taken again
    /// when it registers, and a difference refuses it.</summary>
    [Fact]
    public async Task ALoadSetChangedAfterItsApproval_IsRefused()
    {
        var plugin = Drop("cxagent.Tests.PluginFixture", "well-formed.dll");
        var folder = Path.GetDirectoryName(plugin)!;
        var gate = new RecordingGate(PermissionOutcome.Allow);
        var session = SessionWithGate(gate, out var manager, out var sink);
        using var owned = manager;

        Assert.True(await session.ApproveLoadAsync(plugin, folder, CancellationToken.None));
        var loaded = (ManagedPluginLoadResult.Loaded)await ManagedPluginLoader.Load(plugin, Context(), CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "planted after the question");

        var status = await session.LoadPlugin(loaded.Instance, loaded.Manifest, folder);

        Assert.NotEqual(CommandStatus.Changed, status);
        Assert.Empty(session.Plugins.CurrentTools());
        Assert.Contains(sink.Notices, n => n.Contains("changed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// YOUR PLUGINS BEFORE THE REPOSITORY'S. A cloned project's <c>.cxagent/plugins</c> holding a file named like
    /// one you configured would otherwise load in its place; the global folder is searched first, so it cannot
    /// stand in for a plugin you installed. One only the project has is still found. Configured paths keep the
    /// order you wrote them in, ahead of both.
    /// </summary>
    [Fact]
    public void TheGlobalFolderIsSearchedBeforeTheProjects()
    {
        var project = Path.Combine(_dir, "proj");
        var config = Path.Combine(_dir, "config");
        foreach (var folders in new[]
        {
            PluginResolver.SearchFolders(["/opt/mine"], project, config),
            PluginDiscovery.SearchFolders(["/opt/mine"], project, config),
        })
            Assert.Equal(["/opt/mine", Path.Combine(config, "plugins"), Path.Combine(project, ".cxagent/plugins")], folders);

        foreach (var where in new[] { Path.Combine(config, "plugins"), Path.Combine(project, ".cxagent", "plugins") })
        {
            Directory.CreateDirectory(where);
            File.WriteAllText(Path.Combine(where, "calc.dll"), where);
        }
        Assert.Equal(Path.Combine(config, "plugins"),
            PluginDiscovery.FindLoadSetDirectory("calc.dll", PluginDiscovery.SearchFolders([], project, config)));
    }

    // A FRESH FOLDER PER TEST holding one fixture and its sidecar, so the hash is the test's own.
    private string Drop(string fixture, string fileName)
    {
        var folder = Path.Combine(_dir, "plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(AppContext.BaseDirectory, fixture + ".dll");
        var target = Path.Combine(folder, fileName);
        File.Copy(dll, target);
        File.Copy(Path.ChangeExtension(dll, null) + ".plugin.json",
            Path.Combine(folder, Path.GetFileNameWithoutExtension(fileName) + ".plugin.json"));
        return target;
    }

    private PluginResolver.RuntimeContext Context() =>
        new(new PluginResolver.PluginRuntime(_dir, System.Text.Json.JsonDocument.Parse("{}").RootElement,
            _ => { }, new ChildProcessStore(_dir), "well-formed", SessionId: null, Client: null));

    private Session SessionWithGate(IPermissionGate gate, out SessionManager manager, out BufferedChatSink sink)
    {
        manager = SessionManager.Create(new ProcessSetup { Paths = new AppPaths(_dir), BuildGate = _ => gate });
        sink = new BufferedChatSink();
        return manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
    }

    private sealed class RecordingGate(PermissionOutcome outcome) : IPermissionGate
    {
        public List<PermissionRequest> Requests { get; } = [];

        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }
}
