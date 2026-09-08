using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins;

/// <summary>
/// One loaded plugin: the running instance, what it declared, how many of its calls are currently
/// in flight, and the two things unwire must sever — kept as one record so
/// <see cref="PluginRegistry.UnwireAsync"/> can hold a reference to all of it after removing the
/// plugin from the registry's own list.
/// </summary>
/// <param name="context">
/// The context THIS plugin was loaded with, retained so <see cref="PluginRegistry.UnwireAsync"/> can
/// dispose it — the retention Task 1 could not solve, because nothing before this held a reference
/// to a runtime-loaded plugin's context past its own construction. Disposing it is what cancels
/// <see cref="IPluginContext.Lifetime"/>, which is what lets an abandoned Stop ever observe its
/// session ending rather than running to completion or hanging forever.
/// </param>
/// <param name="client">
/// This plugin's own handle on the session, severed at unwire before Stop runs — see
/// <see cref="PluginRegistry.UnwireAsync"/>. Null for a context built without one (a test fixture,
/// mainly); a plugin loaded through <see cref="Sessions.Session.LoadPlugin"/> always has one.
/// </param>
/// <param name="commands">
/// The table this plugin's <see cref="PluginManifest.Commands"/> were registered into, retained so
/// <see cref="PluginRegistry.UnwireAsync"/> can deregister them without asking the caller to supply
/// the same registry a second time at unwire. Null when this plugin declared no commands, or was
/// loaded with none to register into.
/// </param>
internal sealed class LoadedPlugin(IPlugin instance, PluginManifest manifest,
    IDisposable? context = null, SessionPluginClient? client = null, CommandRegistry? commands = null)
{
    public IPlugin Instance { get; } = instance;
    public PluginManifest Manifest { get; } = manifest;
    public IDisposable? Context { get; } = context;
    public SessionPluginClient? Client { get; } = client;
    public CommandRegistry? Commands { get; } = commands;

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
/// <para>A DUPLICATE NAME REFUSES THE WHOLE PLUGIN, never just the colliding tool: a plugin that
/// half-loaded is a plugin whose behaviour nobody can predict from its manifest. This is
/// deliberately NOT <see cref="Jobs.AgentToolset"/>'s rule, which resolves a duplicate name
/// last-registration-wins; that is right for one embedder's own tools composed together and wrong
/// for a plugin, where silently winning a name it collided with is a collision a user approved
/// neither instance of.</para>
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

    /// <summary>
    /// A command name in this manifest is already taken — by a built-in (<c>/model</c>) or another
    /// plugin. Nothing from this plugin was registered, tools included: checked and refused before
    /// any registration runs, so a plugin whose SECOND command collides never leaves its first (or
    /// its tools) registered behind it — see <see cref="PluginRegistry.Load"/>.
    /// </summary>
    public sealed record CommandNameCollision(string CommandName) : PluginLoadResult;
}

/// <summary>One plugin's system-prompt text and the tools it governs — see
/// <see cref="PluginRegistry.InstructionsForPrompt"/>.</summary>
/// <param name="Plugin">The plugin's own name, for the heading and for a stable sort.</param>
/// <param name="Tools">Its tool names, so the text is attributable to the tools it describes.</param>
/// <param name="Text">The manifest's instructions, trimmed.</param>
public sealed record PluginInstructions(string Plugin, IReadOnlyList<string> Tools, string Text);

