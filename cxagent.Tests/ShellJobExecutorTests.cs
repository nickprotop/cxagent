using CxAgent.Core.Agents;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class ShellJobExecutorTests
{
    private static JobParameters P(params (string k, object? v)[] kv)
        => new(kv.ToDictionary(x => x.k, x => x.v));

    [Fact]
    public void Validate_RejectsEmptyCommand()
    {
        var v = new ShellJobExecutor().Validate(P(("command", "")));
        Assert.False(v.IsValid);
    }

    [Fact]
    public void Validate_AcceptsNonEmptyCommand()
    {
        var v = new ShellJobExecutor().Validate(P(("command", "echo hi")));
        Assert.True(v.IsValid);
    }

    [Fact]
    public async Task Execute_EchoSucceeds_WithExitCodeZero()
    {
        var result = await new ShellJobExecutor().ExecuteAsync(
            P(("command", "echo hi")), new CollectingContext(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Execute_CapturesStdout_SoTheNextJobCanReferenceIt()
    {
        // A bag holding ONLY exit_code leaves {{some_shell_job.stdout}} unresolvable, so any goal
        // that shells out and feeds the result onward fails. Measured on a live drive of "list ~/bin.
        // what it does?": the job succeeded, the reference produced nothing, and the model reported
        // the directory EMPTY — it had six scripts.
        var result = await new ShellJobExecutor().ExecuteAsync(
            P(("command", "echo hello-from-stdout")), new CollectingContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hello-from-stdout", result.Output!["stdout"]!.ToString()!);

        // `content` mirrors stdout: it is the key JobDigest renders BARE and that {{job}} resolves to
        // as shorthand, and a shell job's substance IS its output.
        Assert.Contains("hello-from-stdout", result.Output["content"]!.ToString()!);
    }

    [Fact]
    public async Task Execute_CapturesStderr_SeparatelyFromStdout()
    {
        // Separate keys so a job can reference either — diagnostics must not be silently mixed into
        // the text a downstream job treats as the result.
        var result = await new ShellJobExecutor().ExecuteAsync(
            P(("command", "echo oops >&2")), new CollectingContext(), CancellationToken.None);

        Assert.Contains("oops", result.Output!["stderr"]!.ToString()!);
        Assert.DoesNotContain("oops", result.Output["stdout"]!.ToString()!);
    }

    [Fact]
    public async Task Execute_NonZeroExit_Fails()
    {
        var result = await new ShellJobExecutor().ExecuteAsync(
            P(("command", "exit 2")), new CollectingContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Execute_Timeout_FailsWithTimedOut()
    {
        var result = await new ShellJobExecutor().ExecuteAsync(
            P(("command", "sleep 30"), ("timeout_seconds", 1)), new CollectingContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeName_IsShell()
    {
        Assert.Equal("shell", new ShellJobExecutor().TypeName);
    }

    [Fact]
    public async Task Shell_CommandThatReadsStdinFailsFastInsteadOfHanging()
    {
        // Unredirected, the child INHERITS the TUI's stdin: `git commit` opens $EDITOR, `apt` waits
        // on y/n, and the command blocks until the timeout while stealing the user's keystrokes.
        // Closed, the read returns EOF and the command finishes in milliseconds.
        var executor = new ShellJobExecutor();
        var p = new JobParameters(new Dictionary<string, object?>
        {
            ["command"] = "cat",              // reads stdin until EOF
            ["timeout_seconds"] = 10,
        });

        var start = DateTimeOffset.UtcNow;
        var r = await executor.ExecuteAsync(p, new CollectingContext(), CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(r.Success, r.ErrorMessage);
        Assert.True(elapsed < TimeSpan.FromSeconds(5),
            $"`cat` should hit EOF immediately, took {elapsed.TotalSeconds:N1}s");
    }

    [Fact]
    public async Task Shell_RunsWithNoTimeoutParameterSupplied()
    {
        // timeout_seconds is optional and a model almost never sends it. With no default,
        // ProcessRunner built a CancellationTokenSource that was never scheduled to fire, so a
        // blocking command waited forever with nothing able to interrupt it.
        var executor = new ShellJobExecutor();
        var p = new JobParameters(new Dictionary<string, object?> { ["command"] = "echo hi" });

        var r = await executor.ExecuteAsync(p, new CollectingContext(), CancellationToken.None);

        Assert.True(r.Success, r.ErrorMessage);
    }
    // ---- background -----------------------------------------------------------------------------

    /// <summary>
    /// One temp root, one registry and one recorder per backgrounded test.
    ///
    /// <para>ITS OWN REGISTRY RATHER THAN <c>DetachedProcessRegistry.Default</c>, because the default
    /// is process-wide and this suite runs in parallel with every other: one test's reap would kill
    /// another test's command, and a leaked entry would make a third test's cap accounting wrong.
    /// Reaped on the way out so a test that fails mid-assert does not leave a <c>sleep</c> running for
    /// the rest of the run — which is exactly when a red suite is re-run.</para>
    ///
    /// <para>A REAL <see cref="LogFileManager"/> RATHER THAN <c>logs: null</c>, so <c>JobDir</c>
    /// answers a real directory. A null one yields no output path, and the path is half of what the
    /// backgrounded result has to carry — a test that let it be null would pass against an executor
    /// that never asked for one.</para>
    /// </summary>
    private sealed class BackgroundFixture : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "shell-bg-" + Guid.NewGuid().ToString("N"));

        public DetachedProcessRegistry Registry { get; } = new();
        public RecordingDelivery Told { get; } = new();
        public ShellJobExecutor Executor { get; }
        public JobContext Context { get; }

        public BackgroundFixture()
        {
            Directory.CreateDirectory(_dir);
            var paths = new AppPaths(_dir);
            paths.EnsureCreated();

            Executor = new ShellJobExecutor(Registry);

            // THE CONCRETE JobContext, NOT AN IJobContext DOUBLE, and that is the point of these
            // tests. Delivery and AgentId sit on the concrete class because IJobContext ships in the
            // published plugin ABI, so the executor has to cast to reach them — a double implementing
            // the interface would prove a recorder works and say nothing about whether the cast finds
            // the port.
            Context = new JobContext("agent-bg", "job-bg",
                new Dictionary<string, JobResult>(), new LogFileManager(paths))
            {
                Delivery = Told,
                WorkingDirectory = _dir,
            };
        }

        public void Dispose()
        {
            Registry.ReapAll();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a detached command's last write may still be landing */ }
        }
    }

    /// <summary>Collects what <c>Tell</c> was handed, with a wait so a test does not sleep on an exit
    /// whose timing belongs to the OS.</summary>
    private sealed class RecordingDelivery : IAgentDelivery
    {
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly List<(string AgentId, string Text)> _told = [];

        public DeliveryOutcome Tell(string agentId, string text)
        {
            lock (_told) _told.Add((agentId, text));
            _arrived.Release();
            return DeliveryOutcome.Woke;
        }

        /// <summary>Waits for one delivery, failing rather than hanging the suite.</summary>
        public async Task<(string AgentId, string Text)> Next()
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(20)),
                "the agent was never told the background command finished");
            lock (_told) return _told[^1];
        }
    }

    [Fact]
    public async Task Background_ReturnsBeforeTheCommandFinishes_CarryingThePathNotTheOutput()
    {
        // RETURNING AT ONCE IS THE FEATURE, so the command outlasts any plausible return: `sleep 5`
        // printing at the end means a result carrying that output could only have been produced by
        // waiting. The elapsed assertion is what separates "returned early" from "ran fast".
        using var fx = new BackgroundFixture();

        var start = DateTimeOffset.UtcNow;
        var r = await fx.Executor.ExecuteAsync(
            // THE OUTPUT TEXT IS ASSEMBLED BY THE SHELL so it appears nowhere in the command line
            // itself: the result echoes `command` back, and asserting on a literal the command
            // contains would be satisfied by that echo rather than by the absence of the output.
            P(("command", "sleep 5; echo LA''TEOUTPUT"), ("background", true)),
            fx.Context, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(r.Success, r.ErrorMessage);
        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"a backgrounded call must not wait for the command; took {elapsed.TotalSeconds:N1}s");

        // THE PATH, because it is the only way the model ever reads what the command printed — the
        // tool result was handed back before there was any output to put in it.
        var path = Assert.IsType<string>(r.Output["output_file"]);
        Assert.True(File.Exists(path), $"output file not created at {path}");
        Assert.True(r.Output.TryGetValue("pid", out var pid) && (int)pid! > 0);

        // AND NOT THE OUTPUT. An empty `stdout` key is worse than no key: the model reads it as the
        // command having printed nothing rather than as not having run yet.
        Assert.False(r.Output.ContainsKey("stdout"));
        Assert.DoesNotContain("LATEOUTPUT", string.Join(" ", r.Output.Values));
    }

    [Fact]
    public async Task Background_TellsTheAgentWhenItExits_WithTheExitCode()
    {
        // The delivery is the ONLY report a backgrounded command ever makes — its tool result was
        // read by the model before the command ran. Without it the outcome exists nowhere the model
        // can reach.
        using var fx = new BackgroundFixture();

        await fx.Executor.ExecuteAsync(
            P(("command", "echo done-in-background"), ("background", true)),
            fx.Context, CancellationToken.None);

        var (agentId, text) = await fx.Told.Next();

        // ADDRESSED BY THE ID ITS TOOL CALLS CARRY. A delivery to any other id wakes somebody else
        // and the agent that ran the command never hears about it.
        Assert.Equal("agent-bg", agentId);

        // THE FACTS, NOT "IT FINISHED". A command finishing forty minutes and one compaction later
        // reaches a context that no longer holds why it was started, so the message carries the
        // command and the exit code on its own.
        Assert.Contains("echo done-in-background", text);
        Assert.Contains("exit code 0", text);
    }

    [Fact]
    public async Task Background_AFailingCommandStillReports()
    {
        // THE CASE THAT MATTERS MOST, and the one a happy-path delivery would miss: the tool already
        // reported Success, so a report that only fired on a clean exit would make every background
        // failure indistinguishable from a command still running.
        using var fx = new BackgroundFixture();

        await fx.Executor.ExecuteAsync(
            P(("command", "exit 7"), ("background", true)), fx.Context, CancellationToken.None);

        var (_, text) = await fx.Told.Next();
        Assert.Contains("exit code 7", text);
        Assert.Contains("exit 7", text);
    }

    [Fact]
    public async Task Background_WithNoDeliveryPort_StillStartsTheCommand()
    {
        // A CONTEXT THAT CANNOT BE TOLD IS NOT AN ERROR. A headless run, an embedder that wired no
        // session, and the several test doubles that implement IJobContext all reach here; throwing
        // for them would make backgrounding fail in exactly the places nothing is watching.
        var registry = new DetachedProcessRegistry();
        try
        {
            var r = await new ShellJobExecutor(registry).ExecuteAsync(
                P(("command", "echo hi"), ("background", true)),
                new CollectingContext(), CancellationToken.None);

            Assert.True(r.Success, r.ErrorMessage);

            // SAID PLAINLY IN THE RESULT, because the model is otherwise promised a report that will
            // never arrive and may wait for it instead of checking the file.
            Assert.Contains("not", r.Output["background"]!.ToString()!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            registry.ReapAll();
        }
    }

    [Fact]
    public async Task Background_AtTheConcurrencyCap_IsAToolResultAndNotAnException()
    {
        // THE CAP IS REACHED BY A MODEL BACKGROUNDING ONE COMMAND PER TURN, which is the ordinary way
        // to use the parameter rather than abuse. An exception surfaces as a tool FAULT — the model is
        // told the tool is broken, not that it has too many commands running — so the refusal has to
        // be a result carrying the remedy.
        using var fx = new BackgroundFixture();

        for (var i = 0; i < DetachedProcessRegistry.MaxConcurrent; i++)
        {
            var filled = await fx.Executor.ExecuteAsync(
                P(("command", "sleep 30"), ("background", true)), fx.Context, CancellationToken.None);
            Assert.True(filled.Success, filled.ErrorMessage);
        }

        var refused = await fx.Executor.ExecuteAsync(
            P(("command", "sleep 30"), ("background", true)), fx.Context, CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Contains("background", refused.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DetachedProcessRegistry.MaxConcurrent.ToString(), refused.ErrorMessage!);
    }

    [Fact]
    public void Schema_DeclaresBackground_AndSaysWhatTimeoutMeansUnderIt()
    {
        // BOTH SURFACES OR NEITHER. ToolBindings names the parameters the model is OFFERED and the
        // schema describes them; a parameter present in one and absent from the other is either a
        // parameter the model cannot send or one it is never told about.
        var schema = new ShellJobExecutor().GetSchema();
        var background = Assert.Single(schema.Params, p => p.Name == "background");
        Assert.False(background.Required);

        var timeout = Assert.Single(schema.Params, p => p.Name == "timeout_seconds");
        Assert.Contains("background", timeout.Description!, StringComparison.OrdinalIgnoreCase);
    }
}
