using CxAgent.Core.Jobs;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>
/// A bundle of tools and the executor that runs them — see the plugin design, "What a plugin is".
///
/// <para>THE LIFECYCLE IS LOAD, START, STOP, UNWIRE — NOT UNLOAD. A managed plugin's assembly
/// cannot be removed from the process without an <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// of its own, and even then only if nothing outlives it holding a reference. Naming the last step
/// "Unload" would put a promise in the contract that Core cannot keep — see the plugin design, "Lifecycle".
/// What unwiring actually guarantees: the plugin's tools are gone from the registry, the model is
/// never offered them again, and its child processes are dead. The code staying resident afterward
/// costs memory and nothing else.</para>
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Hands the plugin its context and returns its manifest.
    ///
    /// <para>THE RETURNED MANIFEST MUST MATCH THE SIDECAR FILE. The plugin design, "A SIDECAR FILE, and
    /// also what Describe returns": config-time validation reads the sidecar because it must know a
    /// plugin's tool names before any binary loads, and runtime confirms that promise against this
    /// call. A mismatch is a load failure that names the difference, not a mismatch this method is
    /// free to paper over.</para>
    /// </summary>
    Task<PluginManifest> Load(IPluginContext context, CancellationToken ct);

    /// <summary>Runs after Load. The plugin may spawn processes, open connections, index — whatever it needs before its tools can be called.</summary>
    Task Start(CancellationToken ct);

    /// <summary>
    /// Runs one call to one of this plugin's OWN tools, named by <paramref name="toolName"/> — the
    /// same one-executor-many-tools shape <c>ToolBindings</c> already has, where several tools share
    /// one executor and are told apart by a pinned action. Here the plugin IS that executor: it
    /// holds whatever one connection or one client its tools share (an LSP plugin's server process,
    /// for one), and routing a name to an operation on it is the plugin's own business, not the
    /// registry's — the registry does not know what "csharp_definition" means.
    ///
    /// <para><paramref name="toolName"/> IS ALWAYS ONE THIS PLUGIN'S OWN MANIFEST DECLARED. The
    /// registry that dispatches here only ever calls it for a tool this plugin's own <see
    /// cref="PluginManifest.Tools"/> named at Load — an unrecognised name reaching this method is
    /// this plugin's own bug, not a name the caller must additionally validate.</para>
    /// </summary>
    Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct);

    /// <summary>
    /// Shuts the plugin down; its children exit. Runs before Unwire — see the plugin design, "Unwire is one
    /// ordered operation": deregister, drain, Stop, reap, in that order, so a call already accepted
    /// can still finish before this runs.
    /// </summary>
    Task Stop(CancellationToken ct);
}

/// <summary>
/// Implemented by a plugin that declares any tool <c>"gated": "dynamic"</c> — the per-call decision
/// the manifest's booleans cannot express.
///
/// <para>A SEPARATE INTERFACE, NOT A DEFAULT METHOD ON <see cref="IPlugin"/>. A default returning
/// null would make "declared dynamic and implemented nothing" indistinguishable from "implemented a
/// gate that permits this call" — so a sidecar promising a per-call decision could quietly never
/// ask, and the promise a user read before approving the load would be unfalsifiable. As its own
/// interface the absence is visible at load, where <see cref="ManagedPluginLoader"/> refuses it.</para>
/// </summary>
public interface IPluginGateSource
{
    /// <summary>
    /// Whether THIS call asks, and what the prompt should say. Null means no prompt.
    ///
    /// <para>SYNCHRONOUS, matching <see cref="Jobs.IAgentTool.Gate"/>, which the caller invokes
    /// synchronously. A gate decides over arguments already in hand; forbidding I/O structurally is
    /// what keeps a consent prompt from waiting on a network call.</para>
    ///
    /// <para>THROWING IS SAFE, AND ASKS. Core treats a throw as "ask, and offer no standing grant" —
    /// a broken gate is noisy rather than silently permissive, and cannot accumulate a permanent
    /// allowance while it is broken.</para>
    /// </summary>
    PluginGate? Gate(string toolName, JobParameters call);
}
