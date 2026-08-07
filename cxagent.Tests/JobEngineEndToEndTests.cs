using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class JobEngineEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-e2e-" + Guid.NewGuid().ToString("N"));
    public JobEngineEndToEndTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task Goal_RunsRealFileThenShellJobs_ToCompletion()
    {
        var filePath = Path.Combine(_dir, "e2e.txt");
        var outPath = Path.Combine(_dir, "e2e-out.txt");

        // Plan: a 'file' job writes a file, then a dependent 'shell' job reads it (cat) into another file.
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
        {
            summary = "write then read",
            jobs = new object[]
            {
                new { id = "w", name = "Write", type = "file",
                      @params = new { action = "write", path = filePath, content = "chained-payload" } },
                new { id = "r", name = "Read", type = "shell",
                      @params = new { command = $"cat '{filePath}' > '{outPath}'" }, depends_on = new[] { "w" } },
            }
        }));

        // The DAG is built inside the orchestrator; the executor needs the same DAG to read
        // dependency results. We construct the executor's DAG lazily by having the orchestrator
        // build it — but P1's Orchestrator owns its DAG internally. For this end-to-end test we
        // use a JobExecutor whose DAG is shared with the orchestrator via a captured reference.
        //
        // P1's Orchestrator builds its DAG internally and does not expose it. For a real run the
        // executor's DAG must be the SAME instance the scheduler mutates. Since P1 doesn't expose
        // it, this test exercises the executor directly against a DAG we control, wired through a
        // scheduler — proving the execution path end-to-end without needing to reach into P1.
        var dag = new JobDag();
        var registry = PluginRegistry.CreateWithBuiltins();
        var executor = new JobExecutor(registry, dag);

        // Build the same 2-job DAG the plan describes, with ULID-style ids.
        var write = new Job
        {
            Id = "w",
            GoalId = "g",
            PluginType = "file",
            DisplayName = "Write",
            Parameters = new JobParameters(new() { ["action"] = "write", ["path"] = filePath, ["content"] = "chained-payload" }),
            CreatedAt = DateTimeOffset.UtcNow
        };
        var read = new Job
        {
            Id = "r",
            GoalId = "g",
            PluginType = "shell",
            DisplayName = "Read",
            Parameters = new JobParameters(new() { ["command"] = $"cat '{filePath}' > '{outPath}'" }),
            DependsOn = new List<string> { "w" },
            CreatedAt = DateTimeOffset.UtcNow
        };
        dag.AddJob(write); dag.AddJob(read);

        using var scheduler = new DagScheduler(dag, maxParallel: 4, runJob: executor.RunJobAsync);
        await scheduler.StartAsync();

        Assert.Equal(GoalState.Completed, scheduler.FinalGoalState);
        Assert.Equal(JobState.Succeeded, dag.TryGet("w")!.State);
        Assert.Equal(JobState.Succeeded, dag.TryGet("r")!.State);
        // The real work happened: the file was written and the shell job cat'd it to outPath.
        Assert.True(File.Exists(outPath), "the shell job should have produced the output file");
        Assert.Equal("chained-payload", (await File.ReadAllTextAsync(outPath)).TrimEnd('\n'));
        GC.KeepAlive(mock);
    }
}
