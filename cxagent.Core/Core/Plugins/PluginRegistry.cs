using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>
/// One loaded plugin: the running instance, what it declared, and how many of its calls are
/// currently in flight — kept as one record so <see cref="PluginRegistry.UnwireAsync"/> can hold a
/// reference to all three after removing the plugin from the registry's own list.
/// </summary>
internal sealed class LoadedPlugin(IPlugin instance, PluginManifest manifest)
{
    public IPlugin Instance { get; } = instance;
    public PluginManifest Manifest { get; } = manifest;

    /// <summary>
    /// Calls into this plugin's tools currently in flight. Incremented before dispatch, decremented
    /// in a finally — see <see cref="PluginRegistry.UnwireAsync"/>, which waits on this to reach
    /// zero AFTER deregistering, so draining has something finite to wait for.
    /// </summary>
    public int InFlight;
}

/// <summary>
/// What happened to a load attempt.
///
/// <para>A DUPLICATE NAME REFUSES THE WHOLE PLUGIN, never just the colliding tool — see PLUGINS.md,
/// "Name collisions": a plugin that half-loaded is a plugin whose behaviour nobody can predict from
/// its manifest. This is deliberately NOT <see cref="Jobs.AgentToolset"/>'s rule, which resolves a
/// duplicate name last-registration-wins; that is right for one embedder's own tools composed
/// together and wrong for a plugin, where silently winning a name is exactly what PLUGINS.md
/// forbids.</para>
/// </summary>
public abstract record PluginLoadResult
{
    private PluginLoadResult() { }

    /// <summary>The plugin's tools are live and offered from the next turn boundary.</summary>
    public sealed record Loaded : PluginLoadResult;

    /// <summary>
    /// A tool name in this manifest is already taken — by a built-in, an injected tool, or another
    /// plugin. Nothing from this plugin was registered.
    /// </summary>
    public sealed record NameCollision(string ToolName) : PluginLoadResult;
}