/// <summary>
/// The mutable set of tools plugins contribute to one session: a registry that can be mutated at a
/// turn boundary and that refuses collisions, sitting in the same chain position rather than inside
/// the existing set.
///
/// <para>ONE PER SESSION, like <see cref="Jobs.AgentToolset"/> and everything else a plugin
/// touches.</para>
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

    // BESIDE _plugins, NOT A FLAG ON LoadedPlugin: UnwireAsync removes the LoadedPlugin record,
    // and the fact recorded here has to outlive exactly that removal.
    private readonly HashSet<string> _everLoaded = new(StringComparer.Ordinal);

    /// <summary>
    /// One queue shared by every plugin this registry loads — see <see cref="PluginSubmitQueue"/>'s
    /// own doc. ONE PER SESSION, NOT ONE PER PLUGIN: the bound on how much submitted work a session
    /// tolerates is a fact about the session, not about any one plugin, so two plugins each staying
    /// under a per-plugin cap could otherwise queue an unbounded total between them.
    /// </summary>
    public PluginSubmitQueue SubmitQueue { get; } = new();

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
    /// crashed plugin cannot be trusted to relay its own diagnosis, which is why reaping is Core's
    /// obligation rather than the plugin's bookkeeping. This is the session's own log line, the same
    /// sink <c>Say</c> writes an ordinary notice to.</para>
    /// </summary>
    /// <param name="sessionId">
    /// Whose registry this is, so an unwire reaps only what THIS session's copy of a plugin spawned.
    /// A plugin is loaded per session; matching on the plugin name alone made one session's unwire
    /// kill every other session's children — see <see cref="ChildProcessRecord.Session"/>.
    /// </param>
    internal void AttachChildProcessStore(ChildProcessStore store, Action<string> log,
                                          string? sessionId = null)
    {
        _childProcesses = store;
        _log = log;
        _sessionId = sessionId;
    }

    /// <inheritdoc cref="AttachChildProcessStore"/>
    private string? _sessionId;

    /// <summary>
    /// Registers a plugin's tools and declared commands, refusing the whole plugin on any name
    /// collision — a tool or command already offered by a built-in, an injected tool, a front end's
    /// own command, or another already-loaded plugin.
    /// </summary>
    /// <param name="plugin">The running instance — kept so <see cref="UnwireAsync"/> can call Stop.</param>
    /// <param name="manifest">What this plugin contributes, from its own Load call.</param>
    /// <param name="isNameTaken">
    /// Answers whether a name is already occupied outside this registry. The registry only knows
    /// its own plugins' names; a built-in's or an injected tool's name is Session's to judge.
    /// </param>
    /// <param name="context">
    /// The context this plugin was loaded with, retained so <see cref="UnwireAsync"/> can dispose it
    /// and cancel <see cref="IPluginContext.Lifetime"/>. Null for a caller with no runtime context to
    /// retain — most of this registry's own test fixtures.
    /// </param>
    /// <param name="client">
    /// This plugin's handle on the session, retained so <see cref="UnwireAsync"/> can sever it before
    /// Stop runs. Null when this plugin was loaded without one.
    /// </param>
    /// <param name="isCommandNameTaken">
    /// Answers whether a command name is already registered outside this plugin's own manifest — a
    /// built-in (<c>/model</c>) or a front end's own command. Null for a caller with no command
    /// table to check against (most of this registry's own test fixtures), in which case a manifest
    /// declaring commands is refused only against other loaded plugins, never a built-in.
    /// </param>
    /// <param name="commands">
    /// The table this plugin's commands are registered into, or null to register none — the same
    /// "no gate, no prompt" shape the rest of plugin wiring already uses for an absent dependency.
    /// A caller supplying <paramref name="isCommandNameTaken"/> without this registers nothing
    /// runnable while still refusing collisions, which is never useful; Session always supplies both
    /// or neither.
    /// </param>
    public PluginLoadResult Load(IPlugin plugin, PluginManifest manifest,
        Func<string, bool> isNameTaken, IDisposable? context = null, SessionPluginClient? client = null,
        Func<string, bool>? isCommandNameTaken = null, CommandRegistry? commands = null)
    {
        lock (_gate)
        {
            foreach (var tool in manifest.Tools)
            {
                if (isNameTaken(tool.Name) || _plugins.Any(p => p.Manifest.Tools.Any(t => t.Name == tool.Name)))
                    return new PluginLoadResult.NameCollision(tool.Name);
            }

            // EVERY COMMAND CHECKED BEFORE ANY IS REGISTERED, for the same reason tools are: a
            // plugin whose SECOND command collides must not leave its first sitting in the table —
            // Register REPLACES rather than refusing, so a half-registered plugin here would leave a
            // HOLE where a built-in used to be, not merely fail to add itself. isCommandNameTaken is
            // asked first, matching isNameTaken's own precedence — a null delegate means no built-in
            // table to check, not "nothing is taken".
            //
            // THE SLASH IS ADDED HERE, NOT CARRIED IN THE MANIFEST. A plugin declares "schedule" —
            // see PluginManifest.Parse and IPluginCommandHandler.RunCommand's own doc, "without its
            // leading slash" — but SessionCommand.Name and CommandRegistry both key on the form the
            // user types, "/schedule". One spelling crossing the plugin boundary and another inside
            // Core is deliberate: RunCommand's argument is what THIS plugin calls its own command,
            // while the registry's key is what EVERY command in the session is called, slash
            // included, so two plugins cannot declare names that only differ by it.
            foreach (var command in manifest.Commands)
            {
                var name = "/" + command.Name;
                if ((isCommandNameTaken?.Invoke(name) ?? false)
                    || _plugins.Any(p => p.Manifest.Commands.Any(c => "/" + c.Name == name)))
                    return new PluginLoadResult.CommandNameCollision(command.Name);
            }

            var loaded = new LoadedPlugin(plugin, manifest, context, client, commands);
            _plugins.Add(loaded);
            _everLoaded.Add(manifest.Name);

            // REGISTERED AFTER THE PLUGIN IS ADDED TO _plugins, not before: UnwireAsync's own
            // deregistration (step 1) removes commands by name from `commands` and the plugin from
            // `_plugins` together, and doing this add last here mirrors that — nothing outside this
            // lock can observe a plugin whose commands exist but which the collision scan above does
            // not yet know about.
            if (commands is not null)
                foreach (var command in manifest.Commands)
                    commands.Register(
                        new SessionCommand("/" + command.Name, command.Summary,
                            [.. command.Args.Select(a => new CommandArgument(a.Name, a.Summary))]),
                        (session, arguments) => RunPluginCommand(loaded, command.Name, session, arguments));

            return new PluginLoadResult.Loaded();
        }
    }

    /// <summary>
    /// The <see cref="CommandHandler"/> behind one of a plugin's declared commands — dispatches into
    /// the plugin's own <see cref="IPluginCommandHandler.RunCommand"/> and writes what comes back to
    /// the session's transcript, exactly as <see cref="Sessions.Session"/>'s own DOES-SAYS-ANNOUNCES
    /// command methods do (see <c>Session.ClearContext</c>) — a plugin's command reads like any other
    /// to whoever typed it.
    ///
    /// <para>FIRE AND FORGET, matching <c>/compress</c> and <c>/plugin</c> in
    /// <c>SessionManager.SeedCommands</c>: <see cref="CommandHandler"/> is synchronous, and a
    /// plugin's own command may run for as long as its author wants — a definition lookup, an index
    /// rebuild. The task's own faults are caught and said rather than left to become an unobserved
    /// exception, since nothing here awaits it to propagate one.</para>
    ///
    /// <para><see cref="IPlugin"/> IS NOT CAST AT REGISTRATION TIME. The type check already ran at
    /// load — <c>ManagedPluginLoader</c> refuses a manifest declaring commands a plugin's type does
    /// not implement <see cref="IPluginCommandHandler"/> for — so reaching here with a plugin that
    /// does not implement it would be that loader's own bug, not a caller's mistake to guard
    /// against a second time.</para>
    /// </summary>
    private static bool RunPluginCommand(LoadedPlugin plugin, string name, Sessions.Session session,
        string arguments)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var handler = (IPluginCommandHandler)plugin.Instance;
                var result = await handler.RunCommand(name, arguments, CancellationToken.None);
                if (result.Message is { } text)
                    session.SayPluginCommandResult(text, result.Status);
            }
            catch (Exception ex)
            {
                session.SayPluginCommandResult(
                    $"plugin '{plugin.Manifest.Name}' command '/{name}' failed: {ex.Message}",
                    PluginCommandOutcome.Refused);
            }
        });
        return true;
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
    /// Every plugin name this registry has loaded, including ones since unwired.
    ///
    /// <para>UNWIRE IS NOT UNLOAD. The managed loader deliberately uses no AssemblyLoadContext
    /// (<see cref="ManagedPluginLoader"/>'s own doc), so an assembly stays resident for the
    /// process's life once loaded — and on Windows its FILE stays locked with it. A caller
    /// deleting a plugin's files needs to know it was ever here, not whether it is here now.</para>
    /// </summary>
    public IReadOnlySet<string> EverLoadedNames
    {
        get { lock (_gate) return new HashSet<string>(_everLoaded, StringComparer.Ordinal); }
    }

    /// <summary>
    /// Whether the named loaded plugin holds an <see cref="IPluginClient"/> — TEST-ONLY, so a test
    /// can prove the sidecar's "client" declaration actually gated construction (see
    /// <see cref="Sessions.Session.LoadPlugin"/> and <c>PluginDiscovery</c>, both of which build one
    /// only when <see cref="PluginManifest.Client"/> is true) without a public accessor onto
    /// <see cref="LoadedPlugin"/> itself. Null when no plugin of this name is loaded.
    /// </summary>
    internal bool? HasClientForTest(string pluginName)
    {
        lock (_gate)
            return _plugins.FirstOrDefault(p => p.Manifest.Name == pluginName) is { } plugin
                ? plugin.Client is not null
                : null;
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
    /// Unwires one plugin: deregister, drain, sever, Stop, reap — in that order, and the order is
    /// the contract.
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
    /// <para>SEVER BEFORE STOP, not after. A plugin's own Stop may itself try to submit through
    /// <see cref="IPluginClient"/> — nothing here forbids it — and a client that still worked during
    /// Stop would let a plugin queue one more goal on its way out. Severing first makes that call
    /// throw <see cref="ObjectDisposedException"/> instead, and disposing the context here is what
    /// cancels <see cref="IPluginContext.Lifetime"/> — the signal a well-behaved Stop reads to learn
    /// its session is done with it, so a long-lived timer or connection this plugin started has
    /// something to observe rather than outliving a session nobody is watching.</para>
    ///
    /// <para>REAP KILLS WHATEVER OUTLIVED STOP. A well-behaved plugin's own Stop already exits its
    /// children, so the ordinary case finds nothing left; reap exists for the plugin that did not —
    /// crashed inside Stop, or is the timed-out case below. An orphaned subprocess is the one failure
    /// in this feature that outlives the app, and reaping here, not only at startup, is what closes
    /// that gap: a host killed only at startup survives for the rest of THIS run if the plugin was
    /// merely unwired, not crashed.</para>
    ///
    /// <para>STOP HAS A TIMEOUT, and the remedy differs by loader. A managed plugin runs in-process,
    /// so there is no host to kill when it hangs: the
    /// call is abandoned (its Task is left running rather than awaited further) and the hang is
    /// logged naming the plugin. AN ABANDONED STOP CANNOT BE
    /// CANCELLED FROM HERE — <paramref name="ct"/> is not passed to it, deliberately: a plugin's
    /// Stop is handed its OWN token (<see cref="IPluginContext.Lifetime"/>) and this method has no
    /// authority to interrupt code it does not control, only to stop waiting for it — which is why
    /// disposing the context above, to cancel that token, is the only lever this method has over an
    /// abandoned Stop's own code. THE ABI HALF OF THIS ASYMMETRY — killing a host process after the
    /// same timeout — has no loader to implement it against yet; that is the ABI task's to fill.</para>
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
            // Commands come out here too, before the drain below — a command still dispatchable
            // could start a new call into an instance about to be stopped, the exact race the tool
            // half of this step already exists to close.
            _plugins.Remove(plugin);
            if (plugin.Commands is not null)
                foreach (var command in plugin.Manifest.Commands)
                    plugin.Commands.Deregister("/" + command.Name);
        }

        // STEP 2: DRAIN. Polls rather than a signal, because InFlight is decremented from whatever
        // thread a call happens to finish on — a bounded poll is simpler than wiring a
        // TaskCompletionSource per plugin for an operation that runs once per unwire.
        while (Volatile.Read(ref plugin.InFlight) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }

        // STEP 3: SEVER. The client goes dead before Stop, and the context is disposed here — see
        // this method's own doc for why both happen at this point rather than after Stop returns.
        var dropped = plugin.Client?.Sever() ?? 0;
        if (dropped > 0)
            _log($"plugin '{pluginName}': dropped {dropped} queued goal(s) at unwire.");
        plugin.Context?.Dispose();

        // STEP 4: STOP, bounded by _stopTimeout — see this method's own doc for the managed/ABI
        // asymmetry. Task.WhenAny rather than a CancellationTokenSource on `ct` above: cancelling the
        // token passed to Stop would ask a MANAGED plugin's own code to observe cancellation it may
        // never check, which is indistinguishable from the hang this timeout exists to survive. The
        // await here is abandoned, not cancelled — the Task keeps running until the plugin's own
        // Lifetime token (already cancelled by the dispose above) eventually stops it, if the plugin
        // reads it at all.
        var stop = plugin.Instance.Stop(ct);
        var finished = await Task.WhenAny(stop, Task.Delay(_stopTimeout, ct));
        if (finished != stop)
            _log($"plugin '{pluginName}': Stop did not return within {_stopTimeout} — "
                + "abandoning it and closing the session around it. Any process this plugin spawned "
                + "is left to the pid record to reap.");

        // STEP 5: REAP. Kills any process this plugin registered whose recorded start time still
        // matches — see ChildProcessStore.ReapPlugin. A plugin that Stopped cleanly already exited
        // its own children, so this ordinarily finds nothing; it exists for the plugin that did not.
        _childProcesses?.ReapPlugin(pluginName, _log, _sessionId);

        return true;
    }

    /// <summary>
    /// Unwires every loaded plugin, in no particular order — session close runs this. Closing a
    /// session is unwiring every plugin it loaded, and a plugin cannot tell the difference from the
    /// four-step path above, so this is that path, once per plugin.
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
