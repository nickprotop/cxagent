using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using Xunit;

namespace CxAgent.Tests;

public class OrchestratorTests
{
    private static JobResult Ok() => new() { Success = true, ExitCode = 0 };

    [Fact]
    public async Task StartGoal_DecomposesPlan_BuildsDag_RunsToCompletion()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
        {
            summary = "Test plan",
            jobs = new object[]
            {
                new { id = "s1", name = "Step 1", type = "shell", @params = new { command = "echo hello" } },
                new { id = "s2", name = "Step 2", type = "shell", @params = new { command = "echo world" }, depends_on = new[] { "s1" } }
            }
        }));

        var ran = new List<string>();
        var orch = new Orchestrator(mock, runJob: (job, ct) =>
        {
            ran.Add(job.DisplayName);
            return Task.FromResult(Ok());
        });

        var goal = await orch.StartGoalAsync("Test goal", CancellationToken.None);

        Assert.Equal(GoalState.Completed, goal.State);
        Assert.Equal(new[] { "Step 1", "Step 2" }, ran); // dependency order preserved
    }

    [Fact]
    public async Task StartGoal_MapsPlanLocalIdsToUlids_DependenciesResolve()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
        {
            summary = "fan-in",
            jobs = new object[]
            {
                new { id = "a", name = "A", type = "shell", @params = new { } },
                new { id = "b", name = "B", type = "shell", @params = new { } },
                new { id = "join", name = "Join", type = "shell", @params = new { }, depends_on = new[] { "a", "b" } }
            }
        }));

        string? joinRanAfter = null;
        var completed = new HashSet<string>();
        var orch = new Orchestrator(mock, runJob: (job, ct) =>
        {
            if (job.DisplayName == "Join")
                joinRanAfter = completed.Contains("A") && completed.Contains("B") ? "both" : "early";
            completed.Add(job.DisplayName);
            return Task.FromResult(Ok());
        });

        var goal = await orch.StartGoalAsync("goal", CancellationToken.None);

        Assert.Equal(GoalState.Completed, goal.State);
        Assert.Equal("both", joinRanAfter); // join waited for both a and b (ids mapped correctly)
    }
}
