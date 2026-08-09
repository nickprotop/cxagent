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
    /// As <see cref="CreateWithBuiltins()"/>, taking the permission gate explicitly.
    /// </summary>
    /// <param name="permissions">Wraps shell/file/http before registration, so the agent's tool
    /// calls are gated by construction; <see cref="WorkerToolset"/> needs, and gets, no permission
    /// check of its own. wait registers bare — it touches nothing, so gating it would just be noise.
    /// Required (no default) so every composition root must explicitly choose a gate — see
    /// PermissionGate.DenyAll/AllowAll.</param>
    public static PluginRegistry CreateWithBuiltins(Llm.ProviderRegistry? providers, IPermissionGate permissions)
    {
        _ = providers;   // kept in the signature: callers pass their resolution's registry
        var reg = new PluginRegistry();
        reg.Register(new PermissionGatedPlugin(new ShellJobPlugin(), permissions));
        reg.Register(new PermissionGatedPlugin(new FileJobPlugin(), permissions));
        reg.Register(new WaitJobPlugin());
        reg.Register(new PermissionGatedPlugin(new HttpJobPlugin(), permissions));
        return reg;
    }
}
