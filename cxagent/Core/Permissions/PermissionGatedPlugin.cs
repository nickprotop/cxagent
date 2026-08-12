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
/// <paramref name="parameters"/> here are the SUBSTITUTED, post-`{{job.key}}` values (both callers
/// resolve/pin their parameters before invoking the plugin), every request this builds already
/// reflects what will actually run.</para>
/// </summary>
public sealed class PermissionGatedPlugin : IJobPlugin
{
    private readonly IJobPlugin _inner;
    private readonly IPermissionGate _gate;

    public PermissionGatedPlugin(IJobPlugin inner, IPermissionGate gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public string TypeName => _inner.TypeName;
    public string DisplayName => _inner.DisplayName;
    public JobSchema GetSchema() => _inner.GetSchema();
    public JobValidation Validate(JobParameters parameters) => _inner.Validate(parameters);

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        var requests = PermissionPolicy.RequestsFor(_inner.TypeName, parameters);
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
            bool allowed;
            try
            {
                allowed = await _gate.RequestAsync(request with { Requester = context.Requester }, ct);
            }
            finally
            {
                context.ReportPermissionWait(false);
            }

            if (!allowed)
            {
                return new JobResult
                {
                    Success = false,
                    ExitCode = -1,
                    PermissionDenied = true,
                    ErrorMessage = $"permission denied by the user: {request.Display}. "
                        + "Do not retry this operation or plan it again unless the user explicitly asks.",
                };
            }
        }

        // EVERY GATE HAS CLEARED — the work starts now. Before this line the elapsed time is the
        // user reading a prompt, which belongs to nobody's stopwatch.
        context.WorkStarting();

        return await _inner.ExecuteAsync(parameters, context, ct);
    }
}
