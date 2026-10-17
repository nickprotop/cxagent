using CxAgent.Core.Agents;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The model's view of background shell commands through the task tools: <c>task_list</c> sees every
/// live command, <c>task_stop</c> stops only what the caller is allowed to, <c>task_output</c> reads one —
/// running or ended.
/// </summary>
[Collection(DefaultShellRegistryCollection.Name)]
public class TaskToolsShellTests
{
    private static TaskTools Tools(DetachedProcessRegistry live, FinishedShells? finished = null) =>
        new(new TaskSources(live, finished ?? new FinishedShells(), "SESSION", null));

    private static string Call(TaskTools tools, string caller, string tool, object args) =>
        tools.InvokeAsync(caller, Calls.Named(tool, args), CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void AnEmptyListSaysSoRatherThanNothing()
    {
        Assert.Contains("nothing is running in the background",
            Call(Tools(new DetachedProcessRegistry()), "SESSION", Tool.TaskList, new { }));
    }

    [Fact]
    public void AChildMayNotKillASiblingsJob()
    {
        using var fx = new JobToolsFixture("CHILD_A");
        var tools = Tools(fx.Registry);

        var answer = Call(tools, "CHILD_B", Tool.TaskStop, new { id = fx.Process.Pid });

        Assert.Contains("CHILD_A", answer);          // names the owner
        Assert.False(fx.Process.Finished);            // and did not kill it
    }

    [Fact]
    public void TheSessionMayKillAnyJob()
    {
        using var fx = new JobToolsFixture("CHILD_A");
        var tools = Tools(fx.Registry);

        Assert.Contains("stopped", Call(tools, "SESSION", Tool.TaskStop, new { id = fx.Process.Pid }));
        Assert.True(fx.Process.Finished);
    }

    [Fact]
    public void AChildMayKillItsOwnJob()
    {
        using var fx = new JobToolsFixture("CHILD_A");
        var tools = Tools(fx.Registry);

        Assert.Contains("stopped", Call(tools, "CHILD_A", Tool.TaskStop, new { id = fx.Process.Pid }));
        Assert.True(fx.Process.Finished);
    }

    [Fact]
    public void AnUnknownPidIsNotAFailureToExplain()
    {
        Assert.Contains("no background command with pid 999999",
            Call(Tools(new DetachedProcessRegistry()), "SESSION", Tool.TaskStop, new { id = 999999 }));
    }

    /// <summary>
    /// A TASK_LIST CALL REACHES THE TASK TOOLS — proving both halves of the wiring at once. The model is
    /// only ALLOWED to call task_list if Agent advertised it (were the definition missing, the terminator
    /// would answer "no such tool" rather than the empty text this asserts on), and the call only REACHES
    /// TaskTools if the dispatch chain actually claims it rather than falling through.
    /// </summary>
    [Fact]
    public async Task ATaskListCallReachesTheTaskTools()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "c1", Name = Tool.TaskList, Arguments = default }],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(),
            new TokenLedger(), new BufferedChatSink(), new BufferedJobPanel(), logs: null,
            maxTurns: 5);

        await agent.SendAsync("list background tasks", CancellationToken.None);

        var toolResults = agent.Context.Messages
            .Where(m => m.Role == "tool")
            .Select(m => m.Content)
            .ToList();
        Assert.Contains(toolResults, c => c is not null && c.Contains("nothing is running in the background"));
    }

    /// <summary>
    /// One test's registry, one described job, and the spill directory it wrote to — killed and
    /// deleted on the way out.
    ///
    /// <para>DISPOSABLE SO A FAILING ASSERTION STILL REAPS, as <c>DetachedProcessTests.Fixture</c>
    /// is: an assertion that fails before the kill test runs would otherwise leave a `sleep 5`
    /// running for a minute per failure.</para>
    /// </summary>
    private sealed class JobToolsFixture : IDisposable
    {
        public DetachedProcessRegistry Registry { get; }
        public DetachedProcess Process { get; }

        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "cxagent-jobtools-tests", Guid.NewGuid().ToString("N"));

        public JobToolsFixture(string owner, string command = "sleep 5", DetachedProcessRegistry? registry = null)
        {
            Registry = registry ?? new DetachedProcessRegistry();
            Process = ProcessRunner.DetachAsync(
                new ProcessSpec("/bin/sh", ["-c", command], new RunOptions(WorkingDir: TestProcesses.WorkingDir, SpillDir: _dir)),
                new CollectingContext(), Registry).GetAwaiter().GetResult();

            Registry.Describe(Process, new BackgroundJob(Process.Pid, owner, command,
                DateTimeOffset.UtcNow, Process.OutputPath));
        }

        public void Dispose()
        {
            Registry.ReapAll();
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
        }
    }
    /// <summary>
    /// A LIST AND A REFUSAL MUST NAME THE SAME OWNER THE SAME WAY. task_list renders the session's own
    /// job as "session", so a refusal answering with the raw session id describes one owner two ways —
    /// and it read "only &lt;id&gt; or the session can stop it", naming a single party twice as though
    /// the caller had two routes when it has none.
    /// </summary>
    [Fact]
    public void ARefusalCallsTheSessionTheSessionRatherThanAnId()
    {
        using var fx = new JobToolsFixture("SESSION");
        var tools = Tools(fx.Registry);

        var answer = Call(tools, "CHILD_B", Tool.TaskStop, new { id = fx.Process.Pid });

        Assert.Contains("started by the session", answer);
        Assert.DoesNotContain("SESSION or the session", answer);
        Assert.False(fx.Process.Finished);
    }
    /// <summary>
    /// A LISTED JOB MUST BE READABLE, not merely visible. The output of a background command is in a
    /// file, and the only place the model ever saw that path was the `run_shell` result it may have
    /// dropped from context many turns ago — so a row naming a pid and a command line leaves it
    /// holding an identifier it cannot act on. The path is already on the entry; this pins that it
    /// reaches the row.
    /// </summary>
    [Fact]
    public void AListedJobNamesTheFileItsOutputIsGoingTo()
    {
        using var fx = new JobToolsFixture("SESSION");
        var tools = Tools(fx.Registry);

        var listing = Call(tools, "SESSION", Tool.TaskList, new { });

        Assert.Contains(fx.Process.OutputPath!, listing);
    }

    // ---- ended shells, reading, waiting, and the rest of the surface ---------------------------------

    private static BackgroundJob Job(int pid, string owner, string? path = null) =>
        new(pid, owner, $"cmd{pid}", DateTimeOffset.UtcNow, path);

    [Fact]
    public void TaskList_ShowsAnExitedShell_WithItsExitCode()
    {
        var finished = new FinishedShells();
        finished.Track(Job(4242, "SESSION"), _ => true);
        finished.Finished(4242, exitCode: 3, killed: false);

        var listing = Call(Tools(new DetachedProcessRegistry(), finished), "SESSION", Tool.TaskList, new { });

        Assert.Contains("4242", listing);
        Assert.Contains("exited 3", listing);
    }

    [Fact]
    public void TaskOutput_ForALiveShell_ShowsItsTail()
    {
        using var fx = new JobToolsFixture("SESSION", "echo first-line; sleep 5");
        Assert.True(Poll.Until(() => File.Exists(fx.Process.OutputPath!)
            && File.ReadAllText(fx.Process.OutputPath!).Contains("first-line")).GetAwaiter().GetResult());

        var output = Call(Tools(fx.Registry), "SESSION", Tool.TaskOutput, new { id = fx.Process.Pid });

        Assert.Contains("running", output);
        Assert.Contains("first-line", output);
    }

    [Fact]
    public void TaskOutput_ForAnExitedShell_ShowsItsExitAndTail_AndTakesTheResult()
    {
        var dir = Path.Combine(Path.GetTempPath(), "task-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "out.txt");
            File.WriteAllText(path, "one\ntwo\nthe last line\n");
            var finished = new FinishedShells();
            var handoff = finished.Track(Job(4243, "SESSION", path), _ => true);
            finished.Finished(4243, exitCode: 0, killed: false);
            handoff.Complete("The background command `cmd4243` finished with exit code 0.", _ => false);   // undelivered

            var tools = Tools(new DetachedProcessRegistry(), finished);
            var first = Call(tools, "SESSION", Tool.TaskOutput, new { id = 4243 });
            var second = Call(tools, "SESSION", Tool.TaskOutput, new { id = 4243 });

            Assert.Contains("exit code 0", first);
            Assert.Contains("the last line", first);
            Assert.DoesNotContain("also sent", first);
            Assert.Contains("also sent", second);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TaskOutput_WaitOnAShell_ReturnsWhenItEnds()
    {
        var finished = new FinishedShells();
        var handoff = finished.Track(Job(4244, "SESSION"), _ => true);
        var tools = Tools(new DetachedProcessRegistry(), finished);

        var wait = tools.InvokeAsync("SESSION", Calls.Named(Tool.TaskOutput, new { id = 4244, wait = true, timeout = 10 }),
            CancellationToken.None);
        await Task.Delay(100);
        finished.Finished(4244, exitCode: 0, killed: false);
        var sent = 0;
        handoff.Complete("finished with exit code 0", _ => { sent++; return true; });

        Assert.Contains("exit code 0", await wait);
        Assert.Equal(0, sent);
    }

    /// <summary>
    /// A WAIT ON A SHELL ENDS WHEN THE USER SPEAKS, as one on a sub-agent does, and the exit still reaches
    /// the model once — as the message the handoff sends, since nobody is waiting for it any more.
    /// </summary>
    [Fact]
    public async Task TaskOutput_WaitOnAShell_EndsWhenTheUserSpeaks()
    {
        var finished = new FinishedShells();
        var handoff = finished.Track(Job(4245, "SESSION"), _ => true);
        var spoke = new CancellationTokenSource();
        var tools = new TaskTools(new TaskSources(new DetachedProcessRegistry(), finished, "SESSION", null)
        {
            UserSpoke = () => spoke.Token,
        });

        var wait = tools.InvokeAsync("SESSION", Calls.Named(Tool.TaskOutput, new { id = 4245, wait = true, timeout = 600 }),
            CancellationToken.None);
        await Task.Delay(50);
        spoke.Cancel();
        var answer = await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("the user sent a message", answer);
        var sent = 0;
        finished.Finished(4245, exitCode: 0, killed: false);
        handoff.Complete("finished with exit code 0", _ => { sent++; return true; });
        Assert.Equal(1, sent);
    }

    /// <summary>
    /// A SHELL THE SESSION'S PREVIOUS AGENT STARTED IS STILL THE SESSION'S. A re-wire mints a new agent id,
    /// and an equality test against the current one would hide that agent's finished commands and name its
    /// running ones as somebody else's.
    /// </summary>
    [Fact]
    public void AShellOfAPreviousSessionAgent_IsTheSessions()
    {
        using var fx = new JobToolsFixture("OLD_SESSION");
        var finished = new FinishedShells();
        finished.Track(Job(4246, "OLD_SESSION"), _ => true);
        finished.Finished(4246, exitCode: 0, killed: false);
        var tools = new TaskTools(new TaskSources(fx.Registry, finished, id => id is "SESSION" or "OLD_SESSION", null));

        var listing = Call(tools, "SESSION", Tool.TaskList, new { });

        Assert.Contains($"shell pid {fx.Process.Pid} (", listing);
        Assert.Contains("session) running", listing);
        Assert.Contains("shell pid 4246 exited 0", listing);
    }

    /// <summary>
    /// A CHILD'S task_list SHOWS EVERY SHELL (spec decision 29) — the ended ones too, whoever started them,
    /// as its live rows already do.
    /// </summary>
    [Fact]
    public void AChild_ListsEveryFinishedShell()
    {
        var finished = new FinishedShells();
        foreach (var (pid, owner) in new[] { (4247, "SESSION"), (4248, "CHILD_A"), (4249, "CHILD_B") })
        {
            finished.Track(Job(pid, owner), _ => true);
            finished.Finished(pid, exitCode: 0, killed: false);
        }

        var listing = Call(Tools(new DetachedProcessRegistry(), finished), "CHILD_A", Tool.TaskList, new { });

        Assert.Contains("shell pid 4247 exited 0", listing);
        Assert.Contains("shell pid 4248 exited 0", listing);
        Assert.Contains("shell pid 4249 exited 0", listing);
    }

    [Fact]
    public void AChild_ListsEveryLiveShell_ButStopsOnlyItsOwn()
    {
        using var mine = new JobToolsFixture("CHILD_A");
        var tools = Tools(mine.Registry);
        using var theirs = new JobToolsFixture("CHILD_B", registry: mine.Registry);

        var listing = Call(tools, "CHILD_A", Tool.TaskList, new { });
        Assert.Contains(mine.Process.Pid.ToString(), listing);
        Assert.Contains(theirs.Process.Pid.ToString(), listing);

        Assert.Contains("CHILD_B", Call(tools, "CHILD_A", Tool.TaskStop, new { id = theirs.Process.Pid }));
        Assert.False(theirs.Process.Finished);
    }

    [Fact]
    public void TheRemovedNames_AreNotKnownTools()
    {
        Assert.False(Tool.IsKnown("job_kill"));
        Assert.False(Tool.IsKnown("job_list"));
        Assert.False(Tool.IsKnown("agent_list"));
        Assert.True(Tool.IsKnown(Tool.TaskStop));
    }

    /// <summary>EVERY AGENT IS OFFERED THE TASK TOOLS — a child for its own shells — and none of the removed ones.</summary>
    [Fact]
    public void EveryAgent_IsOfferedTheTaskTools_AndNotTheRemovedOnes()
    {
        var parent = new Agent(new MockLlmProvider(), JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5);
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
        }).Create();

        foreach (var names in new[] { parent.OfferedToolNames(), child.Agent.OfferedToolNames() })
        {
            Assert.Contains(Tool.TaskList, names);
            Assert.Contains(Tool.TaskStop, names);
            Assert.Contains(Tool.TaskOutput, names);
            Assert.DoesNotContain("job_list", names);
            Assert.DoesNotContain("job_kill", names);
            Assert.DoesNotContain("agent_list", names);
        }
    }

    /// <summary>
    /// A CONFIG NAMING A REMOVED TOOL IS WARNED, with the replacement: an unknown name is legal and ignored,
    /// so "-job_kill" would otherwise silently stop restricting the tool that does its job.
    /// </summary>
    [Theory]
    [InlineData("-job_kill", "task_stop")]
    [InlineData("job_list", "task_list")]
    [InlineData("+agent_list", "task_list")]
    public void AConfigNamingARemovedTool_WarnsWithTheReplacement(string term, string replacement)
    {
        var settings = ProviderConfigLoader.Parse($$"""
            { "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local",
              "llmAgent": { "tools": ["inherited", "{{term}}"] } }
            """, new Dictionary<string, string>());

        var warning = Assert.Single(settings.Warnings, w => w.Contains(term.TrimStart('+', '-')));
        Assert.Contains(replacement, warning);
    }
}