/// <summary>
/// The mutable set of tools plugins contribute to one session — the registry PLUGINS.md's whole
/// design rests on: "a registry that can be mutated at a turn boundary and that refuses collisions,
/// sitting in the same chain position rather than inside the existing set."
///
/// <para>ONE PER SESSION, like <see cref="Jobs.AgentToolset"/> and everything else a plugin touches
/// — see PLUGINS.md, "Scope: one instance per session".</para>
///
/// <para><see cref="CurrentTools"/> IS THE SEAM. It is handed to <c>SessionPorts.DynamicTools</c> as
/// a live delegate, exactly the shape <c>DynamicToolSourceTests</c> already exercises: consulted
/// fresh per turn rather than snapshotted, so a plugin loaded or unwired between two turns is
/// offered or withdrawn on the very next one with no restart.</para>
///
/// <para>THREAD-SAFETY: load and unwire both take <see cref="_gate"/>, because a load racing an
/// unwire over the same collection is exactly the kind of interleaving that corrupts a list rather
/// than throwing. Turn boundaries already serialise callers in practice — <see
/// cref="Sessions.Session.LoadPlugin"/> and <c>UnwirePlugin</c> both refuse while a turn is running
/// — but the lock costs nothing and does not depend on that discipline being perfect.</para>
/// </summary>
public sealed class PluginRegistry
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly object _gate = new();

    /// <summary>
    /// Registers a plugin's tools, refusing the whole plugin on any name collision — with a
    /// built-in or injected tool (via <paramref name="isNameTaken"/>, which the session supplies
    /// since only it knows those sets) or with another already-loaded plugin.
    /// </summary>
    /// <param name="plugin">The running instance — kept so <see cref="UnwireAsync"/> can call Stop.</param>
    /// <param name="manifest">What this plugin contributes, from its own Load call.</param>
    /// <param name="isNameTaken">
    /// Answers whether a name is already occupied outside this registry. The registry only knows
    /// its own plugins' names; a built-in's or an injected tool's name is Session's to judge.
    /// </param>
    public PluginLoadResult Load(IPlugin plugin, PluginManifest manifest,
        Func<string, bool> isNameTaken)
    {
        lock (_gate)
        {
            foreach (var tool in manifest.Tools)
            {
                if (isNameTaken(tool.Name) || _plugins.Any(p => p.Manifest.Tools.Any(t => t.Name == tool.Name)))
                    return new PluginLoadResult.NameCollision(tool.Name);
            }

            _plugins.Add(new LoadedPlugin(plugin, manifest));
            return new PluginLoadResult.Loaded();
        }
    }

    /// <summary>
    /// Every tool every loaded plugin currently contributes, as <see cref="IAgentTool"/> — the
    /// value to hand <c>SessionPorts.DynamicTools</c> or a dynamic-tools delegate composed with it.
    ///
    /// <para>A LIVE READ, matching <see cref="Sessions.Session.IsBusy"/>'s own contract: called
    /// fresh at the definitions site of every turn, never cached, so a load or unwire between two
    /// turns is reflected on the very next one.</para>
    /// </summary>
    public IReadOnlyList<IAgentTool> CurrentTools()
    {
        lock (_gate)
            return _plugins
                .SelectMany(p => p.Manifest.Tools.Select(t => (IAgentTool)new PluginTool(p, t)))
                .ToList();
    }

    /// <summary>Every plugin currently loaded, by name — for a caller that needs to know what is
    /// live without reaching for the tools themselves (Session's collision check, and tests).</summary>
    public IReadOnlyList<string> LoadedPluginNames
    {
        get { lock (_gate) return _plugins.Select(p => p.Manifest.Name).ToList(); }
    }

    /// <summary>
    /// Marks one call to this plugin as in flight until <paramref name="release"/> completes —
    /// TEST-ONLY, standing in for a real dispatch (which nothing can yet perform; see
    /// <see cref="PluginTool"/>'s own note) so <c>UnwireDeregistersBeforeDraining</c> can prove the
    /// drain step actually waits, rather than only proving deregistration removed the plugin.
    /// </summary>
    internal Task HoldCallOpenForTest(string pluginName, Task release)
    {
        LoadedPlugin plugin;
        lock (_gate) plugin = _plugins.First(p => p.Manifest.Name == pluginName);

        Interlocked.Increment(ref plugin.InFlight);
        return release.ContinueWith(_ => Interlocked.Decrement(ref plugin.InFlight),
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// Unwires one plugin: deregister, drain, Stop, reap — in that order, and the order is the
    /// contract. See PLUGINS.md, "Unwire is one ordered operation".
    ///
    /// <para>DEREGISTER FIRST. Removing the plugin from <see cref="_plugins"/> before anything else
    /// is what makes the drain below finite: a plugin still reachable from <see cref="CurrentTools"/>
    /// could be handed new work for as long as draining waits, and the wait would never end.</para>
    ///
    /// <para>DRAIN BEFORE STOP. Waiting for <see cref="LoadedPlugin.InFlight"/> to reach zero is what
    /// keeps a call already accepted from failing for a reason nobody could trace to a plugin
    /// command — an executor's job can outlive the turn that started it, so refusing loads mid-turn
    /// does not by itself mean nothing of this plugin's is running.</para>
    ///
    /// <para>REAP TODAY IS A NO-OP: nothing in this task records a plugin's child processes (that is
    /// the managed loader's <c>RegisterChildProcess</c> plumbing), so there is nothing yet for this
    /// step to kill. The step is still named and still runs last, so a later reaping implementation
    /// has an ordered slot rather than a redesign.</para>
    /// </summary>
    /// <returns>False when no plugin of this name is loaded — there was nothing to unwire.</returns>
    public async Task<bool> UnwireAsync(string pluginName, CancellationToken ct)
    {
        LoadedPlugin? plugin;
        lock (_gate)
        {
            plugin = _plugins.FirstOrDefault(p => p.Manifest.Name == pluginName);
            if (plugin is null) return false;

            // STEP 1: DEREGISTER. Removed from the list under the same lock CurrentTools reads
            // through, so no turn beginning after this line can be offered this plugin's tools.
            _plugins.Remove(plugin);
        }

        // STEP 2: DRAIN. Polls rather than a signal, because InFlight is decremented from whatever
        // thread a call happens to finish on — a bounded poll is simpler than wiring a
        // TaskCompletionSource per plugin for an operation that runs once per unwire.
        while (Volatile.Read(ref plugin.InFlight) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }

        // STEP 3: STOP. The plugin's own shutdown — its children exit.
        await plugin.Instance.Stop(ct);

        // STEP 4: REAP. See the no-op note above.

        return true;
    }

    /// <summary>
    /// Unwires every loaded plugin, in no particular order — session close runs this. PLUGINS.md:
    /// "closing a session is unwiring every plugin it loaded, and a plugin cannot tell the
    /// difference" from the four-step path above, so this is that path, once per plugin.
    /// </summary>
    public async Task UnwireAllAsync(CancellationToken ct)
    {
        List<string> names;
        lock (_gate) names = _plugins.Select(p => p.Manifest.Name).ToList();

        foreach (var name in names)
            await UnwireAsync(name, ct);
    }

    /// <summary>
    /// Adapts one plugin tool into <see cref="IAgentTool"/>, counting it in and out of
    /// <see cref="LoadedPlugin.InFlight"/> so <see cref="UnwireAsync"/> has something to drain, and
    /// routing a call into the plugin BY NAME — see <see cref="IPlugin.Invoke"/>: one plugin instance
    /// is the executor behind every tool it declared, told apart by the name pinned here at
    /// construction, the same shape <c>ToolBindings</c> already has for several tools sharing one
    /// executor.
    /// </summary>
    private sealed class PluginTool(LoadedPlugin plugin, PluginToolManifest tool) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(tool.Name, tool.Description, tool.InputSchema);

        // A MINIMAL GATE FOR NOW, NOT THE REAL POLICY. PLUGINS.md's "the plugin provides its own
        // policy; Core enforces it" describes a richer shape — the plugin choosing what to show and
        // how a call generalises — which is a later task's to build. tool.Gated only distinguishes
        // "asks" from "does not"; a plugin declaring gated=true asks EVERY call (no AlwaysRule, so
        // no stored rule can ever match it) rather than being silently ungated, which is the wrong
        // side to default to for a declared-but-unimplemented policy.
        public Permissions.PermissionRequest? Gate(JobParameters call) => tool.Gated
            ? new Permissions.PermissionRequest(Permissions.PermissionKind.Tool,
                $"run '{tool.Name}' from the '{plugin.Manifest.Name}' plugin", AlwaysRule: null)
            : null;

        public async Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref plugin.InFlight);
            try
            {
                return await plugin.Instance.Invoke(tool.Name, call, context, ct);
            }
            finally
            {
                Interlocked.Decrement(ref plugin.InFlight);
            }
        }
    }
}
