using CxAgent.Core.Execution;
using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>
/// Runs a shell command via /bin/sh -c, streaming output and capturing the exit code — or, when the
/// call asks to be backgrounded, starts it and hands back its pid and output file instead.
/// </summary>
/// <param name="registry">Which registry owns the processes this executor detaches, or null for the
/// process-wide <see cref="DetachedProcessRegistry.Default"/> that shutdown reaps.
///
/// <para>A PARAMETER ONLY SO TESTS CAN OWN THEIR OWN. The default is shared by every test in a
/// parallel suite, where one test's reap kills another's command and a leaked entry makes a third
/// test's cap accounting wrong. Production passes nothing; there is one construction site
/// (<c>JobRegistry</c>) to keep honest.</para></param>
public class ShellJobExecutor(DetachedProcessRegistry? registry = null) : IJobExecutor
{
    public string TypeName => "shell";
    public string DisplayName => "Shell Command";

    public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
    {
        new JobParamSpec("command", "string", Required: true, "Shell command to execute"),
        new JobParamSpec("working_dir", "string", Required: false, "Working directory (default: cwd)"),
        new JobParamSpec("env", "object", Required: false, "Additional environment variables"),
        // THE TIMEOUT'S MEANING DEPENDS ON `background`, which is why it is said here rather than
        // left to be inferred: without a call waiting on it, the deadline stops being the caller's
        // patience and becomes the only thing that would ever bound the process's life — and today
        // nothing applies it to a detached command at all, so a model that sets both is otherwise
        // entitled to believe it armed a kill that does not exist.
        new JobParamSpec("timeout_seconds", "integer", Required: false,
            "Max execution time in seconds (default 120). IGNORED when background is true: nothing "
            + "is waiting, so a background command runs until it finishes, is killed, or the app "
            + "exits."),
        new JobParamSpec(ShellArguments.Background, "boolean", Required: false,
            "Start the command and return immediately instead of waiting for it. Use this for work "
            + "measured in minutes — a long build, a test suite, a large download — so the turn is "
            + "not spent waiting. The result carries the pid and a file path, NOT the output: "
            + "nothing has been printed yet. You will be told when it exits, with its exit code, "
            + "and can read the file with read_file at any time."),
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

        // THE AGENT'S FOLDER WHEN THE MODEL NAMES NONE, rather than the process's. `working_dir` is
        // optional and rarely sent, so this fallback IS the common path — and it decides where every
        // unqualified `ls`, `git status` and `npm test` actually runs. Falling through to
        // Environment.CurrentDirectory made that the directory the app was LAUNCHED in, which is only
        // the agent's by coincidence.
        var workingDir = parameters.Get<string?>("working_dir", null)
                         ?? context.WorkingDirectory;
        // A DEFAULT TIMEOUT, because `timeout_seconds` is optional and a model almost never sends
        // it. Without one, ProcessRunner builds a CancellationTokenSource that is never scheduled to
        // fire, so `run_shell {"command":"npm install"}` — or a curl to a dead host, or a grep over
        // a huge tree — waits forever with nothing to interrupt it. Two minutes is long enough for
        // a build step and short enough that a wedged call fails while the user is still watching.
        var timeout = parameters.Get<int?>("timeout_seconds", null) ?? 120;
        var env = parameters.Get<Dictionary<string, string>?>("env", null);

        // WHERE THE REST OF A LONG OUTPUT GOES, when the session has a folder to put it in. Matched
        // on the concrete context rather than read off IJobContext: the interface is what plugins
        // implement, and only this built-in needs somewhere to write. A context without one (headless,
        // a test, an embedder that wired no logs) yields null, and the runner then truncates as
        // before — see RunOptions.SpillDir.
        var spillDir = (context as JobContext)?.JobDir;

        var spec = new ProcessSpec("/bin/sh", new[] { "-c", command },
            new RunOptions(workingDir, env, timeout, spillDir));
        var start = DateTimeOffset.UtcNow;

        // THROUGH ShellArguments, NOT Get("background") HERE. The permission gate reads the same
        // argument through the same helper, and the two must reach the same answer for the same JSON:
        // a call the executor backgrounded while the gate judged it foreground has escaped the
        // unattended prompt, and nothing errors on either side to say so.
        if (ShellArguments.IsBackground(parameters))
            return await StartInBackgroundAsync(command, spec, context, start);

        var result = await ProcessRunner.RunAsync(spec, context, ct);
        var duration = DateTimeOffset.UtcNow - start;

        if (result.TimedOut)
            return new JobResult { Success = false, ExitCode = -1, Duration = duration,
                // NAME background FIRST, NOT A BIGGER TIMEOUT. "Retry with a larger
                // timeout_seconds" was advice to run the identical command AGAIN — and a command
                // that reached a two-minute deadline is usually one that is still doing something,
                // so the retry starts a SECOND copy of a build, an install or a migration while the
                // first is running. Backgrounding it is the same wait without the duplicate: the
                // command runs once and its exit is reported when it happens.
                ErrorMessage = $"timed out after {timeout}s and was killed. If it legitimately "
                             + "needs longer, re-run it with 'background': true rather than a "
                             + "bigger 'timeout_seconds' — you will be told when it exits, and you "
                             + "will not be waiting. If it was waiting for input, re-run it with a "
                             + "non-interactive flag — this shell has no stdin." };

        // `stdout` is what makes a shell job USABLE by the next job. Without it the bag held only
        // exit_code, so {{some_shell_job.stdout}} could never resolve and every goal that shelled
        // out and fed the result onward failed — a live drive of "list ~/bin. what it does?"
        // reported the directory EMPTY, because from the model's side the listing produced nothing.
        //
        // `content` mirrors stdout because that is the key JobDigest renders BARE (everything else
        // is labelled) and the key `{{job}}` resolves to as shorthand. A shell job's substance IS
        // its output, so it should read like one.
        var output = new Dictionary<string, object?>
        {
            ["exit_code"] = result.ExitCode,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["content"] = result.Stdout,
        };

        // A SPILL PATH IS PART OF THE ANSWER, not a diagnostic. `stdout` here is only the tail, so
        // without a key naming the file "the rest is on disk" is a claim the model cannot act on — and
        // ToolBindings' overflow advice tells it to read exactly these keys. Added only when something
        // spilled, so their presence is itself the signal that the text above is incomplete.
        if (result.StdoutSpill is { } outSpill)
        {
            output["stdout_spill"] = outSpill.Path;
            output["stdout_bytes"] = outSpill.TotalBytes;
        }
        if (result.StderrSpill is { } errSpill)
        {
            output["stderr_spill"] = errSpill.Path;
            output["stderr_bytes"] = errSpill.TotalBytes;
        }

        return new JobResult
        {
            Success = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            Duration = duration,
            ErrorMessage = result.ExitCode == 0 ? null : $"command exited with code {result.ExitCode}",
            Output = output,
        };
    }

