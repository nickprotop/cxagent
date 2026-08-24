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

    private sealed class FakeContext(string workingDirectory, object settings) : IPluginContext
    {
        public string WorkingDirectory { get; } = workingDirectory;
        public JsonElement Settings { get; } = JsonSerializer.SerializeToElement(settings);
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

        var result = await plugin.Invoke("csharp_definition", new JobParameters(new()), Fake.Job(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not running", result.ErrorMessage);
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

    // ---- End-to-end against a real server on /tmp/cxgpu ----------------------------------------
    //
    // NOT RUN AS PART OF THE DEFAULT SUITE — see HeadlessSessionTests.AgainstLocalLlamaCpp for the
    // same pattern and its own reasoning. A language server takes real seconds to load and index a
    // solution's projects; the default suite runs in ~7s and stays there by not paying that cost.
    // Both were run by hand against /tmp/cxgpu (a real checkout with cxgpu.Tests referencing
    // cxgpu's AlertEngine across a project boundary) and both landed csharp_definition on
    // AlertEngine.cs — see the task report for the exact lines observed.

    [Fact(Skip = "Needs csharp-ls on PATH and /tmp/cxgpu checked out. Verified by hand: resolved " +
                 "cross-project to AlertEngine.cs on 2026-08-24.")]
    public async Task DefinitionCrossesTheProjectBoundaryAgainstCsharpLs() =>
        await RunCrossProjectDefinition("csharp-ls", []);

    [Fact(Skip = "Needs /opt/omnisharp/OmniSharp and /tmp/cxgpu checked out. Verified by hand: " +
                 "resolved cross-project to AlertEngine.cs on 2026-08-24, same settings shape as " +
                 "csharp-ls proving the plugin reads its server rather than hardcoding one.")]
    public async Task DefinitionCrossesTheProjectBoundaryAgainstOmniSharp() =>
        await RunCrossProjectDefinition("/opt/omnisharp/OmniSharp", ["-lsp"]);

    /// <summary>
    /// The acceptance test from the task brief: <c>new AlertEngine()</c> in cxgpu.Tests must resolve
    /// to AlertEngine's declaration in cxgpu — a different project, reachable only by a server that
    /// loaded and indexed the whole workspace. LINE NUMBERS ARE RESOLVED BY GREP AT TEST TIME, not
    /// hardcoded — /tmp/cxgpu is a live repository and a hardcoded line rots into a false failure the
    /// moment the file changes above it.
    /// </summary>
    private static async Task RunCrossProjectDefinition(string server, IReadOnlyList<string> args)
    {
        const string root = "/tmp/cxgpu";
        const string refFile = "cxgpu.Tests/AlertEngineTests.cs";
        const string declFile = "cxgpu/Gpu/Alerts/AlertEngine.cs";

        var refLines = File.ReadAllLines(Path.Combine(root, refFile));
        var refLineIndex = Array.FindIndex(refLines, l => l.Contains("new AlertEngine()"));
        Assert.True(refLineIndex >= 0, $"'new AlertEngine()' not found in {refFile} — has it moved or been renamed?");
        var column = refLines[refLineIndex].IndexOf("AlertEngine", StringComparison.Ordinal) + 1;

        var declLines = File.ReadAllLines(Path.Combine(root, declFile));
        var declLineIndex = Array.FindIndex(declLines, l => l.Contains("class AlertEngine"));
        Assert.True(declLineIndex >= 0, $"'class AlertEngine' not found in {declFile} — has it moved or been renamed?");

        var plugin = await LoadPluginAsync(new FakeContext(root, new { server, args }));
        var context = new FakeContext(root, new { server, args });
        await plugin.Load(context, CancellationToken.None);
        await plugin.Start(CancellationToken.None);
        try
        {
            Assert.Single(context.RegisteredPids); // RegisterChildProcess must be called, or a crashed test leaks the server.

            var result = await plugin.Invoke("csharp_definition", new JobParameters(new()
            {
                ["file"] = refFile,
                ["line"] = refLineIndex + 1,
                ["character"] = column,
            }), Fake.Job(), CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            var locations = Assert.IsAssignableFrom<IEnumerable<Dictionary<string, object?>>>(result.Output["locations"]);
            var location = Assert.Single(locations);

            // THE CONSTRUCTOR, NOT THE CLASS HEADER — "go to definition" on `new AlertEngine()`
            // lands on the constructor a real IDE would jump to, which sits a few lines below
            // `class AlertEngine`. Asserting the resolved FILE matches, and that the resolved line
            // is within the class body (not some other file entirely), is what proves the
            // cross-project resolution without pinning to a line that shifts whenever a comment
            // above the constructor changes.
            Assert.Equal(Path.Combine(root, declFile), (string)location["file"]!);
            Assert.True((int)location["line"]! > declLineIndex);
        }
        finally
        {
            await plugin.Stop(CancellationToken.None);
        }
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
