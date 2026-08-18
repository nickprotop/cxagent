using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Plugins;

/// <summary>
/// A tool supplied by whoever embeds this library, offered to the model alongside the built-ins.
///
/// <para>WHY THIS EXISTS AT ALL: Core has no transcript, no colour and no markup dialect. A front
/// end has all three. Rather than teach Core about presentation, it accepts a tool the front end
/// implements — which is how <c>show_diff</c> can render into a TUI that Core cannot see. The
/// alternative was a callback on every tool result, which would have made every built-in pay for a
/// hook only one caller wanted.</para>
///
/// <para>TWO GATES, AND THEY ARE NOT THE SAME GATE. The permission engine asks ONCE PER FOLDER
/// whether this tool may run here at all. <see cref="Gate"/> then runs on EVERY CALL, for the life
/// of the session. Being trusted is permission to USE the tool; it is never an exemption from the
/// tool's own checks. Collapsing the two would mean one "always allow" answer disarmed every future
/// call — which is the failure this codebase keeps finding in other forms: a check that examines
/// part of a request and lets the rest through unexamined.</para>
/// </summary>
public interface IAgentTool
{
    /// <summary>What the model is offered: name, description, JSON schema.</summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Whether THIS CALL needs a human. Null when it does not.
    ///
    /// <para>The returned request's <see cref="PermissionRequest.AlwaysRule"/> decides granularity:
    /// a rule of "deploy*" is asked once ever, "deploy env=dev" once per environment, and a NULL
    /// AlwaysRule means the call can never be truthfully generalised, so it asks every time and no
    /// stored rule ever matches it.</para>
    ///
    /// <para>PURE AND CHEAP. It runs before every call and must not do I/O — it inspects the
    /// arguments and says what permission they imply, nothing more.</para>
    /// </summary>
    PermissionRequest? Gate(JobParameters call);

    Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct);
}
