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
// SAME COLLECTION AS PluginCommandTests: both read or write the shared
// 'cxagent.Tests.PluginFixture.plugin.json' sidecar at the test output path (this class writes
// a scenario sidecar there and deletes it in `finally`; PluginCommandTests.DropFixture reads it as
// a template to copy). xUnit runs different classes' tests in parallel by default, so without this
// they can race — one test's write or delete lands mid another test's read of the same file.
[Collection("plugin-fixture-sidecar")]
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
        public string HostVersion => PluginContract.HostVersionOf(GetType().Assembly);
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
    // BELOW THE FLOOR: contract 1 cannot express gated:"dynamic", so its tools would parse as
    // never-ask and it would load as a plugin that silently skips the permission gate.
    [InlineData("""{"pluginContract":1,"name":"old","version":"1.0.0","tools":[]}""", "no longer loads")]
    // ABOVE THE CEILING: it may require something this build has never heard of.
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

    /// <summary>THE IDENTITY CHECK: a plugin whose Load returns a manifest differing from its
    /// sidecar refuses to load, naming the difference — otherwise the file the user was asked to
    /// approve describes something other than what runs.</summary>
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

    // ---- "client": true declared but not implemented -----------------------------------------

    /// <summary>Sidecar and Load() DISAGREE about "client" — a PluginManifestMatch failure, not the
    /// capability check. See <see cref="ASidecarAndLoadAgreeingOnTheClientButATypeThatDoesNotImplementItIsRefused"/>
    /// below for the failure this one is NOT: that test exercises the capability check itself.</summary>
    [Fact]
    public async Task ASidecarClaimingTheClientAgainstAFixtureWhoseLoadNeverReturnsItIsRefused()
    {
        // MATCHES THE FIXTURE'S OWN Load() EXACTLY (name, version, spawns, its one tool) except for
        // "client" — PluginManifestMatch checks every other field first, so a sidecar that differs
        // anywhere else would be refused for THAT mismatch rather than the disagreement this test
        // means to exercise. See PluginFixtures/WellFormedPlugin.plugin.json for the shape echoed.
        //
        // THE REFUSAL IS THE SIDECAR/LOAD MISMATCH, NOT THE IPluginClientConsumer CHECK — and that is
        // not a bug in either check, it is a difference in what they each verify. Mismatch catches a
        // manifest that disagrees with what the plugin's own code returned; the capability check
        // catches an AGREED manifest the binary cannot honour. Precedent: Mismatch already applies
        // the identical rule to "gated" (its per-tool Gated comparison), so a sidecar disagreeing
        // with Load() on gated:"dynamic" is caught there too, before IPluginGateSource is reached.
        // WellFormedPlugin.Load() is hardcoded to Client=false (its own doc comment: it exists to
        // return exactly what its sidecar declares), so no sidecar here can ever AGREE with
        // "client": true — which is exactly why this test can only prove the Mismatch half.
        //
        // THIS SIDECAR IS SHARED WITH PluginCommandTests, which copies it as a template outside this
        // class entirely — so the original content is saved and restored here rather than deleted,
        // unlike every sidecar elsewhere in this file that this class alone ever writes.
        var dll = FixtureDll("cxagent.Tests.PluginFixture");
        var sidecar = Path.ChangeExtension(dll, null) + ".plugin.json";
        var original = await File.ReadAllTextAsync(sidecar);
        await File.WriteAllTextAsync(sidecar,
            """
            {
              "pluginContract": 2,
              "client": true,
              "name": "well-formed",
              "version": "1.0.0",
              "spawns": false,
              "tools": [
                { "name": "wf_tool", "description": "a fixture tool", "inputSchema": { "type": "object" }, "gated": false }
              ]
            }
            """);
        try
        {
            var result = await ManagedPluginLoader.Load(dll, Context(), CancellationToken.None);

            var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
            Assert.Contains("client", failed.Reason);
        }
        finally { await File.WriteAllTextAsync(sidecar, original); }
    }

    /// <summary>Sidecar and Load() AGREE that client=true — PluginManifestMatch has nothing to
    /// refuse — but ClaimsClientPlugin implements only IPlugin, not IPluginClientConsumer. This is
    /// the capability check itself, and it needs its own fixture: WellFormedPlugin.Load() cannot be
    /// made to return Client=true (see the test above), so no sidecar reaches this branch through
    /// it. ClaimsClientPlugin's Load() sets Client = true honestly and matches its own sidecar
    /// (ClaimsClientPlugin.plugin.json) on every other field — it is not lying, it is missing the
    /// marker interface, which is exactly the gap this check exists to catch.</summary>
    [Fact]
    public async Task ASidecarAndLoadAgreeingOnTheClientButATypeThatDoesNotImplementItIsRefused()
    {
        var result = await ManagedPluginLoader.Load(
            FixtureDll("cxagent.Tests.PluginFixture.ClaimsClient"), Context(), CancellationToken.None);

        var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
        Assert.Contains("client", failed.Reason);
        Assert.Contains(nameof(IPluginClientConsumer), failed.Reason);
    }

    /// <summary>The same shape as <see cref="ASidecarAndLoadAgreeingOnTheClientButATypeThatDoesNotImplementItIsRefused"/>,
    /// for commands. A sidecar that DISAGREES with Load() about which commands exist is caught by
    /// PluginManifestMatch before this branch is ever reached — see PluginManifestMatch's own
    /// command-name comparison — so a fixture proving the capability check itself needs Load() to
    /// AGREE with its sidecar while the type lacks IPluginCommandHandler. ClaimsCommandPlugin does
    /// exactly that, the way ClaimsClientPlugin does for the client capability.</summary>
    [Fact]
    public async Task ASidecarAndLoadAgreeingOnACommandButATypeThatDoesNotImplementTheHandlerIsRefused()
    {
        var result = await ManagedPluginLoader.Load(
            FixtureDll("cxagent.Tests.PluginFixture.ClaimsCommand"), Context(), CancellationToken.None);

        var failed = Assert.IsType<ManagedPluginLoadResult.Failed>(result);
        Assert.Contains(nameof(IPluginCommandHandler), failed.Reason);
    }

    [Fact]
    public async Task AManifestThatDoesNotDeclareTheClientLoadsWhateverTheTypeImplements()
    {
        // THE CONVERSE IS NOT AN ERROR, and this is not hypothetical: calculator.plugin.json declares
        // "gated": false while CalculatorPlugin implements IPluginGateSource, and csharp-lsp does the
        // same. A symmetric "manifest and type must agree" rule would refuse both plugins we ship.
        // SAME SIDECAR AS ABOVE, MINUS "client" — the fixture's type implements no client-consuming
        // interface either way; what is under test is that OMITTING the declaration is not itself a
        // mismatch, not that this particular type has nothing to declare.
        //
        // RESTORED RATHER THAN DELETED, same reason as above: PluginCommandTests copies this exact
        // file as a template and expects it to still exist afterward.
        var dll = FixtureDll("cxagent.Tests.PluginFixture");
        var sidecar = Path.ChangeExtension(dll, null) + ".plugin.json";
        var original = await File.ReadAllTextAsync(sidecar);
        await File.WriteAllTextAsync(sidecar,
            """
            {
              "pluginContract": 2,
              "name": "well-formed",
              "version": "1.0.0",
              "spawns": false,
              "tools": [
                { "name": "wf_tool", "description": "a fixture tool", "inputSchema": { "type": "object" }, "gated": false }
              ]
            }
            """);
        try
        {
            Assert.IsType<ManagedPluginLoadResult.Loaded>(
                await ManagedPluginLoader.Load(dll, Context(), CancellationToken.None));
        }
        finally { await File.WriteAllTextAsync(sidecar, original); }
    }
}
