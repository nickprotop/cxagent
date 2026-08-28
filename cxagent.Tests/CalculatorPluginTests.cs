using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The plugin around the evaluator: one tool, no gate, and a manifest that matches what it does.
///
/// <para>LOADED THE WAY PRODUCTION LOADS IT, through <see cref="ManagedPluginLoader"/>, exactly as
/// <see cref="CxagentLspPluginTests"/> does — this project's ProjectReference to calculator carries
/// <c>ReferenceOutputAssembly="false"</c>, so it does not compile against <c>CalculatorPlugin</c>'s
/// types, and going through <see cref="IPlugin"/> is what proves the plugin actually loads with
/// Jace.dll beside it rather than merely compiling.</para>
/// </summary>
public class CalculatorPluginTests
{
    /// <summary>The repository root, found by walking up from the test binary until plugins/calculator's
    /// sidecar is there — the same shape <c>PluginCatalogTests.RepoRoot</c> uses.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "plugins", "calculator", "calculator.plugin.json"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            $"plugins/calculator/calculator.plugin.json not found walking up from '{AppContext.BaseDirectory}'.");
    }

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
        var dll = Path.Combine(AppContext.BaseDirectory, "calculator.dll");
        Assert.True(File.Exists(dll),
            $"calculator.dll is not beside the tests at '{dll}' — the ProjectReference that builds it is missing.");

        var result = await ManagedPluginLoader.Load(dll, new StubContext(), CancellationToken.None);
        var loaded = Assert.IsType<ManagedPluginLoadResult.Loaded>(result);
        return loaded.Instance;
    }

    private static JobParameters Call(string expression) =>
        new(new Dictionary<string, object?> { ["expression"] = expression });

    /// <summary>
    /// ONE TOOL, NOT SEVERAL OPERATIONS. The failure this plugin addresses is a long calculation
    /// done as many calls with reasoning between them — so the manifest offering one tool is the
    /// design, not an omission.
    /// </summary>
    [Fact]
    public async Task ItOffersExactlyOneTool()
    {
        var plugin = await LoadPluginAsync();
        var manifest = await plugin.Load(new StubContext(), CancellationToken.None);

        var tool = Assert.Single(manifest.Tools);
        Assert.Equal("calc_eval", tool.Name);
    }

    /// <summary>
    /// NO GATE, AND IT IS THE FIRST HONEST INSTANCE. The tool reads nothing, writes nothing, spawns
    /// nothing and reaches no network — asking permission would train a user to click through
    /// prompts that never mattered.
    /// </summary>
    [Fact]
    public async Task TheToolNeedsNoPermission()
    {
        var plugin = await LoadPluginAsync();
        var manifest = await plugin.Load(new StubContext(), CancellationToken.None);

        Assert.Equal(PluginGating.Never, Assert.Single(manifest.Tools).Gated);
        Assert.False(manifest.Spawns);
    }

    /// <summary>A whole expression, evaluated in one call.</summary>
    [Fact]
    public async Task ItAnswersAnExpression()
    {
        var plugin = await LoadPluginAsync();
        await plugin.Load(new StubContext(), CancellationToken.None);

        var result = await plugin.Invoke("calc_eval", Call("(1847 * 0.0325) / 12"),
                                         new StubJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("5.00229166666667", result.Output["result"]);
    }

    /// <summary>A refusal reaches the model as a failed call, not as a success whose text happens to
    /// describe a problem — a "success" that is not one trains a model to ignore results.</summary>
    [Fact]
    public async Task ARefusalIsAFailedCall()
    {
        var plugin = await LoadPluginAsync();
        await plugin.Load(new StubContext(), CancellationToken.None);

        var result = await plugin.Invoke("calc_eval", Call("1/0"),
                                         new StubJobContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not a number", result.ErrorMessage);
    }

    /// <summary>An unknown tool name is refused rather than silently doing the only thing it has.</summary>
    [Fact]
    public async Task AnUnknownToolIsRefused()
    {
        var plugin = await LoadPluginAsync();
        await plugin.Load(new StubContext(), CancellationToken.None);

        var result = await plugin.Invoke("calc_something_else", Call("1+1"),
                                         new StubJobContext(), CancellationToken.None);

        Assert.False(result.Success);
    }

    /// <summary>
    /// THE MANIFEST AND THE SIDECAR AGREE. The host reads the sidecar before loading anything and
    /// refuses a plugin whose code disagrees with it, so a drift here is a plugin that cannot load.
    /// </summary>
    [Fact]
    public async Task TheManifestMatchesTheSidecar()
    {
        var plugin = await LoadPluginAsync();
        var manifest = await plugin.Load(new StubContext(), CancellationToken.None);
        var sidecar = PluginManifest.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "plugins", "calculator", "calculator.plugin.json")))
            .Manifest;

        Assert.NotNull(sidecar);
        Assert.Equal(sidecar.Name, manifest.Name);
        Assert.Equal(sidecar.Version, manifest.Version);
        Assert.Equal(sidecar.Tools.Select(t => t.Name), manifest.Tools.Select(t => t.Name));
    }
}
