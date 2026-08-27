using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/plugin</c> — the in-app path to runtime loading. Everything the design built (the mutable
/// registry, the turn-boundary refusal, the four-step unwire, the load gate) was reachable only
/// through <c>config.json</c> at startup until this command; these tests hold Task 7b's own
/// obligations: both forms load, an unresolvable target is reported rather than crashing or silently
/// doing nothing, a disabled plugin refuses and names its escape hatch, and <c>--once</c> is that
/// hatch and nothing more.
///
/// <para>A REAL ASSEMBLY, NOT A FAKE <see cref="CxAgent.Core.Plugins.IPlugin"/>. Unlike
/// <c>PluginRegistryTests</c> and <c>PluginLoadGateTests</c>, <c>/plugin load</c>'s own job is
/// resolving a name or path to a file and running <c>ManagedPluginLoader</c> against it — so these
/// tests load <c>cxagent.Tests.PluginFixture.dll</c> ("well-formed", tool "wf_tool") off disk exactly
/// as <see cref="ManagedPluginLoaderTests"/> does, the one fixture project built as a separate
/// assembly for the same reason that test class states: a fixture inside this project would already
/// be loaded as part of the running process.</para>
/// </summary>
public class PluginCommandTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-command-" + Guid.NewGuid().ToString("N"));

    public PluginCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static readonly string FixtureDll =
        Path.Combine(AppContext.BaseDirectory, "cxagent.Tests.PluginFixture.dll");

    /// <summary>Copies the well-formed fixture (and its sidecar) into <paramref name="into"/>, under
    /// <paramref name="fileName"/> — so a test can control what name it is loaded BY, independent of
    /// the fixture project's own output name.</summary>
    private static void DropFixture(string into, string fileName)
    {
        // UNDER .cxagent/plugins, matching PluginResolver.SearchFolders'
        // project-local default — dropping straight into the project directory is not on that
        // search path, and the path-form target below still resolves relative to the project
        // directory either way.
        into = Path.Combine(into, ".cxagent", "plugins");
        Directory.CreateDirectory(into);
        var sidecarSource = Path.ChangeExtension(FixtureDll, null) + ".plugin.json";
        File.Copy(FixtureDll, Path.Combine(into, fileName), overwrite: true);
        File.Copy(sidecarSource,
            Path.Combine(into, Path.GetFileNameWithoutExtension(fileName) + ".plugin.json"), overwrite: true);
    }

    /// <summary>A session wired with the given plugins, over <c>_dir</c> as its working directory —
    /// which is <c>PluginResolver.SearchFolders</c>'s project-directory, so a fixture dropped under
    /// <c>.cxagent/plugins</c> (see <see cref="DropFixture"/>) is found with no <c>pluginPaths</c>
    /// entry at all.</summary>
    private Session Wired(out SessionManager manager, out BufferedChatSink sink,
        IReadOnlyDictionary<string, PluginConfig>? plugins = null)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        sink = new BufferedChatSink();
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider())
            .WithPlugins(plugins ?? new Dictionary<string, PluginConfig>());

        return manager.Open(_dir, resolution,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    // ---- Refused mid-turn, inherited from LoadPlugin ---------------------------------------------

    /// <summary>REFUSED MID-TURN, inherited rather than reimplemented: LoadPlugin already refuses
    /// while busy and says so, for Core's own reason — the tool list is fixed once a request
    /// begins.</summary>
    [Fact]
    public async Task LoadingDuringATurnIsRefused()
    {
        DropFixture(_dir, "well-formed.dll");

        var arrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var manager = SessionManager.Create(new AppPaths(_dir));
        var sink = new BufferedChatSink();
        var resolution = ResolvedConfig.ForTesting(new BlockingProvider(arrived, release.Task))
            .WithCatalog(ProviderCatalog.Empty);
        var session = manager.Open(_dir, resolution,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        using var _ = manager;

        var started = session.Submit("go");
        Assert.IsType<Session.SubmitOutcome.Started>(started);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsBusy);

        var status = await session.RunPluginCommand("load well-formed.dll", CancellationToken.None);

        Assert.Equal(CommandStatus.Refused, status);
        Assert.Empty(session.Plugins.CurrentTools());
        Assert.Contains(sink.Notices, t => t.Contains("turn is running", StringComparison.OrdinalIgnoreCase));

        release.SetResult();
        await ((Session.SubmitOutcome.Started)started).Turn.WaitAsync(TimeSpan.FromSeconds(5));
    }

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

    // ---- Both forms load -----------------------------------------------------------------------

    /// <summary>A name from config and a path both load. The path form is what makes developing a
    /// plugin bearable; the gate treats them identically because identity is the content, not the
    /// route by which it was named.</summary>
    [Theory]
    [InlineData("by-name")]
    [InlineData("by-path")]
    public async Task BothFormsLoad(string form)
    {
        DropFixture(_dir, "well-formed.dll");

        var plugins = form == "by-name"
            ? new Dictionary<string, PluginConfig> { ["well-formed"] = new("well-formed.dll") }
            : new Dictionary<string, PluginConfig>();

        var session = Wired(out var manager, out var sink, plugins);
        using var _m = manager;

        var target = form == "by-name" ? "well-formed" : "well-formed.dll";
        var status = await session.RunPluginCommand($"load {target}", CancellationToken.None);

        Assert.Equal(CommandStatus.Changed, status);
        Assert.Contains("wf_tool", session.Plugins.CurrentTools().Select(t => t.Definition.Name));
    }

    // ---- An unknown name or path is reported, not silent or a crash -----------------------------

    /// <summary>A name config does not declare, and no file at that path, is reported — not a
    /// silent no-op, and not an unhandled exception surfacing as a crash.</summary>
    [Fact]
    public async Task AnUnknownNameOrPathIsReported()
    {
        var session = Wired(out var manager, out var sink);
        using var _ = manager;

        var status = await session.RunPluginCommand("load does-not-exist", CancellationToken.None);

        Assert.Equal(CommandStatus.Reported, status);
        Assert.Empty(session.Plugins.CurrentTools());
        Assert.Contains(sink.Notices, t => t.Contains("does-not-exist", StringComparison.Ordinal));
    }

    // ---- A disabled plugin refuses, and names --once --------------------------------------------

    /// <summary>A disabled plugin refuses, and the refusal names --once. A gate whose exception is
    /// undiscoverable is a gate with no exception.</summary>
    [Fact]
    public async Task LoadingADisabledPluginRefusesAndNamesTheFlag()
    {
        DropFixture(_dir, "well-formed.dll");
        var plugins = new Dictionary<string, PluginConfig>
        {
            ["well-formed"] = new("well-formed.dll", Enabled: false),
        };
        var session = Wired(out var manager, out var sink, plugins);
        using var _ = manager;

        var status = await session.RunPluginCommand("load well-formed", CancellationToken.None);

        Assert.Equal(CommandStatus.Reported, status);
        Assert.Empty(session.Plugins.CurrentTools());
        Assert.Contains(sink.Notices, t => t.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            && t.Contains("--once", StringComparison.Ordinal));
    }

    // ---- --once loads a disabled plugin, and STILL ASKS ------------------------------------------

    /// <summary>--once loads it, and STILL ASKS. enabled:false is configuration; the prompt is
    /// approval. Overriding the first must not bypass the second.</summary>
    [Fact]
    public async Task OnceLoadsADisabledPluginAndStillAsks()
    {
        DropFixture(_dir, "well-formed.dll");
        var plugins = new Dictionary<string, PluginConfig>
        {
            ["well-formed"] = new("well-formed.dll", Enabled: false),
        };

        var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            BuildGate = _ => new ScriptedGate(PermissionOutcome.Allow),
        });
        var sink = new BufferedChatSink();
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider())
            .WithPlugins(plugins);
        var session = manager.Open(_dir, resolution,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        using var _ = manager;

        var status = await session.RunPluginCommand("load well-formed --once", CancellationToken.None);

        Assert.Equal(CommandStatus.Changed, status);
        Assert.Contains("wf_tool", session.Plugins.CurrentTools().Select(t => t.Definition.Name));
    }

    private sealed class ScriptedGate(PermissionOutcome outcome) : IPermissionGate
    {
        public List<PermissionRequest> Requests { get; } = [];

        public Task<PermissionOutcome> RequestAsync(
            PermissionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    /// <summary>The gate is still consulted for a --once load — proving the load prompt (approval)
    /// is unaffected by the config override (configuration), rather than only proving the load
    /// succeeded.</summary>
    [Fact]
    public async Task OnceStillReachesTheLoadGate()
    {
        DropFixture(_dir, "well-formed.dll");
        var plugins = new Dictionary<string, PluginConfig>
        {
            ["well-formed"] = new("well-formed.dll", Enabled: false),
        };

        var gate = new ScriptedGate(PermissionOutcome.Allow);
        var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            BuildGate = _ => gate,
        });
        var sink = new BufferedChatSink();
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider())
            .WithPlugins(plugins);
        var session = manager.Open(_dir, resolution,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        using var _ = manager;

        await session.RunPluginCommand("load well-formed --once", CancellationToken.None);

        Assert.Single(gate.Requests, r => r.Kind == PermissionKind.Plugin);
    }

    // ---- --once does not outlive the session ------------------------------------------------------

    /// <summary>And it does not persist: the override is the session's, config is untouched, so a
    /// fresh session sees the plugin disabled again.</summary>
    [Fact]
    public async Task OnceDoesNotOutliveTheSession()
    {
        DropFixture(_dir, "well-formed.dll");
        var plugins = new Dictionary<string, PluginConfig>
        {
            ["well-formed"] = new("well-formed.dll", Enabled: false),
        };

        var manager = SessionManager.Create(new AppPaths(_dir));
        using var disposeManager = manager;

        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider())
            .WithPlugins(plugins);

        var first = manager.Open(_dir, resolution,
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        var firstStatus = await first.RunPluginCommand("load well-formed --once", CancellationToken.None);
        Assert.Equal(CommandStatus.Changed, firstStatus);

        // A FRESH SESSION, reading the SAME config dictionary. Nothing --once did wrote to it —
        // the override lives on the first session's own field — so a second session reads
        // enabled:false exactly as the first one originally did.
        var secondSink = new BufferedChatSink();
        var second = manager.Open(_dir, resolution,
            new SessionPorts { Observer = secondSink, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        var secondStatus = await second.RunPluginCommand("load well-formed", CancellationToken.None);

        Assert.Equal(CommandStatus.Reported, secondStatus);
        Assert.Empty(second.Plugins.CurrentTools());
        Assert.Contains(secondSink.Notices, t => t.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    // ---- The listing shows three states, not two --------------------------------------------------

    [Fact]
    public async Task TheListingNamesAllThreeStates()
    {
        DropFixture(_dir, "well-formed.dll");
        var plugins = new Dictionary<string, PluginConfig>
        {
            ["well-formed"] = new("well-formed.dll"),
            ["turned-off"] = new("nonexistent.dll", Enabled: false),
        };
        var session = Wired(out var manager, out var sink, plugins);
        using var _ = manager;

        await session.RunPluginCommand("load well-formed", CancellationToken.None);
        await session.RunPluginCommand("", CancellationToken.None);

        var listing = sink.Notices.Last();
        Assert.Contains("loaded", listing);
        Assert.Contains("disabled", listing);
    }

    /// <summary>
    /// Settings written after the target are taken VERBATIM from the first brace to the end.
    ///
    /// <para>NOT TOKENISED AND REJOINED. A settings object holds spaces and quotes, so rebuilding it
    /// from words would make the result depend on how the user spaced it — and a value containing a
    /// space would come back changed. The JSON parser downstream is the one thing that decides
    /// whether the block is valid.</para>
    /// </summary>
    [Fact]
    public void LoadTakesInlineSettingsVerbatim()
    {
        var request = Assert.IsType<PluginRequest.Load>(
            PluginCommand.Parse("""load csharp-lsp { "server": "csharp ls", "args": [] }"""));

        Assert.Equal("csharp-lsp", request.Target);
        Assert.Equal("""{ "server": "csharp ls", "args": [] }""", request.Settings);
        Assert.False(request.Once);
    }

    /// <summary>--once and inline settings compose: the flag is read from the words BEFORE the
    /// brace, so it cannot be confused with anything inside the settings object.</summary>
    [Fact]
    public void OnceAndInlineSettingsComposeInEitherOrder()
    {
        var request = Assert.IsType<PluginRequest.Load>(
            PluginCommand.Parse("""load csharp-lsp --once { "server": "x" }"""));

        Assert.Equal("csharp-lsp", request.Target);
        Assert.True(request.Once);
        Assert.Equal("""{ "server": "x" }""", request.Settings);
    }

    /// <summary>A load with no brace has no settings — the ordinary form, unchanged.</summary>
    [Fact]
    public void LoadWithoutSettingsCarriesNone()
    {
        var request = Assert.IsType<PluginRequest.Load>(PluginCommand.Parse("load csharp-lsp.dll"));

        Assert.Equal("csharp-lsp.dll", request.Target);
        Assert.Null(request.Settings);
    }
}
