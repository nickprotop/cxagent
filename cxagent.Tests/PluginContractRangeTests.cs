using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which contracts this host loads.
///
/// <para><b>A RANGE, WHERE THIS WAS EXACT EQUALITY.</b> The exact check refused every plugin not built
/// against this precise number — so a host GAINING a capability broke every plugin that had never
/// asked for it, and three published plugins would have needed a coordinated rebuild before a bumped
/// host could load any of them.</para>
///
/// <para><b>BUT THE FLOOR IS 2, AND CONTRACT 1 STAYS REFUSED.</b> Contract 2 is the first that can
/// express <c>gated:"dynamic"</c>; a contract-1 manifest cannot say it, so its tools parse as
/// <c>Never</c> — loaded here they would never ask permission at all. `ManagedPluginLoader`'s own
/// check does not catch that: it refuses a manifest that DECLARES dynamic without a gate, and a
/// contract-1 manifest declares nothing, so the check never fires. Silence, not a refusal.</para>
///
/// <para>TOO NEW IS STILL REFUSED, and that asymmetry is the point: a contract ABOVE this host's may
/// require something this build has never heard of, and no reading of a higher number is safe to
/// guess at.</para>
/// </summary>
public class PluginContractRangeTests
{
    private static PluginManifest Sidecar(int? contract) =>
        new("p", "1.0.0", Instructions: null, Spawns: false, Tools: []) { Contract = contract };

    /// <summary>THE CURRENT CONTRACT LOADS.</summary>
    [Fact]
    public void ThisBuildsOwnContractIsAccepted() =>
        Assert.Null(PluginContract.Refusal(Sidecar(PluginContract.Version), "p.plugin.json"));

    /// <summary>
    /// AND SO DOES THE OLDEST ONE STILL SUPPORTED — the whole point of the range.
    /// </summary>
    /// <remarks>
    /// THE SAME NUMBER AS <c>Version</c> TODAY, so this asserts nothing the test above does not. It
    /// earns its place the moment <c>Version</c> is bumped, which is exactly when a floor stops being
    /// theoretical — and a test that only appears then is a test written under pressure.
    /// </remarks>
    [Fact]
    public void TheOldestSupportedContractIsAccepted() =>
        Assert.Null(PluginContract.Refusal(Sidecar(PluginContract.Oldest), "p.plugin.json"));

    /// <summary>
    /// CONTRACT 1 IS REFUSED, BY NUMBER RATHER THAN BY VALUE.
    ///
    /// <para>Named rather than derived from <c>Oldest - 1</c>, because it is a FACT about contract 1
    /// and not about wherever the floor happens to sit: contract 1 cannot express
    /// <c>gated:"dynamic"</c>, so its tools parse as never-ask and it would load as a plugin that
    /// silently skips the permission gate. If a later floor rises past 2, this test should still
    /// read as "and 1, specifically, for this reason".</para>
    ///
    /// <para>THIS IS THE REGRESSION THAT ACTUALLY HAPPENED. The range was first written with the
    /// floor at 1, on the reasoning that the loader's dynamic-without-a-gate check covered it. It
    /// does not — that check fires on a manifest that DECLARES dynamic, which contract 1 never can.
    /// A real plugin was drive-tested loading at contract 1 before the mistake was caught.</para>
    /// </summary>
    [Fact]
    public void ContractOneIsRefusedBecauseItCannotExpressAPerCallGate()
    {
        var refusal = PluginContract.Refusal(Sidecar(1), "p.plugin.json");

        Assert.NotNull(refusal);
        Assert.Contains("no longer loads", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NEWER ONE IS REFUSED. This is the direction that cannot be guessed at: the plugin may
    /// require something this build has never heard of.
    /// </summary>
    [Fact]
    public void ANewerContractIsRefused()
    {
        var refusal = PluginContract.Refusal(Sidecar(PluginContract.Version + 1), "p.plugin.json");

        Assert.NotNull(refusal);
        Assert.Contains("at most", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND ONE BELOW THE FLOOR IS REFUSED WITH A DIFFERENT REASON — saying support was dropped, not
    /// that the contract is unfamiliar. The two are different facts and a rebuild only fixes one.
    /// </summary>
    [Fact]
    public void AContractBelowTheFloorIsRefused()
    {
        var refusal = PluginContract.Refusal(Sidecar(PluginContract.Oldest - 1), "p.plugin.json");

        Assert.NotNull(refusal);
        Assert.Contains("no longer loads", refusal, StringComparison.Ordinal);
    }

    /// <summary>AND DECLARING NONE AT ALL IS STILL REFUSED. Absent is not a version — it says nothing
    /// about what the plugin was built against, and a range cannot admit an unknown.</summary>
    [Fact]
    public void AManifestWithNoContractIsRefused()
    {
        var refusal = PluginContract.Refusal(Sidecar(null), "p.plugin.json");

        Assert.NotNull(refusal);
        Assert.Contains("pluginContract", refusal, StringComparison.Ordinal);
    }
}
