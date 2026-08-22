using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs.Builtin;

namespace CxAgent.Core.Jobs;

/// <summary>
/// Maps TypeName → IJobExecutor. First-registered-wins per TypeName; a later executor
/// reusing a claimed TypeName is ignored and a shadow warning is recorded. (When the
/// native/script/container tiers arrive, they register after built-ins, so built-ins
/// win — the precedence the spec requires.)
/// </summary>
public class JobRegistry
{
    private readonly Dictionary<string, IJobExecutor> _executors = new();
    private readonly List<string> _shadowWarnings = new();

    public IReadOnlyCollection<IJobExecutor> All => _executors.Values;
    public IReadOnlyList<string> ShadowWarnings => _shadowWarnings;

    public void Register(IJobExecutor executor)
    {
        if (_executors.TryGetValue(executor.TypeName, out var existing))
        {
            _shadowWarnings.Add(
                $"Executor '{executor.DisplayName}' shadowed: TypeName '{executor.TypeName}' already claimed by '{existing.DisplayName}' (first-registered wins).");
            return;
        }
        _executors[executor.TypeName] = executor;
    }

    public bool TryGet(string typeName, out IJobExecutor? executor) => _executors.TryGetValue(typeName, out executor);

    /// <summary>Registry pre-loaded with the v1 self-contained built-in executors. Permission gate
    /// defaults to <see cref="PermissionGate.AllowAll"/> — this overload is for TESTS and
    /// headless P3-era callers only; anything with a real user in the loop must go through the
    /// gate-required overload below so the composition root is forced to choose a gate.</summary>
    public static JobRegistry CreateWithBuiltins() => CreateWithBuiltins(null, PermissionGate.AllowAll);

    /// <summary>
    /// As <see cref="CreateWithBuiltins()"/>, taking the permission gate explicitly.
    /// </summary>
    /// <param name="permissions">Wraps shell/file/http before registration, so the agent's tool
    /// calls are gated by construction; <see cref="ToolBindings"/> needs, and gets, no permission
    /// check of its own. wait registers bare — it touches nothing, so gating it would just be noise.
    /// Required (no default) so every composition root must explicitly choose a gate — see
    /// PermissionGate.DenyAll/AllowAll.</param>
    /// <param name="policy">
    /// THIS SESSION'S POLICY, stamped onto every permission request the registry's executors raise.
    ///
    /// <para>The registry is built per session and the gate is one per process, so this is the seam
    /// where "which session is asking" is known. Without it the gate judges every session against
    /// whichever root and edit mode it happened to capture — invisible with one session, wrong with
    /// two. Null leaves the request unstamped and the gate falls back to that capture, which is all a
    /// caller with no policy of its own can offer.</para>
    /// </param>
    /// <param name="providers">The configured models, for executors that need one.</param>
    public static JobRegistry CreateWithBuiltins(Llm.ProviderRegistry? providers,
        IPermissionGate permissions, PermissionPolicy? policy = null)
    {
        _ = providers;   // kept in the signature: callers pass their resolution's registry
        var reg = new JobRegistry();
        reg.Register(new PermissionGatedExecutor(new ShellJobExecutor(), permissions, policy));
        reg.Register(new PermissionGatedExecutor(new FileJobExecutor(), permissions, policy));
        reg.Register(new WaitJobExecutor());
        reg.Register(new PermissionGatedExecutor(new HttpJobExecutor(), permissions, policy));
        return reg;
    }
}
