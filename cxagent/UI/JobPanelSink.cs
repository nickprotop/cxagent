using CxAgent.Core.Models;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// The real IJobPanel: marshals every update onto the UI thread via EnqueueOnUIThread. GoalRunner
/// (which may run on a background thread) calls these; nothing here mutates a control off the UI thread.
/// </summary>
public sealed class JobPanelSink : IJobPanel
{
    private readonly ConsoleWindowSystem _system;
    private readonly JobPanelControl _panel;

    public JobPanelSink(ConsoleWindowSystem system, JobPanelControl panel)
    {
        _system = system;
        _panel = panel;
    }

    public void SetJobs(IReadOnlyList<Job> jobs) =>
        _system.EnqueueOnUIThread(() => _panel.SetJobs(jobs));

    public void UpdateJob(Job job) =>
        _system.EnqueueOnUIThread(() => _panel.UpdateJob(job));

    public void UpdateResources(string jobId, ResourceSnapshot snapshot) =>
        _system.EnqueueOnUIThread(() => _panel.UpdateResources(jobId, snapshot));

    /// <summary>No-op: the side panel shows a job's outcome, not its prose as it generates. The
    /// inline transcript (InlineJobSink) is where live text belongs — it has the width for it.</summary>
    public void AppendText(string jobId, string delta) { }

    public void SetDraftMode(bool isDraft) =>
        _system.EnqueueOnUIThread(() => _panel.SetDraftMode(isDraft));
}
