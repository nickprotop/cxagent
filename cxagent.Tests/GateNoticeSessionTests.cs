using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That a gate's own words say which session they belong to.
///
/// <para>ONE GATE SERVES EVERY SESSION, so a notice channel that names none is printed wherever the
/// consumer's closure happens to point. It pointed at the first tab's transcript control, captured
/// at startup — so every session's "denied: …" and "trusted this folder" was filed against the FIRST
/// conversation. A security notice recorded against a session that did not produce it accuses the
/// wrong one and leaves the right one with no record of what it was refused.</para>
/// </summary>
public class GateNoticeSessionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-notice-" + Guid.NewGuid().ToString("N"));

    public GateNoticeSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A DENIAL NAMES THE SESSION THAT WAS DENIED.</summary>
    [Fact]
    public async Task ADeniedRequestsNoticeCarriesItsSessionId()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        var said = new List<(string? Session, Message Message)>();

        var policy = new PermissionPolicy(_dir, rules) { SessionId = "session-beta" };
        var gate = PermissionDecider.WithPrompt(rules,
            (sessionId, message) => said.Add((sessionId, message)),
            (_, _, _) => Task.FromResult(PermissionChoice.Deny));

        await gate.RequestAsync(new PermissionRequest(
            PermissionKind.Shell, "rm -rf /", AlwaysRule: null, Subject: "rm -rf /") { Policy = policy },
            CancellationToken.None);

        var denial = said.FirstOrDefault(s => s.Message.Text.StartsWith("denied:", StringComparison.Ordinal));
        Assert.Equal("session-beta", denial.Session);
    }

    /// <summary>
    /// AND A REQUEST WITH NO POLICY SAYS SO rather than guessing. That notice is the one refusing a
    /// request that carried no session at all, so there is nothing to name — and null is what lets a
    /// consumer fall back to the surface in front of the user instead of inventing an owner.
    /// </summary>
    [Fact]
    public async Task ARequestWithNoPolicyNotifiesWithNoSession()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        var said = new List<(string? Session, Message Message)>();

        var gate = PermissionDecider.WithPrompt(rules,
            (sessionId, message) => said.Add((sessionId, message)),
            (_, _, _) => Task.FromResult(PermissionChoice.Once));

        await gate.RequestAsync(
            new PermissionRequest(PermissionKind.Shell, "ls", AlwaysRule: null, Subject: "ls"),
            CancellationToken.None);

        Assert.NotEmpty(said);
        Assert.All(said, s => Assert.Null(s.Session));
    }
}
