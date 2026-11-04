using CxAgent.Core.Jobs;
using CxAgent.Core.Models;

namespace CxAgent.Core.Permissions;

/// <summary>
/// Puts one <see cref="PermissionRequest"/> to the gate on behalf of a running job: stamps who is
/// asking, and reports the wait so the job's row reads as parked rather than working.
///
/// <para>ONE IMPLEMENTATION FOR EVERY ASKER. <see cref="PermissionGatedExecutor"/> asks before an
/// executor runs; an executor that only learns WHAT to ask once it is running — web search, whose
/// fallback reaches a second engine only after the first has failed — asks through this mid-call. Two
/// copies of the stamping would drift, and a request missing its requester is one the prompt cannot
/// attribute and a stored rule cannot scope.</para>
/// </summary>
internal sealed class PermissionAsker(IPermissionGate gate, PermissionPolicy? policy)
{
    /// <summary>The gate's answer. The caller decides what a refusal means for its job.</summary>
    public async Task<PermissionOutcome> AskAsync(PermissionRequest request, IJobContext context, CancellationToken ct)
    {
        // MARKED WAITING FOR THE DURATION OF THE ASK. The row above this job keeps ticking turns
        // and elapsed time while a prompt sits unanswered, so without this a parked child reads
        // as a working one — and with several up, the user cannot tell which row their answer
        // releases. In a finally because a cancelled-while-queued request never returns here.
        context.ReportPermissionWait(true);
        try
        {
            return await gate.RequestAsync(
                request with
                {
                    Requester = context.Requester,
                    RequesterRunsOn = (context as Execution.JobContext)?.RequesterRunsOn,
                    RequesterRoot = (context as Execution.JobContext)?.RequesterRoot,
                    // THE ID TOO, from the concrete context: IJobContext is published and carries no agent id.
                    RequesterAgentId = (context as Execution.JobContext)?.AgentId,
                    // THE CALLER'S POLICY FIRST — see JobContext.Policy.
                    Policy = (context as Execution.JobContext)?.Policy ?? policy,
                    OnReviewing = context.ReportReviewing,
                }, ct);
        }
        finally
        {
            context.ReportPermissionWait(false);
        }
    }
}
