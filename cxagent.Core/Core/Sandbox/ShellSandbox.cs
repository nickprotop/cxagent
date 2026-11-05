using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Sandbox;

/// <summary>How one shell call meets the sandbox.</summary>
public enum SandboxUse
{
    /// <summary>No sandbox: off, not installed, or not this system. The call runs as it always did.</summary>
    Off,

    /// <summary>Runs inside the sandbox.</summary>
    Contained,

    /// <summary>The model asked to run it outside; only the user can say yes.</summary>
    Escape,

    /// <summary>The sandbox is on and will not start; the user is asked whether to run it uncontained.</summary>
    Failed,
}

/// <summary>The decision and, for an escape or a failure, the reason shown in the prompt.</summary>
/// <param name="Use">How the call meets the sandbox.</param>
/// <param name="Reason">The model's reason (escape) or the sandbox's (failed); null otherwise.</param>
public sealed record SandboxDecision(SandboxUse Use, string? Reason);

/// <summary>
/// The one place a shell call's sandbox decision is made, and how it marks the permission request.
///
/// <para>ONE READER. The gate asks about the call and the executor runs it; if they decided separately, a call the
/// gate let through as contained could run uncontained, with nothing on either side to say so. The gate decides
/// here and hands the answer to the executor on the job context.</para>
/// </summary>
public static class ShellSandbox
{
    /// <summary>The decision for one call.</summary>
    /// <param name="policy">The asking agent's policy, whose sandbox applies; null for none.</param>
    /// <param name="parameters">The call's arguments.</param>
    public static SandboxDecision Decide(PermissionPolicy? policy, JobParameters parameters)
    {
        if (policy?.Sandbox is not { } sandbox) return new(SandboxUse.Off, null);
        var status = sandbox.Status;
        return status.Kind switch
        {
            // NOTHING TO ESCAPE when there is no sandbox, so `unsandboxed` is not a question worth asking: the call
            // goes through the gate as any command does.
            SandboxStatusKind.Off or SandboxStatusKind.Unavailable => new(SandboxUse.Off, null),
            SandboxStatusKind.Failed => new(SandboxUse.Failed, status.Reason),
            _ when ShellArguments.IsUnsandboxed(parameters) =>
                new(SandboxUse.Escape, ShellArguments.UnsandboxedReason(parameters) ?? "no reason given"),
            _ => new(SandboxUse.Contained, null),
        };
    }

    /// <summary>
    /// The request marked with the decision. An escape and a failed start lose their Always rule: "run this
    /// outside the sandbox" is a decision about one command, and a stored rule would make it a standing one.
    /// </summary>
    /// <param name="request">The shell request the gate built.</param>
    /// <param name="decision">How the call meets the sandbox.</param>
    public static PermissionRequest Stamp(PermissionRequest request, SandboxDecision decision) => decision.Use switch
    {
        SandboxUse.Contained => request with { Contained = true },
        SandboxUse.Escape => request with { UnsandboxedReason = decision.Reason, AlwaysRule = null },
        SandboxUse.Failed => request with { SandboxFailure = decision.Reason, AlwaysRule = null },
        _ => request,
    };
}
