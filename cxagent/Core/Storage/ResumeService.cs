using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;

namespace CxAgent.Core.Storage;

/// <summary>A goal restored from persistence: its reconciled state, its rebuilt DAG,
/// and the ids of Queued jobs the caller must re-feed to a fresh DagScheduler.</summary>
public record ResumedGoal(Goal Goal, JobDag Dag, IReadOnlyList<string> QueuedJobIdsToReprime);

/// <summary>
/// Startup state reconciliation. Loads Active/Draft goals and reconciles job states
/// per the spec so nothing is stuck Running after a crash, then re-persists the result.
/// </summary>
public class ResumeService
{
    private readonly IGoalStore _store;
    public ResumeService(IGoalStore store) => _store = store;

    public async Task<List<ResumedGoal>> ResumeAsync()
    {
        var goals = await _store.ListGoalsByStateAsync(GoalState.Active, GoalState.Draft);
        var result = new List<ResumedGoal>();

        foreach (var goal in goals)
        {
            var jobs = await _store.GetJobsForGoalAsync(goal.Id);
            var dag = new JobDag();
            var toReprime = new List<string>();

            // Draft goals: scheduler was inert, nothing ran — restore jobs untouched.
            bool isDraft = goal.State == GoalState.Draft;

            foreach (var job in jobs)
            {
                if (!isDraft)
                {
                    switch (job.State)
                    {
                        case JobState.Running:
                            // The child process/container is gone — cannot resume.
                            // TODO(P3): once the job engine tracks OS PIDs / container ids, consult
                            // the recorded handle here to decide re-run vs. fail (double-run guard).
                            job.State = JobState.Failed;
                            job.CompletedAt = DateTimeOffset.UtcNow;
                            job.Result = new JobResult { Success = false, ErrorMessage = "interrupted by app shutdown" };
                            break;
                        case JobState.Queued:
                            // In-memory scheduler state is gone; re-feed to a fresh scheduler.
                            toReprime.Add(job.Id);
                            break;
                        // Paused stays Paused (intentional). Pending/Succeeded/Skipped/Cancelled/Failed unchanged.
                    }
                }
                dag.AddJob(job);
            }

            // Re-persist reconciled states so a second crash sees consistent data.
            if (!isDraft)
                foreach (var job in jobs)
                    await _store.SaveJobAsync(job);

            // TODO(P4/P6): the LLM "this goal was interrupted — retry or re-evaluate?" prompt
            // is the caller's decision using a real provider + the diagnosis flow. ResumeService
            // only returns the reconciled goals; it does not itself talk to an LLM.
            result.Add(new ResumedGoal(goal, dag, toReprime));
        }

        return result;
    }
}
