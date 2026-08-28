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
    /// Bumped when the shape a plugin must produce changes. Checked with EXACT EQUALITY, never a
    /// floor: a host cannot know whether an unfamiliar contract omits something whose absence would
    /// change behaviour silently, and guessing at that is how a permission gate goes missing.
    ///
    /// <para>2 added the per-call gate — <c>"gated": "dynamic"</c> and the callback behind it. A
    /// contract-1 plugin has no gate to consult, and a host that accepted one would be deciding
    /// permission questions on behalf of a plugin that never answered any.</para>
    /// </summary>
    public const int Version = 2;

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

        if (sidecar.Contract != Version)
            return $"'{sidecar.Name}' was built against plugin contract {sidecar.Contract}; this "
                 + $"build speaks {Version} only — refusing rather than guessing at an unfamiliar "
                 + "shape.";

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
