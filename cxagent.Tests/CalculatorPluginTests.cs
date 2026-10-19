using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Plugins.Testing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The plugin around the evaluator: one tool, no gate, and a manifest that matches what it does.
///
/// <para>LOADED THE WAY PRODUCTION LOADS IT — through the plugin harness, which uses the same loader
/// a session does. This project's ProjectReference to calculator carries
/// <c>ReferenceOutputAssembly="false"</c>, so it does not compile against <c>CalculatorPlugin</c>'s
/// types, and going through the loader is what proves the plugin actually loads with Jace.dll beside
/// it rather than merely compiling.</para>
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

    // THE SIDECAR THAT SHIPS, from plugins/calculator, rather than the copy in the test output — so a
    // load that passes here is the shipped sidecar agreeing with the shipped code.
    private static string Folder()
    {
        var dir = Folders.FirstParty("calculator", "Jace.dll");
        File.Copy(Path.Combine(RepoRoot(), "plugins", "calculator", "calculator.plugin.json"),
            Path.Combine(dir, "calculator.plugin.json"), overwrite: true);
        return dir;
    }

    /// <summary>
    /// ONE TOOL, NOT SEVERAL OPERATIONS. The failure this plugin addresses is a long calculation
    /// done as many calls with reasoning between them — so the manifest offering one tool is the
    /// design, not an omission.
    /// </summary>
    [Fact]
    public async Task ItOffersExactlyOneTool()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folder());

        var tool = Assert.Single(plugin.Manifest.Tools);
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
        await using var plugin = await PluginHarness.LoadAsync(Folder());

        var call = await plugin.CallAsync("calc_eval", new { expression = "1+1" });

        Assert.Null(call.Gate);
        Assert.False(plugin.Manifest.Spawns);
    }

    /// <summary>A whole expression, evaluated in one call.</summary>
    [Fact]
    public async Task ItAnswersAnExpression()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folder());

        var call = await plugin.CallAsync("calc_eval", new { expression = "(1847 * 0.0325) / 12" });

        Assert.True(call.Result!.Success);
        Assert.Equal("5.00229166666667", call.Result.Output["result"]);
    }

    /// <summary>A refusal reaches the model as a failed call, not as a success whose text happens to
    /// describe a problem — a "success" that is not one trains a model to ignore results.</summary>
    [Fact]
    public async Task ARefusalIsAFailedCall()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folder());

        var call = await plugin.CallAsync("calc_eval", new { expression = "1/0" });

        Assert.False(call.Result!.Success);
        Assert.Contains("not a number", call.Result.ErrorMessage);
    }

    /// <summary>
    /// An unknown tool name is refused rather than silently doing the only thing it has.
    ///
    /// <para>THROUGH THE LOADER, NOT THE HARNESS: the harness refuses a tool the manifest does not
    /// declare before the plugin sees it, and this asks what the plugin's own code does with one.</para>
    /// </summary>
    [Fact]
    public async Task AnUnknownToolIsRefused()
    {
        var context = new StubContext();
        var loaded = Assert.IsType<ManagedPluginLoadResult.Loaded>(
            await ManagedPluginLoader.Load(Path.Combine(Folder(), "calculator.dll"), context, CancellationToken.None));

        var result = await loaded.Instance.Invoke("calc_something_else",
            new JobParameters(new Dictionary<string, object?> { ["expression"] = "1+1" }),
            new StubJobContext(), CancellationToken.None);

        Assert.False(result.Success);
    }

    /// <summary>
    /// THE MANIFEST AND THE SIDECAR AGREE. The host reads the sidecar before loading anything and
    /// refuses a plugin whose code disagrees with it, so a drift here is a plugin that cannot load —
    /// and the harness loading the shipped sidecar is that same refusal, not passing.
    /// </summary>
    [Fact]
    public async Task TheManifestMatchesTheSidecar()
    {
        Assert.Empty(await PluginHarness.CheckAsync(Folder()));
    }

    private sealed class StubContext : IPluginContext
    {
        public string WorkingDirectory { get; } = ".";
        public JsonElement Settings { get; } = JsonSerializer.SerializeToElement(new { });
        public int HostContract { get; } = PluginContract.Version;
        public string HostVersion => PluginContract.HostVersionOf(GetType().Assembly);
        public IPluginLogger Logger { get; } = new NullLogger();
        public CancellationToken Lifetime { get; } = CancellationToken.None;
        public void RegisterChildProcess(int processId) { }

        private sealed class NullLogger : IPluginLogger
        {
            public void Log(string message) { }
        }
    }

    private sealed class StubJobContext : IJobContext
    {
        public void ReportProgress(double percent, string? message = null) { }
        public string? Requester => null;
        public string? WorkingDirectory => null;
        public void Log(string line) { }
        public void Log(JobLogLevel level, string line) { }
        public void ReportTextDelta(string delta) { }
    }
}
