using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Core.Permissions;

/// <summary>
/// Wraps a risky <see cref="IJobPlugin"/> (shell/file/http) so permission is checked BELOW both
/// execution paths at once — JobExecutor.cs (planned jobs) and WorkerToolset.cs (a worker's tool
/// calls) both dispatch through whatever the registry hands them, so wrapping the instance here
/// closes both chokepoints with one class and zero changes to either caller.
///
/// <para><see cref="TypeName"/>/<see cref="DisplayName"/>/<see cref="GetSchema"/>/
/// <see cref="Validate"/> delegate straight to the inner plugin — nothing about advertising or
/// validating the job type changes. Only <see cref="ExecuteAsync"/> is intercepted.</para>
///
/// <para>Requests from <see cref="PermissionPolicy.RequestsFor"/> are awaited SEQUENTIALLY and
/// SHORT-CIRCUIT on the first denial: a `copy` whose destination is denied must not have prompted
/// for, or performed, anything — including the read of its source, if the source request happens
/// to be ordered first but the dest is what a rule or the user refuses. Because
/// <c>parameters</c> here are the SUBSTITUTED, post-`{{job.key}}` values (both callers
/// resolve/pin their parameters before invoking the plugin), every request this builds already
/// reflects what will actually run.</para>
/// </summary>
public sealed class PermissionGatedPlugin : IJobPlugin
{
    private readonly IJobPlugin _inner;
    private readonly IPermissionGate _gate;

    /// <summary>
    /// THIS SESSION'S POLICY, stamped onto every request so one process-wide gate can judge each
    /// session by its own root and its own edit mode. Null on the paths that have no policy — the
    /// mock and the fixed test gates — where the gate's own is the only one there is.
    /// </summary>
    private readonly PermissionPolicy? _policy;

    public PermissionGatedPlugin(IJobPlugin inner, IPermissionGate gate, PermissionPolicy? policy = null)
    {
        _inner = inner;
        _gate = gate;
        _policy = policy;
    }

    public string TypeName => _inner.TypeName;
    public string DisplayName => _inner.DisplayName;
    public JobSchema GetSchema() => _inner.GetSchema();
    public JobValidation Validate(JobParameters parameters) => _inner.Validate(parameters);

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        // THE CONTEXT'S ROOT, so the gate resolves a relative path against the SAME folder the
        // plugin will. Still a pure function of its arguments — the root is passed in, not read —
        // which keeps RequestsFor ignorant of which agent is running, as below.
        // CARRIES FORWARD to whichever JobResult this call eventually returns (Task 8), so the tool
        // row can badge "auto-approved" / "auto-denied" — see JobResult.DecidedBy. The LAST REQUEST
        // IN THE LOOP THAT GOT A CLASSIFIER VERDICT wins, which is right: it is the most recent word
        // on this call. A single call can raise several requests (`copy` asks about source AND
        // dest), and a later request that clears silently is a plain Allow with DeniedBy null — that
        // must not overwrite an "auto" an earlier request already recorded, or the badge silently
        // disappears even though the classifier decided this call. See the `??` below.
        string? decidedBy = null;

        var requests = PermissionPolicy.RequestsFor(_inner.TypeName, parameters, context.WorkingDirectory);
        foreach (var request in requests)
        {
            // STAMPED HERE, not built into RequestsFor. That method is a pure policy function over
            // (pluginType, parameters) — it has no idea which agent is running and should not learn.
            // This is the one layer that sees both the request and the context it was made in.
            // MARKED WAITING FOR THE DURATION OF THE ASK. The row above this job keeps ticking turns
            // and elapsed time while a prompt sits unanswered, so without this a parked child reads
            // as a working one — and with several up, the user cannot tell which row their answer
            // releases. In a finally because a cancelled-while-queued request never returns here.
            context.ReportPermissionWait(true);
            PermissionOutcome outcome;
            try
            {
                outcome = await _gate.RequestAsync(
                    request with { Requester = context.Requester, Policy = _policy }, ct);
            }
            finally
            {
                context.ReportPermissionWait(false);
            }

            // `??`, NOT a plain assignment. A single call — `copy` is the case that showed this —
            // raises SEVERAL requests, and a later one that clears silently (a stored rule, an
            // in-boundary read) is a plain Allow with DeniedBy null. Assigning that over an
            // earlier "auto" erased the classifier's verdict: a live drive auto-approved a shell
            // command and the row rendered plain "done", no badge, even though DecidedBy="auto"
            // reached the DB and /stats correctly. A null outcome never carries news — it means
            // "nothing to report from this request" — so it must never overwrite whatever the
            // last request that DID name a decider said.
            decidedBy = outcome.DeniedBy ?? decidedBy;

            if (!outcome.Allowed)
            {
                return new JobResult
                {
                    Success = false,
                    ExitCode = -1,
                    PermissionDenied = true,
                    ErrorMessage = DenialMessage.For(outcome, request.Display),
                    DecidedBy = decidedBy,
                };
            }
        }

        // EVERY GATE HAS CLEARED — the work starts now. Before this line the elapsed time is the
        // user reading a prompt, which belongs to nobody's stopwatch.
        context.WorkStarting();

        var result = await _inner.ExecuteAsync(parameters, context, ct);

        // STAMPED ON THE WAY OUT — see GatedAgentTool's identical comment. Only when a gate actually
        // ran, and never clobbering a DecidedBy the inner plugin set itself.
        return decidedBy is null || result.DecidedBy is not null ? result : result with { DecidedBy = decidedBy };
    }
}
