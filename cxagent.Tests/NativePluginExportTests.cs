using CxAgent.PluginHost;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Locks <see cref="NativePlugin.Load"/>'s split between the seven MANDATORY exports (missing any
/// one refuses the load, by name) and the one OPTIONAL export, <c>cxagent_plugin_poll</c> — a
/// contract-2 library was built before <c>poll</c> existed and must still load, or this host would
/// refuse every published plugin the moment it gained an eighth export it does not require.
///
/// <para>Runs against the same prebuilt <c>.so</c> fixtures <see cref="AbiPluginHostTests"/> loads
/// through a real host process — <c>fixture-wellformed.so</c> exports the seven mandatory symbols
/// and no <c>poll</c> (see AbiFixtures/fixture_plugin.c, which has never declared one), so it is
/// already the "contract-2, no poll" case this test needs without a dedicated fixture build.
/// SKIPPED, NOT FAILED, when the fixture is absent — see <see cref="AbiPluginHostTests"/>'s own doc
/// for why (no C compiler, or a non-Linux image).</para>
/// </summary>
public class NativePluginExportTests
{
    private static readonly string OutputDir = AppContext.BaseDirectory;

    private static bool RequireFixture(string name, out string path)
    {
        var found = Path.Combine(OutputDir, name + ".so");
        path = found;
        return File.Exists(found);
    }

    [Fact]
    public void A_library_without_poll_still_loads()
    {
        if (!RequireFixture("fixture-wellformed", out var lib)) return;

        var result = NativePlugin.Load(lib);

        var loaded = Assert.IsType<NativePluginLoadResult.Loaded>(result);
        using var _ = loaded.Plugin;
        Assert.False(loaded.Plugin.HasPoll);
    }

    [Fact]
    public void A_library_missing_a_mandatory_export_is_still_refused_by_name()
    {
        if (!RequireFixture("fixture-noinvoke", out var lib)) return;

        var result = NativePlugin.Load(lib);

        var failed = Assert.IsType<NativePluginLoadResult.Failed>(result);
        Assert.Contains("cxagent_plugin_invoke", failed.Reason);
    }
}
