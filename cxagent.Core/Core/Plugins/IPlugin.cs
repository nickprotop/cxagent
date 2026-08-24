namespace CxAgent.Core.Plugins;

/// <summary>
/// A bundle of tools and the executor that runs them — see PLUGINS.md, "What a plugin is".
///
/// <para>THE LIFECYCLE IS LOAD, START, STOP, UNWIRE — NOT UNLOAD. A managed plugin's assembly
/// cannot be removed from the process without an <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// of its own, and even then only if nothing outlives it holding a reference. Naming the last step
/// "Unload" would put a promise in the contract that Core cannot keep — see PLUGINS.md, "Lifecycle".
/// What unwiring actually guarantees: the plugin's tools are gone from the registry, the model is
/// never offered them again, and its child processes are dead. The code staying resident afterward
/// costs memory and nothing else.</para>
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Hands the plugin its context and returns its manifest.
    ///
    /// <para>THE RETURNED MANIFEST MUST MATCH THE SIDECAR FILE. PLUGINS.md, "A SIDECAR FILE, and
    /// also what Describe returns": config-time validation reads the sidecar because it must know a
    /// plugin's tool names before any binary loads, and runtime confirms that promise against this
    /// call. A mismatch is a load failure that names the difference, not a mismatch this method is
    /// free to paper over.</para>
    /// </summary>
    Task<PluginManifest> Load(IPluginContext context, CancellationToken ct);

    /// <summary>Runs after Load. The plugin may spawn processes, open connections, index — whatever it needs before its tools can be called.</summary>
    Task Start(CancellationToken ct);

    /// <summary>
    /// Shuts the plugin down; its children exit. Runs before Unwire — see PLUGINS.md, "Unwire is one
    /// ordered operation": deregister, drain, Stop, reap, in that order, so a call already accepted
    /// can still finish before this runs.
    /// </summary>
    Task Stop(CancellationToken ct);
}
