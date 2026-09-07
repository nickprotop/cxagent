using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That a decision report says which folder it was judged against.
///
/// <para>ONE GATE SERVES EVERY SESSION, so a report can arrive from any of them. The history
/// consumer read the SESSION from the report but the FOLDER from its own closure — filing rows that
/// named session B beside session A's directory. Internally inconsistent, and wrong in exactly the
/// case history is consulted for: which session was working where when it asked.</para>
/// </summary>
public class PermissionReportRootTests
{
    private static PermissionPolicy Policy(string root, string sessionId) =>
        new(root, new PermissionRulesStore(new AppPaths(
            Path.Combine(Path.GetTempPath(), "cxagent-perm-" + Guid.NewGuid().ToString("N")))))
        {
            SessionId = sessionId,
        };

    /// <summary>THE REPORT CARRIES BOTH, and they describe the same session.</summary>
    [Fact]
    public void ARequestsPolicySuppliesTheRootAndTheSession()
    {
        var policy = Policy("/tmp/beta", "session-beta");

        var report = new PermissionDecisionReport(
            PermissionKind.FileWrite, "denied", "agent", "some/file")
        {
            SessionId = policy.SessionId,
            Root = policy.Root,
        };

        Assert.Equal("session-beta", report.SessionId);
        Assert.Equal("/tmp/beta", report.Root);
    }

    /// <summary>
    /// AND A REPORT WITHOUT A POLICY SAYS SO rather than guessing. That is the only case where the
    /// consumer's own folder is the right fallback — nulls here are what make the fallback correct
    /// instead of a silent overwrite of a real answer.
    /// </summary>
    [Fact]
    public void ARequestWithNoPolicyLeavesBothUnset()
    {
        var report = new PermissionDecisionReport(
            PermissionKind.FileWrite, "denied", "agent", "some/file");

        Assert.Null(report.SessionId);
        Assert.Null(report.Root);
    }
}
