using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>Waits a fixed duration, or (in the full app) until a manual user action.</summary>
public class WaitJobExecutor : IJobExecutor
{
    public string TypeName => "wait";
    public string DisplayName => "Wait";

    public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
    {
        new JobParamSpec("seconds", "number", Required: false, "Seconds to wait"),
        new JobParamSpec("until_condition", "string", Required: false, "\"manual\" = wait for user click"),
    });

    public JobValidation Validate(JobParameters parameters)
    {
        var hasSeconds = parameters.Values.ContainsKey("seconds");
        var hasCondition = !string.IsNullOrWhiteSpace(parameters.Get("until_condition", ""));
        return hasSeconds || hasCondition
            ? JobValidation.Valid()
            : JobValidation.Invalid("wait requires 'seconds' or 'until_condition'.");
    }

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var condition = parameters.Get("until_condition", "");
        if (condition == "manual")
        {
            // TODO(P5): a real manual wait blocks until the user clicks in the UI. Headless P3
            // resolves immediately so the DAG can proceed in tests / non-interactive runs.
            context.Log("wait: manual condition — resolving immediately (headless)");
            return new JobResult { Success = true, ExitCode = 0, Duration = DateTimeOffset.UtcNow - start };
        }

        var seconds = parameters.Get("seconds", 0.0);
        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return new JobResult { Success = true, ExitCode = 0, Duration = DateTimeOffset.UtcNow - start };
    }
}
