namespace CxAgent.Core.Plugins;

/// <summary>
/// The plugin contract this build speaks — the ONE thing a plugin is checked against, and the one
/// number that decides whether a plugin and a host understand each other.
///
/// <para>ONE CONTRACT NUMBER FOR BOTH LOADERS. A managed plugin and an ABI plugin implement the same
/// contract by different means: the same manifest shape, the same gating vocabulary, the same
/// lifecycle. Two numbers could disagree, and a disagreement would be a lie about a single thing —
/// so the ABI handshake reads it too, rather than holding a second number that could drift.</para>
/// </summary>
public static class PluginContract
{
    /// <summary>
    /// Bumped when the shape a plugin must produce changes. This host speaks it, and every contract
    /// down to <see cref="Oldest"/>.
    ///
    /// <para>2 added the per-call gate — <c>"gated": "dynamic"</c> and the callback behind it. 3 added
    /// <see cref="IPluginClient"/> — a plugin that declares it can start a turn in its own session.</para>
    /// </summary>
    public const int Version = 3;

    /// <summary>
    /// The oldest contract this host still loads.
    ///
    /// <para><b>A RANGE, WHERE THIS WAS ONCE EXACT EQUALITY</b> — so a host that GAINS a capability
    /// stops refusing every plugin that never asked for it. Exactness made each bump a coordinated
    /// rebuild of every published plugin, and broke any third-party one until its author noticed.</para>
    ///
    /// <para><b>BUT THE FLOOR IS 2, NOT 1, AND THE REASON IS THE GATE.</b> Contract 2 is the first
    /// that can express <c>gated:"dynamic"</c> — a tool deciding per call whether to interrupt the
    /// user. A contract-1 manifest cannot say it, so its tools parse as <see cref="PluginGating.Never"/>:
    /// loaded here they would never ask permission at all. The loader's own check does not catch that
    /// — it refuses a manifest that DECLARES dynamic without a gate to consult, and a contract-1
    /// manifest declares nothing, so the check never fires. Silence, not a refusal.</para>
    ///
    /// <para><b>WHICH IS WHAT A FLOOR IS FOR.</b> A range says "older shapes still run"; this says how
    /// far back that holds, and the answer is: to the first contract that can express what the host
    /// now assumes every plugin can.</para>
    ///
    /// <para><b>THE OBLIGATION EVERY LATER ADDITION CARRIES:</b> it must be OPT-IN AND DETECTABLE — a
    /// separate interface a plugin implements, or a manifest field whose absence the host can act on
    /// — never a change to the meaning of something a plugin already produces, and never a new
    /// ASSUMPTION about what a plugin supplies. An addition failing that test is one this floor
    /// cannot honestly admit, and the answer is to raise this number, exactly as contract 2 did.</para>
    /// </summary>
    public const int Oldest = 2;

    /// <summary>
    /// Why this sidecar cannot be loaded here, or null when it can.
    ///
    /// <para>BOTH LOADERS CALL THIS, and that is the point of it living here. A managed plugin and
    /// an ABI plugin declare the contract in the same field of the same file, so refusing them for
    /// different reasons — or at different costs, one before reading a file and one after spawning
    /// a process — would be two behaviours where the manifest describes one.</para>
    ///
    /// <para>READ FROM THE SIDECAR, BEFORE ANYTHING RUNS: no assembly loaded, no host process
    /// spawned, no library mapped. What the plugin's own code reports is checked separately, by
    /// <see cref="PluginManifestMatch"/>, once there is code to ask.</para>
    /// </summary>
    public static string? Refusal(PluginManifest sidecar, string sidecarPath)
    {
        if (sidecar.Contract is null)
            return $"'{sidecarPath}' declares no 'pluginContract'. This build speaks contract "
                 + $"{Version}; a manifest that does not say which it was built against cannot be "
                 + "checked, and is refused rather than assumed compatible.";

        // TOO NEW IS THE UNKNOWABLE DIRECTION. A contract above this host's may require something
        // this build has never heard of, and there is no reading of a higher number that is safe to
        // guess at — which is the whole of what the old exact check was protecting.
        if (sidecar.Contract > Version)
            return $"'{sidecar.Name}' was built against plugin contract {sidecar.Contract}; this "
                 + $"build speaks {Version} at most — refusing rather than guessing at an unfamiliar "
                 + "shape.";

        // AND TOO OLD IS A REAL FLOOR, not a formality. Below Oldest the host no longer carries
        // whatever compatibility an older shape needed, and saying so beats loading something whose
        // support was quietly dropped.
        if (sidecar.Contract < Oldest)
            return $"'{sidecar.Name}' was built against plugin contract {sidecar.Contract}; this "
                 + $"build no longer loads anything below {Oldest} — rebuild it against a current "
                 + "contract.";

        return null;
    }

    /// <summary>
    /// This build's own version, for a plugin to log or display.
    ///
    /// <para>NOT A COMPATIBILITY MECHANISM. Whether a plugin and this host understand each other is
    /// <see cref="Version"/>'s question and nothing else's: a version floor would say the same thing
    /// less precisely, and could be satisfied by a build that had dropped the very feature the
    /// plugin needed.</para>
    ///
    /// <para>READ FROM THE CALLER'S ASSEMBLY, not this one. The release workflow stamps the host from
    /// the git tag, so a hardcoded copy here would be a second version to forget — but THIS assembly
    /// is the contract, and its own version is frozen so a plugin's binding survives a release. Ask
    /// it for a release number and the answer is the frozen identity, which is true of nothing.
    /// A local build reports the deliberately implausible 0.0.0 placeholder.</para>
    /// </summary>
    /// <param name="host">The host's own assembly — typically <c>Assembly.GetExecutingAssembly()</c>
    /// from Core or the front end, both of which carry the release version.</param>
    public static string HostVersionOf(System.Reflection.Assembly host) =>
        host.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

}
