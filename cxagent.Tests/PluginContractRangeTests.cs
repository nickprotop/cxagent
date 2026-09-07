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
/// <para>WHAT THE EXACTNESS PROTECTED IS ALREADY PROTECTED ELSEWHERE. Its stated hazard was a
/// contract-1 plugin having no per-call gate — and `ManagedPluginLoader` refuses a manifest declaring
/// <c>gated:"dynamic"</c> whose type does not implement `IPluginGateSource`, whatever contract it
/// claims. A plugin declaring no dynamic tools has no gate to miss. The number was a second lock on a
/// bolted door, and its cost was refusing the plugin outright rather than running it without a
/// capability it never had.</para>
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

    /// <summary>AND SO DOES THE OLDEST ONE STILL SUPPORTED — the whole point of the range.</summary>
    [Fact]
    public void TheOldestSupportedContractIsAccepted() =>
        Assert.Null(PluginContract.Refusal(Sidecar(PluginContract.Oldest), "p.plugin.json"));

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
