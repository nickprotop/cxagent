using CxAgent.Core.Models;

namespace CxAgent.Core.Jobs;

/// <summary>
/// The host's reports, callable on any <see cref="IJobContext"/>: told to the host's own context,
/// and to nothing else.
///
/// <para>EVERY ONE IS BEST-EFFORT ALREADY — a row's timer, a "waiting for permission" mark, a
/// resource sample, a badge — so a context that is not the host's (an embedder's, a plugin author's
/// test fake) loses nothing by not hearing them. The same names as <see cref="IJobHostContext"/>'s
/// members, so a call site reads as it always did; on a context typed as the host's, the member
/// itself is called. Internal: this is how Core reports, not something a tool calls.</para>
/// </summary>
internal static class JobHostReporting
{
    public static void WorkStarting(this IJobContext context)
    {
        if (context is IJobHostContext host) host.WorkStarting();
    }

    public static void ReportPermissionWait(this IJobContext context, bool waiting)
    {
        if (context is IJobHostContext host) host.ReportPermissionWait(waiting);
    }

    public static void ReportReviewing(this IJobContext context, bool reviewing)
    {
        if (context is IJobHostContext host) host.ReportReviewing(reviewing);
    }

    public static void ReportResources(this IJobContext context, ResourceSnapshot snapshot)
    {
        if (context is IJobHostContext host) host.ReportResources(snapshot);
    }

    public static void ReportToolCall(this IJobContext context, string toolName, string summary)
    {
        if (context is IJobHostContext host) host.ReportToolCall(toolName, summary);
    }

    /// <summary>Records the gate's verdict on the host's context — see <see cref="IJobHostContext.DecidedBy"/>.</summary>
    public static void SetDecidedBy(this IJobContext context, string? decidedBy)
    {
        if (context is IJobHostContext host) host.DecidedBy = decidedBy;
    }
}
