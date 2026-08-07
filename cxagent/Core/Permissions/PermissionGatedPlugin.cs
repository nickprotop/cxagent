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
            var allowed = await _gate.RequestAsync(request, ct);
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

        return await _inner.ExecuteAsync(parameters, context, ct);
    }
}
