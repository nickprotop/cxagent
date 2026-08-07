using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins.Builtin;

namespace CxAgent.Core.Plugins;

/// <summary>
/// Maps TypeName → IJobPlugin. First-registered-wins per TypeName; a later plugin
/// reusing a claimed TypeName is ignored and a shadow warning is recorded. (When the
/// native/script/container tiers arrive, they register after built-ins, so built-ins
/// win — the precedence the spec requires.)
/// </summary>
public class PluginRegistry
{
    private readonly Dictionary<string, IJobPlugin> _plugins = new();
    private readonly List<string> _shadowWarnings = new();

    public IReadOnlyCollection<IJobPlugin> All => _plugins.Values;
    public IReadOnlyList<string> ShadowWarnings => _shadowWarnings;

    public void Register(IJobPlugin plugin)
    {
        if (_plugins.TryGetValue(plugin.TypeName, out var existing))
        {
            _shadowWarnings.Add(
                $"Plugin '{plugin.DisplayName}' shadowed: TypeName '{plugin.TypeName}' already claimed by '{existing.DisplayName}' (first-registered wins).");
            return;
        }
        _plugins[plugin.TypeName] = plugin;
    }

    public bool TryGet(string typeName, out IJobPlugin? plugin) => _plugins.TryGetValue(typeName, out plugin);

    /// <summary>Registry pre-loaded with the v1 self-contained built-in plugins. Permission gate
    /// defaults to <see cref="PermissionGate.AllowAll"/> — this overload is for TESTS and
    /// headless P3-era callers only; anything with a real user in the loop must go through the
    /// gate-required overload below so the composition root is forced to choose a gate.</summary>
    public static PluginRegistry CreateWithBuiltins() => CreateWithBuiltins(null, PermissionGate.AllowAll);

    /// <summary>
    /// As <see cref="CreateWithBuiltins()"/>, plus <c>llm_agent</c> when a resolver is supplied.
    /// Registered conditionally because the plugin cannot dispatch without one, and advertising a
    /// job type that always fails is worse than not advertising it — the orchestrator would plan
    /// jobs that cannot run.
    /// </summary>
    /// <param name="permissions">Wraps shell/file/http before registration, so BOTH execution
    /// paths — JobExecutor (planned jobs) and WorkerToolset (a worker's tool calls) — are gated
    /// by construction; neither caller needs, or gets, its own permission check. wait and
    /// llm_agent register bare: llm_agent's own tools are already gated one level down (they
    /// dispatch through this same registry), so gating it too would double-prompt; wait touches
    /// nothing, so gating it would just be noise. Required (no default) so every composition root
    /// must explicitly choose a gate — see PermissionGate.DenyAll/AllowAll.</param>
    /// <param name="maxWorkerTurns">Cap on one worker's tool-loop round-trips (config.json's
    /// <c>orchestrator.maxWorkerTurns</c>).</param>
    /// <param name="onUsage">Routes each worker turn's token usage into the caller's ledger.</param>
    /// <param name="fanOut">
    /// Whether to register <c>llm_agent</c> at all. FALSE (the default) is single-agent mode, and
    /// the absence is the WHOLE mechanism: with no plugin registered there is no type name in
    /// create_plan's enum, no entry in the params reference, no worked example — the orchestrator
    /// cannot plan a worker because, as far as every schema it is shown is concerned, workers do not
    /// exist. A mode enforced by prompt wording is a mode the model can talk itself out of.
    /// </param>
    public static PluginRegistry CreateWithBuiltins(Llm.ProviderRegistry? providers, IPermissionGate permissions,
        int maxWorkerTurns = 200, Action<LlmUsage>? onUsage = null, bool fanOut = false)
    {
        var reg = new PluginRegistry();
        reg.Register(new PermissionGatedPlugin(new ShellJobPlugin(), permissions));
        reg.Register(new PermissionGatedPlugin(new FileJobPlugin(), permissions));
        reg.Register(new WaitJobPlugin());
        reg.Register(new PermissionGatedPlugin(new HttpJobPlugin(), permissions));
        // `reg` itself is handed to the worker plugin so its tool calls dispatch through the SAME
        // registry — that is how read_file reaches the (gated) FileJobPlugin. Registered last, so
        // the four built-ins above are already present by the time a worker can call one.
        if (providers is not null && fanOut)
            reg.Register(new LlmAgentJobPlugin(providers, reg, maxWorkerTurns, onUsage));
        return reg;
    }
}
