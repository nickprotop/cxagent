using System.Text;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;

namespace CxAgent.UI;

/// <summary>
/// Mid-run introspection for the orchestrator: "what is running, what is this job doing right now,
/// and what has it produced so far." Unlike <see cref="JobDigest"/> (built once a job finishes and
/// handed to consult), these are pull-based — the orchestrator asks, at any point during the drive,
/// including while a job is still <see cref="JobState.Running"/>.
///
/// <para>No new plumbing lives here. Progress comes from <see cref="Job.Progress"/> and
/// <see cref="Job.ProgressMessage"/> (P6), which plugins already report continuously via
/// <c>IJobContext.ReportProgress</c>; a finished job's rendering reuses <see cref="JobDigest"/> so the
/// truncation policy — bounded head+tail, a visible elision marker, errors never truncated — is not
/// duplicated and cannot drift from what consult already shows.</para>
///
/// <para>Every method here returns an explanatory string rather than throwing on an unknown job id.
/// These are called by the orchestrator mid-goal; a typo'd id must not end the drive.</para>
/// </summary>
public static class IntrospectionTools
{
    /// <summary>
    /// Looks a job up by either the ULID <see cref="Job.Id"/> or the plan-local id the orchestrator
    /// itself chose (<see cref="Job.PlanLocalId"/>) — the orchestrator mostly remembers the latter
    /// (e.g. "r1"), the same convention <see cref="JobDigest"/> falls back to for its label.
    /// </summary>
    private static Job? Find(JobDag dag, string jobId) =>
        dag.TryGet(jobId) ?? dag.AllJobs.FirstOrDefault(j => j.PlanLocalId == jobId);

    /// <summary>Every job in the DAG with its current state — the "what is left" view.</summary>
    public static string ListJobs(JobDag dag)
    {
        var jobs = dag.GetTopologicalOrder();
        if (jobs.Count == 0) return "No jobs in this plan.";

        var sb = new StringBuilder();
        foreach (var job in jobs)
        {
            var label = string.IsNullOrEmpty(job.PlanLocalId) ? job.Id : job.PlanLocalId;
            sb.Append($"[{label}] {job.DisplayName} ({job.PluginType}) — {job.State}");
            if (job.State == JobState.Running && job.Progress is { } p)
                sb.Append($" ({p * 100:0}%)");
            if (job.DependsOn.Count > 0)
                sb.Append($" [depends on: {string.Join(", ", job.DependsOn.Select(id => Find(dag, id) is { } d ? (d.PlanLocalId ?? d.Id) : id))}]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// "What is this job doing right now." Works on a running job — Progress/ProgressMessage are
    /// live fields, updated continuously, not only present post-mortem. A finished job's status is
    /// rendered via <see cref="JobDigest"/> so the two views don't diverge.
    /// </summary>
    public static string GetJobStatus(JobDag dag, string jobId)
    {
        var job = Find(dag, jobId);
        if (job is null) return $"Job '{jobId}' not found in this plan.";

        if (job.State == JobState.Running)
        {
            var sb = new StringBuilder();
            var label = string.IsNullOrEmpty(job.PlanLocalId) ? job.Id : job.PlanLocalId;
            sb.AppendLine($"[{label}] {job.DisplayName} ({job.PluginType}) — Running");
            if (job.Progress is { } p) sb.AppendLine($"  progress: {p * 100:0}%");
            if (!string.IsNullOrWhiteSpace(job.ProgressMessage)) sb.AppendLine($"  {job.ProgressMessage}");
            if (job.StartedAt is { } started) sb.AppendLine($"  running since: {started:O}");
            return sb.ToString();
        }

        // Pending/Queued/Paused/finished states: no live progress to add, JobDigest already covers
        // what's known (params, output/error so far, log location).
        return JobDigest.From(job).Render();
    }

    /// <summary>
    /// A windowed slice of a job's output, plus the true total size — large results live in files
    /// (see <see cref="Core.Storage.LogFileManager"/>) rather than being handed to the orchestrator
    /// in one piece.
    /// </summary>
    public static string GetJobOutput(JobDag dag, string jobId, int offset, int limit)
    {
        var job = Find(dag, jobId);
        if (job is null) return $"Job '{jobId}' not found in this plan.";

        var body = JobDigest.RenderOutput(job.Result?.Output);
        if (body.Length == 0)
            return job.State == JobState.Running
                ? "No output yet — the job is still running."
                : "This job produced no output.";

        offset = Math.Clamp(offset, 0, body.Length);
        limit = Math.Max(0, limit);
        var end = Math.Min(body.Length, offset + limit);
        var window = body[offset..end];

        var sb = new StringBuilder();
        sb.AppendLine($"bytes {offset:N0}-{end:N0} of {body.Length:N0} total:");
        sb.AppendLine(window);
        if (end < body.Length)
        {
            var logFile = job.Result?.LogFile;
            sb.AppendLine(!string.IsNullOrWhiteSpace(logFile)
                ? $"[... {body.Length - end:N0} bytes remaining — full output: {logFile}]"
                : $"[... {body.Length - end:N0} bytes remaining ...]");
        }
        return sb.ToString();
    }

}
