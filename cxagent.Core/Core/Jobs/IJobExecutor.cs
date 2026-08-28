using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>A native job executor: advertises a type + schema, validates params, and executes.
///
/// <para>ITS <see cref="IJobContext"/> LIVES IN THE ABSTRACTIONS ASSEMBLY, because a plugin's tool
/// is handed one and a plugin references the contract rather than the runtime. The executor itself
/// is the host's pipeline and stays here.</para>
/// </summary>
public interface IJobExecutor
{
    string TypeName { get; }              // "shell", "file", etc.
    string DisplayName { get; }
    JobSchema GetSchema();
    JobValidation Validate(JobParameters parameters);
    Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct);
}
