namespace CxAgent.Core.Permissions;

/// <summary>
/// The answer to one permission request, and WHO gave it.
///
/// <para>A BARE BOOL COULD NOT SAY WHO. Every refusal path read "permission denied by the user",
/// which was true while only a user could deny and became a lie the moment a classifier could. The
/// model reads this text and acts on it: a human refusal is final for the turn, a machine refusal is
/// an argument it may answer.</para>
/// </summary>
public sealed record PermissionOutcome(bool Allowed, string? DeniedBy = null, string? Reason = null)
{
    public static PermissionOutcome Allow => new(true);
    public static PermissionOutcome ByUser => new(false, "user");
    public static PermissionOutcome ByClassifier(string? reason) => new(false, "auto", reason);
}

/// <summary>The text a MODEL reads when an action was refused.</summary>
public static class DenialMessage
{
    public static string For(PermissionOutcome outcome, string display) =>
        outcome.DeniedBy == "auto"
            ? $"auto review refused this: {display}."
              + (outcome.Reason is { Length: > 0 } r ? $" Reason: {r}." : "")
              + " Reconsider the approach; if you believe it is necessary, say so and the user will be"
              + " asked. Do not retry this operation unchanged."
            : $"permission denied by the user: {display}. Do not retry this operation or plan it"
              + " again unless the user explicitly asks.";
}

/// <summary>The seam <see cref="PermissionGatedPlugin"/> awaits before letting an inner plugin
/// run. One call per request, sequential and short-circuiting on the first denial — see
/// PermissionGatedPlugin's doc comment for why that ordering matters.</summary>
public interface IPermissionGate
{
    /// <summary>The decision AND who made it. Implementations own the whole decision: silent
    /// policy allows, stored rules, auto mode's classifier, and prompting are all behind this one
    /// call.</summary>
    Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct);
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
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct) =>
            Task.FromResult(_allow ? PermissionOutcome.Allow : PermissionOutcome.ByUser);
    }

    /// <summary>Headless / nothing-can-answer default: every request is refused. Nothing can
    /// silently execute a risky operation just because no gate was wired up.</summary>
    public static readonly IPermissionGate DenyAll = new FixedGate(allow: false);

    /// <summary>EXPLICIT test opt-out — greppable. Every request is allowed; use only where a
    /// test is not exercising the permission system itself.</summary>
    public static readonly IPermissionGate AllowAll = new FixedGate(allow: true);
}
