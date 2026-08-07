using CxAgent.Core.Models;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// In-memory DAG of jobs keyed by ULID id. Dependency edges are jobId -> dependsOn.
/// Pure graph logic — no scheduling, no async. All queries are snapshots.
/// </summary>
public class JobDag
{
    private readonly Dictionary<string, Job> _jobs = new();

    public IReadOnlyList<Job> AllJobs => _jobs.Values.ToList();

    public Job? TryGet(string jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

    public void AddJob(Job job) => _jobs[job.Id] = job;

    public void RemoveJob(string jobId)
    {
        _jobs.Remove(jobId);
        foreach (var j in _jobs.Values)
            j.DependsOn.Remove(jobId);
    }

    public void AddDependency(string jobId, string dependsOnJobId)
    {
        if (_jobs.TryGetValue(jobId, out var j) && !j.DependsOn.Contains(dependsOnJobId))
            j.DependsOn.Add(dependsOnJobId);
    }

    public void RemoveDependency(string jobId, string dependsOnJobId)
    {
        if (_jobs.TryGetValue(jobId, out var j))
            j.DependsOn.Remove(dependsOnJobId);
    }

    private static bool IsSatisfied(JobState s) => s is JobState.Succeeded or JobState.Skipped;

    public IReadOnlyList<Job> GetReadyJobs() =>
        _jobs.Values
            .Where(j => j.State == JobState.Pending &&
                        j.DependsOn.All(d => _jobs.TryGetValue(d, out var dep) && IsSatisfied(dep.State)))
            .ToList();

    public IReadOnlyList<Job> GetDependents(string jobId) =>
        _jobs.Values.Where(j => j.DependsOn.Contains(jobId)).ToList();

    public IReadOnlyList<Job> GetAncestors(string jobId)
    {
        var result = new HashSet<string>();
        var stack = new Stack<string>();
        if (_jobs.TryGetValue(jobId, out var start))
            foreach (var d in start.DependsOn) stack.Push(d);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            if (_jobs.TryGetValue(id, out var j))
                foreach (var d in j.DependsOn) stack.Push(d);
        }
        return result.Select(id => _jobs[id]).ToList();
    }

    public IReadOnlyList<Job> GetJobsByState(JobState state) =>
        _jobs.Values.Where(j => j.State == state).ToList();

    public IReadOnlyList<Job> GetTopologicalOrder()
    {
        var visited = new HashSet<string>();
        var order = new List<Job>();

        void Visit(string id)
        {
            if (!visited.Add(id)) return;
            if (_jobs.TryGetValue(id, out var j))
            {
                foreach (var d in j.DependsOn) Visit(d);
                order.Add(j);
            }
        }

        // Deterministic: visit in ULID order so ties are stable.
        foreach (var id in _jobs.Keys.OrderBy(x => x, StringComparer.Ordinal))
            Visit(id);
        return order;
    }

    /// <summary>Validates the graph: no cycles and no dependency on a job not in the DAG.</summary>
    public bool Validate(out string? error)
    {
        foreach (var j in _jobs.Values)
            foreach (var d in j.DependsOn)
                if (!_jobs.ContainsKey(d))
                {
                    error = $"Job '{j.Id}' depends on unknown job '{d}'.";
                    return false;
                }

        // Cycle detection via DFS colouring.
        var state = new Dictionary<string, int>(); // 0=unvisited,1=in-stack,2=done
        bool HasCycle(string id)
        {
            state[id] = 1;
            foreach (var d in _jobs[id].DependsOn)
            {
                var s = state.GetValueOrDefault(d, 0);
                if (s == 1) return true;
                if (s == 0 && HasCycle(d)) return true;
            }
            state[id] = 2;
            return false;
        }

        foreach (var id in _jobs.Keys)
            if (state.GetValueOrDefault(id, 0) == 0 && HasCycle(id))
            {
                error = "Dependency cycle detected in the DAG.";
                return false;
            }

        error = null;
        return true;
    }
}
