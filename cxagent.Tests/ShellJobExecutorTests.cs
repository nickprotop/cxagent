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

    /// <summary>
    /// AN `env` A MODEL SENDS REACHES THE PROCESS.
    ///
    /// <para>THE PLUMBING WAS COMPLETE AND THE ADVERTISEMENT WAS NOT, which is why this needs a test
    /// rather than a glance: the schema described `env`, the executor read it and the runner applied it
    /// to both the foreground and detached paths — but `ToolBindings.Params` omitted the name, so no
    /// model was ever told it existed and nothing exercised the path end to end. Asserting that the
    /// name appears in a list would only restate the fix; asserting the CHILD PROCESS saw the value is
    /// the only thing that proves the chain.</para>
    ///
    /// <para>THE ASYMMETRY THAT HID IT: <c>BuildDefinition</c> throws for a name in <c>Params</c> that
    /// the schema lacks, and nothing walks the schema asking whether every param is advertised. Loud
    /// one way, silent the other.</para>
    /// </summary>
    [Fact]
    public async Task Execute_PassesEnvToTheProcess()
    {
        var result = await new ShellJobExecutor().ExecuteAsync(
            P(("command", "echo $CXAGENT_ENV_PROBE"),
              ("env", new Dictionary<string, string> { ["CXAGENT_ENV_PROBE"] = "reached" })),
            new CollectingContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("reached", result.Output["stdout"]?.ToString() ?? "");
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

    /// <summary>
    /// A DEADLINE IS STILL REPORTED, AND NOW REPORTED AS STILL RUNNING.
    ///
    /// <para>THE OLD MESSAGE IS THE HAZARD THIS PINS. It said the command "was killed" and told the
    /// model to retry with a bigger timeout_seconds — for a command that is in fact still working,
    /// that is a second `npm install`, a second migration, a second push. So the assertions are about
    /// what the model is told, not only about Success: the word "killed" must be gone, and the advice
    /// must be not to run it again.</para>
    ///
    /// <para>AND IT MUST STILL FAIL. The call did not produce what was asked for — a plan step that
    /// read Success would otherwise move on believing the command was done.</para>
    /// </summary>
    [Fact]
    public async Task Execute_Timeout_ReportsStillRunning_AndDoesNotSayItWasKilled()
    {
        using var fx = new BackgroundFixture();

        var result = await fx.Executor.ExecuteAsync(
            P(("command", "sleep 30"), ("timeout_seconds", 1)), fx.Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("still running", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("killed", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO NOT run it again", result.ErrorMessage!);

        // THE PID AND THE FILE, so the model can find the command again — the same keys a
        // `background: true` call returns, since the model is in the same position.
        Assert.True(result.Output!.ContainsKey("pid"), "a handed-over command must carry its pid");
        Assert.True(result.Output.ContainsKey("output_file"), "and the file its output is going to");

        // NO exit_code AT ALL, not a zero: the command has not finished, and a zero would read as a
        // command that succeeded.
        Assert.False(result.Output.ContainsKey("exit_code"));

        // AND THE PROCESS IS ACTUALLY THERE. Every assertion above is about text the executor wrote;
        // this one asks the OS whether the claim is true.
        Assert.True(LiveProcess(Convert.ToInt32(result.Output["pid"])),
            "the command must still be running, not merely described as running");
    }

    /// <summary>
    /// THE AGENT IS TOLD WHEN A HANDED-OVER COMMAND EXITS, through the same delivery a
    /// `background: true` call uses.
    ///
    /// <para>THE COMMAND FAILS, AND FAILS FAST AFTER THE HANDOVER, which is the shape that catches a
    /// report wired only to a subscription: <c>Exited</c> is raised once and never replayed, so a
    /// command that ends in the instant after the handover reaches a subscriber that may not exist
    /// yet. That is the defect this plan already shipped once on the background path — a happy-path
    /// test passed on timing luck while every fast failure went unreported.</para>
    /// </summary>
    [Fact]
    public async Task Execute_Timeout_TellsTheAgentWhenTheHandedOverCommandFails()
    {
        using var fx = new BackgroundFixture();

        // Past the 1s deadline, then fails immediately — so the exit lands in the window between the
        // handover and anything subscribing to it.
        var result = await fx.Executor.ExecuteAsync(
            P(("command", "sleep 2; exit 7"), ("timeout_seconds", 1)), fx.Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("You will be told when it exits", result.ErrorMessage!);

        var (agentId, text) = await fx.Told.Next();
        Assert.Equal("agent-bg", agentId);
        Assert.Contains("exit code 7", text);
        Assert.Contains("sleep 2; exit 7", text);
    }

    /// <summary>
    /// CONFIG'S <c>shellDetachOnTimeout: false</c> REACHES THE PROCESS, which is the only thing worth
    /// asserting about it.
    ///
    /// <para>NOT THAT THE FLAG IS STORED ON A RECORD. A test that builds a RunOptions and reads its
    /// member back proves the record works and says nothing about whether anything applies it — and
    /// the chain here is four hops long (config.json, ProviderSettings, ProviderCatalog,
    /// ResolvedConfig, JobRegistry, this constructor), any one of which can drop the value silently.
    /// So the assertion is that the OS has no process left, reached by asking for the old behaviour
    /// through the door a user's config actually opens.</para>
    /// </summary>
    [Fact]
    public async Task Execute_Timeout_WithDetachTurnedOffInConfig_KillsTheCommand()
    {
        using var fx = new BackgroundFixture();
        var executor = new ShellJobExecutor(new ShellBackgrounding(fx.Registry, DetachOnTimeout: false));

        // A CollectingContext, NOT the fixture's JobContext, only because this test needs the pid the
        // command printed and a JobContext's log write is fire-and-forget async — a race a test must
        // not depend on. What is under test here is the executor's flag, and it reads neither.
        var ctx = new CollectingContext();
        var result = await executor.ExecuteAsync(
            P(("command", "echo mypid $$; sleep 30"), ("timeout_seconds", 1)),
            ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("was killed", result.ErrorMessage!);
        Assert.False(result.Output?.ContainsKey("pid") ?? false,
            "a killed command has no pid worth handing back");
        Assert.Empty(fx.Registry.Live);

        // THE SHELL'S OWN PID, out of what the command itself printed — the executor cannot fake
        // this the way it can fake a field on its own result.
        var printed = ctx.Lines.First(l => l.Contains("mypid "));
        var pid = int.Parse(printed[(printed.IndexOf("mypid ", StringComparison.Ordinal) + 6)..].Trim());
        Assert.False(LiveProcess(pid), "shellDetachOnTimeout false must leave no process behind");
    }

    /// <summary>Whether a pid is a live process — see ProcessRunnerTests.ProcessExists for why this is
    /// HasExited and never a pgrep on a pattern.</summary>
    private static bool LiveProcess(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
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

            Executor = new ShellJobExecutor(new ShellBackgrounding(Registry));

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
            var r = await new ShellJobExecutor(new ShellBackgrounding(registry)).ExecuteAsync(
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
