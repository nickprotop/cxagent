using System.Text.Json;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Constructs an <see cref="IPlugin"/> from a real assembly on disk — every fixture here is a
/// SEPARATE project (<c>cxagent.Tests.PluginFixture*</c>), landing beside <c>cxagent.Tests.dll</c>
/// in the test output. That is deliberate: <see cref="ManagedPluginLoader"/> loads by path with
/// <c>Assembly.LoadFrom</c>, so a fixture declared inside this project would never exercise the
/// thing being tested — it would already be loaded as part of the running process.
/// </summary>
public class ManagedPluginLoaderTests
{
    private static readonly string OutputDir = AppContext.BaseDirectory;

    private static string FixtureDll(string name) => Path.Combine(OutputDir, name + ".dll");

    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Lines { get; } = [];
        public void Log(string message) => Lines.Add(message);
    }

    private sealed class FakeContext(string workingDirectory) : IPluginContext
    {
        public string WorkingDirectory { get; } = workingDirectory;
        public JsonElement Settings { get; } = JsonSerializer.SerializeToElement(new { });
        public int HostContract => PluginContract.Version;
        public string HostVersion => PluginContract.HostVersion;
        public IPluginLogger Logger { get; } = new FakeLogger();
        public CancellationToken Lifetime { get; } = CancellationToken.None;
        public void RegisterChildProcess(int processId) { }
    }

    private static FakeContext Context() => new(OutputDir);

    // ---- The clean case ----------------------------------------------------------------------

    [Fact]
    public async Task AMatchingPluginLoads()
    {
        var result = await ManagedPluginLoader.Load(
            FixtureDll("cxagent.Tests.PluginFixture"), Context(), CancellationToken.None);

        var loaded = Assert.IsType<ManagedPluginLoadResult.Loaded>(result);
        Assert.Equal("well-formed", loaded.Manifest.Name);
        Assert.Equal(["wf_tool"], loaded.Manifest.Tools.Select(t => t.Name).ToList());
    }

    // ---- Bad path / missing sidecar -----------------------------------------------------------

    [Fact]
    public async Task ANonexistentPathFails()
    {
        var result = await ManagedPluginLoader.Load(
            Path.Combine(OutputDir, "does-not-exist.dll"), Context(), CancellationToken.None);

        var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
        Assert.Contains("no plugin assembly at", failed.Reason);
    }

    /// <summary>An assembly that genuinely exists but has no sidecar next to it — the ordinary DLLs
    /// this test binary ships beside are exactly that, so this needs no dedicated fixture.</summary>
    [Fact]
    public async Task AMissingSidecarFails()
    {
        var dllWithNoSidecar = FixtureDll("cxagent.Tests.PluginFixture.Ambiguous");
        Assert.True(File.Exists(dllWithNoSidecar));

        var result = await ManagedPluginLoader.Load(dllWithNoSidecar, Context(), CancellationToken.None);

        var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
        Assert.Contains("no sidecar manifest at", failed.Reason);
    }

    // ---- Zero or multiple IPlugin implementations ----------------------------------------------

    /// <summary>A real assembly with no IPlugin implementation at all fails with a clear message —
    /// <summary>
    /// THE REFUSAL HAPPENS BEFORE THE ASSEMBLY IS LOADED, which is the only placement worth having:
    /// Assembly.LoadFrom is irreversible and a constructor is arbitrary code, so a check after
    /// either discards a result rather than preventing anything.
    /// </summary>
    [Theory]
    [InlineData("""{"name":"nocontract","version":"1.0.0","tools":[]}""", "pluginContract")]
    [InlineData("""{"pluginContract":1,"name":"old","version":"1.0.0","tools":[]}""", "contract 1")]
    [InlineData("""{"pluginContract":99,"name":"future","version":"1.0.0","tools":[]}""", "contract 99")]
    public async Task AManifestThisBuildCannotVouchForIsRefused(string manifest, string expected)
    {
        var dll = FixtureDll("cxagent.Tests.PluginFixture.Empty");
        var sidecar = Path.ChangeExtension(dll, null) + ".plugin.json";
        await File.WriteAllTextAsync(sidecar, manifest);
        try
        {
            var result = await ManagedPluginLoader.Load(dll, Context(), CancellationToken.None);

            var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
            Assert.Contains(expected, failed.Reason);
        }
        finally { File.Delete(sidecar); }
    }

    /// not a guess and not a crash. Needs its own sidecar so the failure is attributable to the
    /// type search rather than a missing file.</summary>
    [Fact]
    public async Task ZeroImplementationsFails()
    {
        var dll = FixtureDll("cxagent.Tests.PluginFixture.Empty");
        var sidecar = Path.ChangeExtension(dll, null) + ".plugin.json";
        await File.WriteAllTextAsync(sidecar, """{"pluginContract":2,"name":"empty","version":"1.0.0","tools":[]}""");
        try
        {
            var result = await ManagedPluginLoader.Load(dll, Context(), CancellationToken.None);

            var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
            Assert.Contains("no", failed.Reason);
            Assert.Contains("IPlugin", failed.Reason);
        }
        finally
        {
            File.Delete(sidecar);
        }
    }

    /// <summary>An assembly declaring two IPlugin types is refused rather than guessed at — the
    /// loader must not pick one by reflection order.</summary>
    [Fact]
    public async Task TwoImplementationsFailsWithoutGuessing()
    {
        var dll = FixtureDll("cxagent.Tests.PluginFixture.Ambiguous");
        var sidecar = Path.ChangeExtension(dll, null) + ".plugin.json";
        await File.WriteAllTextAsync(sidecar, """{"pluginContract":2,"name":"ambiguous","version":"1.0.0","tools":[]}""");
        try
        {
            var result = await ManagedPluginLoader.Load(dll, Context(), CancellationToken.None);

            var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
            Assert.Contains("more than one", failed.Reason);
            Assert.Contains("FirstPlugin", failed.Reason);
            Assert.Contains("SecondPlugin", failed.Reason);
        }
        finally
        {
            File.Delete(sidecar);
        }
    }

    // ---- A plugin that throws from Load ---------------------------------------------------------

    [Fact]
    public async Task APluginThatThrowsFromLoadFailsWithTheReason()
    {
        var result = await ManagedPluginLoader.Load(
            FixtureDll("cxagent.Tests.PluginFixture.Throwing"), Context(), CancellationToken.None);

        var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
        Assert.Contains("fixture: this plugin always fails to load", failed.Reason);
    }

    // ---- Sidecar / Load mismatch -----------------------------------------------------------------

    /// <summary>THE IDENTITY CHECK the plugin design IS EXPLICIT ABOUT: a plugin whose Load returns a
    /// manifest differing from its sidecar refuses to load, naming the difference — otherwise the
    /// file the user was asked to approve describes something other than what runs.</summary>
    [Fact]
    public async Task ASidecarLoadMismatchFailsAndNamesTheDifference()
    {
        var result = await ManagedPluginLoader.Load(
            FixtureDll("cxagent.Tests.PluginFixture.Mismatched"), Context(), CancellationToken.None);

        var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
        Assert.Contains("does not match its sidecar manifest", failed.Reason);
        // NAMES WHICH TOOL DIFFERED, not just "something differed" — sidecar_tool is what the
        // sidecar declared and Load never returned; a_different_tool is the reverse.
        Assert.Contains("sidecar_tool", failed.Reason);
    }
}
