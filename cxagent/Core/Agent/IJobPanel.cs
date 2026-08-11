using CxAgent.Core.Models;

namespace CxAgent.Core.Agent;

/// <summary>
/// The job-panel UI seam AgentHost writes to (parallel to IChatSink). SetJobs is called once when
/// the plan compiles; UpdateJob on each JobTransitioned. A real implementation (JobPanelSink) marshals
/// each call onto the UI thread; tests use a recording fake. AgentHost never touches a control.
/// </summary>
public interface IJobPanel
{
    void SetJobs(IReadOnlyList<Job> jobs);
    void UpdateJob(Job job);

    /// <summary>
    /// New progress text for a RUNNING job's row — turns, occupancy, elapsed.
    ///
    /// <para>SEPARATE FROM <see cref="UpdateJob"/>, and it must be. That method force-expands the row
    /// and BLANKS ITS BODY on every call, which is right for a real state transition and ruinous once
    /// a second: a row that re-opens under a user who collapsed it, and that erases anything a later
    /// step might stream into it. This path touches the header and nothing else.</para>
    /// </summary>
    void UpdateProgress(Job job);

    /// <summary>A CPU/memory sample for a running job. Best-effort — not every job reports one.</summary>
    void UpdateResources(string jobId, ResourceSnapshot snapshot);

    /// <summary>
    /// A chunk of a worker's generated text, as it arrives. Best-effort: a panel that does not show
    /// live text simply ignores it, and the job's real output still arrives whole via
    /// <see cref="UpdateJob"/> when it finishes.
    /// </summary>
    void AppendText(string jobId, string delta);

}
