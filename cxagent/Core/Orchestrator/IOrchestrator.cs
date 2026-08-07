using CxAgent.Core.Models;

namespace CxAgent.Core.Orchestrator;

public interface IOrchestrator
{
    Task<Goal> StartGoalAsync(string description, CancellationToken ct);

    event EventHandler<Job>? JobStateChanged;
    event EventHandler<GoalState>? GoalStateChanged;
}
