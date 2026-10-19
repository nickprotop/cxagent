using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Plugins.Testing;
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

    // AS A USER INSTALLS IT: the dll and its sidecar in a folder of their own, loaded through the
    // plugin harness — the same loader a session uses.
    private static string Folder() => Folders.FirstParty("clone-finder");

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
        await using var plugin = await PluginHarness.LoadAsync(Folder());

        var tool = Assert.Single(plugin.Manifest.Tools);
        Assert.Equal("find_clones", tool.Name);
    }

    /// <summary>The whole pipeline through the plugin's front door: scan a real directory, and the
    /// report names BOTH files holding the duplicate — relative to the scanned root, because the
    /// report's job is to send the reader somewhere they can open.</summary>
    [Fact]
    public async Task ItReportsADuplicateAcrossTwoFiles()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folder());

        var dir = Directory.CreateTempSubdirectory("clone-finder-plugin-test").FullName;
        try
        {
            // A distinct first line per file — differing LITERALS, since identifiers fold to `_` —
            // so the files are not byte-identical and the finding is the shared block, not
            // "these files are the same file".
            File.WriteAllText(Path.Combine(dir, "a.cs"), "int alpha = 111;\n" + DuplicatedBlock);
            File.WriteAllText(Path.Combine(dir, "b.cs"), "int beta = 222;\n" + DuplicatedBlock);

            var result = (await plugin.CallAsync("find_clones", new { path = dir })).Result!;

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
        await using var plugin = await PluginHarness.LoadAsync(Folder());

        var result = (await plugin.CallAsync("find_clones")).Result!;

        Assert.False(result.Success);
        Assert.Contains("path", result.ErrorMessage);
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
        var loaded = Assert.IsType<ManagedPluginLoadResult.Loaded>(
            await ManagedPluginLoader.Load(Path.Combine(Folder(), "clone-finder.dll"), new StubContext(), CancellationToken.None));
        var plugin = loaded.Instance;

        var result = await plugin.Invoke("find_something_else",
            new JobParameters(new Dictionary<string, object?> { ["path"] = "." }),
            new StubJobContext(), CancellationToken.None);

        Assert.False(result.Success);
    }
}
