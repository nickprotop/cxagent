using CxAgent.Core.Agents;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The model's own view of background work: <c>job_list</c> sees everything live, <c>job_kill</c>
/// stops only what the caller is allowed to.
/// </summary>
public class BackgroundJobToolsTests
{
    [Fact]
    public void AnEmptyListSaysSoRatherThanNothing()
    {
        var tools = new BackgroundJobTools(new DetachedProcessRegistry(), "SESSION");
        Assert.Contains("no background commands", tools.Invoke(Tool.JobList, "SESSION", 0));
    }

    [Fact]
    public void AChildMayNotKillASiblingsJob()
    {
        using var fx = new JobToolsFixture("CHILD_A");
        var tools = new BackgroundJobTools(fx.Registry, "SESSION");

        var answer = tools.Invoke(Tool.JobKill, "CHILD_B", fx.Process.Pid);

        Assert.Contains("CHILD_A", answer);          // names the owner
        Assert.False(fx.Process.Finished);            // and did not kill it
    }

    [Fact]
    public void TheSessionMayKillAnyJob()
    {
        using var fx = new JobToolsFixture("CHILD_A");
        var tools = new BackgroundJobTools(fx.Registry, "SESSION");

        Assert.Contains("stopped", tools.Invoke(Tool.JobKill, "SESSION", fx.Process.Pid));
        Assert.True(fx.Process.Finished);
    }

    [Fact]
    public void AChildMayKillItsOwnJob()
    {
        using var fx = new JobToolsFixture("CHILD_A");
        var tools = new BackgroundJobTools(fx.Registry, "SESSION");

        Assert.Contains("stopped", tools.Invoke(Tool.JobKill, "CHILD_A", fx.Process.Pid));
        Assert.True(fx.Process.Finished);
    }

    [Fact]
    public void AnUnknownPidIsNotAFailureToExplain()
    {
        var tools = new BackgroundJobTools(new DetachedProcessRegistry(), "SESSION");
        Assert.Contains("no background command with pid 999999",
            tools.Invoke(Tool.JobKill, "SESSION", 999999));
    }

    /// <summary>
    /// A JOB_LIST CALL REACHES BackgroundJobTools — proving both halves of the wiring at once. The
    /// model is only ALLOWED to call job_list if Agent advertised it (were the definition missing,
    /// the terminator would answer "no such tool" rather than the empty-registry text this asserts
    /// on), and the call only REACHES BackgroundJobTools if the dispatch chain actually claims it
    /// rather than falling through. Either half missing produces a different wrong answer, so this
    /// one assertion covers both.
    /// </summary>
    [Fact]
    public async Task AJobListCallReachesTheJobTools()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "", StopReason = "tool_use",
            ToolCalls = [new ToolCall { Id = "c1", Name = Tool.JobList, Arguments = default }],
        });
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(),
            new TokenLedger(), new BufferedChatSink(), new BufferedJobPanel(), logs: null,
            maxTurns: 5);

        await agent.SendAsync("list background jobs", CancellationToken.None);

        var toolResults = agent.Context.Messages
            .Where(m => m.Role == "tool")
            .Select(m => m.Content)
            .ToList();
        Assert.Contains(toolResults, c => c is not null && c.Contains("no background commands are running"));
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
        public DetachedProcessRegistry Registry { get; } = new();
        public DetachedProcess Process { get; }

        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "cxagent-jobtools-tests", Guid.NewGuid().ToString("N"));

        public JobToolsFixture(string owner)
        {
            Process = ProcessRunner.DetachAsync(
                new ProcessSpec("/bin/sh", ["-c", "sleep 5"], new RunOptions(SpillDir: _dir)),
                new CollectingContext(), Registry).GetAwaiter().GetResult();

            Registry.Describe(Process, new BackgroundJob(Process.Pid, owner, "sleep 5",
                DateTimeOffset.UtcNow, Process.OutputPath));
        }

        public void Dispose()
        {
            Registry.ReapAll();
            try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
        }
    }
    /// <summary>
    /// A LIST AND A REFUSAL MUST NAME THE SAME OWNER THE SAME WAY. job_list renders the session's own
    /// job as "session", so a refusal answering with the raw session id describes one owner two ways —
    /// and it read "only &lt;id&gt; or the session can stop it", naming a single party twice as though
    /// the caller had two routes when it has none.
    /// </summary>
    [Fact]
    public void ARefusalCallsTheSessionTheSessionRatherThanAnId()
    {
        using var fx = new JobToolsFixture("SESSION");
        var tools = new BackgroundJobTools(fx.Registry, "SESSION");

        var answer = tools.Invoke(Tool.JobKill, "CHILD_B", fx.Process.Pid);

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
        var tools = new BackgroundJobTools(fx.Registry, "SESSION");

        var listing = tools.Invoke(Tool.JobList, "SESSION", 0);

        Assert.Contains(fx.Process.OutputPath!, listing);
    }


}
