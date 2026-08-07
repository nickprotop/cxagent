using CxAgent.Core.Models;

namespace CxAgent.Core.Orchestrator;

/// <summary>
/// Applies a <see cref="DagModification"/> to a live <see cref="JobDag"/>.
///
/// Pure graph editing — no locking, no scheduling. Operates directly on the live dag
/// (never a clone: <see cref="JobDag"/> holds live <see cref="Job"/> references that
/// <see cref="DagScheduler"/> concurrently mutates, so a clone would desynchronise).
///
/// Atomicity: if the resulting graph would be invalid, every operation this call performed
/// is undone in reverse before returning, leaving the dag byte-for-byte as it was.
/// </summary>
public static class DagModifier
{
    /// <summary>
    /// Applies <paramref name="mod"/> to <paramref name="dag"/>. Order of operations:
    /// parameter changes, then added jobs (rewiring <paramref name="insertBeforeJobId"/> to
    /// depend on each of them), then removals. Validates the result; on failure rolls back
    /// exactly what was applied and returns false with <paramref name="error"/> set.
    /// </summary>
    public static bool TryApply(JobDag dag, DagModification mod, string? insertBeforeJobId, out string? error)
    {
        // --- Track exactly what we do, so we can undo it precisely on failure. ---
        // Job.Parameters is init-only, so a "change" means replacing the contents of its
        // Values dictionary in place; we snapshot the previous contents to restore verbatim.
        var appliedParamChanges = new List<(string JobId, Dictionary<string, object?> Previous)>();
        var addedJobIds = new List<string>();
        var addedDependencyEdges = new List<(string JobId, string DependsOnJobId)>();
        var removedJobs = new List<Job>();

        // --- Guard: reject removing a job that a surviving job still depends on. ---
        // JobDag.RemoveJob silently strips the removed id from every DependsOn list, so the
        // dangling-dependency case Validate() checks for would never actually occur — the edge
        // is gone, not dangling. We must catch "still depended upon" ourselves, before removing.
        var removeSet = new HashSet<string>(mod.JobIdsToRemove);
        foreach (var jobId in mod.JobIdsToRemove)
        {
            var dependents = dag.GetDependents(jobId).Where(d => !removeSet.Contains(d.Id)).ToList();
            if (dependents.Count > 0)
            {
                error = $"Cannot remove job '{jobId}': job(s) {string.Join(", ", dependents.Select(d => d.Id))} still depend on it.";
                return false;
            }
        }

        // --- Apply parameter changes. ---
        foreach (var (jobId, newParams) in mod.ParameterChanges)
        {
            var job = dag.TryGet(jobId);
            if (job is null)
            {
                error = $"Cannot change parameters for unknown job '{jobId}'.";
                Rollback();
                return false;
            }
            appliedParamChanges.Add((jobId, new Dictionary<string, object?>(job.Parameters.Values)));
            job.Parameters.Values.Clear();
            foreach (var (k, v) in newParams.Values)
                job.Parameters.Values[k] = v;
        }

        // --- Add new jobs, rewiring the insertion target to depend on each. ---
        foreach (var job in mod.JobsToAdd)
        {
            dag.AddJob(job);
            addedJobIds.Add(job.Id);
        }

        if (insertBeforeJobId is not null && mod.JobsToAdd.Count > 0)
        {
            var target = dag.TryGet(insertBeforeJobId);
            if (target is null)
            {
                error = $"Cannot insert before unknown job '{insertBeforeJobId}'.";
                Rollback();
                return false;
            }
            foreach (var job in mod.JobsToAdd)
            {
                dag.AddDependency(insertBeforeJobId, job.Id);
                addedDependencyEdges.Add((insertBeforeJobId, job.Id));
            }
        }

        // --- Remove requested jobs. ---
        foreach (var jobId in mod.JobIdsToRemove)
        {
            var job = dag.TryGet(jobId);
            if (job is null)
            {
                error = $"Cannot remove unknown job '{jobId}'.";
                Rollback();
                return false;
            }
            removedJobs.Add(job);
            dag.RemoveJob(jobId);
        }

        // --- Validate the result; roll back everything on failure. ---
        if (!dag.Validate(out error))
        {
            Rollback();
            return false;
        }

        error = null;
        return true;

        void Rollback()
        {
            // Reverse order of application.

            // Undo removals: re-add the job object (its own DependsOn list is untouched by
            // RemoveJob, which only ever strips edges pointing AT the removed id). The guard
            // above already refuses removal whenever a surviving job still depends on it, so
            // by the time we reached actual removal no surviving job's DependsOn needed to be
            // (or was) stripped — re-adding the job fully restores the pre-removal graph.
            for (var i = removedJobs.Count - 1; i >= 0; i--)
                dag.AddJob(removedJobs[i]);

            // Undo added dependency edges (insertBefore rewiring).
            for (var i = addedDependencyEdges.Count - 1; i >= 0; i--)
            {
                var (jobId, dependsOnJobId) = addedDependencyEdges[i];
                dag.RemoveDependency(jobId, dependsOnJobId);
            }

            // Undo added jobs.
            for (var i = addedJobIds.Count - 1; i >= 0; i--)
                dag.RemoveJob(addedJobIds[i]);

            // Undo parameter changes.
            for (var i = appliedParamChanges.Count - 1; i >= 0; i--)
            {
                var (jobId, previous) = appliedParamChanges[i];
                var job = dag.TryGet(jobId);
                if (job is not null)
                {
                    job.Parameters.Values.Clear();
                    foreach (var (k, v) in previous)
                        job.Parameters.Values[k] = v;
                }
            }
        }
    }
}
