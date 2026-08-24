namespace CxAgent.Core.Plugins.Abi;

/// <summary>
/// What a load attempt against the ABI host produced — the same two-shape result
/// <see cref="ManagedPluginLoadResult"/> already gives the managed loader's callers, so a caller
/// choosing between the two loaders (config says "kind": "abi" vs a plain assembly) handles both
/// the identical way. See the plugin design, "Failure": a load failure is reported, never silent.
/// </summary>
public abstract record AbiPluginLoadResult
{
    private AbiPluginLoadResult() { }

    /// <summary>The host process started, the ABI handshake and <c>describe</c> both succeeded, and
    /// its process is registered for reaping. <see cref="AbiPlugin.Start"/> has not run yet — same
    /// point in the lifecycle <see cref="ManagedPluginLoadResult.Loaded"/> marks for a managed
    /// plugin, whose constructor has run but whose own <c>Start</c> is still the caller's to call.</summary>
    public sealed record Loaded(IPlugin Instance, PluginManifest Manifest) : AbiPluginLoadResult;

    /// <summary>Any reason the plugin is not running — the host executable could not be launched,
    /// the native library could not be loaded, the ABI version handshake failed, or
    /// <c>cxagent_plugin_describe</c> returned something malformed or mismatched against the
    /// sidecar. <see cref="Reason"/> names which one.</summary>
    public sealed record Failed(string Reason) : AbiPluginLoadResult;
}