    /// <summary>
    /// Starts the command, hands back its pid and output file, and arranges for the agent to be told
    /// when it exits.
    ///
    /// <para>SUCCESS HERE MEANS "STARTED", NOT "WORKED". The command has not finished, so there is no
    /// exit code to judge it by and <c>ExitCode</c> is deliberately absent rather than 0 — a zero the
    /// model reads as a command that succeeded would make every background failure invisible. What
    /// the result carries instead is everything needed to find the command again: the pid to kill, the
    /// file to read, and the promise of a report.</para>
    ///
    /// <para>NO <c>stdout</c> KEY AT ALL, not an empty one. An empty string reads as a command that
    /// printed nothing, which is a claim about a command that has not run yet; an absent key reads as
    /// a question this result does not answer.</para>
    /// </summary>
    private async Task<JobResult> StartInBackgroundAsync(
        string command, ProcessSpec spec, IJobContext context, DateTimeOffset start)
    {
        // THE PORT IS ON THE CONCRETE CONTEXT AND THIS MAY FIND NEITHER. IJobContext ships in
        // CxAgent.Plugins.Abstractions, which has no reference to Core, so Delivery and AgentId
        // cannot be members of it; matching on the concrete type is the only way to reach them.
        // BEST-EFFORT RATHER THAN A THROW, because the miss is ordinary: a headless run, an embedder
        // that wired no session, and several test doubles all implement the interface alone, and
        // refusing to background a command for them would break the feature exactly where nothing is
        // watching. What the model is told changes instead — see `background` below.
        var jc = context as JobContext;
        var delivery = jc?.Delivery;
        var agentId = jc?.AgentId;

        DetachedProcess detached;
        try
        {
            detached = await ProcessRunner.DetachAsync(spec, context, registry);
        }
        catch (InvalidOperationException refusal)
        {
            // THE CAP, AS A RESULT RATHER THAN A FAULT. DetachAsync throws when the registry is full,
            // having already killed the process it could not register — refusing is right, since an
            // unregistered detached process is the orphan the registry exists to prevent. But an
            // exception out of an executor reaches the model as the TOOL being broken, and the model's
            // answer to a broken tool is to stop using it; the cap is a state it can act on, so it is
            // reported as one.
            return new JobResult
            {
                Success = false,
                Duration = DateTimeOffset.UtcNow - start,
                ErrorMessage = refusal.Message,
                Output = new Dictionary<string, object?> { ["command"] = command },
            };
        }

        if (delivery is not null && agentId is not null)
        {
            // ONCE, WHICHEVER PATH GETS THERE FIRST, and both can. The subscription below is the
            // ordinary route; the by-hand check after it covers a command that finished BEFORE the
            // subscription existed, because Exited is raised once and never replayed — `exit 7`, a
            // failing `git push`, anything that rejects its arguments finishes in milliseconds, so
            // without the second check the agent would hear nothing about exactly the commands that
            // failed fastest. And the two can also RACE: Complete sets Finished inside its lock and
            // raises Exited outside it, so a subscription landing between them is reached by both.
            // Interlocked makes the first one win and the second a no-op — an agent told twice that
            // a command finished reports it twice, and a model reading two reports has no way to know
            // it was one command.
            var report = new BackgroundReport(delivery, agentId, command, start, detached.OutputPath);
            var reported = 0;
            void ReportOnce(int code)
            {
                if (Interlocked.Exchange(ref reported, 1) == 0) Report(report, code);
            }

            // Tell is synchronous by contract precisely so a caller in this position — a handler on
            // whatever thread noticed the exit — cannot be made to block on somebody else's turn.
            detached.Exited += ReportOnce;
            if (detached.Finished) ReportOnce(detached.ExitCode);
        }

        var output = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["pid"] = detached.Pid,
            ["background"] = delivery is not null && agentId is not null
                ? "running; you will be told when it exits"
                // SAID PLAINLY WHEN NOBODY CAN BE TOLD, because the schema promised a report. A model
                // that believes one is coming waits for it instead of reading the file, and the wait
                // never ends.
                : "running; nothing will report its exit here — read the output file to check on it",
        };

