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
    /// Whether a SUB-AGENT is offered this tool. True by default: most tools work anywhere.
    ///
    /// <para>FALSE FOR A TOOL THAT NEEDS THE USER'S SCREEN. A child writes to a
    /// <c>BufferedJobPanel</c> that nothing ever displays — the buffer exists to keep a child's rows
    /// OUT of the parent's transcript — so a tool that renders for a person does the work, reports
    /// success, and the output is discarded. That is worse than a wasted call: the model is told its
    /// showing succeeded when nobody saw anything.</para>
    ///
    /// <para>NOT A PERMISSION, AND NOT A PREFERENCE. It is the same structural fact that withholds
    /// <c>ask_user</c> from a child — "a child has no user", and here "a child has no transcript".
    /// A withheld tool is one the child was NEVER GIVEN, so a call to it gets the ordinary "no such
    /// tool" and the model picks a real one. That is the mechanism that makes "no sub-agents of
    /// sub-agents" structural rather than a rule an agent is asked to follow.</para>
    ///
    /// <para>DEFAULT TRUE so that adding this changed nothing for tools that already existed, and so
    /// an embedder writing a calculator never has to think about it.</para>
    /// </summary>
    bool OfferToSubAgents => true;

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
