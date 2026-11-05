using CxAgent.Core.Permissions;

namespace CxAgent.Headless;

/// <summary>
/// The permission prompt of a run nobody is watching.
///
/// <para>ONLY WHAT WOULD PROMPT ARRIVES HERE. Silent allows, stored "Always" rules, the edit mode and
/// auto mode's reviewer decide first, exactly as in the TUI; this answers what is left.</para>
///
/// <para>A COMMAND OUTSIDE THE SANDBOX is refused like every other prompt, and under bypass runs with a line on
/// <c>said</c> naming why.</para>
///
/// <para>NEVER Always OR TrustFolder. Both persist to permissions.json, and a script must not change
/// the user's own setup.</para>
/// </summary>
public sealed class HeadlessPrompt(bool bypass, Action<string>? said = null)
{
    private readonly HashSet<string> _runApproved = new(StringComparer.Ordinal);

    /// <summary>A plugin named with --plugin: its load is approved for this run.</summary>
    public void ApprovePluginForRun(string loadSetHash) => _runApproved.Add(loadSetHash);

    public Task<PermissionChoice> Answer(PermissionRequest request, bool offerTrust, CancellationToken ct)
    {
        // A PLUGIN IS FOREIGN CODE, approved ahead of time or by name — never by bypass, which
        // approves actions, not binaries.
        if (request.Kind == PermissionKind.Plugin)
            return Task.FromResult(request.AlwaysRule is { } hash && _runApproved.Contains(hash)
                ? PermissionChoice.Once
                : PermissionChoice.Deny);

        if (!bypass) return Task.FromResult(PermissionChoice.Deny);

        // BYPASS RUNS IT, AND SAYS SO. Bypass means no gate, so a command outside the sandbox runs — but a run that
        // was meant to be contained and is not must not be silent about it.
        if (request.SandboxFailure is { } failure)
            said?.Invoke($"cxagent: running uncontained: {failure}");
        else if (request.UnsandboxedReason is { } why)
            said?.Invoke($"cxagent: running outside the sandbox: {why}");

        // THE ONE THING BYPASS STILL REFUSES: a file-tool write into a folder whose contents run
        // later, outside this run. A shell command is not seen here — see IsProtectedWrite.
        return Task.FromResult(request.Policy?.IsProtectedWrite(request) == true
            ? PermissionChoice.Deny
            : PermissionChoice.Once);
    }
}
