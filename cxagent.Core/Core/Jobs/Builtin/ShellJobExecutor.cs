using CxAgent.Core.Execution;
using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>
/// Everything that decides what happens to a shell command this executor stops waiting for.
///
/// <para>A RECORD BECAUSE THE THIRD MEMBER MADE THEM ONE. Two constructor arguments were two
/// unrelated favours to tests; the third — a store for surviving a crash — is when a name fits, and
/// one does: these are the terms on which a command outlives its call. A caller setting one usually
/// has something to say about the others, and a caller with nothing to say passes the record not at
/// all.</para>
/// </summary>
/// <param name="Registry">Which registry owns the processes this executor detaches, or null for the
/// process-wide <see cref="DetachedProcessRegistry.Default"/> that shutdown reaps.
///
/// <para>A MEMBER ONLY SO TESTS CAN OWN THEIR OWN. The default is shared by every test in a parallel
/// suite, where one test's reap kills another's command and a leaked entry makes a third test's cap
/// accounting wrong.</para></param>
/// <param name="DetachOnTimeout">
/// Whether a command still running at its <c>timeout_seconds</c> is handed back alive rather than
/// killed — config's <c>shellDetachOnTimeout</c>, default true.
///
/// <para>IT ARRIVES HERE RATHER THAN ON <see cref="RunOptions"/> BY ITSELF because
/// <see cref="ProcessRunner"/> is static and has no config to read: somebody has to put the user's
/// answer into the options, and this executor is the one place in the app that builds a
/// <c>RunOptions</c> from what a session was configured with. The option on the record is what
/// <c>ProcessRunner</c> obeys; this is what decides its value.</para></param>
/// <param name="Children">
/// Where a detached command's pid is written so the NEXT launch can kill it, or null for a caller
/// with nowhere to persist — a headless run, a test, an embedder that wired no config directory.
///
/// <para>NULL IS A REAL CASE AND A REAL GAP, stated rather than papered over: without a store, a
/// SIGKILL or a crash leaves a backgrounded command running with nothing on disk naming it. The
/// in-memory registry still reaps it at an orderly shutdown; only the crash case is uncovered.</para>
/// </param>
public sealed record ShellBackgrounding(
    DetachedProcessRegistry? Registry = null,
    bool DetachOnTimeout = true,
    Plugins.ChildProcessStore? Children = null)
{
    /// <summary>The process-wide registry, detaching on a deadline, and nothing persisted — what a
    /// caller that has read no config gets.</summary>
    public static readonly ShellBackgrounding Default = new();
}

