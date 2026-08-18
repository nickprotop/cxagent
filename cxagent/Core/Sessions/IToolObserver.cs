using CxAgent.Core.Models;

namespace CxAgent.Core.Sessions;

/// <summary>
/// What the session REPORTS about the tools it runs — which exist, how they progress, what they
/// produce.
///
/// <para>Named for the fact, not the surface. It was <c>IJobPanel</c>, which presumed a panel; the
/// session has no opinion about whether these become rows, log lines or JSON.</para>
/// </summary>
public interface IToolObserver
{
    void ToolsChanged(IReadOnlyList<Job> jobs);
    void ToolUpdated(Job job);

    /// <summary>
    /// New progress text for a RUNNING job's row — turns, occupancy, elapsed.
    ///
    /// <para>SEPARATE FROM <see cref="ToolUpdated"/>, and it must be. That method force-expands the row
    /// and BLANKS ITS BODY on every call, which is right for a real state transition and ruinous once
    /// a second: a row that re-opens under a user who collapsed it, and that erases anything a later
    /// step might stream into it. This path touches the header and nothing else.</para>
    /// </summary>
    void ToolProgressed(Job job);

    /// <summary>A CPU/memory sample for a running job. Best-effort — not every job reports one.</summary>
    void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot);

    /// <summary>
    /// A chunk of a worker's generated text, as it arrives. Best-effort: a panel that does not show
    /// live text simply ignores it, and the job's real output still arrives whole via
    /// <see cref="ToolUpdated"/> when it finishes.
    /// </summary>
    void ToolOutputAppended(string jobId, string delta);

}
