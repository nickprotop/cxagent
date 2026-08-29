using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The plugin around the clone detector: one tool, no gate, and a report the model can act on.
///
/// <para>LOADED THE WAY PRODUCTION LOADS IT, through <see cref="ManagedPluginLoader"/>, exactly as
/// <see cref="CalculatorPluginTests"/> does — this project's ProjectReference to clone-finder
/// carries <c>ReferenceOutputAssembly="false"</c>, so it does not compile against
/// <c>CloneFinderPlugin</c>'s types, and going through <see cref="IPlugin"/> is what proves the
/// plugin and its sidecar actually load rather than merely compiling.</para>
/// </summary>
public class CloneFinderPluginTests
{
    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(string message) => Messages.Add(message);
    }

    private sealed class StubContext : IPluginContext
    {
        public string WorkingDirectory { get; } = ".";
        public JsonElement Settings { get; } = JsonSerializer.SerializeToElement(new { });
        public int HostContract { get; } = PluginContract.Version;
        public string HostVersion => PluginContract.HostVersionOf(GetType().Assembly);
        public IPluginLogger Logger { get; } = new FakeLogger();
        public CancellationToken Lifetime { get; } = CancellationToken.None;
        public List<int> RegisteredPids { get; } = [];
        public void RegisterChildProcess(int processId) => RegisteredPids.Add(processId);
    }

    private sealed class StubJobContext : IJobContext
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

    private static async Task<IPlugin> LoadPluginAsync()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "clone-finder.dll");
        Assert.True(File.Exists(dll),
            $"clone-finder.dll is not beside the tests at '{dll}' — the ProjectReference that builds it is missing.");

        var result = await ManagedPluginLoader.Load(dll, new StubContext(), CancellationToken.None);
        var loaded = Assert.IsType<ManagedPluginLoadResult.Loaded>(result);
        return loaded.Instance;
    }

    /// <summary>A block big enough to clear BOTH default floors on its own — eight lines, and
    /// comfortably past 50 tokens — so the duplicate test exercises the defaults the model will
    /// actually run with rather than floors lowered to fit a small fixture.</summary>
    private const string DuplicatedBlock = """
        int total = 0;
        for (int i = 0; i < values.Length; i++)
        {
            total = total + values[i] * weights[i];
            sum = sum + values[i] + weights[i];
            product = product * (values[i] + 1);
        }
        return total + sum + product;
        """;

    /// <summary>ONE TOOL, DELIBERATELY. The tool's value is a whole scan answered in one call; a
    /// family of narrower tools would put the model back to assembling the answer itself.</summary>
    [Fact]
    public async Task ItOffersExactlyOneTool()
    {
        var plugin = await LoadPluginAsync();
        var manifest = await plugin.Load(new StubContext(), CancellationToken.None);

        var tool = Assert.Single(manifest.Tools);
        Assert.Equal("find_clones", tool.Name);
    }

    /// <summary>The whole pipeline through the plugin's front door: scan a real directory, and the
    /// report names BOTH files holding the duplicate — relative to the scanned root, because the
    /// report's job is to send the reader somewhere they can open.</summary>
    [Fact]
    public async Task ItReportsADuplicateAcrossTwoFiles()
    {
        var plugin = await LoadPluginAsync();
        await plugin.Load(new StubContext(), CancellationToken.None);

        var dir = Directory.CreateTempSubdirectory("clone-finder-plugin-test").FullName;
        try
        {
            // A distinct first line per file — differing LITERALS, since identifiers fold to `_` —
            // so the files are not byte-identical and the finding is the shared block, not
            // "these files are the same file".
            File.WriteAllText(Path.Combine(dir, "a.cs"), "int alpha = 111;\n" + DuplicatedBlock);
            File.WriteAllText(Path.Combine(dir, "b.cs"), "int beta = 222;\n" + DuplicatedBlock);

            var result = await plugin.Invoke("find_clones",
                new JobParameters(new Dictionary<string, object?> { ["path"] = dir }),
                new StubJobContext(), CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            var report = Assert.IsType<string>(result.Output["content"]);
            Assert.Contains("a.cs", report);
            Assert.Contains("b.cs", report);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A missing required argument is a FAILED CALL naming what to pass, never an
    /// exception — a model that omitted the argument can fix that if told, where an unhandled
    /// KeyNotFoundException just reports that the tool broke.</summary>
    [Fact]
    public async Task AMissingPathIsAFailedCallNamingTheArgument()
    {
        var plugin = await LoadPluginAsync();
        await plugin.Load(new StubContext(), CancellationToken.None);

        var result = await plugin.Invoke("find_clones",
            new JobParameters(new Dictionary<string, object?>()),
            new StubJobContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("path", result.ErrorMessage);
    }

    /// <summary>An unknown tool name is refused rather than silently doing the only thing it has.</summary>
    [Fact]
    public async Task AnUnknownToolIsRefused()
    {
        var plugin = await LoadPluginAsync();
        await plugin.Load(new StubContext(), CancellationToken.None);

        var result = await plugin.Invoke("find_something_else",
            new JobParameters(new Dictionary<string, object?> { ["path"] = "." }),
            new StubJobContext(), CancellationToken.None);

        Assert.False(result.Success);
    }
}
