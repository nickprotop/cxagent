namespace CxAgent.Core.Permissions;

/// <summary>The seam <see cref="PermissionGatedPlugin"/> awaits before letting an inner plugin
/// run. One call per request, sequential and short-circuiting on the first denial — see
/// PermissionGatedPlugin's doc comment for why that ordering matters.</summary>
public interface IPermissionGate
{
    /// <summary>True = the operation may proceed. Implementations own the whole decision:
    /// silent policy allows, stored rules, and prompting are all behind this one call.</summary>
    Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct);
}

/// <summary>The two fixed, greppable gate implementations that need no UI: a headless default
/// that can never say yes, and an explicit test opt-out that always does. The real interactive
/// gate (prompting the user, consulting PermissionPolicy/PermissionRulesStore) is Task 4's.</summary>
public static class PermissionGate
{
    private sealed class FixedGate : IPermissionGate
    {
        private readonly bool _allow;
        public FixedGate(bool allow) => _allow = allow;
        public Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct) =>
            Task.FromResult(_allow);
    }

    /// <summary>Headless / nothing-can-answer default: every request is refused. Nothing can
    /// silently execute a risky operation just because no gate was wired up.</summary>
    public static readonly IPermissionGate DenyAll = new FixedGate(allow: false);

    /// <summary>EXPLICIT test opt-out — greppable. Every request is allowed; use only where a
    /// test is not exercising the permission system itself.</summary>
    public static readonly IPermissionGate AllowAll = new FixedGate(allow: true);
}
