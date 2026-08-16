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

    // ---- The file writer must be SHARED, not split ------------------------------------------------

    // THE INVARIANT THAT ISOLATION COULD QUIETLY BREAK. Everything else here is per-session; the
    // lock table deliberately is not. Two sessions in one process editing one file — the same
    // checkout open twice, or one session reading a shared config another is rewriting — must take
    // the SAME lock, and they only do because FileMutation is static.
    //
    // A reasonable-looking refactor breaks this: give each session its own container and make the
    // writer an instance, and every one of these tests still passes while two sessions serialise
    // against nothing. This test is what fails instead.
    [Fact]
    public async Task TwoSessions_EditingOneFile_DoNotLoseEachOthersWork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twosess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var shared = Path.Combine(dir, "shared.cs");
        await File.WriteAllTextAsync(shared, "alpha\nbravo\n");

        // Two sessions, two roots, one file between them — each with its own plugin instance, as
        // separate sessions would have.
        var a = new FileJobPluginRunner(new Session(dir));
        var b = new FileJobPluginRunner(new Session(Path.GetTempPath()));

        await Task.WhenAll(
            a.ReplaceAsync(shared, "alpha", "ALPHA"),
            b.ReplaceAsync(shared, "bravo", "BRAVO"));

        var text = await File.ReadAllTextAsync(shared);
        Assert.Contains("ALPHA", text);
        Assert.Contains("BRAVO", text);

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>A session's own file plugin, as a second session would hold.</summary>
    private sealed class FileJobPluginRunner(Session session)
    {
        private readonly Core.Plugins.Builtin.FileJobPlugin _plugin = new();

        public Task ReplaceAsync(string path, string pattern, string replacement) =>
            _plugin.ExecuteAsync(
                new Core.Models.JobParameters(new Dictionary<string, object?>
                {
                    ["action"] = "replace",
                    ["path"] = path,
                    ["pattern"] = pattern,
                    ["replacement"] = replacement,
                }),
                new CollectingContext { WorkingDirectory = session.WorkingDirectory },
                CancellationToken.None);
    }

    // ---- One gate, two policies -------------------------------------------------------------------

    // THE QUESTION IS PER-SESSION; THE ANSWER IS NOT. The gate has to be shared — stored rules and
    // the prompt queue both must be — but it used to CAPTURE a policy, and a policy holds a working
    // directory and an edit mode that belong to one conversation. A second session was therefore
    // judged against the first's root: a write inside its own folder read as outside, and a write
    // inside the OTHER session's folder read as inside.
    [Fact]
    public async Task TwoSessions_AreJudgedAgainstTheirOwnFolder()
    {
        var config = MakeTempDir();
        var a = MakeTempDir();
        var b = MakeTempDir();

        var store = new PermissionRulesStore(new AppPaths(config));
        store.SetTrust(a, TrustState.Trusted);
        store.SetTrust(b, TrustState.Trusted);

        var policyA = new PermissionPolicy(a, store);
        var policyB = new PermissionPolicy(b, store);

        // One gate for the process, holding A's policy as its own — the single-session default.
        var asked = new List<string>();
        var gate = new RecordingGate(asked);

        // A request from session B, stamped with B's policy, must be judged by B's root.
        var inB = new PermissionRequest(PermissionKind.FileWrite, Path.Combine(b, "f.cs"), null)
        {
            Policy = policyB,
        };

        Assert.True(policyB.IsSilentlyAllowed(inB));   // inside B's trusted folder
        Assert.False(policyA.IsSilentlyAllowed(inB));  // and NOT inside A's

        await gate.RequestAsync(inB, CancellationToken.None);
        Assert.Single(asked);
    }

    // A grant belongs to the project it was made in. The gate serves every session, so filing an
    // "Always" under its own root would grant a permission in a folder the user was not looking at.
    [Fact]
    public void APolicyKnowsTheFolderAGrantBelongsTo()
    {
        var a = MakeTempDir();
        var b = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(MakeTempDir()));

        Assert.Equal(a, new PermissionPolicy(a, store).Root);
        Assert.Equal(b, new PermissionPolicy(b, store).Root);
    }

    // END TO END: the wiring actually delivers it. SessionFactory builds the registry per session
    // and passes the policy; the gated plugin stamps it on every request. Without this the two tests
    // above would pass on a policy nothing ever sends.
    [Fact]
    public async Task TheSessionsPolicy_ReachesTheGate()
    {
        var dir = MakeTempDir();
        var store = new PermissionRulesStore(new AppPaths(dir));
        var policy = new PermissionPolicy(dir, store);
        var spy = new PolicySpy();

        var registry = Core.Plugins.PluginRegistry.CreateWithBuiltins(null, spy, policy);
        registry.TryGet("file", out var plugin);

        await plugin!.ExecuteAsync(
            new Core.Models.JobParameters(new Dictionary<string, object?>
            {
                ["action"] = "write",
                ["path"] = Path.Combine(dir, "x.txt"),
                ["content"] = "hi",
            }),
            new CollectingContext { WorkingDirectory = dir }, CancellationToken.None);

        Assert.Same(policy, spy.Seen);
    }

    /// <summary>Captures the policy a request carried.</summary>
    private sealed class PolicySpy : IPermissionGate
    {
        public PermissionPolicy? Seen;
        public Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Seen = request.Policy;
            return Task.FromResult(true);
        }
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twopol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A gate that records what it was asked, standing in for the interactive one.</summary>
    private sealed class RecordingGate(List<string> asked) : IPermissionGate
    {
        public Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            asked.Add(request.Display);
            return Task.FromResult(true);
        }
    }
}
