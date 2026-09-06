using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which session a permission decision belongs to.
///
/// <para>ONE GATE SERVES EVERY SESSION IN THE PROCESS, and its reports used to name none of them —
/// so a consumer had to close over a session to file a history row, and filed every row against that
/// one however many sessions were actually asking. Invisible with one session, wrong with two, and
/// the wrongness is silent: the rows exist, they are just attributed to the wrong conversation.</para>
/// </summary>
public class PermissionAttributionTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-attr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PermissionPolicy PolicyFor(string sessionId, string root) =>
        new(root, new PermissionRulesStore(new AppPaths(TempDir())), EditMode.AcceptEdits)
        {
            SessionId = sessionId,
        };

    [Fact]
    public void APolicyCarriesItsSession()
    {
        Assert.Equal("s-1", PolicyFor("s-1", TempDir()).SessionId);
    }

    /// <summary>A CONSUMER WITH NO SESSIONS REPORTS NULL rather than guessing — Core is embedded by
    /// callers that have no such concept, and an invented id would be worse than an absent one.</summary>
    [Fact]
    public void APolicyWithoutOneSaysSo()
    {
        var policy = new PermissionPolicy(TempDir(),
            new PermissionRulesStore(new AppPaths(TempDir())), EditMode.AcceptEdits);

        Assert.Null(policy.SessionId);
    }

    /// <summary>
    /// THE DECISION REPORT CARRIES IT THROUGH. This is the property the whole task exists for: a
    /// report arriving from session B must say B, whoever wired the callback.
    /// </summary>
    [Fact]
    public async Task ADecisionReportNamesTheAskingSession()
    {
        var root = TempDir();
        var rules = new PermissionRulesStore(new AppPaths(TempDir()));
        var policy = new PermissionPolicy(root, rules, EditMode.AcceptEdits) { SessionId = "session-b" };

        var gate = PermissionDecider.ForTesting(policy, rules, notice: null,
            (_, _, _) => Task.FromResult(PermissionChoice.Once));

        PermissionDecisionReport? seen = null;
        gate.OnDecision = r => seen = r;

        await gate.RequestAsync(
            new PermissionRequest(PermissionKind.Shell, "ls", null) { Policy = policy },
            CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal("session-b", seen!.SessionId);
    }

    /// <summary>TWO SESSIONS, TWO ATTRIBUTIONS, through one gate — the case that was silently wrong
    /// and the reason a report cannot rely on whoever built the callback.</summary>
    [Fact]
    public async Task TwoSessionsThroughOneGateAreToldApart()
    {
        var rules = new PermissionRulesStore(new AppPaths(TempDir()));
        var a = new PermissionPolicy(TempDir(), rules, EditMode.AcceptEdits) { SessionId = "a" };
        var b = new PermissionPolicy(TempDir(), rules, EditMode.AcceptEdits) { SessionId = "b" };

        var gate = PermissionDecider.ForTesting(a, rules, notice: null,
            (_, _, _) => Task.FromResult(PermissionChoice.Once));

        var seen = new List<string?>();
        gate.OnDecision = r => seen.Add(r.SessionId);

        await gate.RequestAsync(
            new PermissionRequest(PermissionKind.Shell, "ls", null) { Policy = a }, CancellationToken.None);
        await gate.RequestAsync(
            new PermissionRequest(PermissionKind.Shell, "ls", null) { Policy = b }, CancellationToken.None);

        Assert.Equal(["a", "b"], seen);
    }
}