        // THE PATH IS THE ANSWER, since the output is going to a file rather than into this result.
        // Absent when there was nowhere to write (no JobDir), which is the same supported case a
        // missing spill is — and then the pid is all the model has, so the key's absence is the
        // signal.
        if (detached.OutputPath is { } path) output["output_file"] = path;

        return new JobResult
        {
            Success = true,
            Duration = DateTimeOffset.UtcNow - start,
            Output = output,
        };
    }

    /// <summary>
    /// Everything needed to tell one agent about one finished background command.
    ///
    /// <para>A RECORD BECAUSE THE PIECES ARE ONE THING and there were six of them as parameters —
    /// three of which are strings. <c>AgentId</c>, <c>Command</c> and <c>OutputPath</c> are all
    /// <c>string</c>, so transposing any two compiles cleanly and delivers a command line to an agent
    /// named after a file path. Named members make that a build error.</para>
    /// </summary>
    /// <param name="Delivery">Where to say it.</param>
    /// <param name="AgentId">Who to say it to — the id the command's own tool call carried.</param>
    /// <param name="Command">The command line, quoted back because the message may be read in a
    /// context that no longer holds why it was started.</param>
    /// <param name="Started">When it was launched, for the elapsed time.</param>
    /// <param name="OutputPath">Where its output went, or null when there was nowhere to write.</param>
    private sealed record BackgroundReport(Agents.IAgentDelivery Delivery, string AgentId,
        string Command, DateTimeOffset Started, string? OutputPath);

    /// <summary>
    /// Tells the agent that started the command how it ended.
    ///
    /// <para>EVERY FACT IT NEEDS, IN ONE MESSAGE, AND NO INSTRUCTION. Core states what happened and
    /// the model decides what to do about it — but the facts have to stand on their own, because a
    /// command that finishes forty minutes and one compaction later arrives at a context that no
    /// longer holds why it was run. So the command line, the exit code, how long it took and where
    /// its output is are all here, rather than "the background command you started has finished".</para>
    ///
    /// <para>THE EXIT CODE IS STATED FOR A SUCCESS TOO, not just a failure. "Finished" and "finished
    /// with exit code 0" differ by the only thing the model can act on, and a message whose shape
    /// changed with the outcome would make the absence of a code ambiguous between success and a code
    /// nobody recorded.</para>
    ///
    /// <para>SWALLOWS WHAT DELIVERY THROWS. This runs on the thread that noticed the exit, which no
    /// caller holds, so an escaping exception takes the process down over a message.</para>
    /// </summary>
    private static void Report(BackgroundReport report, int exitCode)
    {
        try
        {
            var elapsed = DateTimeOffset.UtcNow - report.Started;
            var text = $"The background command `{report.Command}` finished with exit code "
                     + $"{exitCode} after {Describe(elapsed)}.";
            if (report.OutputPath is not null)
                text += $" Its output is in {report.OutputPath} — read it with read_file.";

            report.Delivery.Tell(report.AgentId, text);
        }
        catch (Exception)
        {
            // Nothing to report to: the agent that would have been told is the thing that failed.
        }
    }

    /// <summary>Elapsed time as a model reads it — seconds for a short run, minutes for the long ones
    /// backgrounding exists for, because "after 2712.4 seconds" is arithmetic the reader has to do.</summary>
    private static string Describe(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
        : $"{elapsed.TotalSeconds:N1}s";
}