/// <summary>
/// Constructs an <see cref="IPlugin"/> backed by a <c>cxagent-plugin-host</c> subprocess — the
/// second of the two v1 loaders (the plugin design, "The v1 cut": "Both loaders ship in v1. Managed
/// in-process and ABI out-of-process, against one contract."), and the counterpart
/// <see cref="ManagedPluginLoader"/> already documents its own doc as needing.
///
/// <para>ONE HOST PROCESS PER PLUGIN INSTANCE — Task 9's brief: "so a faulting plugin does not take
/// its neighbours with it." Each call to <see cref="Load"/> spawns its own subprocess; nothing here
/// pools or reuses one across plugins.</para>
///
/// <para>THE SIDECAR IS STILL WHAT'S CHECKED, NOT THE WIRE MANIFEST ALONE — mirroring
/// <see cref="ManagedPluginLoader"/>'s own "the sidecar and what Load returns must match" gate.
/// <c>cxagent_plugin_describe</c> answers the wire equivalent of a managed plugin's
/// <c>IPlugin.Load</c> return value, and the same mismatch check applies for the same reason: the
/// file a user was asked to approve must describe what actually runs, regardless of which loader
/// loaded it.</para>
/// </summary>
public static class AbiPluginLoader
{
    /// <summary>
    /// Loads the ABI plugin at <paramref name="libraryPath"/>: launches <paramref name="hostDllPath"/>
    /// against it, waits for the ABI handshake and <c>describe</c>, checks the result against the
    /// sidecar, registers the host process for reaping, and — on success — runs the managed
    /// <see cref="IPlugin.Load"/> step so the returned instance is at the same point in its
    /// lifecycle <see cref="ManagedPluginLoader.Load"/> leaves a managed plugin at.
    /// </summary>
    /// <param name="hostDllPath">
    /// Path to <c>cxagent-plugin-host.dll</c> — resolved by the CALLER, not this method. Task 9c's
    /// own lesson (see the LSP plugin's sidecar fix) is that a path resolved from
    /// <c>AppContext.BaseDirectory</c> silently means "the loading process's own directory," which
    /// is right for a plugin sitting beside the host app in a test but wrong for any real install
    /// layout; only the caller knows where <c>cxagent-plugin-host.dll</c> was actually deployed.
    /// </param>
    /// <param name="libraryPath">
    /// Path to the plugin's native shared library — ABSOLUTE, resolved from the load-set directory
    /// the same way <see cref="PluginResolver.Resolve"/> already resolves a managed plugin's
    /// assembly path, for the identical reason: a relative path here would be relative to the HOST
    /// PROCESS'S working directory, not the plugin's own load-set directory, the first time the two
    /// differ.
    /// </param>
    /// <param name="context">Handed to the resulting <see cref="IPlugin.Load"/> unchanged — the
    /// working directory and settings it carries are what <see cref="AbiPlugin.Start"/> later sends
    /// as <c>cxagent_plugin_start</c>'s context. <see cref="IPluginContext.RegisterChildProcess"/>
    /// is called on this context for the host process itself, separately from and before that.</param>
    /// <param name="ct">Cancels the handshake wait. Not threaded into the returned instance's own
    /// calls — those each take their own token, matching <see cref="IPlugin.Load"/>'s own contract.</param>
    public static async Task<AbiPluginLoadResult> Load(string hostDllPath, string libraryPath,
        IPluginContext context, CancellationToken ct)
    {
        if (!File.Exists(hostDllPath))
            return new AbiPluginLoadResult.Failed($"no plugin host executable at '{hostDllPath}'.");
        if (!File.Exists(libraryPath))
            return new AbiPluginLoadResult.Failed($"no plugin library at '{libraryPath}'.");

        var sidecarPath = Path.ChangeExtension(libraryPath, null) + ".plugin.json";
        if (!File.Exists(sidecarPath))
            return new AbiPluginLoadResult.Failed(
                $"no sidecar manifest at '{sidecarPath}' for plugin '{libraryPath}'.");

        string sidecarJson;
        try
        {
            sidecarJson = await File.ReadAllTextAsync(sidecarPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AbiPluginLoadResult.Failed($"could not read sidecar manifest '{sidecarPath}': {ex.Message}");
        }

        var parsedSidecar = PluginManifest.Parse(sidecarJson);
        if (parsedSidecar.Manifest is null)
            return new AbiPluginLoadResult.Failed(
                $"sidecar manifest '{sidecarPath}' is invalid: {string.Join("; ", parsedSidecar.Errors)}");
        // A REFUSED KIND STILL FAILS THE LOAD — see ManagedPluginLoader.Load's identical check for
        // why: forward compatibility is for a FUTURE build reading an old manifest, not license to
        // silently accept a sidecar declaring more than THIS build services.
        if (!parsedSidecar.IsSuccess)
            return new AbiPluginLoadResult.Failed(
                $"sidecar manifest '{sidecarPath}' declares more than this build services: "
                + string.Join("; ", parsedSidecar.Errors));

        var sidecar = parsedSidecar.Manifest;

        (AbiHostProcess host, AbiHostProcess.StartResult handshake) launch;
        try
        {
            launch = await AbiHostProcess.Launch(hostDllPath, libraryPath);
        }
        catch (Exception ex)
        {
            // Process.Start itself can throw (the "dotnet" launcher missing, a permissions error) —
            // caught here rather than left to propagate, the same "a load failure is reported, never
            // an unhandled exception" discipline ManagedPluginLoader.Load applies to Assembly.LoadFrom.
            return new AbiPluginLoadResult.Failed($"could not launch plugin host for '{libraryPath}': {ex.Message}");
        }

        var (host, handshake) = launch;

        // REGISTERED THE MOMENT THE PROCESS EXISTS, before the handshake is even read — see
        // CxagentLspPlugin.Start's identical ordering for its own child process: a host that dies
        // partway through its own startup (a version mismatch, a describe that throws) still leaves
        // a process that needs reaping, and registering only after a successful handshake would
        // leak exactly that case.
        context.RegisterChildProcess(host.ProcessId);

        if (!handshake.Ready || handshake.Manifest is null)
        {
            await host.DisposeAsync();
            return new AbiPluginLoadResult.Failed(
                $"plugin host for '{libraryPath}' failed to start: {handshake.Error ?? "no reason given."}");
        }

        var wireManifest = handshake.Manifest;

        var difference = PluginManifestMatch.Mismatch(sidecar, wireManifest, "describe");
        if (difference is not null)
        {
            await host.DisposeAsync();
            return new AbiPluginLoadResult.Failed(
                $"'{libraryPath}' does not match its sidecar manifest '{sidecarPath}': {difference}");
        }

        var plugin = new AbiPlugin(host, wireManifest);

        PluginManifest loaded;
        try
        {
            loaded = await plugin.Load(context, ct);
        }
        catch (Exception ex)
        {
            await host.DisposeAsync();
            return new AbiPluginLoadResult.Failed($"'{sidecar.Name}' threw from Load: {ex.Message}");
        }

        return new AbiPluginLoadResult.Loaded(plugin, loaded);
    }
}
