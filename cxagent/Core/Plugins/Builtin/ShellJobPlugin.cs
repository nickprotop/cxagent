using CxAgent.Core.Execution;
using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins.Builtin;

/// <summary>Runs a shell command via /bin/sh -c, streaming output and capturing the exit code.</summary>
public class ShellJobPlugin : IJobPlugin
{
    public string TypeName => "shell";
    public string DisplayName => "Shell Command";

    public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
    {
        new JobParamSpec("command", "string", Required: true, "Shell command to execute"),
        new JobParamSpec("working_dir", "string", Required: false, "Working directory (default: cwd)"),
        new JobParamSpec("env", "object", Required: false, "Additional environment variables"),
        new JobParamSpec("timeout_seconds", "integer", Required: false, "Max execution time"),
    });

    public JobValidation Validate(JobParameters parameters)
    {
        var command = parameters.Get("command", "");
        return string.IsNullOrWhiteSpace(command)
            ? JobValidation.Invalid("'command' is required.")
            : JobValidation.Valid();
    }

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        var command = parameters.Get<string>("command");
        var workingDir = parameters.Get<string?>("working_dir", null);
        var timeout = parameters.Get<int?>("timeout_seconds", null);
        var env = parameters.Get<Dictionary<string, string>?>("env", null);

        var spec = new ProcessSpec("/bin/sh", new[] { "-c", command }, workingDir, env, timeout);
        var start = DateTimeOffset.UtcNow;
        var result = await ProcessRunner.RunAsync(spec, context, ct);
        var duration = DateTimeOffset.UtcNow - start;

        if (result.TimedOut)
            return new JobResult { Success = false, ExitCode = -1, Duration = duration,
                ErrorMessage = $"timed out after {timeout}s" };

        return new JobResult
        {
            Success = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            Duration = duration,
            ErrorMessage = result.ExitCode == 0 ? null : $"command exited with code {result.ExitCode}",
            // `stdout` is what makes a shell job USABLE by the next job. Without it the bag held only
            // exit_code, so {{some_shell_job.stdout}} could never resolve and every goal that shelled
            // out and fed the result onward failed — a live drive of "list ~/bin. what it does?"
            // reported the directory EMPTY, because from the model's side the listing produced nothing.
            //
            // `content` mirrors stdout because that is the key JobDigest renders BARE (everything else
            // is labelled) and the key `{{job}}` resolves to as shorthand. A shell job's substance IS
            // its output, so it should read like one.
            Output = new Dictionary<string, object?>
            {
                ["exit_code"] = result.ExitCode,
                ["stdout"] = result.Stdout,
                ["stderr"] = result.Stderr,
                ["content"] = result.Stdout,
            },
        };
    }
}
