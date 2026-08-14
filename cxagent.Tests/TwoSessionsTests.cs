using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// TWO SESSIONS, ONE PROCESS, TWO FOLDERS — the property the whole isolation effort exists for.
///
/// <para>The stores are deliberately NOT split per session: every one of them is already keyed or
/// scoped so that sharing is safe. SqliteSessionStore and UsageHistoryStore key by agent id and run
/// in WAL with a busy timeout; PermissionRulesStore scopes by folder and merges another writer's
/// newer rules; LogFileManager is immutable and nests by agent ancestry. Splitting them would break
/// features that depend on the sharing — /stats spanning sessions, a trust decision surviving a
/// second window.</para>
///
/// <para>What is per-session is the FOLDER, and these tests pin that a second session with a second
/// root does not disturb the first.</para>
/// </summary>
public class TwoSessionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-two-" + Guid.NewGuid().ToString("N"));
    private readonly string _a;
    private readonly string _b;

    public TwoSessionsTests()
    {
        _a = Path.Combine(_dir, "project-a");
        _b = Path.Combine(_dir, "project-b");
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private AppPaths Paths() => new(Path.Combine(_dir, "config"));

    [Fact]
    public void TwoSessions_KeepTheirOwnFolders()
    {
        var a = new Session(_a);
        var b = new Session(_b);

        Assert.Equal(_a, a.WorkingDirectory);
        Assert.Equal(_b, b.WorkingDirectory);
    }

    /// <summary>
    /// A TRUST DECISION IS SCOPED TO ITS FOLDER. Trusting one project must not silently trust the
    /// other — that is the whole basis of the permission model, and the case a second session makes
    /// reachable for the first time.
    /// </summary>
    [Fact]
    public void TrustingOneFolder_DoesNotTrustTheOther()
    {
        var rules = new PermissionRulesStore(Paths());

        rules.SetTrust(_a, TrustState.Trusted);

        Assert.Equal(TrustState.Trusted, rules.GetTrust(_a));
        Assert.Equal(TrustState.Unknown, rules.GetTrust(_b));
    }

    /// <summary>
    /// THE GATE RESOLVES AGAINST ITS OWN SESSION'S ROOT. Two policies over two folders must disagree
    /// about the same relative path — otherwise session B could approve a write that lands in
    /// session A's checkout, with every layer behaving correctly on the way.
    /// </summary>
    [Fact]
    public void TwoPolicies_ResolveTheSameRelativePathDifferently()
    {
        var rules = new PermissionRulesStore(Paths());
        var policyA = new PermissionPolicy(_a, rules);
        var policyB = new PermissionPolicy(_b, rules);

        var inA = PermissionPolicy.RequestsFor("file",
            new Core.Models.JobParameters(new Dictionary<string, object?>
            { ["action"] = "write", ["path"] = "src/foo.cs" }), _a);
        var inB = PermissionPolicy.RequestsFor("file",
            new Core.Models.JobParameters(new Dictionary<string, object?>
            { ["action"] = "write", ["path"] = "src/foo.cs" }), _b);

        // The SAME string the model wrote resolves to two different files.
        Assert.NotEqual(inA[0].Display, inB[0].Display);
        Assert.Contains("project-a", inA[0].Display, StringComparison.Ordinal);
        Assert.Contains("project-b", inB[0].Display, StringComparison.Ordinal);

        // And each policy calls only its OWN one inside the boundary.
        Assert.True(policyA.IsInBoundary(inA[0].Display));
        Assert.False(policyA.IsInBoundary(inB[0].Display));
        Assert.True(policyB.IsInBoundary(inB[0].Display));
        Assert.False(policyB.IsInBoundary(inA[0].Display));
    }

    /// <summary>
    /// THE RESUME STORE IS SHARED AND KEYED BY FOLDER. Two sessions write to one database; each must
    /// see only its own work when it asks what is resumable here.
    /// </summary>
    [Fact]
    public void TheResumeStore_SeparatesSessionsByFolder()
    {
        var store = new SqliteSessionStore(Paths());

        store.SaveTurn("agent-a", [], 0, 0, _a);
        store.SaveTurn("agent-b", [], 0, 0, _b);

        Assert.Equal(_a, Assert.Single(store.List(_a)).WorkingDir);
        Assert.Equal(_b, Assert.Single(store.List(_b)).WorkingDir);

        // And listing everything sees both — which is what /sessions --all is for.
        Assert.Equal(2, store.List(null, all: true).Count);
    }

    /// <summary>
    /// LOG DIRECTORIES DO NOT COLLIDE. LogFileManager is immutable and keys by agent id, so two
    /// sessions in one process write beneath different paths without either being told about the
    /// other.
    /// </summary>
    [Fact]
    public void TwoAgents_WriteToDifferentLogPaths()
    {
        var logs = new LogFileManager(Paths());

        var pathA = logs.PathFor("agent-a", "job-1", "log");
        var pathB = logs.PathFor("agent-b", "job-1", "log");

        Assert.NotEqual(pathA, pathB);
    }
}