/// <param name="backgrounding">What happens to a command this executor stops waiting for — see
/// <see cref="ShellBackgrounding"/>. Null takes every default, which is what a test and a headless
/// caller want; the composition root that has read config passes its own.</param>
public class ShellJobExecutor(ShellBackgrounding? backgrounding = null) : IJobExecutor
{
    private readonly ShellBackgrounding _bg = backgrounding ?? ShellBackgrounding.Default;

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
            new RunOptions(workingDir, env, timeout, spillDir, _bg.DetachOnTimeout));
        var start = DateTimeOffset.UtcNow;

        // THROUGH ShellArguments, NOT Get("background") HERE. The permission gate reads the same
        // argument through the same helper, and the two must reach the same answer for the same JSON:
        // a call the executor backgrounded while the gate judged it foreground has escaped the
        // unattended prompt, and nothing errors on either side to say so.
        if (ShellArguments.IsBackground(parameters))
            return await StartInBackgroundAsync(command, spec, context, start);

        var result = await ProcessRunner.RunAsync(spec, context, ct, _bg.Registry);
        var duration = DateTimeOffset.UtcNow - start;

        // CHECKED BEFORE TimedOut, BECAUSE BOTH ARE SET AND THEY MEAN OPPOSITE THINGS. The deadline
        // passed, and the command is still running — so nothing below may say it was killed, and the
        // advice must not tell the model to run it again: a second `npm install` alongside the first
        // is the failure the old message caused.
        if (result.Detached is { } handedOver)
            return StillRunning(command, timeout, handedOver, context, start, duration);

        if (result.TimedOut)
            return new JobResult { Success = false, ExitCode = -1, Duration = duration,
                // KILLED HERE, WHICH IS THE UNCOMMON CASE: either the run asked for the deadline to
                // be a kill, or the hand-over was refused because sixteen commands are already
                // detached. Naming `background` first is still the right advice — it is the same wait
                // without a duplicate, whereas a bigger `timeout_seconds` re-runs a command that was
                // probably still doing something.
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
    /// Reports a command whose DEADLINE passed while it was still working: handed over alive, not
    /// killed.
    ///
    /// <para>THE SAME KEYS A <c>background: true</c> CALL RETURNS, because the model is in the same
    /// position — a command running somewhere with a pid, a file and a report to come — and a second
    /// vocabulary for it would be a second thing to learn. What differs is the one sentence saying
    /// how it got here, since the model did not ask for this and would otherwise have no idea why a
    /// command it expected to wait for came back unfinished.</para>
    ///
    /// <para>FAILURE, NOT SUCCESS, AND NO EXIT CODE. The call did not do what the model asked: it
    /// asked for the command's result and is getting a pid instead. Reporting success would let a
    /// plan step on to the next job believing this one was done, and there is no exit code to judge
    /// it by yet — a zero here would make every eventual failure invisible.</para>
    /// </summary>
    private JobResult StillRunning(string command, int timeout, DetachedProcess detached,
        IJobContext context, DateTimeOffset start, TimeSpan duration)
    {
        var reporting = ArrangeTheReport(detached, command, context, start);
        RecordForTheNextLaunch(detached);

        var output = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["pid"] = detached.Pid,
            ["background"] = reporting
                ? "running; you will be told when it exits"
                : "running; nothing will report its exit here — read the output file to check on it",
        };
        if (detached.OutputPath is { } path) output["output_file"] = path;

        return new JobResult
        {
            Success = false,
            Duration = duration,
            // SAYS "STILL RUNNING", NEVER "KILLED", and says NOT to run it again. The old message's
            // "retry with a larger timeout_seconds" is actively dangerous here: the command is alive,
            // so a retry is a second `npm install`, a second migration, a second push. And the model
            // must not conclude the work was lost, because it was not.
            // THE WORD "KILLED" APPEARS NOWHERE, not even to deny it. A model skimming a long
            // result for a verb finds the one that is there, and "not killed" read as "killed" is
            // precisely the wrong conclusion — it would decide the work was lost and start again.
            ErrorMessage = $"still running after {timeout}s: the command was left to finish and this "
                         + "call stopped waiting for it. DO NOT run it again — it is still working, "
                         + "and a second copy would duplicate whatever it is doing. "
                         + (reporting
                            ? "You will be told when it exits, with its exit code."
                            : "Nothing will report its exit here — read the output file to check "
                            + "on it.")
                         + " Its output is going to the file named in this result.",
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
        DetachedProcess detached;
        try
        {
            detached = await ProcessRunner.DetachAsync(spec, context, _bg.Registry);
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

        var reporting = ArrangeTheReport(detached, command, context, start);
        RecordForTheNextLaunch(detached);

        var output = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["pid"] = detached.Pid,
            ["background"] = reporting
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
    /// Writes the detached command's pid where the NEXT launch can kill it, and clears the record when
    /// it exits on its own.
    ///
    /// <para>THE ONLY THING THAT SURVIVES A CRASH. <c>DetachedProcessRegistry</c> is reaped from
    /// <c>SessionManager.Dispose</c>, which a SIGKILL never reaches — so without this file a
    /// backgrounded command outlives the app with nothing left knowing its pid. That is the failure
    /// backgrounding introduces, and it got worse when a deadline stopped killing: the timeout used to
    /// guarantee a dead process.</para>
    ///
    /// <para>CLEARED ON EXIT, so the file holds what is RUNNING rather than a log of everything ever
    /// backgrounded. A stale record is not merely untidy — the next launch looks up its pid, and every
    /// stale entry is another chance for the pid to have been reused by a process the start-time match
    /// then has to rule out.</para>
    ///
    /// <para>AND BY HAND AFTER SUBSCRIBING, for the reason every other subscriber here does it:
    /// <c>Exited</c> is raised once and never replayed, so a command that finished before the
    /// subscription would leave its record behind forever. The window is narrower here than for the
    /// exit report, because <see cref="Plugins.ChildProcessStore.Record"/> itself writes nothing for a
    /// process that has already gone — what remains is an exit landing between that write and the line
    /// above. NO TEST REACHES IT, and it is kept anyway: it costs one comparison, and the alternative
    /// is a stale record that makes the next launch ask the OS about a pid for nothing.</para>
    /// </summary>
    private void RecordForTheNextLaunch(DetachedProcess detached)
    {
        if (_bg.Children is not { } children) return;

        children.Record(detached.Pid);
        detached.Exited += _ => children.Remove(detached.Pid);
        if (detached.Finished) children.Remove(detached.Pid);
    }

    /// <summary>
    /// Subscribes the agent's exit report to a command still running, and says whether anyone will
    /// actually hear it.
    ///
    /// <para>SHARED BY BOTH WAYS A COMMAND ENDS UP DETACHED — an explicit <c>background: true</c>, and
    /// a deadline that handed a slow command over. They differ in how the caller got here and in
    /// nothing about what the agent needs told, so a second copy of this would be two places to fix
    /// when the message changes and two chances for them to disagree.</para>
    ///
    /// <para>THE PORT IS ON THE CONCRETE CONTEXT AND THIS MAY FIND NEITHER. <c>IJobContext</c> ships
    /// in CxAgent.Plugins.Abstractions, which has no reference to Core, so <c>Delivery</c> and
    /// <c>AgentId</c> cannot be members of it; matching on the concrete type is the only way to reach
    /// them. BEST-EFFORT RATHER THAN A THROW, because the miss is ordinary: a headless run, an
    /// embedder that wired no session, and several test doubles all implement the interface alone,
    /// and refusing to hand a command over for them would break the feature exactly where nothing is
    /// watching. What the model is told changes instead — hence the return value.</para>
    /// </summary>
    /// <returns>True when an agent will be told the exit; false when there is nobody to tell, which
    /// the caller MUST say plainly rather than repeat a promise of a report nothing will keep.</returns>
    private static bool ArrangeTheReport(DetachedProcess detached, string command,
        IJobContext context, DateTimeOffset start)
    {
        var jc = context as JobContext;
        if (jc?.Delivery is not { } delivery || jc.AgentId is not { } agentId) return false;

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
        return true;
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
