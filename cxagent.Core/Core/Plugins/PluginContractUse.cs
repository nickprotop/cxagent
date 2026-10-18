namespace CxAgent.Core.Plugins;

/// <summary>
/// Whether a sidecar uses only what the contract it names can express.
///
/// <para>A FEATURE BELONGS TO THE CONTRACT THAT ADDED IT. The client arrived in contract 3, so a
/// sidecar naming contract 2 and declaring <c>"client": true</c> describes something its own
/// contract has no word for — a build that says it targets one contract while using another's. Its
/// range check alone passed it.</para>
///
/// <para>HERE, NOT IN <see cref="PluginContract.Refusal"/>, because which features a contract may
/// use is the host's knowledge, and the contract assembly is what every plugin is rebuilt against
/// when it changes. Both loaders ask this straight after that one, before anything runs.</para>
/// </summary>
internal static class PluginContractUse
{
    /// <summary>The contract that added <see cref="IPluginClient"/>.</summary>
    public const int ClientSince = 3;

    /// <summary>Why this sidecar uses something its contract cannot express, or null when it does not.</summary>
    public static string? Refusal(PluginManifest sidecar) =>
        sidecar.Client && sidecar.Contract < ClientSince
            ? $"'{sidecar.Name}' declares \"client\" but names plugin contract {sidecar.Contract}; "
              + $"the client arrived in contract {ClientSince}, so declare {ClientSince}."
            : null;
}
