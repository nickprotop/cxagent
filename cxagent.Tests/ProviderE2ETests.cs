using System.Collections.Concurrent;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using Xunit;

namespace CxAgent.Tests;

[Collection("http-listeners")]
public class ProviderE2ETests : IDisposable
{
    private readonly LoopbackServer _srv = new();
    public void Dispose() => _srv.Dispose();

    [Fact]
    public async Task Orchestrator_DecomposesAndRunsGoal_ThroughRealDriver_OverRealHttp()
    {
        // A real registry with one openai-compatible instance pointed at the loopback server.
        var settings = new ProviderSettings(
            new Dictionary<string, ProviderInstanceConfig>
            {
                ["local"] = new("openai-compatible", "test-model", "sk-test",
                    _srv.BaseUrl.TrimEnd('/'), null)
            },
            DefaultProvider: "local",
            AllowedProviders: Array.Empty<string>(),
            Routing: new Dictionary<string, RoutingTarget>());
        var registry = ProviderRegistry.Build(settings);

        // The provider returns a create_plan tool call. Shape matches P1's BuildDag parser
        // (jobs[].id/name/type/params/depends_on) — the exact shape OrchestratorTests uses.
        _srv.EnqueueJson(200, """
		{"choices":[{"message":{"role":"assistant","tool_calls":[{
		  "id":"call_1","type":"function","function":{
		    "name":"create_plan",
		    "arguments":"{\"summary\":\"two steps\",\"jobs\":[{\"id\":\"s1\",\"name\":\"Step 1\",\"type\":\"shell\",\"params\":{\"command\":\"echo hi\"}},{\"id\":\"s2\",\"name\":\"Step 2\",\"type\":\"shell\",\"params\":{\"command\":\"echo bye\"},\"depends_on\":[\"s1\"]}]}"
		  }}]},"finish_reason":"tool_calls"}]}
		""");

        // runJob IS load-bearing: StartGoalAsync runs the DAG. Capture order to prove the
        // driver's normalized response decomposed into a real, dependency-ordered plan.
        var ran = new ConcurrentQueue<string>();
        Func<Job, CancellationToken, Task<JobResult>> runJob = (job, ct) =>
        {
            ran.Enqueue(job.DisplayName);
            return Task.FromResult(new JobResult { Success = true });
        };

        var orchestrator = new Orchestrator(registry.Default, runJob);
        var goal = await orchestrator.StartGoalAsync("do two steps", CancellationToken.None);

        // Observable end state (Goal has no .Jobs — jobs live in the orchestrator's internal DAG):
        Assert.Equal(GoalState.Completed, goal.State);
        Assert.Equal(new[] { "Step 1", "Step 2" }, ran.ToArray());  // decomposed + dependency-ordered
    }
}
