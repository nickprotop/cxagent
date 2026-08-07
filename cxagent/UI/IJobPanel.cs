using CxAgent.Core.Models;

namespace CxAgent.UI;

/// <summary>
/// The job-panel UI seam GoalRunner writes to (parallel to IChatSink). SetJobs is called once when
/// the plan compiles; UpdateJob on each JobTransitioned. A real implementation (JobPanelSink) marshals
/// each call onto the UI thread; tests use a recording fake. GoalRunner never touches a control.
/// </summary>
public interface IJobPanel
{
    void SetJobs(IReadOnlyList<Job> jobs);
    void UpdateJob(Job job);

    /// <summary>A CPU/memory sample for a running job. Best-effort — not every job reports one.</summary>
    void UpdateResources(string jobId, ResourceSnapshot snapshot);

    /// <summary>
    /// A chunk of a worker's generated text, as it arrives. Best-effort: a panel that does not show
    /// live text simply ignores it, and the job's real output still arrives whole via
    /// <see cref="UpdateJob"/> when it finishes.
    /// </summary>
    void AppendText(string jobId, string delta);

    /// <summary>
    /// Copilot mode (P9 Task 2): true while the goal sits in GoalState.Draft awaiting F9/Esc — the
    /// panel must make it UNMISTAKABLE that the jobs it is showing are not running. Set true right
    /// before IChatSink.ShowApprovalRequest() and false the instant the gate resolves (approve,
    /// discard, or a cancelled draft), so it can never be left on past the goal that raised it.
    /// </summary>
    void SetDraftMode(bool isDraft);
}
