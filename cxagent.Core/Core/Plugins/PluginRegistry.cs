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
/// <para>A DUPLICATE NAME REFUSES THE WHOLE PLUGIN, never just the colliding tool — see the plugin design,
/// "Name collisions": a plugin that half-loaded is a plugin whose behaviour nobody can predict from
/// its manifest. This is deliberately NOT <see cref="Jobs.AgentToolset"/>'s rule, which resolves a
/// duplicate name last-registration-wins; that is right for one embedder's own tools composed
/// together and wrong for a plugin, where silently winning a name is exactly what the plugin design
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

/// <summary>One plugin's system-prompt text and the tools it governs — see
/// <see cref="PluginRegistry.InstructionsForPrompt"/>.</summary>
/// <param name="Plugin">The plugin's own name, for the heading and for a stable sort.</param>
/// <param name="Tools">Its tool names, so the text is attributable to the tools it describes.</param>
/// <param name="Text">The manifest's instructions, trimmed.</param>
public sealed record PluginInstructions(string Plugin, IReadOnlyList<string> Tools, string Text);

/// <summary>
/// The mutable set of tools plugins contribute to one session — the registry the plugin design's whole
/// design rests on: "a registry that can be mutated at a turn boundary and that refuses collisions,
/// sitting in the same chain position rather than inside the existing set."
///
/// <para>ONE PER SESSION, like <see cref="Jobs.AgentToolset"/> and everything else a plugin touches
/// — see the plugin design, "Scope: one instance per session".</para>
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
    /// <summary>The production default for <see cref="UnwireAsync"/>'s Stop timeout — see that
    /// method's own doc for why a hung Stop is abandoned rather than awaited forever.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);

    private readonly List<LoadedPlugin> _plugins = [];
    private readonly object _gate = new();
    private readonly TimeSpan _stopTimeout;
    private ChildProcessStore? _childProcesses;
    private Action<string> _log = _ => { };

    /// <param name="stopTimeout">How long <see cref="UnwireAsync"/> waits for a plugin's Stop before
    /// abandoning it. Null takes <see cref="DefaultStopTimeout"/> — a parameter rather than a fixed
    /// constant so a test proving the timeout actually fires does not have to run it for ten real
    /// seconds to do so.</param>
    public PluginRegistry(TimeSpan? stopTimeout = null)
    {
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
    }

    /// <summary>
    /// Gives this registry somewhere to record and reap the processes its plugins spawn, and
    /// somewhere to say so when a reap or a Stop timeout happens.
    ///
    /// <para>NOT A CONSTRUCTOR PARAMETER. <see cref="Sessions.Session.Plugins"/> is built in a field
    /// initialiser, before the session's first wire — the same ordering constraint
    /// <see cref="Sessions.Session"/>'s own doc states for why it takes only its working directory.
    /// <see cref="Sessions.SharedServices.GlobalInstructionsDir"/>, which the store's directory comes
    /// from, is not known until then. A test that never calls this attaches nothing, and every reap
    /// below is a no-op rather than a null-reference — the same "no gate, no prompt" shape the rest
    /// of wiring already uses for an absent dependency.</para>
    ///
    /// <para><paramref name="log"/> IS NOT A PLUGIN'S OWN <see cref="IPluginLogger"/> — a hung or
    /// crashed plugin cannot be trusted to relay its own diagnosis, which is the same reasoning
    /// the plugin design gives for why reaping is Core's obligation rather than the plugin's bookkeeping.
    /// This is the session's own log line, the same sink <c>Say</c> writes an ordinary notice to.</para>
    /// </summary>
    internal void AttachChildProcessStore(ChildProcessStore store, Action<string> log)
    {
        _childProcesses = store;
        _log = log;
    }

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

    /// <summary>
    /// Each loaded plugin's system-prompt text, by plugin name — empty when it declared none.
    ///
    /// <para>READ PER TURN, NOT CACHED, and that is the difference from MCP's equivalent. An MCP
    /// server can finish its handshake at turn 82, unbidden, and rewriting the prompt then would
    /// force a full reprocess for something nobody asked for — so that one is cached at first use.
    /// A plugin arrives because the user typed <c>/plugin load</c> at a turn boundary and asked the
    /// tool list to change. Its guidance has to move with its tools, or the model reads instructions
    /// for tools it does not have, or has tools with no instructions.</para>
    ///
    /// <para>STABLE WHILE NOTHING LOADS OR UNWIRES: the manifest is a record fixed at load, and this
    /// list only changes under those two operations, both refused mid-turn. So a per-turn read
    /// renders byte-identical text and the prompt prefix stays cached until a user action changes
    /// it — which is the same rule the tool list itself already follows.</para>
    ///
    /// <para>THE TOOL NAMES TRAVEL WITH THE TEXT. A second language-server plugin's guidance would
    /// otherwise be a second block making overlapping claims about "positions" and "the server",
    /// with nothing saying which tools each governs.</para>
    /// </summary>
    public IReadOnlyList<PluginInstructions> InstructionsForPrompt()
    {
        lock (_gate)
            return _plugins
                .Where(p => !string.IsNullOrWhiteSpace(p.Manifest.Instructions))
                .Select(p => new PluginInstructions(
                    p.Manifest.Name,
                    [.. p.Manifest.Tools.Select(t => t.Name)],
                    p.Manifest.Instructions!.Trim()))
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
    /// contract. See the plugin design, "Unwire is one ordered operation".
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
    /// <para>REAP KILLS WHATEVER OUTLIVED STOP. A well-behaved plugin's own Stop already exits its
    /// children, so the ordinary case finds nothing left; reap exists for the plugin that did not —
    /// crashed inside Stop, or is the timed-out case below — and closes the plugin design's stated gap:
    /// "an orphaned subprocess is the one failure in this feature that outlives the app." Reaping
    /// here, not only at startup, is what "Unwiring must reap" asks for: a host killed only at
    /// startup survives for the rest of THIS run if the plugin was merely unwired, not crashed.</para>
    ///
    /// <para>STOP HAS A TIMEOUT — the plugin design, "Stop has a timeout, and the remedy differs by
    /// loader". A managed plugin runs in-process, so there is no host to kill when it hangs: the
    /// call is abandoned (its Task is left running rather than awaited further) and the hang is
    /// logged naming the plugin, exactly as that section specifies. AN ABANDONED STOP CANNOT BE
    /// CANCELLED FROM HERE — <paramref name="ct"/> is not passed to it, deliberately: a plugin's
    /// Stop is handed its OWN token (<see cref="IPluginContext.Lifetime"/>, cancelled by the loader
    /// that owns the instance) and this method has no authority to interrupt code it does not
    /// control, only to stop waiting for it. THE ABI HALF OF THIS ASYMMETRY — killing a host process
    /// after the same timeout — has no loader to implement it against yet; this is the managed half
    /// the plugin design asks Task 6 to ship, with the process-kill path left for the ABI task to fill.</para>
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

        // STEP 3: STOP, bounded by _stopTimeout — see this method's own doc for the managed/ABI
        // asymmetry. Task.WhenAny rather than a CancellationTokenSource on `ct` above: cancelling the
        // token passed to Stop would ask a MANAGED plugin's own code to observe cancellation it may
        // never check, which is indistinguishable from the hang this timeout exists to survive. The
        // await here is abandoned, not cancelled — the Task keeps running until the plugin's own
        // Lifetime token (cancelled by whoever owns the instance) eventually stops it, if ever.
        var stop = plugin.Instance.Stop(ct);
        var finished = await Task.WhenAny(stop, Task.Delay(_stopTimeout, ct));
        if (finished != stop)
            _log($"plugin '{pluginName}': Stop did not return within {_stopTimeout} — "
                + "abandoning it and closing the session around it. Any process this plugin spawned "
                + "is left to the pid record to reap.");

        // STEP 4: REAP. Kills any process this plugin registered whose recorded start time still
        // matches — see ChildProcessStore.ReapPlugin. A plugin that Stopped cleanly already exited
        // its own children, so this ordinarily finds nothing; it exists for the plugin that did not.
        _childProcesses?.ReapPlugin(pluginName, _log);

        return true;
    }

    /// <summary>
    /// Unwires every loaded plugin, in no particular order — session close runs this. The plugin design:
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

        //
        // "ALWAYS" IS OFFERED, AND THE USER OWNS THAT DECISION. Withholding it would not make a
        // plugin safer: the binary was already approved at load, against a hash of its whole load
        // set, which is the boundary that actually decides whether this code runs at all. What
        // withholding DOES do is make a trusted plugin's every call a question, and a tool that
        // interrupts on all of them is one users route around by disabling gating wholesale — a
        // worse outcome than the standing grant it was avoiding.
        //
        // THE RULE NAMES THE PLUGIN, NOT ONLY THE TOOL. A bare "tool lsp_definition" would survive
        // uninstalling this plugin and installing a different one that happens to declare the same
        // name, handing the newcomer a grant the user gave someone else. Built-in tools can use the
        // bare form (GatedAgentTool) because nothing else can ever claim their names.
        public Permissions.PermissionRequest? Gate(JobParameters call) => tool.Gated switch
        {
            PluginGating.Never => null,
            PluginGating.Always => Ask($"run '{tool.Name}' from the '{plugin.Manifest.Name}' plugin",
                                       tool.AlwaysAskable),
            _ => DynamicGate(call),
        };

        /// <summary>
        /// The per-call decision, asked of the plugin itself.
        ///
        /// <para>FAILURE ASKS AND WITHHOLDS "ALWAYS". A gate that throws has told us nothing about
        /// this call, so the safe reading is "ask" — and a broken gate must not be able to earn a
        /// standing grant while broken, which is the one case where a manifest saying
        /// alwaysAskable:true is overruled.</para>
        /// </summary>
        private Permissions.PermissionRequest? DynamicGate(JobParameters call)
        {
            // NOT A CAST. ManagedPluginLoader refuses a plugin that declares "dynamic" without a
            // gate, so reaching here without one is a host that skipped that check — an embedder
            // wiring a plugin by hand, or a loader yet to grow the same refusal. Asking is the only
            // safe reading: the manifest said this call might need permission and nothing can now
            // say it does not.
            if (plugin.Instance is not IPluginGateSource source)
                return Ask($"run '{tool.Name}' from the '{plugin.Manifest.Name}' plugin "
                         + "(it declares a per-call permission check but provides none)",
                    alwaysAskable: false);

            PluginGate? gate;
            try
            {
                gate = source.Gate(tool.Name, call);
            }
            catch (Exception)
            {
                return Ask($"run '{tool.Name}' from the '{plugin.Manifest.Name}' plugin "
                         + "(its permission check failed)", alwaysAskable: false);
            }

            if (gate is null) return null;

            // THE PLUGIN'S WORDING, NEVER ITS SCOPE. Display is the plugin's — it saw the arguments
            // and can name the file. Kind and AlwaysRule are built here, so no plugin can turn its
            // own prompt into a grant over shell, files or anything else it does not own.
            //
            // ALWAYSASKABLE IS AN AND: the manifest is a floor. A sidecar that withheld "Always" is
            // a promise the user read before approving the load, and a runtime call does not get to
            // hand it back.
            return Ask(string.IsNullOrWhiteSpace(gate.Display)
                    ? $"run '{tool.Name}' from the '{plugin.Manifest.Name}' plugin"
                    : gate.Display,
                gate.AlwaysAskable && tool.AlwaysAskable);
        }

        /// <summary>
        /// A NULL AlwaysRule IS HOW "no Always button" IS EXPRESSED — see PermissionRequest's own
        /// doc. The plugin declaring alwaysAskable:false is marking its own sharp edge: the tool it
        /// believes should never hold a standing grant, which is a judgement only its author can make.
        /// </summary>
        private Permissions.PermissionRequest Ask(string display, bool alwaysAskable) =>
            new(Permissions.PermissionKind.Tool, display,
                AlwaysRule: alwaysAskable ? $"plugin {plugin.Manifest.Name} tool {tool.Name}" : null);

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
