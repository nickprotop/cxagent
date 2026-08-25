using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Tests against the real csharp-lsp plugin — most of them without a language server at all, since
/// Load's own manifest match and the settings-reading logic (which server, which args) do not need
/// one. The end-to-end tests that DO need a real server are separate <see cref="Fact"/>s marked
/// Skip, in the same style as <c>HeadlessSessionTests.AgainstLocalLlamaCpp</c> — a language server
/// takes real seconds to index a workspace, and this suite runs in ~7s.
/// </summary>
public class CxagentLspPluginTests
{
    /// <summary>Records what the plugin logged — the plugin's only way of telling a user something,
    /// so a test asserting it said something has to keep the lines.</summary>
    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(string message) => Messages.Add(message);
    }

    /// <summary>
    /// The plugin, loaded from disk THE WAY PRODUCTION LOADS IT rather than constructed.
    ///
    /// <para>THIS PROJECT DOES NOT REFERENCE THE PLUGIN'S TYPES — its ProjectReference carries
    /// ReferenceOutputAssembly="false", so the plugin is built and sits beside these tests without
    /// the core suite compiling against any particular plugin. That is what stops the next plugin
    /// arriving as a second reference until this project is a plugin registry.</para>
    ///
    /// <para>The cost is that everything here goes through <see cref="IPlugin"/>, which is also the
    /// benefit: a test that cannot reach past the interface is a test of the contract a plugin
    /// actually ships.</para>
    /// </summary>
    private static async Task<IPlugin> LoadPluginAsync(FakeContext context)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "csharp-lsp.dll");
        Assert.True(File.Exists(dll),
            $"csharp-lsp.dll is not beside the tests at '{dll}' — the ProjectReference that builds it is missing.");

        var result = await ManagedPluginLoader.Load(dll, context, CancellationToken.None);
        var loaded = Assert.IsType<ManagedPluginLoadResult.Loaded>(result);
        return loaded.Instance;
    }

    private sealed class FakeContext(string workingDirectory, object settings, int hostContract = -1)
        : IPluginContext
    {
        public string WorkingDirectory { get; } = workingDirectory;
        public JsonElement Settings { get; } = JsonSerializer.SerializeToElement(settings);
        public int HostContract { get; } = hostContract < 0 ? PluginContract.Version : hostContract;
        public string HostVersion => PluginContract.HostVersion;
        public IPluginLogger Logger { get; } = new FakeLogger();
        public CancellationToken Lifetime { get; } = CancellationToken.None;
        public List<int> RegisteredPids { get; } = [];

        /// <summary>What the plugin logged, for a test that asserts it announced something.</summary>
        public List<string> Logged => ((FakeLogger)Logger).Messages;
        public void RegisterChildProcess(int processId) => RegisteredPids.Add(processId);
    }

    // ---- Load: the manifest returned matches the sidecar --------------------------------------

    [Fact]
    public async Task LoadReturnsTheSidecarManifest()
    {
        var plugin = await LoadPluginAsync(new FakeContext(".", new { server = "csharp-ls" }));
        var manifest = await plugin.Load(new FakeContext(".", new { server = "csharp-ls" }), CancellationToken.None);

        Assert.Equal("csharp-lsp", manifest.Name);
        Assert.True(manifest.Spawns);
        Assert.Equal(["csharp_definition", "csharp_references", "csharp_diagnostics"],
            manifest.Tools.Select(t => t.Name).ToArray());
    }

    /// <summary>
    /// A HOST TOO OLD TO REFUSE THIS PLUGIN IS REFUSED BY IT. An older cxagent never heard of
    /// <c>"gated": "dynamic"</c>, so it reads the sidecar with its own rules and takes the value for
    /// false — offering these three tools UNGATED. Nothing fails; the gate simply is not there. The
    /// host cannot catch that, so the plugin does.
    /// </summary>
    [Fact]
    public async Task AHostBelowContract2IsRefusedByThePluginItself()
    {
        // 1, NOT 0: both take the same branch, but 1 is the case that actually shipped — a cxagent
        // that knows plugins and predates per-call gating. 0 only ever means "too old to have the
        // property at all", which the same comparison covers.
        var dll = Path.Combine(AppContext.BaseDirectory, "csharp-lsp.dll");
        var context = new FakeContext(".", new { server = "csharp-ls" }, hostContract: 1);

        var result = await ManagedPluginLoader.Load(dll, context, CancellationToken.None);

        var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
        Assert.Contains("contract 2", failed.Reason);
    }

    // ---- Gate: a read outside the workspace asks; one inside does not -------------------------

    /// <summary>
    /// THE CASE A BOOLEAN CANNOT EXPRESS. Every tool here reads, so gating them all would ask on
    /// every symbol lookup — dozens per turn — and gating none would read any .cs file on the disk
    /// unasked. The argument is what separates the two.
    /// </summary>
    [Fact]
    public async Task AReadInsideTheWorkspaceIsNotGated()
    {
        var dir = Directory.CreateTempSubdirectory("lsp-gate-in-").FullName;
        try
        {
            var plugin = await LoadPluginAsync(new FakeContext(dir, new { server = "csharp-ls" }));
            var source = Assert.IsAssignableFrom<IPluginGateSource>(plugin);

            Assert.Null(source.Gate("csharp_definition", Call("Program.cs")));
            Assert.Null(source.Gate("csharp_definition", Call(Path.Combine(dir, "src", "Program.cs"))));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// BOTH WAYS OUT ARE COVERED. An absolute path was used verbatim and a relative one was joined
    /// without normalising, so ".." walked straight out of the tree — neither is a rootedness
    /// question, both are the same missing containment check.
    /// </summary>
    [Theory]
    [InlineData("/etc/passwd.cs")]
    [InlineData("../../elsewhere/Secrets.cs")]
    public async Task AReadOutsideTheWorkspaceAsksAndNamesTheFile(string file)
    {
        var dir = Directory.CreateTempSubdirectory("lsp-gate-out-").FullName;
        try
        {
            var plugin = await LoadPluginAsync(new FakeContext(dir, new { server = "csharp-ls" }));
            var source = Assert.IsAssignableFrom<IPluginGateSource>(plugin);

            var gate = source.Gate("csharp_definition", Call(file));

            Assert.NotNull(gate);
            Assert.Contains("outside", gate.Display);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A CALL WITH NO USABLE PATH IS NOT THE GATE'S TO REFUSE. Invoke already answers a missing or
    /// wrong-typed argument with a message naming what was wrong; a gate that asked here would put
    /// a permission prompt in front of an error the user can do nothing about.
    /// </summary>
    [Fact]
    public async Task ACallWithNoFileArgumentIsNotGated()
    {
        var dir = Directory.CreateTempSubdirectory("lsp-gate-none-").FullName;
        try
        {
            var plugin = await LoadPluginAsync(new FakeContext(dir, new { server = "csharp-ls" }));
            var source = Assert.IsAssignableFrom<IPluginGateSource>(plugin);

            Assert.Null(source.Gate("csharp_definition", new JobParameters()));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static JobParameters Call(string file) =>
        new(new Dictionary<string, object?> { ["file"] = file });

    // ---- Start: reads server/args from settings, never hardcodes either -----------------------

    /// <summary>
    /// No <c>server</c> setting means csharp-ls, not a refusal — and the plugin SAYS so.
    ///
    /// <para>A DEFAULT EXISTS BECAUSE <c>/plugin load</c> CARRIES NO SETTINGS unless the user types
    /// them: a plugin that required one could be tried only by editing config first, which is the
    /// opposite of what that command is for. csharp-ls is the default because it is pure LSP over
    /// stdio and needs no flags — OmniSharp speaks its own protocol without <c>-lsp</c>, so
    /// defaulting to it would fail in a way that reads as the plugin being broken.</para>
    ///
    /// <para>ASSERTS THE LOG, NOT THE PROCESS. Starting a real server here would make this test need
    /// csharp-ls installed; what this pins is that the choice was made and announced, which is the
    /// part a user depends on when a session drives a server they never named.</para>
    /// </summary>
    [Fact]
    public async Task StartWithoutAServerSettingUsesCsharpLsAndSaysSo()
    {
        var context = new FakeContext(".", new { });
        var plugin = await LoadPluginAsync(context);

        // The start itself fails without csharp-ls on PATH (or on the fake working directory), and
        // that is not what this test is about — the log line is written before any of that.
        try { await plugin.Start(CancellationToken.None); } catch { /* see above */ }

        Assert.Contains(context.Logged, m => m.Contains("csharp-ls") && m.Contains("no 'server'"));
    }

    [Fact]
    public async Task InvokeBeforeStartReportsTheServerIsNotRunningRatherThanThrowing()
    {
        var plugin = await LoadPluginAsync(new FakeContext(".", new { server = "csharp-ls" }));

        // A CALL THAT PASSES EVERY EARLIER CHECK, so the only thing left to fail on is the server.
        // The file must exist: resolving the argument verifies that before the server is consulted,
        // which is deliberate — a bad path should not need a running server to be reported.
        var real = Path.Combine(Path.GetTempPath(), $"cxagent-lsp-{Guid.NewGuid():N}.cs");
        File.WriteAllText(real, "class C { }");
        try
        {
            var result = await plugin.Invoke("csharp_definition",
                new JobParameters(new Dictionary<string, object?>
                    { ["file"] = real, ["line"] = 1, ["character"] = 1 }),
                Fake.Job(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("not running", result.ErrorMessage);
        }
        finally { File.Delete(real); }
    }

    /// <summary>
    /// A file this plugin does not serve is REFUSED, naming the tool and what to do instead.
    ///
    /// <para>AN EMPTY RESULT WOULD BE WORSE THAN AN ERROR. A language server handed a Go file
    /// returns no locations, and "no locations" reads to the model as "nothing found here" — so it
    /// explains the silence rather than reaching for a tool that could answer. Observed with this
    /// exact plugin: an empty result produced an invented account of why the lookup failed.</para>
    ///
    /// <para>NAMES THE TOOL, NOT THE PLUGIN. The model sees a flat list of tools and has no concept
    /// of a plugin, so a message about "the csharp-lsp plugin" names something it cannot act on.</para>
    /// </summary>
    [Fact]
    public async Task AFileItDoesNotServeIsRefusedRatherThanAnsweredEmptily()
    {
        // AN ABSOLUTE WORKING DIRECTORY, as every real session has — Session hands the folder it
        // opened. A relative one makes ResolvePath produce a relative path, which is not a valid URI.
        // NO Start() — see the extension test below. The refusal happens before the client is
        // touched, which is also the right ordering: a wrong file should not need a running server
        // to be told it is wrong.
        var plugin = await LoadPluginAsync(new FakeContext(Path.GetTempPath(), new { server = "csharp-ls" }));

        var result = await plugin.Invoke("csharp_definition",
            new JobParameters(new Dictionary<string, object?>
                { ["file"] = "main.go", ["line"] = 1, ["character"] = 1 }),
            Fake.Job(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("csharp_definition", result.ErrorMessage);
        Assert.Contains("main.go", result.ErrorMessage);
    }

    /// <summary>The four served extensions get past the check — a refusal that also blocked C# would
    /// pass the test above while breaking the plugin entirely.</summary>
    [Theory]
    [InlineData("Foo.cs")]
    [InlineData("Foo.csx")]
    [InlineData("Foo.razor")]
    [InlineData("Foo.cshtml")]
    public async Task TheServedExtensionsAreNotRefused(string file)
    {
        // NO Start(), DELIBERATELY. Starting spawns a real language server, and this test asserts
        // something decided BEFORE any server contact — that the extension check let the file
        // through. Starting would make it need csharp-ls installed, which is how it passed here and
        // failed on CI where it is not.
        var plugin = await LoadPluginAsync(new FakeContext(Path.GetTempPath(), new { server = "csharp-ls" }));

        var result = await plugin.Invoke("csharp_definition",
            new JobParameters(new Dictionary<string, object?>
                { ["file"] = file, ["line"] = 1, ["character"] = 1 }),
            Fake.Job(), CancellationToken.None);

        // IT FAILS FOR A DIFFERENT REASON, and that is the assertion. With no server started the
        // call cannot succeed — what matters is that it got PAST the extension check, so the failure
        // is about the server rather than about the file being one this tool does not serve.
        Assert.False(result.Success);
        Assert.DoesNotContain("is not one of those", result.ErrorMessage);
    }

    // ---- Invoke: an unknown tool name is this plugin's own bug, not a normal failure ----------

    [Fact]
    public async Task InvokeWithAnUnknownToolNameThrows()
    {
        var plugin = await LoadPluginAsync(new FakeContext(".", new { server = "csharp-ls" }));

        // No Start() call, so _client is null — but an unrecognised tool name must fail with the
        // "unknown tool" reason, not the "not running" one, or a caller cannot tell its own bug
        // (routing a name this manifest never declared) from an ordinary startup-order mistake.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.Invoke("lsp_rename", new JobParameters(new()), Fake.Job(), CancellationToken.None));
    }

    // ---- LspClient.ParseLocations: both response shapes the two servers actually send ---------

    [Fact]
    public void ParseLocationsAcceptsALocationLinkArray()
    {
        var json = """
        [
          { "targetUri": "file:///a.cs", "targetRange": { "start": {"line": 4, "character": 2}, "end": {"line": 4, "character": 10} },
            "targetSelectionRange": { "start": {"line": 4, "character": 2}, "end": {"line": 4, "character": 10} } }
        ]
        """;
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        var locations = InvokeParseLocations(node);

        Assert.Single(locations);
        Assert.Equal("file:///a.cs", Read(locations[0], "UriOrPath"));
        Assert.Equal(4, Read(locations[0], "Start.Line"));
        Assert.Equal(2, Read(locations[0], "Start.Character"));
    }

    [Fact]
    public void ParseLocationsAcceptsAPlainLocationObject()
    {
        var json = """
        { "uri": "file:///b.cs", "range": { "start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 5} } }
        """;
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        var locations = InvokeParseLocations(node);

        Assert.Single(locations);
        Assert.Equal("file:///b.cs", Read(locations[0], "UriOrPath"));
    }

    [Fact]
    public void ParseLocationsOnNullResultIsEmptyNotAnError()
    {
        var locations = InvokeParseLocations(null);
        Assert.Empty(locations);
    }

    /// <summary>
    /// <c>LspClient.ParseLocations</c>, reached by reflection over the plugin's own assembly.
    ///
    /// <para>ALREADY REFLECTION BEFORE THIS PROJECT STOPPED REFERENCING THE PLUGIN — the method is
    /// private, so a compile-time reference never helped reach it. What changed is where the TYPE
    /// comes from: the assembly loaded from disk, rather than a name the compiler resolved.</para>
    ///
    /// <para>WORTH TESTING DESPITE BEING PRIVATE: it decodes the two different shapes real servers
    /// answer with — csharp-ls sends LocationLink[], OmniSharp a plain Location — and getting that
    /// wrong yields an empty result rather than an error, which is the failure mode hardest to spot
    /// from outside.</para>
    /// </summary>
    /// <summary>One property of a reflected LspLocation — <c>UriOrPath</c>, or a nested
    /// <c>Start.Line</c> via a dotted path. Keeps the assertions below reading like assertions
    /// rather than like reflection.</summary>
    private static object? Read(object target, string path)
    {
        var current = target;
        foreach (var part in path.Split('.'))
        {
            current = current!.GetType().GetProperty(part)!.GetValue(current);
        }
        return current;
    }

    private static IReadOnlyList<object> InvokeParseLocations(System.Text.Json.Nodes.JsonNode? node)
    {
        var assembly = System.Reflection.Assembly.LoadFrom(
            Path.Combine(AppContext.BaseDirectory, "csharp-lsp.dll"));
        var type = assembly.GetType("CxAgent.Plugins.Lsp.LspClient")
                   ?? throw new InvalidOperationException("LspClient not found in the plugin assembly.");

        var method = type.GetMethod("ParseLocations",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return ((System.Collections.IEnumerable)method.Invoke(null, [node])!).Cast<object>().ToList();
    }

    private static class Fake
    {
        public static IJobContext Job() => new FakeJobContext();
    }

    private sealed class FakeJobContext : IJobContext
    {
        public void ReportProgress(double percent, string? message = null) { }
        public void WorkStarting() { }
        public void ReportPermissionWait(bool waiting) { }
        public void ReportReviewing(bool reviewing) { }
        public string? Requester => null;
        public string? WorkingDirectory => null;
        public string? DecidedBy { get; set; }
        public void Log(string line) { }
        public void Log(JobLogLevel level, string line) { }
        public void ReportResources(ResourceSnapshot snapshot) { }
        public void ReportToolCall(string toolName, string summary) { }
        public void ReportTextDelta(string delta) { }
        public IReadOnlyDictionary<string, JobResult> CompletedJobOutputs { get; } = new Dictionary<string, JobResult>();
        public IReadOnlyDictionary<string, string> CompletedJobNames { get; } = new Dictionary<string, string>();
    }
}
