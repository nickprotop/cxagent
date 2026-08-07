using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Helpers;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// Wires the LLM to the DAG: sends the goal to the provider with create_plan as a
/// tool, maps the plan's local ids to ULIDs, builds and validates the DAG, then runs
/// it via DagScheduler. v1 autopilot: the plan runs immediately (confirm-before-execute
/// and copilot's Draft phase are later plans).
/// </summary>
public class Orchestrator : IOrchestrator
{
    private readonly ILlmProvider _provider;
    private readonly Func<Job, CancellationToken, Task<JobResult>> _runJob;
    private readonly int _maxParallel;

    public event EventHandler<Job>? JobStateChanged;
    public event EventHandler<GoalState>? GoalStateChanged;

    public Orchestrator(ILlmProvider provider,
        Func<Job, CancellationToken, Task<JobResult>> runJob, int maxParallel = 4)
    {
        _provider = provider;
        _runJob = runJob;
        _maxParallel = maxParallel;
    }

    public async Task<Goal> StartGoalAsync(string description, CancellationToken ct)
    {
        var goal = new Goal
        {
            Id = UlidGenerator.NewId(),
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
            ProviderId = _provider.ProviderId,
            State = GoalState.Active
        };

        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = description, Timestamp = DateTimeOffset.UtcNow }
        };

        // TODO (later plan): build and pass the create_plan ToolDefinition here. v1 relies on
        // the provider being pre-configured (the mock is pre-loaded); a real provider needs the
        // tool definition to know create_plan is callable.
        var response = await _provider.ChatAsync(messages, tools: null, ct);
        // Note: goal.State is left Active here (not set to Failed) — goal is a local not yet
        // persisted, so its state is cosmetic on the throw path. Callers catch the exception.
        var plan = response.ToolCalls.FirstOrDefault(t => t.Name == "create_plan")
                   ?? throw new InvalidOperationException("LLM did not return a create_plan tool call.");

        var dag = PlanCompiler.BuildDag(goal.Id, plan.Arguments);
        if (!dag.Validate(out var error))
        {
            goal.State = GoalState.Failed;
            GoalStateChanged?.Invoke(this, goal.State);
            throw new InvalidOperationException($"Invalid plan: {error}");
        }

        using var scheduler = new DagScheduler(dag, _maxParallel, _runJob);
        scheduler.JobTransitioned += (_, job) => JobStateChanged?.Invoke(this, job);

        await scheduler.StartAsync();

        goal.State = scheduler.FinalGoalState;
        goal.CompletedAt = DateTimeOffset.UtcNow;
        GoalStateChanged?.Invoke(this, goal.State);
        return goal;
    }
}
