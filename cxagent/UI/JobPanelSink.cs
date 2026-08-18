using CxAgent.Core.Sessions;
using CxAgent.Core.Models;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// The real IToolObserver: marshals every update onto the UI thread via EnqueueOnUIThread. AgentHost
/// (which may run on a background thread) calls these; nothing here mutates a control off the UI thread.
/// </summary>
public sealed class JobPanelSink : IToolObserver
{
    private readonly ConsoleWindowSystem _system;
    private readonly JobPanelControl _panel;

    public JobPanelSink(ConsoleWindowSystem system, JobPanelControl panel)
    {
        _system = system;
        _panel = panel;
    }

    public void ToolsChanged(IReadOnlyList<Job> jobs) =>
        _system.EnqueueOnUIThread(() => _panel.ToolsChanged(jobs));

    public void ToolUpdated(Job job) =>
        _system.EnqueueOnUIThread(() => _panel.ToolUpdated(job));

    // The side panel redraws a whole row from the job it holds, so a progress tick is just an
    // update — there is no separate header to touch as there is in the inline transcript.
    public void ToolProgressed(Job job) =>
        _system.EnqueueOnUIThread(() => _panel.ToolUpdated(job));

    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) =>
        _system.EnqueueOnUIThread(() => _panel.ToolResourcesSampled(jobId, snapshot));

    /// <summary>No-op: the side panel shows a job's outcome, not its prose as it generates. The
    /// inline transcript (InlineJobSink) is where live text belongs — it has the width for it.</summary>
    public void ToolOutputAppended(string jobId, string delta) { }

}
